using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using ClipDesk.App;
using ClipDesk.Core;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Ui = typeof(App).Assembly.GetType("ClipDesk.App.Ui")!;
    private static readonly Dictionary<string, object> results = [];
    private static App app = null!;
    private static IDictionary editors = null!;
    private static Store store = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || Directory.Exists(args[0]) || File.Exists(args[0]))
        {
            Console.Error.WriteLine("请为检查提供尚不存在的隔离数据目录");
            return 2;
        }
        var path = args[0];
        using var data = new Store(path);
        store = data;
        app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // 不启动用户实例、监听、托盘或云同步；检查只使用传入的隔离 Store。
        typeof(App).GetField("suppressServiceStartup", Members)!.SetValue(app, true);
        typeof(App).GetProperty("Store", Members)!.SetValue(app, store);
        editors = (IDictionary)typeof(App).GetField("editors", Members)!.GetValue(app)!;
        Ui.GetMethod("Theme", Members)!.Invoke(null, [app]);
        var clipboardType = typeof(App).Assembly.GetType("ClipDesk.App.ClipboardService")!;
        // Exercise the missing-image failure path without starting a clipboard listener
        // or providing a window handle that could commit a write to the system clipboard.
        var clipboard = RuntimeHelpers.GetUninitializedObject(clipboardType);
        clipboardType.GetField("store", Members)!.SetValue(clipboard, store);
        typeof(App).GetProperty("Clipboard", Members)!.SetValue(app, clipboard);

        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = dispatcher.InvokeAsync(async () =>
        {
            try { await Run(path); }
            catch (Exception ex) { results["error"] = ex.ToString(); }
            finally
            {
                try
                {
                    typeof(App).GetProperty("Exiting", Members)!.SetValue(app, true);
                    foreach (var window in app.Windows.OfType<Window>().ToArray()) window.Close();
                    data.Dispose();
                }
                catch (Exception ex) { results["cleanupError"] = ex.ToString(); }
                var json = Codec.Json(results);
                File.WriteAllText(Path.Combine(path, "editor-check.json"), json);
                Console.WriteLine(json);
                Console.Out.Flush();
                Environment.Exit(results.ContainsKey("error") || results.ContainsKey("cleanupError") ? 1 : 0);
            }
        });
        Dispatcher.Run();
        return results.ContainsKey("error") ? 1 : 0;
    }

    private static async Task Run(string path)
    {
        using var audit = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "clipdesk.sqlite"), Pooling = false }.ToString());
        audit.Open();
        void Sql(string sql)
        {
            using var cmd = audit.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        long Writes(string key)
        {
            using var cmd = audit.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM draft_writes WHERE key=$key";
            cmd.Parameters.AddWithValue("$key", key);
            return (long)cmd.ExecuteScalar()!;
        }
        Sql("""
            CREATE TABLE draft_writes(key TEXT NOT NULL);
            CREATE TRIGGER draft_i AFTER INSERT ON drafts BEGIN INSERT INTO draft_writes VALUES(NEW.key); END;
            CREATE TRIGGER draft_u AFTER UPDATE ON drafts BEGIN INSERT INTO draft_writes VALUES(NEW.key); END;
            """);

        var largeText = string.Concat(Enumerable.Repeat("This is a line of synthetic text for editor checks.\r\n", 2000));
        var sample = new Entry { Kind = "memo", Body = largeText, Protected = true };
        const int changes = 40;
        var editor = Open(sample, "batch");
        var body = Body(editor);
        Need(AutomationProperties.GetLabeledBy(body) is TextBlock, "editor body accessible label");
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < changes; i++) body.AppendText("x");
        watch.Stop();
        var newMs = watch.Elapsed.TotalMilliseconds;
        Need(Writes("batch") == 0, "no per-keystroke durable writes");
        await Until(() => Writes("batch") == 1, "batched draft write");
        Need(store.GetDraft("batch")?.Value.Body == body.Text, "latest input stored");
        var syncInput = new TextBox { Text = largeText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var clock = new Dictionary<string, long>();
        watch.Restart();
        for (var i = 0; i < changes; i++)
        {
            syncInput.AppendText("x");
            store.Draft("per-key-baseline", new Draft(sample with { Body = syncInput.Text }, clock));
        }
        watch.Stop();
        Need(Writes("per-key-baseline") == changes, "synchronous comparison writes");
        results["syntheticInput"] = new { initialChars = largeText.Length, changes, batchedUiMs = newMs, perKeyComparisonMs = watch.Elapsed.TotalMilliseconds, writes = 1, comparisonWrites = changes };

        var gate = typeof(Store).GetField("gate", Members)!.GetValue(store)!;
        using var release = new ManualResetEventSlim();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = Task.Run(() =>
        {
            lock (gate)
            {
                blocked.SetResult();
                release.Wait(TimeSpan.FromSeconds(6));
            }
        });
        await blocked.Task;
        try
        {
            body.AppendText("first");
            await Until(() => editor.GetType().GetField("draftWrite", Members)!.GetValue(editor) is Task { IsCompleted: false }, "draft worker pending");
            body.AppendText("last");
        }
        finally { release.Set(); }
        await block;
        Invoke(editor, "SetExitPending", true);
        Need(!body.IsEnabled, "input frozen before exit");
        Need((bool)Invoke(editor, "FlushDraft")!, "exit flush succeeds");
        Need(store.GetDraft("batch")?.Value.Body == body.Text && body.Text.EndsWith("firstlast", StringComparison.Ordinal), "later revision survives older worker");
        Invoke(editor, "SetExitPending", false);
        body.AppendText("before close");
        editor.Close();
        Need(!editor.IsVisible && store.GetDraft("batch")?.Value.Body == body.Text, "close flushes before hiding");
        await Until(() => !editors.Contains("batch"), "close releases editor");
        editor = Open(sample, "batch");
        Need(Body(editor).Text.EndsWith("before close", StringComparison.Ordinal), "close draft restored");
        await Save(editor, false);
        await Task.Delay(500);
        Need(store.Get(sample.Id)?.Body.EndsWith("before close", StringComparison.Ordinal) == true &&
            store.GetDraft("batch") == null && !editors.Contains("batch"), "saving clears and never recreates draft");
        results["draftRaceAndClose"] = true;

        Sql("CREATE TRIGGER fail_editor_draft BEFORE INSERT ON drafts WHEN NEW.key='failing' BEGIN SELECT RAISE(ABORT,'synthetic draft failure'); END;");
        editor = Open(new Entry { Kind = "memo" }, "failing");
        Body(editor).Text = "retained in editor";
        editor.Close();
        Need(editor.IsVisible && Body(editor).Text == "retained in editor" && store.GetDraft("failing") == null, "write failure keeps window and input");
        Sql("DROP TRIGGER fail_editor_draft;");
        editor.Close();
        Need(!editor.IsVisible && store.GetDraft("failing")?.Value.Body == "retained in editor", "failed close can retry");
        await Until(() => !editors.Contains("failing"), "retried close releases editor");
        results["failedDraftCloseRetry"] = true;

        var prior = store.Save(new Entry { Body = "previous" });
        editor = Open(prior, prior.Id);
        editor.Close();
        await Until(() => !editors.Contains(prior.Id), "untouched close releases editor");
        Need(store.GetDraft(prior.Id) == null, "untouched close creates no draft");
        store.Save(prior with { Body = "updated after closing" });
        editor = Open(prior, prior.Id);
        Need(Body(editor).Text == "updated after closing", "reopen loads the current saved snapshot");
        Invoke(editor, "Cancel");
        results["reopenCurrentSnapshot"] = true;

        var discard = store.Save(new Entry { Body = "saved" });
        editor = Open(discard, discard.Id);
        Body(editor).Text = "local edit";
        await Until(() => store.GetDraft(discard.Id) != null, "local edit becomes draft");
        Body(editor).AppendText(" with a pending change");
        Invoke(editor, "DiscardChanges");
        await Task.Delay(500);
        Need(store.Get(discard.Id)?.Body == "saved" && store.GetDraft(discard.Id) == null && !editors.Contains(discard.Id),
            "confirmed discard clears draft and leaves the saved entry");
        results["discardPendingDraft"] = true;

        var todo = store.Save(new Entry { Kind = "todo", Title = "date entry", Due = "2030-12-23" });
        editor = Open(todo, todo.Id);
        var datePicker = (DatePicker)editor.GetType().GetField("due", Members)!.GetValue(editor)!;
        await Until(() => datePicker.IsLoaded, "date box loaded");
        var dateBox = (DatePickerTextBox)datePicker.Template.FindName("PART_TextBox", datePicker)!;
        dateBox.Text = new DateTime(2030, 12, 24).ToString("d", CultureInfo.CurrentCulture);
        var dateHadFocusCommit = datePicker.SelectedDate == new DateTime(2030, 12, 24);
        editor.Close();
        await Until(() => !editors.Contains(todo.Id), "date editor closes");
        Need(store.GetDraft(todo.Id)?.Value.Due == "2030-12-24" && store.Get(todo.Id)?.Due == "2030-12-23",
            "typed date is flushed without changing the saved record");
        results["dateDraft"] = new { selectedBeforeClose = dateHadFocusCommit };

        var rich = store.Save(new Entry { Title = "original", Body = "first line\nsecond line", Html = "<b>original</b>", Rtf = @"{\rtf1 original}" });
        store.Draft(rich.Id, new Draft(rich with { Body = "changed", Title = "new title" }, store.Clock(rich.Id)));
        editor = Open(rich, rich.Id);
        await Save(editor, false);
        var changed = store.Get(rich.Id)!;
        Need(changed.Body == "changed" && changed.Html == null && changed.Rtf == null && changed.OriginalHtml == rich.Html && changed.OriginalRtf == rich.Rtf, "legacy draft rich format fixed");
        editor = Open(changed, rich.Id);
        Invoke(editor, "RestoreOriginal");
        Need(store.Get(rich.Id)?.Body == "changed", "restore requires explicit save");
        Need((bool)Invoke(editor, "FlushDraft")! && store.GetDraft(rich.Id) is { BodyFormatVerified: true, Value.Html: not null }, "restored rich draft persists");
        Invoke(app, "ReleaseEditor", rich.Id);
        editor = Open(changed, rich.Id);
        await Save(editor, false);
        var restored = store.Get(rich.Id)!;
        Need(restored.Body == rich.Body && restored.Title == rich.Title && restored.Html == rich.Html && restored.Rtf == rich.Rtf, "restored rich formats survive reopening");
        editor = Open(restored, rich.Id);
        var title = (TextBox)editor.GetType().GetField("title", Members)!.GetValue(editor)!;
        title.Text += " edited title";
        await Save(editor, false);
        Need(store.Get(rich.Id) is { } titleEdited && titleEdited.Body == rich.Body && titleEdited.Html == rich.Html, "title-only saves preserve exact multiline body and rich formats");
        results["richDraftAndRestore"] = true;

        var sourceBitmap = BitmapSource.Create(100, 1600, 96, 96, PixelFormats.Bgra32, null, new byte[100 * 1600 * 4], 100 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(sourceBitmap));
        using var bytes = new MemoryStream();
        encoder.Save(bytes);
        var assetId = store.Assets.Put(bytes.ToArray());
        var firstThumbnail = (Grid)Ui.GetMethod("Thumbnail", Members)!.Invoke(null, [store.Assets, assetId, 64, 64d])!;
        var secondThumbnail = (Grid)Ui.GetMethod("Thumbnail", Members)!.Invoke(null, [store.Assets, assetId, 64, 64d])!;
        var group = new StackPanel();
        group.Children.Add(firstThumbnail);
        group.Children.Add(secondThumbnail);
        var preview = new Window { Content = group, Width = 240, Height = 220 };
        preview.Show();
        var imageA = firstThumbnail.Children.OfType<Image>().Single();
        var imageB = secondThumbnail.Children.OfType<Image>().Single();
        await Until(() => imageA.Source != null && imageB.Source != null, "portrait thumbnails load");
        Need(imageA.Source is BitmapSource { IsFrozen: true, PixelHeight: <= 128 } portrait && portrait.PixelWidth <= 8, "portrait pixels bounded");
        Need(ReferenceEquals(imageA.Source, imageB.Source), "decoded thumbnails share cache");
        var thumbnailDpi = VisualTreeHelper.GetDpi(firstThumbnail);
        results["portraitThumbnail"] = new { original = "100×1600", decoded = $"{((BitmapSource)imageA.Source).PixelWidth}×{((BitmapSource)imageA.Source).PixelHeight}", scale = thumbnailDpi.DpiScaleY, shared = true };
        preview.Close();
        await Until(() => imageA.Source == null && imageB.Source == null, "unloaded image sources released");

        var missing = store.Save(new Entry { Body = "missing asset", Format = "image", Assets = [assetId] });
        File.Delete(store.Assets.PathFor(assetId));
        editor = Open(missing, missing.Id);
        Body(editor).Text = "updated while missing";
        var before = store.PendingCount;
        await Save(editor, true);
        var feedback = (TextBlock)editor.GetType().GetField("feedback", Members)!.GetValue(editor)!;
        Need(store.PendingCount == before + 1 && store.Get(missing.Id)?.Body == "updated while missing" && store.GetDraft(missing.Id) == null &&
            feedback.Text.Contains("原图还未下载完成", StringComparison.Ordinal), "copy failure leaves the save committed once");
        await (Task)Invoke(editor, "CopySaved")!;
        Need(store.PendingCount == before + 1 && store.GetDraft(missing.Id) == null, "copy retry only copies");
        editor.Close();
        await Until(() => !editors.Contains(missing.Id), "copy failure window releases on close");
        results["copyFailureNoRepeatCommit"] = true;

        // Recover a local draft that predates a content save on this device.
        store.Assets.Put(bytes.ToArray());
        var draftSource = store.Save(new Entry
        {
            Kind = "todo",
            Format = "image",
            Title = "original title",
            Body = "draft body",
            Html = "<strong>draft body</strong>",
            Rtf = @"{\rtf1 draft body}",
            Due = "2030-12-25",
            Assets = [assetId],
            Pinned = true,
            Done = true
        });
        var draftBaseline = store.Clock(draftSource.Id);
        store.Draft(draftSource.Id, new Draft(draftSource with { Title = "draft title" }, draftBaseline, BodyFormatVerified: true));
        var newer = store.Save(draftSource with { Body = "newer body", Html = null, Rtf = null });
        var newerVersion = store.Versions(draftSource.Id).Single().EventId;
        editor = Open(newer, draftSource.Id);
        var recovery = RecoveryAction(editor);
        Need(recovery.Visibility == Visibility.Collapsed, "save as new appears only after a draft conflict");
        await Save(editor, false);
        Need(recovery.IsVisible && editor.IsVisible && store.GetDraft(draftSource.Id)?.Value.Title == "draft title"
            && store.Get(draftSource.Id)?.Body == newer.Body, "stale draft opens the save-as-new action");
        var queuedBeforeRecovery = store.PendingCount;
        Sql($"CREATE TRIGGER fail_recovery BEFORE DELETE ON drafts WHEN OLD.key='{draftSource.Id}' BEGIN SELECT RAISE(ABORT,'synthetic recovery failure'); END;");
        recovery.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var recoveryId = (string?)editor.GetType().GetField("recoveryId", Members)!.GetValue(editor);
        Need(recoveryId != null && store.Get(recoveryId) == null && store.PendingCount == queuedBeforeRecovery
            && editor.IsVisible && recovery.IsVisible
            && store.GetDraft(draftSource.Id)?.BaseClock.GetValueOrDefault(store.DeviceId) == draftBaseline[store.DeviceId],
            "failed save as new rolls back its entry and outbox and keeps the original draft clock");
        Sql("DROP TRIGGER fail_recovery;");
        recovery.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var recovered = store.Get(recoveryId!)!;
        Need(recovered.Id != draftSource.Id && recovered.SourceId == draftSource.Id && recovered.Kind == "todo"
            && recovered.Title == "draft title" && recovered.Body == draftSource.Body
            && recovered.Html == draftSource.Html && recovered.Rtf == draftSource.Rtf
            && recovered.Assets.SequenceEqual(draftSource.Assets) && recovered.Due == draftSource.Due
            && recovered.Protected && !recovered.Pinned && !recovered.Done && !recovered.Deleted,
            "retry creates one independent protected record with the draft's fields");
        Need(store.GetDraft(draftSource.Id) == null && !editors.Contains(draftSource.Id)
            && store.Versions(draftSource.Id).Single().EventId == newerVersion,
            "successful save as new clears the draft and preserves the source version");
        Invoke(editor, "SaveAsNew");
        await Task.Delay(500);
        Need(store.PendingCount == queuedBeforeRecovery + 1 && store.GetDraft(draftSource.Id) == null,
            "a second action on a closed recovery editor does not save or recreate its draft");
        results["saveStaleDraftAsNew"] = true;

        // Opening after an existing fork has joined its clocks must still keep both versions.
        var conflict = store.Save(new Entry { Body = "local version" });
        var remoteDevice = Codec.Id();
        Need(store.Receive(new Change
        {
            DeviceId = remoteDevice,
            Sequence = 1,
            Clock = new Dictionary<string, long> { [remoteDevice] = 1 },
            Value = conflict with { Body = "remote version" }
        }), "concurrent version created");
        var conflictVersions = store.Versions(conflict.Id).Select(change => change.EventId).Order().ToArray();
        Need(conflictVersions.Length == 2, "two source versions exist");
        store.Draft(conflict.Id, new Draft(conflict with { Body = "separate conflicted draft" },
            store.Clock(conflict.Id), BodyFormatVerified: true));
        editor = Open(conflict, conflict.Id);
        Need(Body(editor).Text == "separate conflicted draft", "existing draft still opens when source has two versions");
        await Save(editor, false);
        recovery = RecoveryAction(editor);
        Need(recovery.IsVisible && store.GetDraft(conflict.Id)?.Value.Body == "separate conflicted draft",
            "editing a known fork offers recovery and keeps the draft");
        recovery.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Need(store.Versions(conflict.Id).Select(change => change.EventId).Order().SequenceEqual(conflictVersions)
            && store.List("clip").Any(item => item.SourceId == conflict.Id && item.Body == "separate conflicted draft")
            && store.GetDraft(conflict.Id) == null && !editors.Contains(conflict.Id),
            "save as new preserves both source versions and keeps the draft independently");
        results["saveConflictedDraftAsNew"] = true;

        var layout = store.Settings;
        layout.Windows["editor-memo"] = new WindowLayout(120, 120, 660, 720, false, false, 99_999, -99_999);
        store.SaveSettings(layout);
        editor = Open(new Entry { Kind = "memo" }, "layout");
        var hwnd = new WindowInteropHelper(editor).Handle;
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        var placed = store.Settings.Windows["editor-memo"];
        Need(placed.ScreenX is { } x && placed.ScreenY is { } y && x >= screen.Left && x < screen.Right && y >= screen.Top && y < screen.Bottom,
            "saved unavailable monitor coordinates clamped");
        Invoke(editor, "Cancel");
        Need(!editors.Contains("layout") && store.GetDraft("layout") == null, "untouched cancel closes without draft");
        results["offscreenPlacement"] = new { placed.ScreenX, placed.ScreenY, workArea = screen.ToString() };
        await CheckPanelWindows(Sql);
    }

    private static async Task CheckPanelWindows(Action<string> sql)
    {
        var settings = store.Settings;
        settings.Windows["clipboard"] = new WindowLayout(120, 120, 520, 500);
        store.SaveSettings(settings);
        var clipboard = NewDeskWindow("clipboard", 520);
        var todo = NewDeskWindow("todo", 350);
        var memo = NewDeskWindow("memo", 350);
        var pinned = NewDeskWindow("pinned", 420);
        Need(!KeepsVisible(clipboard) && KeepsVisible(todo) && KeepsVisible(memo) && KeepsVisible(pinned),
            "legacy clipboard and new panels have distinct keep-visible defaults");
        Need(todo.MinWidth == 300 && todo.MinHeight == 250 && memo.MinWidth == 300 && memo.MinHeight == 250,
            "todo and memo can shrink to compact panels");

        var activations = 0;
        todo.Activated += (_, _) => activations++;
        Invoke(todo, "Open", false);
        Invoke(memo, "Open", false);
        Invoke(pinned, "Open", false);
        Need(todo.IsVisible && memo.IsVisible && pinned.IsVisible && !todo.ShowActivated && !todo.IsActive && activations == 0,
            "background restore shows panels without activation");
        var placed = store.Settings.Windows;
        Need(placed["memo"].ScreenY <= placed["todo"].ScreenY && placed["pinned"].ScreenX <= placed["todo"].ScreenX,
            "new panels start at distinct desktop edges");

        var button = (Button)Invoke(todo, "WindowOptionsButton")!;
        var menu = button.ContextMenu!;
        var keep = (MenuItem)menu.Items[0];
        var top = (MenuItem)menu.Items[1];
        var opacity = (MenuItem)menu.Items[2];
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Need(keep.IsChecked && !top.IsChecked, "menu reflects independent window settings");
        keep.IsChecked = false;
        keep.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        top.IsChecked = true;
        top.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var lowOpacity = opacity.Items.OfType<MenuItem>().Single(item => item.Header as string == "45%");
        lowOpacity.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        var handle = new WindowInteropHelper(todo).Handle;
        var activeAlpha = RenderedAlpha(todo);
        Need(activeAlpha == 255, "window settings interaction renders fully opaque");
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        var idleAlpha = RenderedAlpha(todo);
        Need(idleAlpha == ExpectedAlpha(todo), "inactive panel actually renders at the configured opacity");
        Need((GetWindowLongPtr(handle, -20) & 0x00080000) != 0 && todo.AllowsTransparency
            && todo.ResizeMode == ResizeMode.CanResize && WindowChrome.GetWindowChrome(todo) is { CaptionHeight: > 0 },
            "WPF composites a native layered window with window chrome");
        var captionHit = NonClientHit(todo, 75, 20);
        var resizeHit = NonClientHit(todo, todo.ActualWidth - 2, todo.ActualHeight - 2);
        Need(captionHit == 2 && resizeHit == 17, "native hit testing preserves caption dragging and corner resizing");
        var minimize = (Button)todo.Template.FindName("MinimizePanel", todo)!;
        var close = (Button)todo.Template.FindName("ClosePanel", todo)!;
        var maximize = (Button)todo.Template.FindName("MaximizePanel", todo)!;
        Need(AutomationProperties.GetName(minimize) == "最小化窗口" && AutomationProperties.GetName(close) == "关闭窗口"
            && AutomationProperties.GetName(maximize) == "最大化窗口", "custom caption controls are named for assistive technology");

        todo.Width = 310;
        todo.Height = 255;
        await Until(() => store.Settings.Windows["todo"] is { Width: 310, Height: 255 }, "compact size saved");
        Need(RenderedAlpha(todo) == ExpectedAlpha(todo), "resize preserves the rendered opacity");
        var layouts = store.Settings.Windows;
        Need(layouts["todo"] is { KeepVisible: false, Topmost: true, Opacity: .45, Open: true }
            && layouts["memo"] is { KeepVisible: true, Topmost: false, Opacity: 1, Open: true }
            && layouts["pinned"] is { KeepVisible: true, Topmost: false, Opacity: 1, Open: true },
            "panel preferences and visibility persist independently");

        minimize.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Until(() => todo.WindowState == WindowState.Minimized, "caption minimize command works");
        Invoke(todo, "Open", false);
        Need(todo.WindowState == WindowState.Minimized, "background restore does not restore a minimized panel");
        Invoke(todo, "Dismiss");
        todo.WindowState = WindowState.Normal;
        Invoke(todo, "Open", false);

        var childInteraction = true;
        Invoke(todo, "EnableAutoHide", (Func<bool>)(() => childInteraction));
        Invoke(todo, "RequestAutoHide");
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Need(todo.IsVisible, "a scoped child interaction suppresses inactive hide");
        childInteraction = false;
        Invoke(todo, "RequestAutoHide");
        await Until(() => !todo.IsVisible, "inactive panel can hide after the interaction");
        Invoke(todo, "Open", false);
        sql("CREATE TRIGGER fail_panel_layout BEFORE UPDATE ON meta WHEN NEW.key='settings' BEGIN SELECT RAISE(ABORT,'synthetic layout failure'); END;");
        try
        {
            Invoke(todo, "Dismiss");
            Need(!todo.IsVisible && store.Settings.Windows["todo"].Open, "layout failure does not prevent explicit dismissal");
        }
        finally { sql("DROP TRIGGER fail_panel_layout;"); }
        Invoke(todo, "Open", false);
        Need(RenderedAlpha(todo) == ExpectedAlpha(todo), "hide and show preserve the rendered opacity");
        close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Until(() => !todo.IsVisible, "caption close command hides the panel");
        Need(store.Settings.Windows["todo"].Open == false && memo.IsVisible && pinned.IsVisible,
            "dismissing one window persists only its closed state");
        Invoke(todo, "ClosePermanently");
        todo = NewDeskWindow("todo", 350);
        Invoke(todo, "Open", false);
        Need(todo.IsVisible && !todo.IsActive && !KeepsVisible(todo) && todo.Topmost && todo.Width == 310 && todo.Height == 255,
            "reopened panel restores size and its preferences without activation");
        foreach (var window in new[] { clipboard, todo, memo, pinned }) Invoke(window, "ClosePermanently");

        results["panelWindowPreferences"] = new { renderedIdleAlpha = idleAlpha, renderedInteractionAlpha = activeAlpha, captionHit, resizeHit, inactiveRestore = true };
    }

    private static Window NewDeskWindow(string key, double width)
    {
        var type = typeof(App).Assembly.GetType("ClipDesk.App.DeskWindow")!;
        return (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [app, key, "窗口检查", width], null)!;
    }

    private static bool KeepsVisible(Window window) => (bool)window.GetType().GetProperty("KeepVisible", Members)!.GetValue(window)!;

    private static int ExpectedAlpha(Window window) => window.IsActive || window.IsMouseOver ? 255 : 115;

    private static byte RenderedAlpha(Window window)
    {
        window.UpdateLayout();
        var pixels = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        pixels.Render(window);
        var pixel = new byte[4];
        pixels.CopyPixels(new Int32Rect(20, 65, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static int NonClientHit(Window window, double x, double y)
    {
        var screen = window.PointToScreen(new Point(x, y));
        var point = ((int)screen.X & 0xffff) | (((int)screen.Y & 0xffff) << 16);
        return (int)SendMessage(new WindowInteropHelper(window).Handle, 0x0084, 0, point);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    private static Window Open(Entry entry, string key)
    {
        Invoke(app, "Edit", entry, key);
        return (Window)editors[key]!;
    }

    private static TextBox Body(Window editor) => (TextBox)editor.GetType().GetProperty("BodyInput", Members)!.GetValue(editor)!;

    private static Button RecoveryAction(Window editor) => (Button)editor.GetType().GetField("saveAsNew", Members)!.GetValue(editor)!;

    private static Task Save(Window editor, bool copy) => (Task)Invoke(editor, "Save", copy)!;

    private static object? Invoke(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, Members)!.Invoke(target, args);

    private static async Task Until(Func<bool> condition, string check)
    {
        for (var i = 0; i < 120 && !condition(); i++) await Task.Delay(25);
        Need(condition(), check);
    }

    private static void Need(bool value, string check)
    {
        if (!value) throw new InvalidOperationException(check);
    }
}
