using Microsoft.Win32;

namespace ClipDesk.App;

internal sealed class SettingsWindow : DeskWindow
{
    private readonly TextBox hotkey, pinnedHotkey, todoHotkey, memoHotkey, days, maxCount, excluded, endpoint, region, bucket, prefix;
    private readonly PasswordBox ak = new(), sk = new();
    private readonly CheckBox enabled = new() { Content = "开启七牛同步", Margin = new Thickness(0, 8, 0, 8) };
    private readonly CheckBox receive = new() { Content = "自动接收其他设备最新复制内容（不自动粘贴）", Margin = new Thickness(0, 8, 0, 8) };
    private readonly CheckBox startup = new() { Content = "登录 Windows 时启动 ClipDesk", Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock status = Ui.Text("", 13, Ui.Accent), syncStatus = Ui.Text("", 12, Ui.Muted), fingerprint = Ui.Text("", 12, Ui.Muted);
    private readonly TabControl tabs = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly Button save;
    internal bool HasPendingWork { get; private set; }
    private bool dirty;
    internal SettingsWindow(App host) : base(host, "settings", "ClipDesk · 设置", 740)
    {
        var settings = host.Store.Settings; var sync = settings.Sync;
        hotkey = Ui.Input(settings.Hotkey); pinnedHotkey = Ui.Input(settings.PinnedHotkey); todoHotkey = Ui.Input(settings.TodoHotkey); memoHotkey = Ui.Input(settings.MemoHotkey); days = Ui.Input(settings.RetentionDays.ToString()); maxCount = Ui.Input(settings.RetentionCount.ToString()); excluded = Ui.Input(settings.ExcludedApps, true); excluded.MinHeight = 76; excluded.Padding = new Thickness(9, 7, 9, 7);
        endpoint = Ui.Input(sync.Endpoint); endpoint.ToolTip = "例如 https://s3.cn-east-1.qiniucs.com"; region = Ui.Input(sync.Region); bucket = Ui.Input(sync.Bucket); prefix = Ui.Input(sync.Prefix); enabled.IsChecked = sync.Enabled; receive.IsChecked = sync.ReceiveClipboard;
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) startup.IsChecked = run?.GetValue("ClipDesk") != null;
        var root = new DockPanel { Margin = new Thickness(22) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(Ui.Text("设置", 27)); heading.Children.Add(Ui.Text("先在本机使用，需要多电脑接续时再配置同步。", 13, Ui.Muted));
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        save = Ui.Button("保存设置", Save, true); footer.Children.Add(status); footer.Children.Add(Ui.Row(save, Ui.Button("关闭设置", Hide)));
        root.Children.Add(tabs);
        var panel = AddTab("本机使用");
        panel.Children.Add(Ui.Text("各窗口快捷键", 18));
        Ui.Field(panel, "剪贴记录", hotkey); Ui.Field(panel, "置顶剪贴", pinnedHotkey);
        Ui.Field(panel, "待办窗口", todoHotkey); Ui.Field(panel, "备忘窗口", memoHotkey);
        hotkey.ToolTip = "例如 Ctrl+Shift+V"; pinnedHotkey.ToolTip = "例如 Ctrl+Alt+P";
        todoHotkey.ToolTip = "例如 Ctrl+Alt+T"; memoHotkey.ToolTip = "例如 Ctrl+Alt+M";
        panel.Children.Add(Ui.Text("填写组合键，例如 Ctrl+Shift+V、Ctrl+Alt+T。留空表示不启用该快捷键；仍可从首页和托盘打开。若组合重复或被占用，原来的快捷键保持有效。", 12, Ui.Muted));
        panel.Children.Add(startup);
        Ui.Field(panel, "排除应用（进程名，逗号分隔，例如 KeePass,1Password）", excluded);
        panel.Children.Add(Ui.Text("带有敏感标记的剪贴内容也会跳过。", 12, Ui.Muted));
        panel.Children.Add(Ui.Card(Ui.Text("单击复制，列表保持打开。双击或在列表中按 Enter，尝试粘贴回原输入框；Esc 收起剪贴。置顶、待办、备忘均可独立打开。在各窗口的“窗口选项”菜单分别设置保持显示、始终最前和透明度。")));
        panel = AddTab("数据与恢复"); Ui.Field(panel, "普通自动历史保留天数（0 为不限）", days); Ui.Field(panel, "普通自动历史保留条数（0 为不限）", maxCount);
        panel.Children.Add(Ui.Text("置顶、手动新建、主动编辑过的剪贴，以及待办、备忘不参与普通清理。删除内容进入回收站，附件继续保留以便恢复。", 12, Ui.Muted));
        panel.Children.Add(Ui.Row(Ui.AsyncButton("清空普通历史", () => Work(ClearHistory)), Ui.Button("回收站", host.ShowTrash)));
        panel.Children.Add(Ui.Text("当前版本不会永久释放回收站和图片占用。", 12, Ui.Muted));
        panel.Children.Add(Ui.Row(Ui.AsyncButton("导出完整备份", () => Work(Export)), Ui.AsyncButton("恢复备份", () => Work(Import))));
        panel.Children.Add(Ui.Text("备份含正文、原图、来源关系、状态与本机草稿，不含凭据或同步密钥。备份 ZIP 未加密，请保管在安全位置。", 12, Ui.Muted));
        var viewDraftsBtn = Ui.Button("查看本机草稿", host.ShowDrafts);
        viewDraftsBtn.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(viewDraftsBtn);
        panel = AddTab("多电脑同步"); panel.Children.Add(syncStatus);
        var disableSyncBtn = Ui.Button("关闭云同步", DisableSync);
        disableSyncBtn.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(disableSyncBtn);
        panel.Children.Add(Ui.Text("1  填写七牛私有空间", 18)); panel.Children.Add(Ui.Text("从七牛控制台查找 S3 配置。本应用不会修改空间权限。", 12, Ui.Muted));
        Ui.Field(panel, "Endpoint（HTTPS）", endpoint); Ui.Field(panel, "Region", region); Ui.Field(panel, "S3 空间名", bucket); Ui.Field(panel, "独立对象前缀", prefix);
        ak.Height = sk.Height = 36; ak.Padding = sk.Padding = new Thickness(8);
        Ui.Field(panel, "Access Key（留空保留本机已有值）", ak); Ui.Field(panel, "Secret Key（留空保留本机已有值）", sk);
        panel.Children.Add(Ui.Text("凭据只进入 Windows 凭据管理器。建议使用仅限该空间/前缀的读写凭据，不需要其他空间权限。", 12, Ui.Muted));
        panel.Children.Add(Ui.Text("2  准备同步加密密钥", 18)); panel.Children.Add(fingerprint);
        panel.Children.Add(Ui.Row(Ui.Button("第一台电脑：生成密钥", GenerateKey), Ui.Button("其他电脑：导入密钥", ImportKey), Ui.Button("导出恢复密钥", ExportKey)));
        panel.Children.Add(Ui.Text("所有电脑使用同一份恢复密钥。云端正文与图片采用 AES-256-GCM 客户端加密；密钥与七牛凭据分开保存。丢失全部密钥将无法解密云端内容。", 12, Ui.Muted));
        panel.Children.Add(Ui.Text("3  测试连接并开启同步", 18));
        panel.Children.Add(Ui.Row(Ui.AsyncButton("测试七牛连接", () => Work(Test)), Ui.Button("立即同步已保存的配置", () => { if (!Host.Store.Settings.Sync.Enabled) { status.Text = "请先开启同步并保存设置"; return; } host.RequestSync(); status.Text = "已请求同步，请查看上方同步状态"; })));
        panel.Children.Add(enabled); panel.Children.Add(receive);
        panel.Children.Add(Ui.Text("首次初始化、离线积压和备份恢复不会接管当前剪贴板；收到其他设备的新复制也只更新剪贴板。", 12, Ui.Muted));
        panel.Children.Add(Ui.Text("连接测试只在此独立前缀的 _connection-test/ 中创建、读取、列举并删除一个小型测试对象。", 12, Ui.Muted));
        panel.Children.Add(Ui.Text("版本 " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "预览版") + " · Windows 桌面预览版", 12, Ui.Muted));
        host.SyncChanged += message => syncStatus.Text = message; syncStatus.Text = host.SyncStatus; UpdateFingerprint();
        foreach (var field in new[] { hotkey, pinnedHotkey, todoHotkey, memoHotkey, days, maxCount, excluded, endpoint, region, bucket, prefix }) field.TextChanged += (_, _) => MarkDirty();
        ak.PasswordChanged += (_, _) => MarkDirty(); sk.PasswordChanged += (_, _) => MarkDirty();
        foreach (var toggle in new[] { enabled, receive, startup }) { toggle.Checked += (_, _) => MarkDirty(); toggle.Unchecked += (_, _) => MarkDirty(); }
        receive.IsEnabled = enabled.IsChecked == true;
        enabled.Checked += (_, _) => receive.IsEnabled = true; enabled.Unchecked += (_, _) => receive.IsEnabled = false;
        IsVisibleChanged += (_, _) => { if (IsVisible && dirty) status.Text = "设置修改尚未保存；关闭窗口后本次会话仍保留"; };
    }
    private StackPanel AddTab(string title)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 14, 12, 4) };
        tabs.Items.Add(new TabItem { Header = title, Padding = new Thickness(16, 9, 16, 9), Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }); return panel;
    }
    private void MarkDirty() { dirty = true; status.Text = "设置修改尚未保存"; }
    private void DisableSync()
    {
        var settings = Host.Store.Settings;
        Host.Store.SaveSettings(settings with { Sync = settings.Sync with { Enabled = false } });
        enabled.IsChecked = false;
        Host.ConnectionChanged();
        status.Text = "已关闭云同步，本机功能继续可用。其他表单修改尚未保存。";
    }
    internal void ShowSection(int index) => tabs.SelectedIndex = index;
    private async Task Work(Func<Task> action)
    {
        if (HasPendingWork) return;
        HasPendingWork = true; tabs.IsEnabled = false; save.IsEnabled = false;
        try { await action(); }
        catch { status.Text = "操作未完成；请查看错误提示后重试"; throw; }
        finally { HasPendingWork = false; tabs.IsEnabled = true; save.IsEnabled = true; }
    }
    private async Task ClearHistory()
    {
        if (!Ui.Confirm("仅将未置顶、未经手动编辑的自动剪贴移入回收站。待办、备忘和长期内容保留。继续？")) return;
        var n = await Task.Run(() => Host.Store.Cleanup(true)); status.Text = $"已将 {n} 条普通历史移入回收站";
    }
    private SyncConfig Config() => new(endpoint.Text.Trim().TrimEnd('/'), region.Text.Trim(), bucket.Text.Trim(), prefix.Text.Trim(), enabled.IsChecked == true, receive.IsChecked == true);
    private void Save()
    {
        if (HasPendingWork) throw new InvalidOperationException("当前操作完成后再保存设置");
        if (!int.TryParse(days.Text, out var retentionDays) || retentionDays < 0 || retentionDays > 36500 || !int.TryParse(maxCount.Text, out var retentionCount) || retentionCount < 0) throw new InvalidOperationException("保留天数和条数需要填写非负整数");
        var config = Config(); var old = Host.Store.Settings;
        if (config.Enabled || config.Endpoint.Length > 0) KodoStore.ValidateConfig(config);
        var binding = Host.Store.GetMeta("cloud-binding"); var next = Codec.Json(new { config.Endpoint, config.Region, config.Bucket, config.Prefix });
        if (Host.SyncInProgress && (old.Sync.Endpoint != config.Endpoint || old.Sync.Region != config.Region || old.Sync.Bucket != config.Bucket || old.Sync.Prefix != config.Prefix)) throw new InvalidOperationException("正在同步，请等当前请求结束后再修改连接位置");
        if (binding != null && binding != next) throw new InvalidOperationException("本机已绑定同步位置。为避免混入其他数据，本版暂不支持直接迁移空间或前缀；请先导出备份并使用独立数据目录。可随时关闭同步。");
        var changeAk = ak.Password.Length > 0; var changeSk = sk.Password.Length > 0;
        var oldAk = changeAk || config.Enabled ? Host.Vault.Get("kodo-ak") : null; var oldSk = changeSk || config.Enabled ? Host.Vault.Get("kodo-sk") : null;
        if (config.Enabled && (string.IsNullOrWhiteSpace(changeAk ? ak.Password : oldAk) || string.IsNullOrWhiteSpace(changeSk ? sk.Password : oldSk) || Host.Vault.Get("sync-key") == null)) throw new InvalidOperationException("开启同步前，请填写七牛凭据，并生成或导入同步密钥");
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var previousRun = Registry.CurrentUser.OpenSubKey(runPath);
        var oldStartup = previousRun?.GetValue("ClipDesk"); var oldStartupKind = oldStartup == null ? RegistryValueKind.String : previousRun!.GetValueKind("ClipDesk");
        var changedStartup = (oldStartup != null) != (startup.IsChecked == true);
        var requestedSettings = old with { Hotkey = hotkey.Text.Trim(), PinnedHotkey = pinnedHotkey.Text.Trim(), TodoHotkey = todoHotkey.Text.Trim(), MemoHotkey = memoHotkey.Text.Trim(), RetentionDays = retentionDays, RetentionCount = retentionCount, ExcludedApps = excluded.Text, Sync = config };
        var hotkeyChanged = false; var wroteAk = false; var wroteSk = false; var wroteStartup = false;
        try
        {
            Host.Clipboard.Register(requestedSettings); hotkeyChanged = true;
            if (changeAk) { Host.Vault.Put("kodo-ak", ak.Password); wroteAk = true; }
            if (changeSk) { Host.Vault.Put("kodo-sk", sk.Password); wroteSk = true; }
            if (changedStartup)
            {
                using var run = Registry.CurrentUser.CreateSubKey(runPath);
                if (startup.IsChecked == true) run.SetValue("ClipDesk", "\"" + Environment.ProcessPath + "\" --background"); else run.DeleteValue("ClipDesk", false);
                wroteStartup = true;
            }
            Host.Store.SaveSettings(requestedSettings);
        }
        catch (Exception original)
        {
            var rollbackFailed = false;
            void Rollback(Action action) { try { action(); } catch { rollbackFailed = true; } }
            if (wroteAk) Rollback(() => { if (oldAk == null) Host.Vault.Delete("kodo-ak"); else Host.Vault.Put("kodo-ak", oldAk); });
            if (wroteSk) Rollback(() => { if (oldSk == null) Host.Vault.Delete("kodo-sk"); else Host.Vault.Put("kodo-sk", oldSk); });
            if (wroteStartup) Rollback(() => { using var run = Registry.CurrentUser.CreateSubKey(runPath); if (oldStartup == null) run.DeleteValue("ClipDesk", false); else run.SetValue("ClipDesk", oldStartup, oldStartupKind); });
            if (hotkeyChanged) Rollback(() => Host.Clipboard.Register(old));
            if (rollbackFailed) throw new InvalidOperationException("设置未保存，部分本机设置无法还原。请重新核对快捷键、开机启动和凭据后保存。", original);
            throw;
        }
        ak.Clear(); sk.Clear(); dirty = false; status.Text = "设置已保存";
        if (changeAk || changeSk || old.Sync != config) Host.ConnectionChanged(); else Host.RequestSync();
    }
    private void UpdateFingerprint()
    { try { var key = Host.Vault.Get("sync-key"); fingerprint.Text = key == null ? "尚未生成或导入密钥" : "密钥指纹：" + new SyncCrypto(Convert.FromBase64String(key)).Fingerprint; } catch (Exception ex) { fingerprint.Text = ex.Message; } }
    private void GenerateKey()
    {
        if (Host.Vault.Get("sync-key") != null) { status.Text = "已有密钥，请导出给其他电脑，不重复生成"; return; }
        Host.Vault.Put("sync-key", Convert.ToBase64String(SyncCrypto.GenerateKey())); UpdateFingerprint(); Host.ConnectionChanged(); status.Text = "已生成同步密钥，请导出恢复文件妥善保管";
    }
    private void ExportKey()
    {
        var key = Host.Vault.Get("sync-key") ?? throw new InvalidOperationException("请先生成或导入密钥");
        var dialog = new SaveFileDialog { Title = "保存同步恢复密钥（请勿分享或上传）", FileName = "ClipDesk-recovery.clipdesk-key", Filter = "ClipDesk 恢复密钥|*.clipdesk-key" };
        if (dialog.ShowDialog(this) == true) { AtomicFile.Write(dialog.FileName, System.Text.Encoding.UTF8.GetBytes(key)); status.Text = "恢复密钥已导出，请保管在安全位置"; }
    }
    private void ImportKey()
    {
        var dialog = new OpenFileDialog { Title = "导入另一台电脑导出的恢复密钥", Filter = "ClipDesk 恢复密钥|*.clipdesk-key" }; if (dialog.ShowDialog(this) != true) return;
        if (new FileInfo(dialog.FileName).Length > 256) throw new InvalidDataException("恢复密钥文件不正确");
        var key = File.ReadAllText(dialog.FileName).Trim(); _ = new SyncCrypto(Convert.FromBase64String(key));
        var existing = Host.Vault.Get("sync-key"); if (existing != null && existing != key) throw new InvalidOperationException("本机已有另一份密钥；本版禁止直接替换，以免已有数据无法解密");
        Host.Vault.Put("sync-key", key); UpdateFingerprint(); Host.ConnectionChanged(); status.Text = "恢复密钥已导入";
    }
    private async Task Test()
    {
        var config = Config(); KodoStore.ValidateConfig(config);
        var access = ak.Password.Length > 0 ? ak.Password : Host.Vault.Get("kodo-ak"); var secret = sk.Password.Length > 0 ? sk.Password : Host.Vault.Get("kodo-sk"); var key = Host.Vault.Get("sync-key");
        if (access == null || secret == null || key == null) throw new InvalidOperationException("请填写 AK/SK，并生成或导入同步密钥");
        using var remote = new KodoStore(config, access, secret); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        status.Text = "正在专用前缀中测试七牛真实接口…";
        try { await SyncEngine.TestConnection(remote, new SyncCrypto(Convert.FromBase64String(key)), config.Prefix, cancellation.Token); status.Text = "七牛接口测试通过。表单修改仍需保存；两台电脑同步仍需验证。"; }
        catch (Exception ex) { status.Text = "真实接口测试未通过：" + KodoStore.DescribeError(ex); throw new InvalidOperationException(status.Text, ex); }
    }
    private async Task Export()
    { var dialog = new SaveFileDialog { Title = "导出完整备份", Filter = "ClipDesk 备份|*.clipdesk.zip", FileName = "ClipDesk-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".clipdesk.zip" }; if (dialog.ShowDialog(this) == true) { await Task.Run(() => Backup.Export(Host.Store, dialog.FileName)); status.Text = "完整备份已导出（不含凭据与同步密钥）"; } }
    private async Task Import()
    { var dialog = new OpenFileDialog { Title = "恢复备份", Filter = "ClipDesk 备份|*.clipdesk.zip" }; if (dialog.ShowDialog(this) == true && Ui.Confirm("合并恢复备份？重复版本会跳过，现有删除状态和冲突会保留，不替换本机配置。")) { var n = await Task.Run(() => Backup.Import(Host.Store, dialog.FileName)); status.Text = $"恢复完成，新增 {n} 条变更；当前剪贴板未改变"; Host.RequestSync(); } }
}
