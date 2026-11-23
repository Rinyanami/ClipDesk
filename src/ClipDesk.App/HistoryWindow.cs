using System.Collections.ObjectModel;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace ClipDesk.App;

internal sealed class HistoryWindow : DeskWindow
{
    private const int HistoryLimit = 500;
    private readonly bool pinnedOnly;
    private readonly TextBox search = Ui.Input();
    private readonly ListBox list = RecordList.Create(typeof(ItemView), "剪贴列表");
    private readonly TextBlock status = Ui.Text(" ", 13);
    private readonly TextBlock syncStatus;
    private readonly TextBlock count = Ui.Text("正在读取…", 13, Ui.Muted);
    private readonly TextBlock recording = Ui.Text("剪贴记录已暂停", 13, Ui.Accent);
    private readonly TextBlock emptyTitle = Ui.Text("", 18);
    private readonly TextBlock emptyHint = Ui.Text("", 14, Ui.Muted);
    private readonly ScrollViewer empty;
    private readonly Button clearSearch;
    private readonly RecordRefresh refresh;
    private ObservableCollection<HistoryItem> items = [];
    private ContextMenu? openMenu;
    private PasteTarget? target;
    private CopyClick? firstClick, secondClick;
    private HistoryItem? pendingEnter;
    private string? renderedQuery;
    private long freezeUntil;
    private int interaction, copyAttempt, copiesInProgress;
    private bool composing, pasting, choosingFiles, importing;

    internal HistoryWindow(App host, bool pinnedOnly = false)
        : base(host, pinnedOnly ? "pinned" : "clipboard", pinnedOnly ? "ClipDesk · 置顶" : "ClipDesk · 剪贴记录",
            pinnedOnly ? 420 : 580)
    {
        this.pinnedOnly = pinnedOnly;
        ShowInTaskbar = pinnedOnly;
        search.ToolTip = pinnedOnly ? "搜索已置顶的文字、链接和标题" : "搜索已保存的文字、链接和标题";
        AutomationProperties.SetName(search, pinnedOnly ? "搜索置顶剪贴" : "搜索剪贴记录");
        AutomationProperties.SetName(list, pinnedOnly ? "置顶剪贴列表" : "剪贴记录列表");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        status.Text = "单击复制 · 双击或 Enter 粘贴 · Esc 收起";
        status.Foreground = Ui.Muted;
        status.FontSize = 12;
        status.MinHeight = 18;
        status.Margin = new Thickness(0);
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextWrapping = TextWrapping.NoWrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        syncStatus = Ui.Text(host.SyncStatus, 11.5, Ui.Muted);
        syncStatus.Margin = new Thickness(8, 0, 0, 0);
        syncStatus.VerticalAlignment = VerticalAlignment.Center;
        syncStatus.TextWrapping = TextWrapping.NoWrap;
        host.SyncChanged += message => syncStatus.Text = message;

        var root = new DockPanel { Margin = new Thickness(pinnedOnly ? 8 : 12, 4, pinnedOnly ? 8 : 12, 6) };
        Content = root;
        var footer = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        if (!pinnedOnly)
        {
            DockPanel.SetDock(syncStatus, Dock.Right);
            footer.Children.Add(syncStatus);
        }
        footer.Children.Add(status);
        SizeChanged += (_, e) =>
        {
            if (!pinnedOnly)
                syncStatus.Visibility = e.NewSize.Width >= 420 ? Visibility.Visible : Visibility.Collapsed;
        };

        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var headingActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (pinnedOnly)
            headingActions.Children.Add(NavigationButton("剪贴", false, () => Host.ShowHistory(CurrentTarget)));
        var windowOpts = WindowOptionsButton();
        windowOpts.Padding = new Thickness(8, 3, 8, 3);
        windowOpts.FontSize = 12;
        headingActions.Children.Add(windowOpts);
        var moreMenu = BuildMoreMenu();
        var more = new Button { Content = "更多", ContextMenu = moreMenu, Padding = new Thickness(8, 3, 8, 3), FontSize = 12 };
        more.ToolTip = "打开回收站、设置与其他功能";
        more.Click += (_, e) => { e.Handled = true; OpenMenu(moreMenu, more); };
        headingActions.Children.Add(more);
        DockPanel.SetDock(headingActions, Dock.Right);
        heading.Children.Add(headingActions);

        count.FontSize = 11.5;
        count.Margin = new Thickness(2, 1, 0, 1);
        recording.FontSize = 11.5;
        recording.Margin = new Thickness(8, 1, 0, 1);
        recording.Visibility = Visibility.Collapsed;

        if (pinnedOnly)
        {
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var title = Ui.Text("置顶", 15);
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(2, 0, 8, 0);
            title.VerticalAlignment = VerticalAlignment.Center;
            count.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(title);
            titleRow.Children.Add(count);
            heading.Children.Add(titleRow);
            top.Children.Add(heading);
        }
        else
        {
            var navigation = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            navigation.Children.Add(NavigationButton("剪贴", true, () => search.Focus()));
            navigation.Children.Add(NavigationButton("置顶", false, () => Host.ShowPinned(CurrentTarget)));
            navigation.Children.Add(NavigationButton("待办", false, Host.ShowTodos));
            navigation.Children.Add(NavigationButton("备忘", false, Host.ShowMemos));
            heading.Children.Add(navigation);
            top.Children.Add(heading);
        }

        var toolbar = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(Ui.SearchBox(search, pinnedOnly ? "搜索置顶剪贴" : "搜索文字、链接或标题"));
        clearSearch = Ui.Button("清除", () => { search.Clear(); search.Focus(); });
        AutomationProperties.SetName(clearSearch, "清除搜索");
        clearSearch.Visibility = Visibility.Collapsed;
        Grid.SetColumn(clearSearch, 1);
        toolbar.Children.Add(clearSearch);
        if (!pinnedOnly)
        {
            var create = Ui.Button("新建剪贴", () => Host.Edit(new Entry { Protected = true }), true);
            Grid.SetColumn(create, 2);
            toolbar.Children.Add(create);
        }
        top.Children.Add(toolbar);
        if (!pinnedOnly)
        {
            var metaRow = new DockPanel { Margin = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(recording, Dock.Right);
            metaRow.Children.Add(recording);
            metaRow.Children.Add(count);
            top.Children.Add(metaRow);
        }

        var results = new Grid();
        results.Children.Add(list);
        empty = new ScrollViewer
        {
            Content = Ui.Card(new StackPanel { Children = { emptyTitle, emptyHint } }),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Visibility = Visibility.Collapsed
        };
        results.Children.Add(empty);
        root.Children.Add(results);

        refresh = new RecordRefresh(this, Refresh, CanRefresh);
        Host.Store.Changed += refresh.Request;
        search.TextChanged += (_, _) => SearchChanged();
        search.AddHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler((_, _) => composing = true), true);
        search.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler((_, _) => composing = true), true);
        search.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler((_, _) => composing = false), true);
        search.LostKeyboardFocus += (_, _) => composing = false;

        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        PreviewMouseDown += (_, e) =>
        {
            if (pasting || e.ChangedButton != MouseButton.Left || !IsOnCopySurface(e.OriginalSource as DependencyObject))
                CancelGestures();
            else
            {
                secondClick = null;
                pendingEnter = null;
            }
        };
        EnableAutoHide(() => openMenu?.IsOpen == true || choosingFiles);
        Deactivated += (_, _) => EndSession();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) UpdateRecording();
            else EndSession();
        };
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(CaptureOnMouseActivate);
        };

        AllowDrop = !pinnedOnly;
        PreviewDragOver += (_, e) =>
        {
            if (pinnedOnly || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = importing ? DragDropEffects.None : DragDropEffects.Copy;
            e.Handled = true;
        };
        PreviewDrop += async (_, e) =>
        {
            if (pinnedOnly || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            e.Handled = true;
            await ImportFiles(files);
        };
    }

    internal async void Warm()
    {
        if (IsVisible || Host.Exiting) return;
        new WindowInteropHelper(this).EnsureHandle();
        await refresh.Warm();
        if (!IsVisible && !Host.Exiting && Width > 0 && Height > 0)
        {
            Measure(new Size(Width, Height));
            Arrange(new Rect(0, 0, Width, Height));
            UpdateLayout();
        }
    }

    internal bool IsListReady => renderedQuery != null;
    internal bool HasPendingWork => importing || choosingFiles || pasting || copiesInProgress > 0;
    internal PasteTarget? CurrentTarget => IsActive ? target : null;

    private string Query => search.Text.Trim();
    private bool QueryCurrent => renderedQuery == Query;
    private bool FromSearchOrList => search.IsKeyboardFocusWithin
        || (list.IsKeyboardFocusWithin && Keyboard.FocusedElement is ListBox or ListBoxItem);

    internal void Summon()
    {
        if (IsVisible && IsActive && !KeepVisible)
        {
            Dismiss();
            return;
        }
        Present();
    }

    internal void Present(PasteTarget? priorTarget = null)
    {
        if (!IsVisible || !IsActive)
        {
            EndSession();
            target = priorTarget ?? PasteTarget.Capture();
            Open();
        }
        search.Focus();
    }

    private Button NavigationButton(string label, bool current, Action open)
    {
        var button = Ui.Button(label, open, current);
        button.Padding = new Thickness(10, 3.5, 10, 3.5);
        button.FontSize = 12.5;
        var hint = current ? "当前窗口，点击可聚焦搜索" : "打开" + label + "窗口";
        button.ToolTip = hint;
        AutomationProperties.SetHelpText(button, hint);
        return button;
    }

    private nint CaptureOnMouseActivate(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int mouseActivate = 0x0021;
        const int leftButtonDown = 0x0201;
        var mouseMessage = (int)((lParam.ToInt64() >> 16) & 0xffff);
        if (message != mouseActivate || mouseMessage != leftButtonDown || IsActive || pasting
            || choosingFiles || openMenu?.IsOpen == true)
            return 0;

        // 鼠标激活常驻窗前，重新确认用户刚在用的输入框。
        EndSession();
        target = PasteTarget.Capture();
        return 0;
    }

    internal void Status(string message)
    {
        status.Foreground = Ui.Ink;
        status.Text = message;
        status.ToolTip = message;
        if (System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(status) is { } peer)
            peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void CancelGestures()
    {
        interaction++;
        firstClick = null;
        secondClick = null;
        pendingEnter = null;
        freezeUntil = 0;
    }

    private void EndSession()
    {
        target = null;
        CancelGestures();
    }

    internal void UpdateRecording()
    {
        if (pinnedOnly) return;
        recording.Visibility = Host.Store.Settings.Paused ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count == 0 && empty.Visibility == Visibility.Visible && Query.Length == 0)
            ShowEmpty(Query);
    }

    private void SearchChanged()
    {
        CancelGestures();
        clearSearch.Visibility = search.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        list.IsEnabled = QueryCurrent;
        if (!QueryCurrent)
        {
            count.Text = "正在查找…";
            empty.Visibility = Visibility.Collapsed;
        }
        refresh.Request();
    }

    private bool CanRefresh() => !pasting && openMenu?.IsOpen != true && pendingEnter == null
        && (secondClick == null || Mouse.LeftButton != MouseButtonState.Pressed)
        && Environment.TickCount64 >= freezeUntil;

    private async Task Refresh()
    {
        var query = Query;
        try
        {
            var next = await Task.Run(() =>
            {
                var entries = Host.Store.List("clip", query, limit: HistoryLimit, pinnedOnly: pinnedOnly, summaryOnly: true);
                var conflicts = Host.Store.ConflictIds(entries.Select(entry => entry.Id));
                return entries.Select(entry => new HistoryItem(this, entry, conflicts.Contains(entry.Id),
                    entry.Assets.Where(id => !Host.Store.Assets.FileExists(id)).ToArray())).ToList();
            });
            if (query != Query || Host.Exiting) return;
            if (!CanRefresh())
            {
                refresh.Request();
                return;
            }

            items = RecordList.Update(list, items, next, item => item.Value.Id,
                (previous, current) => previous.HasConflict == current.HasConflict
                    && RecordList.SameEntry(previous.Value, current.Value)
                    && previous.MissingImages.SequenceEqual(current.MissingImages),
                renderedQuery != query, search);
            renderedQuery = query;
            list.IsEnabled = true;
            list.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (items.Count == 0)
            {
                count.Text = "";
                ShowEmpty(query);
                return;
            }

            var pinned = items.Count(item => item.Value.Pinned);
            count.Text = query.Length > 0
                ? $"找到 {items.Count} 条记录"
                : pinnedOnly ? $"{items.Count} 条置顶剪贴" : $"置顶 {pinned} 条   ·   最近 {items.Count - pinned} 条";
            if (items.Count == HistoryLimit) count.Text += "   ·   可继续搜索缩小范围";
        }
        catch (Exception ex)
        {
            refresh.RetryOnShow();
            if (IsVisible) Status((pinnedOnly ? "读取置顶剪贴失败，" : "读取剪贴记录失败，") + ex.Message);
        }
    }

    private void ShowEmpty(string query)
    {
        if (query.Length > 0)
        {
            emptyTitle.Text = pinnedOnly ? "没有找到匹配的置顶剪贴" : "没有找到匹配的剪贴";
            emptyHint.Text = "试试更短的关键词，或点击「清除」。";
            return;
        }

        if (pinnedOnly)
        {
            emptyTitle.Text = "还没有置顶剪贴";
            emptyHint.Text = "打开「剪贴」，在常用内容旁点击「置顶」。";
            return;
        }

        emptyTitle.Text = "还没有剪贴记录";
        emptyHint.Text = recording.Visibility == Visibility.Visible
            ? "记录已暂停，可从「更多」恢复记录，也可以手动新建剪贴。"
            : "在其他应用复制文字、链接或图片，ClipDesk 会自动记录。也可以点击「新建剪贴」手动保存。";
    }

    private FrameworkElement BuildItem(HistoryItem item)
    {
        var entry = item.Value;
        var layout = new DockPanel();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(actions, Dock.Right);
        layout.Children.Add(actions);
        layout.SizeChanged += (_, e) =>
        {
            var dock = e.NewSize.Width < 330 ? Dock.Bottom : Dock.Right;
            if (DockPanel.GetDock(actions) != dock) DockPanel.SetDock(actions, dock);
        };
        var pin = Ui.Button(entry.Pinned ? "取消置顶" : "置顶", () =>
        {
            Host.Store.SetPinned(entry.Id, !entry.Pinned);
            Status(entry.Pinned ? "已取消置顶" : "已置顶");
        });
        pin.Padding = new Thickness(8, 3, 8, 3);
        pin.FontSize = 12;
        AutomationProperties.SetAutomationId(pin, "Pin");
        actions.Children.Add(pin);

        ContextMenu? menu = null;
        ContextMenu Actions() => menu ??= BuildItemMenu(item);
        var open = new Button { Content = "操作", ToolTip = "编辑、加入待办、保存为备忘等操作", Padding = new Thickness(8, 3, 8, 3), FontSize = 12 };
        AutomationProperties.SetAutomationId(open, "Actions");
        open.Click += (_, e) => { e.Handled = true; OpenMenu(Actions(), open); };
        actions.Children.Add(open);

        var content = new StackPanel();
        var copySurface = new CopySurface { Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = content };
        layout.Children.Add(copySurface);
        copySurface.PreviewMouseLeftButtonDown += async (_, e) => await OnCopyDown(item, e);
        copySurface.PreviewMouseLeftButtonUp += async (_, e) => await OnCopyUp(item, e);

        if (!string.IsNullOrWhiteSpace(entry.Title))
        {
            var title = Ui.Text(Ui.PreviewText(entry.Title, 120), 14.5);
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0, 1, 0, 1);
            content.Children.Add(title);
        }
        var body = entry.Body.Trim();
        if (body.Length > 0 && body != entry.Title)
        {
            var preview = Ui.Text(Ui.PreviewText(body, 320), 13.5);
            preview.MaxHeight = 62;
            preview.Margin = new Thickness(0, 1, 0, 2);
            preview.TextTrimming = TextTrimming.CharacterEllipsis;
            content.Children.Add(preview);
        }
        if (entry.Assets.Count > 0)
        {
            var id = entry.Assets[0];
            if (!item.MissingImages.Contains(id))
            {
                var image = Ui.Thumbnail(Host.Store.Assets, id, 280, 92);
                image.MaxWidth = 220;
                image.HorizontalAlignment = HorizontalAlignment.Left;
                content.Children.Add(image);
            }
            if (item.MissingImages.Length > 0)
            {
                var unavailable = Ui.Text("图片待下载", 12, Ui.Muted);
                unavailable.ToolTip = MissingImageMessage();
                content.Children.Add(unavailable);
            }
        }

        var format = entry.Assets.Count > 0 ? "图片" : entry.Format == "link" ? "链接" : "文字";
        var meta = pinnedOnly ? format : entry.Pinned ? $"已置顶 · {format}"
            : entry.Protected ? $"{format} · 长期保留" : format;
        meta += " · " + DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedAt).ToLocalTime().ToString("MM-dd HH:mm");
        if (entry.SourceApp.Length > 0) meta += " · " + Ui.PreviewText(entry.SourceApp, 28);
        if (item.HasConflict) meta += " · 有多个版本";
        var metaColor = (entry.Pinned && !pinnedOnly) || item.HasConflict ? Ui.Accent : Ui.Muted;
        var metaBlock = Ui.Text(meta, 11.5, metaColor);
        metaBlock.Margin = new Thickness(0, 2, 0, 0);
        content.Children.Add(metaBlock);

        var card = Ui.Card(layout);
        card.Padding = new Thickness(10, 6, 10, 6);
        card.ContextMenu = new ContextMenu();
        card.ContextMenuOpening += (_, _) => card.ContextMenu = Actions();
        return card;
    }

    private ContextMenu BuildMoreMenu()
    {
        var menu = CreateMenu();
        AddMenu(menu, "本机草稿", Host.ShowDrafts);
        AddMenu(menu, "回收站", Host.ShowTrash);
        menu.Items.Add(new Separator());
        MenuItem? pause = null;
        if (!pinnedOnly)
        {
            AddMenu(menu, "导入图片", () => _ = Import());
            pause = AddMenu(menu, "暂停记录", Host.TogglePause);
        }
        var sync = AddMenu(menu, "立即同步", () =>
        {
            Host.RequestSync();
            Status("已请求同步");
        });
        AddMenu(menu, "设置与同步", Host.ShowSettings);
        menu.Items.Add(new Separator());
        AddMenu(menu, "退出 ClipDesk", Host.ExitApp);
        menu.Opened += (_, _) =>
        {
            var settings = Host.Store.Settings;
            if (pause != null) pause.Header = settings.Paused ? "恢复记录" : "暂停记录";
            sync.IsEnabled = settings.Sync.Enabled;
        };
        return menu;
    }

    private ContextMenu BuildItemMenu(HistoryItem item)
    {
        var entry = item.Value;
        var menu = CreateMenu();
        var copy = AddMenu(menu, entry.Assets.Count > 0 ? "复制原图" : "复制内容", () => _ = TryCopy(item));
        copy.IsEnabled = entry.Assets.Count == 0 || !item.MissingImages.Contains(entry.Assets[0]);
        var plain = AddMenu(menu, "复制为纯文本", () => _ = TryCopy(item, plain: true));
        plain.IsEnabled = !string.IsNullOrWhiteSpace(entry.Body) || !string.IsNullOrWhiteSpace(entry.Title);
        if (item.MissingImages.Length > 0)
        {
            AddMenu(menu, "下载图片", () =>
            {
                if (Host.Store.Settings.Sync.Enabled) { Host.RequestSync(); Status("已请求同步"); }
                else Host.ShowSettings();
            });
        }
        menu.Items.Add(new Separator());
        AddMenu(menu, "编辑", () => Host.Edit(Host.Store.Get(entry.Id) ?? entry));
        AddMenu(menu, "加入待办", () => Host.Convert(Host.Store.Get(entry.Id) ?? entry, "todo"));
        AddMenu(menu, "保存为备忘", () => Host.Convert(Host.Store.Get(entry.Id) ?? entry, "memo"));
        AddMenu(menu, "详情和版本", () => Host.Details(Host.Store.Get(entry.Id) ?? entry));
        AddMenu(menu, "移入回收站", () =>
        {
            Host.Store.Delete(entry.Id);
            Status("已移入回收站。可从「更多」打开回收站恢复。");
        });
        return menu;
    }

    private ContextMenu CreateMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            CancelGestures();
            openMenu = menu;
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(openMenu, menu)) openMenu = null;
            menu.ClearValue(ContextMenu.PlacementTargetProperty);
            menu.ClearValue(ContextMenu.PlacementProperty);
            RequestAutoHide();
        };
        return menu;
    }

    private void OpenMenu(ContextMenu menu, FrameworkElement anchor)
    {
        openMenu = menu;
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem AddMenu(ContextMenu menu, string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, e) =>
        {
            e.Handled = true;
            try { action(); }
            catch (Exception ex) { Status("操作未完成，" + ex.Message); }
        };
        menu.Items.Add(item);
        return item;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (openMenu?.IsOpen == true || e.Key is Key.ImeProcessed or Key.DeadCharProcessed
            || (search.IsKeyboardFocusWithin && composing)) return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Dismiss();
            return;
        }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CancelGestures();
            search.Focus();
            search.SelectAll();
            e.Handled = true;
            return;
        }
        if (!FromSearchOrList) return;

        if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            if (!search.IsKeyboardFocusWithin && QueryCurrent && list.SelectedItem is HistoryItem selected)
            {
                list.ScrollIntoView(selected);
                list.UpdateLayout();
                if (list.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem anchor)
                {
                    OpenMenu(BuildItemMenu(selected), anchor);
                    e.Handled = true;
                }
            }
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        if (e.Key == Key.Enter)
        {
            // 等实际按键释放后再隐藏窗口，长按 Enter 不会把重复按键送到原窗口。
            e.Handled = true;
            if (!e.IsRepeat && !pasting && QueryCurrent) pendingEnter = list.SelectedItem as HistoryItem;
            return;
        }
        if (search.IsKeyboardFocusWithin && QueryCurrent && list.Items.Count > 0 && e.Key is Key.Up or Key.Down)
        {
            pendingEnter = null;
            var step = e.Key == Key.Down ? 1 : -1;
            list.SelectedIndex = Math.Clamp(list.SelectedIndex + step, 0, list.Items.Count - 1);
            list.ScrollIntoView(list.SelectedItem);
            e.Handled = true;
        }
    }

    private async void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || pendingEnter is not { } item) return;
        pendingEnter = null;
        if (!FromSearchOrList || composing || !QueryCurrent || Keyboard.Modifiers != ModifierKeys.None
            || openMenu?.IsOpen == true || !ReferenceEquals(list.SelectedItem, item)) return;
        e.Handled = true;
        await Paste(item);
    }

    private static bool IsOnCopySurface(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is CopySurface) return true;
            source = source switch
            {
                FrameworkContentElement content => content.Parent,
                Visual visual => VisualTreeHelper.GetParent(visual),
                _ => null
            };
        }
        return false;
    }

    private async Task OnCopyDown(HistoryItem item, MouseButtonEventArgs e)
    {
        if (!QueryCurrent || pasting || openMenu?.IsOpen == true) return;
        e.Handled = true;
        list.SelectedItem = item;
        (list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)?.Focus();
        var doubleClickTime = System.Windows.Forms.SystemInformation.DoubleClickTime;
        var previous = firstClick;
        firstClick = null;
        if (e.ClickCount == 2 && previous != null && ReferenceEquals(previous.Item, item)
            && previous.Interaction == interaction && unchecked((uint)(e.Timestamp - previous.Timestamp)) <= doubleClickTime)
        {
            secondClick = previous;
            return;
        }
        if (e.ClickCount > 2) return;

        freezeUntil = Environment.TickCount64 + doubleClickTime + 80;
        var copying = TryCopy(item);
        firstClick = new CopyClick(item, copying, interaction, e.Timestamp);
        await copying;
    }

    private async Task OnCopyUp(HistoryItem item, MouseButtonEventArgs e)
    {
        var click = secondClick;
        secondClick = null;
        if (click == null || !ReferenceEquals(click.Item, item) || click.Interaction != interaction) return;
        e.Handled = true;
        await Paste(item, click.Copy);
    }

    private string MissingImageMessage() => Host.Store.Settings.Sync.Enabled
        ? "图片待下载。可从「更多」点击「立即同步」。"
        : "图片待下载。可从「更多」打开「设置与同步」。";

    private async Task<uint?> TryCopy(HistoryItem item, bool plain = false)
    {
        var attempt = ++copyAttempt;
        var currentInteraction = interaction;
        var summary = item.Value;
        copiesInProgress++;
        try
        {
            var entry = await Task.Run(() => Host.Store.Get(summary.Id) ?? summary);
            if (!plain && entry.Assets.Count > 0 && !Host.Store.Assets.Exists(entry.Assets[0]))
            {
                Status(MissingImageMessage());
                return null;
            }

            var sequence = await Host.Clipboard.Copy(entry, plain);
            if (attempt == copyAttempt && currentInteraction == interaction)
                Status(plain ? "已复制为纯文本" : entry.Assets.Count > 0 ? "原图已复制" : "已复制");
            return sequence;
        }
        catch (Exception ex)
        {
            if (attempt == copyAttempt && currentInteraction == interaction) Status("复制未完成，" + ex.Message);
            return null;
        }
        finally
        {
            copiesInProgress--;
        }
    }

    private async Task Paste(HistoryItem item, Task<uint?>? firstCopy = null)
    {
        if (pasting || !IsVisible || !IsActive) return;
        pasting = true;
        var currentInteraction = interaction;
        var destination = target;
        firstClick = null;
        try
        {
            var sequence = await (firstCopy ?? TryCopy(item));
            if (sequence == null || currentInteraction != interaction || !IsVisible || !IsActive
                || openMenu?.IsOpen == true || !QueryCurrent || !ReferenceEquals(list.SelectedItem, item)
                || Native.GetForegroundWindow() != new WindowInteropHelper(this).Handle) return;
            if (Native.GetClipboardSequenceNumber() != sequence)
            {
                Status("剪贴板已更新，已取消粘贴");
                return;
            }

            const string manualPaste = "已复制。请返回输入框，按 Ctrl+V 手动粘贴。";
            const string changedClipboard = "剪贴板已更新，已取消粘贴";
            var hwnd = new WindowInteropHelper(this).Handle;
            if (destination == null)
            {
                Dismiss();
                ReportPaste(Native.GetClipboardSequenceNumber() == sequence
                    ? "已复制。未识别刚才的输入框，请按 Ctrl+V 手动粘贴。" : changedClipboard);
                return;
            }

            var result = await destination.PasteAfterHide(sequence.Value, hwnd, Dismiss);
            if (result == PasteResult.Requested)
            {
                Status("已尝试粘贴");
                return;
            }

            var clipboardChanged = result == PasteResult.ClipboardChanged || Native.GetClipboardSequenceNumber() != sequence;
            if (result == PasteResult.InputBusy && !clipboardChanged && currentInteraction == interaction
                && IsVisible && IsActive && Native.GetForegroundWindow() == hwnd)
            {
                target = null;
                if (Native.IsEnterDown())
                {
                    Status("已复制。松开 Enter 后，可返回输入框手动粘贴。");
                    return;
                }
                Dismiss();
            }
            var reason = result switch
            {
                PasteResult.FocusNotRestored => "未能恢复刚才的输入焦点。",
                PasteResult.TargetUnavailable => "原输入窗口或输入框已不可用。",
                PasteResult.InputBusy => "仍有按键按下，已停止自动粘贴。",
                PasteResult.PickerInactive => "剪贴窗口已不在前台。",
                PasteResult.InputRejected => "Windows 未完成自动粘贴。",
                _ => ""
            };
            ReportPaste(clipboardChanged ? changedClipboard : reason + manualPaste);
        }
        finally
        {
            pasting = false;
        }
    }

    private void ReportPaste(string message)
    {
        Status(message);
        if (!IsVisible) Host.Notify(message);
    }

    private async Task Import()
    {
        if (choosingFiles || importing) return;
        choosingFiles = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入图片",
                Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) == true) await ImportFiles(dialog.FileNames);
        }
        catch (Exception ex)
        {
            Status("无法打开图片，" + ex.Message);
        }
        finally
        {
            choosingFiles = false;
            RequestAutoHide();
        }
    }

    private async Task ImportFiles(string[] paths)
    {
        if (importing || paths.Length == 0) return;
        importing = true;
        Status($"正在导入 {paths.Length} 张图片…");
        try
        {
            var (saved, error) = await Task.Run(() =>
            {
                var count = 0;
                string? lastError = null;
                foreach (var path in paths)
                {
                    try
                    {
                        Host.Store.Save(Host.Clipboard.Import(path));
                        count++;
                    }
                    catch (Exception ex) { lastError = ex.Message; }
                }
                return (count, lastError);
            });
            var message = saved == 0 ? "图片导入失败" : $"已导入 {saved} 张图片并长期保留";
            if (error != null)
                message += saved == 0 ? "，" + error : $"。{paths.Length - saved} 张未导入，" + error;
            if (IsVisible) Status(message);
            else Host.Notify(message);
        }
        finally
        {
            importing = false;
        }
    }

    private sealed class HistoryItem(HistoryWindow owner, Entry value, bool hasConflict, string[] missingImages)
    {
        internal HistoryWindow Owner { get; } = owner;
        internal Entry Value { get; } = value;
        internal bool HasConflict { get; } = hasConflict;
        internal string[] MissingImages { get; } = missingImages;
    }

    private sealed record CopyClick(HistoryItem Item, Task<uint?> Copy, int Interaction, int Timestamp);

    private sealed class CopySurface : Border { }

    private sealed class ItemView : Decorator
    {
        private HistoryItem? rendered;
        public ItemView()
        {
            DataContextChanged += (_, e) =>
            {
                if (ReferenceEquals(rendered, e.NewValue)) return;
                rendered = e.NewValue as HistoryItem;
                Child = rendered != null ? rendered.Owner.BuildItem(rendered) : null;
            };
        }
    }
}
