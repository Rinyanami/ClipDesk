using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ClipDesk.App;

public sealed class App : Application
{
    internal Store Store { get; private set; } = null!;
    internal ClipboardService Clipboard { get; private set; } = null!;
    internal Vault Vault { get; private set; } = null!;
    internal bool Exiting { get; private set; }
    internal string SyncStatus { get; private set; } = "未配置同步 · 内容保存在本机";
    internal event Action<string>? SyncChanged;
    internal bool SyncInProgress { get; private set; }
    private HistoryWindow history = null!;
    private HistoryWindow? pinned;
    private CollectionWindow? todos, memos, trash;
    private SettingsWindow? settings;
    private Forms.NotifyIcon tray = null!;
    private System.Drawing.Icon? trayIcon;
    private readonly Dictionary<string, EditorWindow> editors = [];
    private readonly CancellationTokenSource quit = new();
    private readonly SemaphoreSlim syncSignal = new(0, 1);
    private Mutex? instance;
    private Mutex? installGuard;
    private EventWaitHandle? showSignal;
    private RegisteredWaitHandle? showWait;
    private EventWaitHandle? quitSignal;
    private RegisteredWaitHandle? quitWait;
    private Task? syncTask;
    private Task? maintenanceTask;
    private CancellationTokenSource? currentSync;
    private bool renderCheck;
    private bool suppressServiceStartup = false;
    private bool exitPending;
    private long connectionRevision;
    private int manualSync;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--native-check") || args.Contains("--native-isolated-check")) { NativeChecks.Run(args); return; }
        if (args.Contains("--quit")) { SignalQuitRunningInstance(); return; }
        if (!args.Contains("--ui-check") && SignalRunningInstance()) return;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.renderCheck = args.Contains("--ui-check");
        app.DispatcherUnhandledException += (_, e) => { Ui.Error("操作未完成：" + e.Exception.Message); e.Handled = true; };
        app.Run();
    }
    private static string DataRoot => Environment.GetEnvironmentVariable("CLIPDESK_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipDesk");
    private static string InstanceScope => System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(DataRoot))))[..16];

    private static bool SignalRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting("Local\\ClipDesk.Show." + InstanceScope, out var signal)) return false;
        using (signal) return signal.Set();
    }

    private static void SignalQuitRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting("Local\\ClipDesk.Quit." + InstanceScope, out var signal)) return;
        using (signal) signal.Set();
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 4000)
        {
            if (!EventWaitHandle.TryOpenExisting("Local\\ClipDesk.Show." + InstanceScope, out var active)) break;
            active.Dispose();
            Thread.Sleep(50);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (suppressServiceStartup) return;
        try
        {
            var root = DataRoot;
            var scope = InstanceScope;
            instance = new Mutex(true, "Local\\ClipDesk." + scope, out var first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ClipDesk.Show." + scope);
            quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ClipDesk.Quit." + scope);
            if (!first) { showSignal.Set(); Shutdown(); return; }
            if (!renderCheck) installGuard = new Mutex(false, "Local\\ClipDesk.InstallGuard");
            _ = Task.Run(() => { try { _ = System.Windows.Automation.AutomationElement.RootElement; } catch { } });
            Store = new Store(root, singleOwner: true); Vault = new(scope); Ui.Theme(this);
            Store.LocalChanged += QueueSync;
            Clipboard = new ClipboardService(Store, observeClipboard: !renderCheck); history = new HistoryWindow(this);
            MainWindow = history;
            Clipboard.WindowRequested += RequestWindow; Clipboard.Feedback += Notify;
            if (renderCheck) { BuildTray(); _ = Dispatcher.InvokeAsync(RenderChecks); return; }
            string? registrationIssue = null;
            try
            {
                var issues = Clipboard.RegisterStartup(Store.Settings);
                if (issues.Count > 0) registrationIssue = "部分窗口快捷键未启用：" + string.Join("；", issues) + "。可在设置中更换，其他入口仍可使用。";
            }
            catch (Exception ex) { registrationIssue = ex.Message; }
            showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(history.Summon), null, -1, false);
            quitWait = ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => Dispatcher.BeginInvoke(ExitApp), null, -1, false);
            if (!e.Args.Contains("--background")) history.Summon();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (exitPending || Exiting) return;
                BuildTray();
                if (registrationIssue != null) Notify(registrationIssue);
                var layouts = Store.Settings.Windows;
                if (pinned == null && layouts.GetValueOrDefault("pinned")?.Open == true) { pinned = new HistoryWindow(this, pinnedOnly: true); pinned.Open(activate: false); }
                if (todos == null && layouts.GetValueOrDefault("todo")?.Open == true) { todos = new CollectionWindow(this, "todo"); todos.Open(activate: false); }
                if (memos == null && layouts.GetValueOrDefault("memo")?.Open == true) { memos = new CollectionWindow(this, "memo"); memos.Open(activate: false); }
                if (e.Args.Contains("--background")) history.Warm();
                maintenanceTask = MaintainHistory(quit.Token); syncTask = SyncLoop(quit.Token);
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
            }));
        }
        catch (Exception ex) { Ui.Error("ClipDesk 启动失败：" + ex.Message); Shutdown(1); }
    }
    private void BuildTray()
    {
        using var iconStream = GetResourceStream(new Uri("/ClipDesk;component/Assets/Brand/App.ico", UriKind.Relative)).Stream;
        using var icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize);
        trayIcon = (System.Drawing.Icon)icon.Clone();
        tray = new Forms.NotifyIcon { Text = Store.Settings.Paused ? "ClipDesk · 已暂停记录" : "ClipDesk · 正在记录", Icon = trayIcon, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        void Item(string text, Action action) => menu.Items.Add(text, null, (_, _) => Dispatcher.BeginInvoke(action));
        Item("打开剪贴板", () => ShowHistory()); Item("置顶剪贴", () => ShowPinned()); Item("待办", ShowTodos); Item("备忘", ShowMemos); menu.Items.Add(new Forms.ToolStripSeparator());
        var pause = new Forms.ToolStripMenuItem(); pause.Click += (_, _) => Dispatcher.BeginInvoke(TogglePause); menu.Items.Add(pause);
        menu.Opening += (_, _) => { pause.Text = Store.Settings.Paused ? "恢复剪贴记录" : "暂停剪贴记录"; pause.Checked = Store.Settings.Paused; };
        Item("本机草稿", ShowDrafts); Item("回收站", ShowTrash); Item("立即同步", RequestSync); Item("设置", ShowSettings); Item("退出 ClipDesk", ExitApp);
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(history.Summon);
    }
    internal void Notify(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Notify(message)); return; }
        history?.Status(message); pinned?.Status(message);
        if (tray != null && history?.IsVisible != true && pinned?.IsVisible != true) { tray.BalloonTipTitle = "ClipDesk"; tray.BalloonTipText = message; tray.ShowBalloonTip(2500); }
    }
    private void RequestWindow(string key)
    {
        switch (key)
        {
            case "clipboard":
                if (pinned?.IsActive == true) history.Present(pinned.CurrentTarget); else history.Summon();
                break;
            case "pinned":
                pinned ??= new HistoryWindow(this, pinnedOnly: true);
                if (history.IsActive) pinned.Present(history.CurrentTarget); else pinned.Summon();
                break;
            case "todo": ShowTodos(); break;
            case "memo": ShowMemos(); break;
        }
    }
    internal void ShowHistory(PasteTarget? priorTarget = null) => history.Present(priorTarget);
    internal void ShowPinned(PasteTarget? priorTarget = null) => (pinned ??= new HistoryWindow(this, pinnedOnly: true)).Present(priorTarget);
    internal void ShowTodos() { todos ??= new CollectionWindow(this, "todo"); todos.Open(); }
    internal void ShowMemos() { memos ??= new CollectionWindow(this, "memo"); memos.Open(); }
    internal void ShowTrash() { trash ??= new CollectionWindow(this, "trash"); trash.Open(); }
    internal void ShowSettings() { settings ??= new SettingsWindow(this); settings.Open(); }
    internal void Edit(Entry entry, string? draftKey = null)
    {
        if (exitPending || Exiting) return;
        var key = draftKey ?? (Store.Get(entry.Id) == null ? "new:" + entry.Kind : entry.Id);
        if (!editors.ContainsKey(key) && Store.GetDraft(key) == null && Store.Versions(entry.Id).Count > 1)
        {
            Details(entry);
            Notify("这条内容有多个版本，请先在详情中核对并选择采用；已有草稿仍可继续编辑或另存");
            return;
        }
        if (!editors.TryGetValue(key, out var window))
        {
            window = new EditorWindow(this, entry, key);
            var parent = entry.Kind == "memo" ? memos : entry.Kind == "todo" ? todos : null;
            if (parent?.IsVisible == true && parent.IsActive) window.Owner = parent;
            editors[key] = window;
        }
        window.Open();
    }
    internal void ReleaseEditor(string key) { if (editors.Remove(key, out var window)) window.ClosePermanently(); }
    internal void Convert(Entry source, string kind)
    {
        var full = Store.Get(source.Id) ?? source;
        var value = Store.Convert(full, kind, full.Id + ":" + full.UpdatedAt + ":" + kind);
        if (kind == "todo") ShowTodos(); else ShowMemos(); Notify(kind == "todo" ? "已加入待办" : "已保存为备忘");
    }
    internal void ShowDrafts()
    {
        var window = new Window { Title = "本机草稿 · ClipDesk", Width = 560, Height = 450, Background = Ui.Background, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var panel = new StackPanel { Margin = new Thickness(18) };
        void RenderList()
        {
            panel.Children.Clear();
            var heading = Ui.Text("未保存的本机草稿", 20);
            heading.FontWeight = FontWeights.SemiBold;
            panel.Children.Add(heading);
            panel.Children.Add(Ui.Text("草稿仅保存在本机，可继续编辑并保存，或直接丢弃。", 12, Ui.Muted));
            var keys = Store.DraftKeys();
            if (keys.Count == 0)
            {
                panel.Children.Add(Ui.Card(Ui.Text("当前没有本机草稿", 13.5, Ui.Muted)));
                return;
            }
            foreach (var key in keys)
            {
                var draft = Store.GetDraft(key);
                if (draft == null) continue;
                var value = draft.Value;
                var row = new DockPanel();
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                actions.Children.Add(Ui.Button("继续编辑", () => { Edit(value, key); window.Close(); }, true));
                actions.Children.Add(Ui.Button("丢弃", () =>
                {
                    if (!Ui.Confirm("丢弃这份未保存的本机草稿？已保存的原记录不会受影响。")) return;
                    ReleaseEditor(key);
                    Store.DiscardDraft(key);
                    RenderList();
                }));
                DockPanel.SetDock(actions, Dock.Right);
                row.Children.Add(actions);

                var info = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
                var caption = Ui.Text(Ui.PreviewText(value.Caption, 80), 14);
                caption.FontWeight = FontWeights.SemiBold;
                info.Children.Add(caption);
                var kindName = value.Kind == "todo" ? "待办" : value.Kind == "memo" ? "备忘" : "剪贴";
                var timeText = DateTimeOffset.FromUnixTimeMilliseconds(value.UpdatedAt).ToLocalTime().ToString("MM-dd HH:mm");
                var meta = $"{kindName} · 更新于 {timeText}" + (value.Assets.Count > 0 ? $" · {value.Assets.Count} 张图片" : "");
                info.Children.Add(Ui.Text(meta, 11.5, Ui.Muted));
                row.Children.Add(info);
                panel.Children.Add(Ui.Card(row));
            }
        }
        RenderList();
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Ui.PresentTransient(window);
    }
    internal void Details(Entry entry)
    {
        var window = new Window { Title = "详情与版本 · ClipDesk", Width = 680, Height = 650, Background = Ui.Background, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var panel = new StackPanel { Margin = new Thickness(20) }; panel.Children.Add(Ui.Text("内容详情", 23));
        panel.Children.Add(Ui.Text("记录 ID：" + entry.Id + (entry.SourceId == null ? "" : "\n来源剪贴 ID：" + entry.SourceId), 12, Ui.Muted));
        var versions = Store.Versions(entry.Id);
        var inspectedClock = Clocks.Join(versions);
        panel.Children.Add(Ui.Text(versions.Count > 1 ? "存在并发版本。全部保留；选择采用前，请逐一核对。" : "当前只有一个有效版本。", 13, Ui.Accent));
        foreach (var version in versions.OrderByDescending(x => x.Time))
        {
            var block = new StackPanel(); block.Children.Add(Ui.Text((version.Value.Deleted ? "已删除 · " : "") + "设备 " + version.DeviceId[..8] + " · " + DateTimeOffset.FromUnixTimeMilliseconds(version.Time).ToLocalTime().ToString("MM-dd HH:mm"), 12));
            var text = Ui.Input(version.Value.Title + "\n" + version.Value.Body, true); text.IsReadOnly = true; text.Height = 150; block.Children.Add(text);
            var value = version.Value;
            block.Children.Add(Ui.Text($"{(value.Kind == "todo" ? "待办" : value.Kind == "memo" ? "备忘" : "剪贴")} · {(value.Pinned ? "已置顶" : "未置顶")} · {(value.Protected ? "长期保留" : "普通历史")}" + (value.Kind == "todo" ? $" · {(value.Done ? "已完成" : "未完成")} · 到期：{value.Due ?? "未设置"}" : ""), 12, Ui.Muted));
            foreach (var asset in value.Assets) block.Children.Add(Ui.Thumbnail(Store.Assets, asset, 320, 160));
            if (versions.Count > 1)
            {
                var adoptBtn = Ui.Button("采用这个版本", () => { if (Ui.Confirm("采用这个版本的正文、附件和状态？稍后新收到的远端版本仍保留为冲突；未保存的本机草稿不会被覆盖。")) { Store.AdoptVersion(version.Value, inspectedClock); window.Close(); } }, true);
                adoptBtn.HorizontalAlignment = HorizontalAlignment.Left;
                block.Children.Add(adoptBtn);
            }
            panel.Children.Add(Ui.Card(block));
        }
        window.Content = new ScrollViewer { Content = panel }; Ui.PresentTransient(window);
    }
    internal void TogglePause()
    { var s = Store.Settings; Store.SaveSettings(s with { Paused = !s.Paused }); history.UpdateRecording(); if (tray != null) tray.Text = s.Paused ? "ClipDesk · 正在记录" : "ClipDesk · 已暂停记录"; Notify(s.Paused ? "已恢复剪贴记录" : "已暂停剪贴记录"); }
    private void QueueSync() { try { syncSignal.Release(); } catch (SemaphoreFullException) { } }
    internal void RequestSync() { Interlocked.Exchange(ref manualSync, 1); QueueSync(); }
    internal void ConnectionChanged()
    {
        Interlocked.Increment(ref connectionRevision);
        try { currentSync?.Cancel(); } catch (ObjectDisposedException) { }
        RequestSync();
    }
    private void SetSyncStatus(string message)
    { SyncStatus = message; Dispatcher.BeginInvoke(() => SyncChanged?.Invoke(message)); }
    private async Task SyncLoop(CancellationToken ct)
    {
        SyncEngine? engine = null; IObjectStore? remote = null; string? binding = null; int idle = 15;
        long engineRevision = -1; var retryAfter = DateTimeOffset.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var explicitlyRequested = Interlocked.Exchange(ref manualSync, 0) != 0;
                var remainingBackoff = retryAfter - DateTimeOffset.UtcNow;
                if (!explicitlyRequested && engineRevision == Interlocked.Read(ref connectionRevision) && remainingBackoff > TimeSpan.Zero)
                { await syncSignal.WaitAsync(remainingBackoff, ct); continue; }
                try
                {
                    var config = Store.Settings.Sync;
                    if (!config.Enabled) { SetSyncStatus("未开启同步 · 内容保存在本机"); engine = null; remote?.Dispose(); remote = null; binding = null; idle = 60; }
                    else
                    {
                        var nextBinding = Codec.Json(config);
                        if (engine == null || binding != nextBinding || engineRevision != Interlocked.Read(ref connectionRevision))
                        {
                            remote?.Dispose();
                            engine = null; remote = null; engineRevision = Interlocked.Read(ref connectionRevision);
                            var ak = Vault.Get("kodo-ak") ?? throw new InvalidOperationException("请在设置中填写七牛 AK");
                            var sk = Vault.Get("kodo-sk") ?? throw new InvalidOperationException("请在设置中填写七牛 SK");
                            var key = Vault.Get("sync-key") ?? throw new InvalidOperationException("请生成或导入同步密钥");
                            remote = new KodoStore(config, ak, sk); engine = new SyncEngine(Store, remote, new SyncCrypto(System.Convert.FromBase64String(key)), config.Prefix);
                            engine.RemoteWritten += () => Store.SetMeta("cloud-binding", Codec.Json(new { config.Endpoint, config.Region, config.Bucket, config.Prefix }));
                            var liveRevision = engineRevision;
                            engine.Status += SetSyncStatus; engine.LiveCopy += change => Dispatcher.BeginInvoke(async () =>
                            {
                                if (exitPending || Exiting || !Store.Settings.Sync.Enabled || !Store.Settings.Sync.ReceiveClipboard || liveRevision != Interlocked.Read(ref connectionRevision) || change.Time <= Clipboard.LastLocalCopy + 2000 || Codec.Now - change.Time > 30000) return;
                                try { if (await Clipboard.Copy(change.Value, remoteCopyTime: change.Time, remoteStillCurrent: () => liveRevision == Interlocked.Read(ref connectionRevision) && !exitPending) != 0) history.Status("已接收其他设备的新复制内容"); } catch (Exception ex) { Notify("远端内容已保存；剪贴板未更新：" + ex.Message); }
                            }); binding = nextBinding;
                        }
                        SyncResult result;
                        SyncInProgress = true;
                        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        currentSync = runCancellation;
                        try { result = await Task.Run(() => engine.Run(runCancellation.Token), runCancellation.Token); }
                        finally { currentSync = null; SyncInProgress = false; }
                        idle = Store.PendingCount > 0 ? 2 : config.ReceiveClipboard || result.Received + result.Uploaded > 0 ? 15 : Math.Min(120, idle * 2);
                    }
                    retryAfter = DateTimeOffset.MinValue;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { idle = 1; retryAfter = DateTimeOffset.MinValue; }
                catch (Exception ex)
                {
                    var message = ex is System.Net.Http.HttpRequestException || ex is Amazon.Runtime.AmazonServiceException { InnerException: System.Net.Http.HttpRequestException } ? "离线 · 内容已保存在本机，联网后重试"
                        : ex is Amazon.S3.AmazonS3Exception ? "同步失败 · " + KodoStore.DescribeError(ex)
                        : ex is InvalidDataException ? "同步数据校验未通过 · " + Ui.PreviewText(ex.Message, 160)
                        : "同步失败 · 请检查配置、权限或网络，将自动重试";
                    SetSyncStatus(message); idle = Math.Min(300, Math.Max(30, idle * 2)); retryAfter = DateTimeOffset.UtcNow.AddSeconds(idle);
                }
                await syncSignal.WaitAsync(TimeSpan.FromSeconds(idle), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { remote?.Dispose(); }
    }
    private async Task MaintainHistory(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Run(() => Store.Cleanup(), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch { _ = Dispatcher.BeginInvoke(() => Notify("普通历史暂未清理；内容已保留，将稍后重试")); }
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
        }
        catch (OperationCanceledException) { }
    }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { foreach (Window window in Windows) if (window.IsVisible) Ui.MakeVisible(window); });
    private async void RenderChecks()
    {
        var results = new Dictionary<string, object>();
        try
        {
            if (Environment.GetEnvironmentVariable("CLIPDESK_DATA") == null) throw new InvalidOperationException("界面检查必须指定隔离数据目录");
            Store.Save(new Entry { Title = "常用项目说明", Body = "ClipDesk 以剪贴为核心。\n收集内容，随手复用；需要时再加入待办或备忘。", Pinned = true, Protected = true });
            Store.Save(new Entry { Body = "https://developer.qiniu.com/kodo", Format = "link", SourceApp = "浏览器" });
            Store.Save(new Entry { Body = "今天需要核对薄垫线产品尺寸、包装和运输信息。\n把重要事项保存为待办，长期材料保存为备忘。", SourceApp = "文字编辑器" });
            var memo = Store.Save(new Entry { Kind = "memo", Title = "工作记录", Body = "调研记录：\n• 用户需求与使用场景\n• 产品尺寸与材质\n• 竞品与定价", Protected = true });
            Store.Save(new Entry { Kind = "todo", Title = "整理本周待确认事项", Body = "核对资料并整理疑问。", Due = DateTime.Today.AddDays(2).ToString("yyyy-MM-dd"), Protected = true });
            Store.Save(new Entry { Kind = "todo", Title = "试用 ClipDesk 的剪贴与备忘", Body = "文字和图片都能单独保存。", Protected = true });
            void Render(Window w, string name)
            {
                w.UpdateLayout(); var content = w.AllowsTransparency ? w : (FrameworkElement)w.Content; content.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) { var rect = new Rect(0, 0, content.ActualWidth, content.ActualHeight); dc.DrawRectangle(Ui.Background, null, rect); dc.DrawRectangle(new VisualBrush(content), null, rect); } bitmap.Render(visual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(Store.Root, name + ".png")); encoder.Save(stream);
            }
            ListBox? FindList(DependencyObject node)
            {
                if (node is ListBox found) return found;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    if (FindList(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
                return null;
            }
            async Task Ready(Func<bool> ready, string name)
            {
                var timer = Stopwatch.StartNew();
                while (!ready() && timer.ElapsedMilliseconds < 5000) await Task.Delay(25);
                results[name + "列表载入完成"] = ready(); results[name + "列表等待毫秒"] = timer.ElapsedMilliseconds;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            }
            history.Open(); await Ready(() => history.IsListReady, "剪贴"); Render(history, "剪贴首页"); history.Hide();
            ShowTodos(); ShowMemos(); await Ready(() => todos!.IsListReady && memos!.IsListReady, "待办备忘"); Render(todos!, "待办窗口"); Render(memos!, "备忘窗口");
            results["待办备忘独立可见"] = todos!.IsVisible && memos!.IsVisible;
            ShowPinned(); await Ready(() => pinned!.IsListReady, "置顶"); Render(pinned!, "置顶窗口");
            var samePinned = pinned; ShowPinned(); results["重复打开置顶复用窗口"] = ReferenceEquals(samePinned, pinned);
            results["置顶待办备忘同时可见"] = pinned!.IsVisible && todos!.IsVisible && memos!.IsVisible;
            pinned!.Width = 360; pinned.Height = 480; todos!.Width = 360; todos.Height = 480; memos!.Width = 360; memos.Height = 480;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(pinned, "置顶_小面板"); Render(todos, "待办_小面板"); Render(memos, "备忘_小面板");
            foreach (var (panel, name) in new (Window, string)[] { (pinned, "置顶"), (todos, "待办"), (memos, "备忘") })
            {
                panel.Width = panel.MinWidth; panel.Height = panel.MinHeight;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                Render(panel, name + "_最小尺寸");
                if (FindList(panel) is { } visibleList)
                    results[name + "最小窗口列表高度"] = Math.Round(visibleList.ActualHeight, 1);
                panel.Width = 360; panel.Height = 480;
            }
            todos!.Topmost = true; memos!.Topmost = true; history.Summon(); await Task.Delay(150);
            results["常驻窗口在剪贴唤出后仍可见"] = todos.IsVisible && memos.IsVisible && pinned.IsVisible && history.IsVisible;
            history.Hide(); results["收起剪贴不关闭常驻窗口"] = todos.IsVisible && memos.IsVisible && pinned.IsVisible;
            Edit(memo); await Task.Delay(150); var editor = editors[memo.Id]; Render(editor, "备忘编辑器");
            editor.Activate(); editor.BodyInput.Focus(); editor.BodyInput.Select(5, 0); await Task.Delay(150);
            var editorWasForeground = Native.GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(editor).Handle;
            var destination = editorWasForeground ? PasteTarget.Capture() : null;
            // 只检查本应用控件的目标保存；不向外部软件注入输入。
            results["本应用备忘输入目标已识别"] = editorWasForeground && destination != null;
            results["备忘编辑窗口获得前台"] = Native.GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(editor).Handle;
            results["备忘输入框获得键盘焦点"] = editor.BodyInput.IsKeyboardFocused;
            editor.Hide(); ShowSettings(); await Task.Delay(150); Render(settings!, "设置窗口");
            settings!.ShowSection(1); Render(settings, "设置_数据与恢复"); settings.ShowSection(2); Render(settings, "设置_多电脑同步");
            results["原生主窗口数"] = Windows.Count; results["鼠标双击及第三方粘贴"] = "未执行";
        }
        catch (Exception ex) { results["error"] = ex.ToString(); }
        finally { File.WriteAllText(Path.Combine(Store.Root, "ui-check.json"), Codec.Json(results)); ExitApp(); }
    }
    internal async void ExitApp()
    {
        if (Exiting || exitPending) return;
        if (settings?.HasPendingWork == true) { Notify("备份、恢复或连接测试正在进行，请完成后再退出"); return; }
        if (history.HasPendingWork || pinned?.HasPendingWork == true || todos?.HasPendingWork == true || memos?.HasPendingWork == true || trash?.HasPendingWork == true || editors.Values.Any(w => w.HasPendingWork))
        { Notify("正在处理或复制内容，请操作完成后再退出"); return; }
        exitPending = true;
        foreach (var editor in editors.Values) editor.SetExitPending(true);
        try
        {
            foreach (var editor in editors.Values.ToArray()) if (!editor.FlushDraft()) throw new InvalidOperationException("有草稿尚未保存成功，请处理编辑窗口中的提示后再退出");
            foreach (DeskWindow window in Windows.OfType<DeskWindow>()) window.Persist(window.IsVisible && window.LayoutKey is "pinned" or "todo" or "memo");
        }
        catch (Exception ex) { foreach (var editor in editors.Values) editor.SetExitPending(false); exitPending = false; Notify(ex.Message); return; }
        foreach (Window window in Windows) window.IsEnabled = false;
        quit.Cancel(); if (syncTask != null) { try { await syncTask; } catch (OperationCanceledException) { } }
        if (maintenanceTask != null) { try { await maintenanceTask; } catch (OperationCanceledException) { } }
        Exiting = true; Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (suppressServiceStartup) { base.OnExit(e); return; }
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged; quit.Cancel();
        tray?.Dispose(); trayIcon?.Dispose(); Clipboard?.Dispose(); quitWait?.Unregister(null); quitSignal?.Dispose(); showWait?.Unregister(null); showSignal?.Dispose(); Store?.Dispose(); installGuard?.Dispose(); instance?.Dispose(); base.OnExit(e);
    }
}
