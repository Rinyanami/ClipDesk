using System.ComponentModel;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ClipDesk.App;

internal sealed class EditorWindow : DeskWindow
{
    private static readonly TimeSpan DraftDelay = TimeSpan.FromMilliseconds(450);
    private const int MaxDraftDelayMs = 1800;
    private readonly record struct OriginalText(string Title, string Body, string? Html, string? Rtf);

    private Entry entry;
    private readonly Entry? savedAtOpen;
    private readonly OriginalText? original;
    private readonly Dictionary<string, long> baseClock;
    private readonly string draftKey, initialBodyText;
    private readonly TextBox title, body;
    private readonly DatePicker due;
    private readonly Button saveAsNew;
    private readonly StackPanel fields = new(), attachments = new();
    private readonly WrapPanel editCommands = new(), copyCommands = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock feedback = Ui.Text("修改会自动保留为本机草稿", 12, Ui.Muted);
    private readonly DispatcherTimer draftTimer = new(DispatcherPriority.Background);
    private Task? draftWrite;
    private long revision, savedRevision, writingRevision, firstPendingChange = -1;
    private string? restoredBodyText, recoveryId;
    private Entry? savedForCopy;
    private bool loadingFields, hasDraft, finished, copying, exitPending, observingDate;

    internal string Kind => entry.Kind;
    internal TextBox BodyInput => body;
    internal bool HasPendingWork => copying;

    internal EditorWindow(App host, Entry value, string key) : base(host, "editor-" + value.Kind,
        value.Kind == "clip" ? "编辑剪贴 · ClipDesk" : value.Kind == "todo" ? "编辑待办 · ClipDesk" : "编辑备忘 · ClipDesk", 680)
    {
        draftKey = key;
        var draft = host.Store.GetDraft(key);
        var snapshot = host.Store.EditSnapshot(draft?.Value.Id ?? value.Id);
        savedAtOpen = snapshot.Value;
        entry = draft?.Value ?? savedAtOpen ?? value;
        baseClock = draft?.BaseClock ?? snapshot.Clock;
        hasDraft = draft != null;

        var source = entry.OriginalBody != null || entry.OriginalTitle != null ? entry : savedAtOpen;
        if (source != null)
        {
            var hasOriginal = source.OriginalBody != null || source.OriginalTitle != null;
            original = new OriginalText(source.OriginalTitle ?? source.Title, source.OriginalBody ?? source.Body,
                hasOriginal ? source.OriginalHtml : source.Html, hasOriginal ? source.OriginalRtf : source.Rtf);
        }
        if (draft is { BodyFormatVerified: false })
        {
            // Older drafts may contain rich formats from a different body.
            var matching = savedAtOpen?.Body == entry.Body ? savedAtOpen : null;
            entry = entry with { Html = matching?.Html, Rtf = matching?.Rtf };
        }

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var head = new DockPanel();
        root.Children.Add(head);
        var top = TopSwitch();
        DockPanel.SetDock(top, Dock.Right);
        head.Children.Add(top);
        head.Children.Add(Ui.Text(entry.Kind == "todo" ? "编辑待办" : entry.Kind == "memo" ? "编辑备忘" : "编辑剪贴", 23));

        var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        title = Ui.Input(entry.Title);
        body = Ui.Input(entry.Body, true);
        body.MinHeight = 240;
        body.MaxHeight = 420;
        initialBodyText = body.Text;
        due = new DatePicker
        {
            SelectedDate = DateTime.TryParseExact(entry.Due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
            Margin = new Thickness(0, 5, 0, 10)
        };
        Ui.Field(fields, entry.Kind == "todo" ? "标题（必填）" : "标题（可选）", title);
        Ui.Field(fields, entry.Kind == "todo" ? "说明（可选，多行）" : "内容（支持多行）", body);
        if (entry.Kind == "todo") Ui.Field(fields, "到期日期（仅作记录，无提醒）", due);
        fields.Children.Add(attachments);
        var addImageButton = Ui.Button("添加图片", AddImage);
        addImageButton.HorizontalAlignment = HorizontalAlignment.Left;
        fields.Children.Add(addImageButton);
        if (entry.SourceId != null) fields.Children.Add(Ui.Text("来源内容已作为独立副本保存。", 12, Ui.Muted));
        RenderAttachments();
        CommandManager.AddPreviewCanExecuteHandler(body, OnPasteCanExecute);
        CommandManager.AddPreviewExecutedHandler(body, OnPasteExecuted);
        DataObject.AddPastingHandler(body, OnBodyPaste);

        var footer = new StackPanel();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        footer.Children.Add(feedback);
        editCommands.Children.Add(Ui.AsyncButton("保存", () => Save(false), true));
        editCommands.Children.Add(Ui.AsyncButton("保存并复制", () => Save(true)));
        editCommands.Children.Add(Ui.Button("取消修改", Cancel));
        if (original != null) editCommands.Children.Add(Ui.Button("恢复原文", RestoreOriginal));
        saveAsNew = Ui.Button("另存为新内容", SaveAsNew);
        saveAsNew.Visibility = Visibility.Collapsed;
        saveAsNew.ToolTip = "将当前内容长期保留为独立记录，原记录的版本可在详情中核对";
        editCommands.Children.Add(saveAsNew);
        footer.Children.Add(editCommands);
        copyCommands.Children.Add(Ui.AsyncButton("重试复制", CopySaved, true));
        copyCommands.Children.Add(Ui.Button("关闭", Finish));
        footer.Children.Add(copyCommands);
        if (hasDraft) SetFeedback("已恢复本机草稿 · 尚未更新记录");

        draftTimer.Tick += WriteDraftOnTimer;
        title.TextChanged += (_, _) => DraftChanged();
        body.TextChanged += (_, _) => DraftChanged();
        due.SelectedDateChanged += (_, _) => DraftChanged();
        due.DateValidationError += (_, _) => SetFeedback("请输入有效的到期日期", true);
        Deactivated += (_, _) => FlushDraft();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && !FlushDraft()) Host.Notify("本机草稿保存失败，请重新打开编辑器重试");
        };
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (!exitPending && savedForCopy == null && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                AttachFiles(paths);
            e.Handled = true;
        };
        Loaded += (_, _) =>
        {
            if (entry.Kind == "todo" && !observingDate)
            {
                observingDate = true;
                due.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) =>
                {
                    if (e.OriginalSource is DatePickerTextBox) DraftChanged();
                }), true);
            }
            if (entry.Kind == "todo" && string.IsNullOrWhiteSpace(title.Text)) title.Focus();
            else body.Focus();
        };
    }

    private Entry Current()
    {
        var input = body.Text;
        var unchanged = input == initialBodyText;
        var restoreRich = restoredBodyText != null && input == restoredBodyText && original != null;
        var text = restoreRich ? original!.Value.Body : unchanged ? entry.Body : input;
        var sameAsSaved = savedAtOpen?.Body == text;
        var next = entry with
        {
            Title = title.Text,
            Body = text,
            Html = restoreRich ? original!.Value.Html : unchanged ? entry.Html : sameAsSaved ? savedAtOpen?.Html : null,
            Rtf = restoreRich ? original!.Value.Rtf : unchanged ? entry.Rtf : sameAsSaved ? savedAtOpen?.Rtf : null,
            Due = CurrentDueDate(),
            Format = entry.Assets.Count > 0 ? "image" : TextFormat(text),
            Protected = true
        };
        if (original is { } source && entry.OriginalBody == null && entry.OriginalTitle == null &&
            savedAtOpen is { } saved && (saved.Body != next.Body || saved.Title != next.Title))
        {
            next = next with
            {
                OriginalBody = source.Body,
                OriginalTitle = source.Title,
                OriginalHtml = source.Html,
                OriginalRtf = source.Rtf
            };
        }
        return next;
    }

    private string? CurrentDueDate()
    {
        if (entry.Kind != "todo") return entry.Due;
        // The date box can still be focused when the window is closed or the tray requests exit.
        if (string.IsNullOrWhiteSpace(due.Text)) return null;
        var parsed = DateTime.TryParse(due.Text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var date);
        return (parsed ? date : due.SelectedDate)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string TextFormat(string text)
    {
        var candidate = text.AsSpan().Trim();
        return candidate.Length <= 65_519 && Uri.TryCreate(candidate.ToString(), UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http" ? "link" : "text";
    }

    private void SetFeedback(string message, bool announce = false)
    {
        if (feedback.Text == message) return;
        feedback.Text = message;
        if (announce)
            FrameworkElementAutomationPeer.FromElement(feedback)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void DraftChanged()
    {
        if (loadingFields || finished || savedForCopy != null) return;
        revision++;
        if (firstPendingChange < 0) firstPendingChange = Environment.TickCount64;
        SetFeedback("尚未更新记录 · 正在保留本机草稿");
        ScheduleDraft();
    }

    private void ScheduleDraft()
    {
        var elapsed = Environment.TickCount64 - firstPendingChange;
        draftTimer.Stop();
        draftTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(MaxDraftDelayMs - elapsed, 1, DraftDelay.TotalMilliseconds));
        draftTimer.Start();
    }

    private Draft CaptureDraft() => new(Current(), baseClock, BodyFormatVerified: true);

    private async void WriteDraftOnTimer(object? sender, EventArgs e)
    {
        draftTimer.Stop();
        if (finished || savedForCopy != null || revision == savedRevision || draftWrite != null) return;
        var snapshot = CaptureDraft();
        writingRevision = revision;
        firstPendingChange = -1;
        var write = Task.Run(() => Host.Store.Draft(draftKey, snapshot));
        draftWrite = write;
        try { await write; }
        catch { /* CompleteDraftWrite reads the task result on the dispatcher. */ }
        CompleteDraftWrite(write);
    }

    private void CompleteDraftWrite(Task write)
    {
        if (!ReferenceEquals(draftWrite, write)) return;
        draftWrite = null;
        try
        {
            // The worker only uses Store and cannot wait on this dispatcher.
            write.GetAwaiter().GetResult();
            savedRevision = writingRevision;
            hasDraft = true;
            if (revision == savedRevision) SetFeedback("本机草稿已保存 · 尚未更新记录");
        }
        catch (Exception ex)
        {
            SetFeedback("本机草稿保存失败：" + ex.Message, true);
        }
        if (revision != savedRevision && firstPendingChange >= 0) ScheduleDraft();
    }

    private void StopDraftWrites()
    {
        if (draftWrite is { } write) CompleteDraftWrite(write);
        draftTimer.Stop();
    }

    internal bool FlushDraft()
    {
        StopDraftWrites();
        if (finished || savedForCopy != null || revision == savedRevision) return true;
        try
        {
            Host.Store.Draft(draftKey, CaptureDraft());
            savedRevision = revision;
            firstPendingChange = -1;
            hasDraft = true;
            SetFeedback("本机草稿已保存 · 尚未更新记录");
            return true;
        }
        catch (Exception ex)
        {
            SetFeedback("本机草稿保存失败：" + ex.Message, true);
            return false;
        }
    }

    internal void SetExitPending(bool value)
    {
        exitPending = value;
        if (Content is FrameworkElement root) root.IsEnabled = !value;
        AllowDrop = !value && savedForCopy == null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!Host.Exiting && (copying || !FlushDraft()))
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
        if (e.Cancel)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (!finished && !IsVisible) Host.ReleaseEditor(draftKey);
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        finished = true;
        draftTimer.Stop();
        base.OnClosed(e);
    }

    private async Task Save(bool copy)
    {
        if (exitPending || finished || savedForCopy != null) return;
        Entry next;
        try
        {
            next = Current();
            Store.ValidateEntry(next);
            StopDraftWrites();
            Host.Store.Save(next, "save", baseClock, draftKey);
        }
        catch (DraftConflictException ex)
        {
            saveAsNew.Visibility = Visibility.Visible;
            ReportSaveFailure(ex.Message + "。可以将当前内容另存为独立记录");
            return;
        }
        catch (Exception ex)
        {
            ReportSaveFailure("记录保存失败：" + ex.Message);
            return;
        }
        hasDraft = false;
        savedRevision = revision;
        if (!copy)
        {
            Finish("已保存");
            return;
        }

        savedForCopy = next;
        fields.IsEnabled = false;
        editCommands.Visibility = Visibility.Collapsed;
        AllowDrop = false;
        await CopySaved();
    }

    private void SaveAsNew()
    {
        if (exitPending || finished || savedForCopy != null || saveAsNew.Visibility != Visibility.Visible) return;
        try
        {
            var current = Current();
            var copy = new Entry
            {
                Id = recoveryId ??= Codec.Id(),
                Kind = current.Kind,
                Format = current.Format,
                Title = current.Title,
                Body = current.Body,
                Html = current.Html,
                Rtf = current.Rtf,
                Assets = current.Assets,
                Due = current.Due,
                SourceId = entry.Id,
                Protected = true
            };
            StopDraftWrites();
            Host.Store.Save(copy, draftKey: draftKey);
        }
        catch (Exception ex)
        {
            ReportSaveFailure("另存失败：" + ex.Message);
            return;
        }
        Finish("已另存为新内容");
    }

    private void ReportSaveFailure(string message)
    {
        var protectedNow = FlushDraft();
        if (protectedNow && hasDraft) message += " · 本机草稿已保留";
        if (!protectedNow) message += " · 本机草稿也未写入，输入仍留在编辑器";
        SetFeedback(message, true);
    }

    private async Task CopySaved()
    {
        if (exitPending || finished || copying || savedForCopy is not { } value) return;
        copying = true;
        copyCommands.IsEnabled = false;
        SetFeedback("记录已保存 · 正在复制");
        try
        {
            await Host.Clipboard.Copy(value);
        }
        catch (Exception ex)
        {
            if (!exitPending && !Host.Exiting)
            {
                copyCommands.Visibility = Visibility.Visible;
                SetFeedback("记录已保存 · 复制失败：" + ex.Message, true);
            }
            return;
        }
        finally
        {
            copying = false;
            copyCommands.IsEnabled = true;
        }
        if (!exitPending && !Host.Exiting) Finish("已保存并复制");
    }

    private void Cancel()
    {
        if (exitPending || finished || savedForCopy != null) return;
        if ((hasDraft || revision != 0) && !Ui.Confirm("放弃本次修改并清除本机草稿？已保存的记录会继续保留。")) return;
        DiscardChanges();
    }

    private void DiscardChanges()
    {
        StopDraftWrites();
        try
        {
            if (hasDraft || revision != 0) Host.Store.DiscardDraft(draftKey);
            Finish();
        }
        catch (Exception ex)
        {
            FlushDraft();
            SetFeedback("无法清除本机草稿：" + ex.Message, true);
        }
    }

    private void Finish() => Finish(null);
    private void Finish(string? message)
    {
        finished = true;
        draftTimer.Stop();
        Hide();
        Host.ReleaseEditor(draftKey);
        if (message != null) Host.Notify(message);
    }

    private void RestoreOriginal()
    {
        if (original is not { } source) return;
        loadingFields = true;
        try
        {
            body.Text = source.Body;
            title.Text = source.Title;
            restoredBodyText = body.Text;
        }
        finally { loadingFields = false; }
        DraftChanged();
        SetFeedback("已将原文放回编辑框 · 保存后更新记录", true);
    }

    private void OnPasteCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || exitPending || finished || savedForCopy != null) return;
        try
        {
            if (!Clipboard.ContainsText() && (Clipboard.ContainsImage() || Clipboard.ContainsData("PNG")))
            {
                e.CanExecute = true;
                e.Handled = true;
            }
        }
        catch { }
    }

    private void OnPasteExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || exitPending || finished || savedForCopy != null) return;
        if (TryPasteClipboardImage()) e.Handled = true;
    }

    private void OnBodyPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (exitPending || finished || savedForCopy != null) return;
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText, true) && TryPasteClipboardImage())
            e.CancelCommand();
    }

    private bool TryPasteClipboardImage()
    {
        try
        {
            var png = ClipboardService.TryReadClipboardImage();
            if (png == null) return false;
            if (entry.Assets.Count >= 20)
            {
                SetFeedback("每条记录最多 20 张图片", true);
                return true;
            }
            var id = Host.Store.Assets.Put(png);
            if (!entry.Assets.Contains(id))
            {
                entry = entry with { Assets = [.. entry.Assets, id] };
                RenderAttachments();
                DraftChanged();
            }
            SetFeedback("已从剪贴板粘贴图片附件", true);
            return true;
        }
        catch (Exception ex)
        {
            SetFeedback("剪贴板图片粘贴失败：" + ex.Message, true);
            return true;
        }
    }

    private void AddImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true) AttachFiles(dialog.FileNames);
    }

    private void AttachFiles(IEnumerable<string> paths)
    {
        if (exitPending || finished || savedForCopy != null) return;
        var assets = new List<string>(entry.Assets);
        string? error = null;
        foreach (var path in paths)
        {
            if (assets.Count >= 20)
            {
                error = "每条记录最多 20 张图片";
                break;
            }
            try
            {
                foreach (var id in Host.Clipboard.Import(path).Assets)
                    if (!assets.Contains(id)) assets.Add(id);
            }
            catch (Exception ex) { error = "图片导入失败：" + ex.Message; }
        }
        if (assets.Count != entry.Assets.Count)
        {
            entry = entry with { Assets = assets };
            RenderAttachments();
            DraftChanged();
        }
        if (error != null) SetFeedback(error, true);
    }

    private void RenderAttachments()
    {
        attachments.Children.Clear();
        for (var index = 0; index < entry.Assets.Count; index++)
        {
            var id = entry.Assets[index];
            var row = new DockPanel();
            var remove = Ui.Button("移除附件", () =>
            {
                entry = entry with { Assets = entry.Assets.Where(x => x != id).ToList() };
                RenderAttachments();
                DraftChanged();
            });
            AutomationProperties.SetName(remove, $"移除附件 {index + 1}");
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(Ui.Thumbnail(Host.Store.Assets, id, 320, 120));
            attachments.Children.Add(Ui.Card(row));
        }
    }
}
