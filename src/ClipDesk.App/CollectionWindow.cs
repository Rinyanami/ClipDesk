using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ClipDesk.App;

internal sealed class CollectionWindow : DeskWindow
{
    private readonly string kind;
    private readonly TextBox search = Ui.Input();
    private readonly TextBox quickAdd = Ui.Input();
    private readonly ComboBox filter = new()
    {
        MinWidth = 76,
        MinHeight = 26,
        Margin = new Thickness(4, 1, 2, 1),
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly ListBox list = RecordList.Create(typeof(ItemView), "记录列表");
    private readonly TextBlock count = Ui.Text("正在读取记录…", 11.5, Ui.Muted);
    private readonly TextBlock status = Ui.Text("", 12);
    private readonly TextBlock emptyTitle = Ui.Text("", 16);
    private readonly TextBlock emptyHint = Ui.Text("", 13, Ui.Muted);
    private readonly ScrollViewer empty;
    private readonly Button clearSearch;
    private readonly RecordRefresh refresh;
    private ObservableCollection<CollectionItem> items = [];
    private Query? rendered;
    private ContextMenu? openMenu;
    private int copiesInProgress;

    internal CollectionWindow(App host, string kind)
        : base(host, kind, "ClipDesk · " + KindName(kind), kind == "trash" ? 620 : 350)
    {
        this.kind = kind;
        var caption = KindName(kind);
        AutomationProperties.SetName(list, caption + "列表");
        AutomationProperties.SetName(search, "搜索" + caption);
        AutomationProperties.SetName(filter, "按待办状态筛选");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var root = new DockPanel { Margin = new Thickness(kind == "trash" ? 12 : 8, 4, kind == "trash" ? 12 : 8, 6) };
        Content = root;
        status.Visibility = Visibility.Collapsed;
        status.Margin = new Thickness(2, 2, 0, 0);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var options = WindowOptionsButton();
        options.Padding = new Thickness(8, 3, 8, 3);
        options.FontSize = 12;
        DockPanel.SetDock(options, Dock.Right);
        heading.Children.Add(options);
        if (kind == "todo")
        {
            filter.ItemsSource = new[] { "未完成", "已完成", "全部" };
            filter.SelectedIndex = 0;
            DockPanel.SetDock(filter, Dock.Right);
            heading.Children.Add(filter);
        }
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var title = Ui.Text(caption, 15);
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(2, 0, 8, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        count.VerticalAlignment = VerticalAlignment.Center;
        count.Margin = new Thickness(0);
        titleRow.Children.Add(title);
        titleRow.Children.Add(count);
        heading.Children.Add(titleRow);
        top.Children.Add(heading);

        var toolbar = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(Ui.SearchBox(search, kind == "trash" ? "搜索已删除的内容" : "搜索标题或正文"));
        clearSearch = Ui.Button("清除", () => { search.Clear(); search.Focus(); });
        AutomationProperties.SetName(clearSearch, "清除搜索");
        clearSearch.Visibility = Visibility.Collapsed;
        Grid.SetColumn(clearSearch, 1);
        toolbar.Children.Add(clearSearch);
        if (kind != "trash")
        {
            var create = Ui.Button("新建" + caption, () => Host.Edit(new Entry { Kind = kind, Protected = true }), true);
            Grid.SetColumn(create, 2);
            toolbar.Children.Add(create);
        }
        top.Children.Add(toolbar);

        if (kind == "todo")
        {
            var quickBox = Ui.SearchBox(quickAdd, "快速添加待办，按 Enter 直接保存…");
            quickBox.Margin = new Thickness(0, 0, 0, 2);
            AutomationProperties.SetName(quickAdd, "快速添加待办");
            quickAdd.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
                e.Handled = true;
                var text = quickAdd.Text.Trim();
                if (text.Length == 0) return;
                try
                {
                    Host.Store.Save(new Entry { Kind = "todo", Title = Ui.PreviewText(text, 200), Body = "", Protected = true });
                    quickAdd.Clear();
                    Feedback("已添加待办");
                }
                catch (Exception ex) { Feedback("添加失败：" + ex.Message); }
            };
            top.Children.Add(quickBox);
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

        refresh = new RecordRefresh(this, Refresh, () => openMenu?.IsOpen != true);
        if (kind != "trash") EnableAutoHide(() => openMenu?.IsOpen == true);
        search.TextChanged += (_, _) => QueryChanged();
        filter.SelectionChanged += (_, _) => QueryChanged();
        Host.Store.Changed += refresh.Request;
        Loaded += (_, _) => search.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.IsRepeat && Keyboard.FocusedElement is ButtonBase)
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                search.Focus();
                search.SelectAll();
                e.Handled = true;
            }
        };
    }

    internal bool IsListReady => rendered != null;
    internal bool HasPendingWork => copiesInProgress > 0;

    private Query CurrentQuery => new(search.Text.Trim(), kind == "todo" ? filter.SelectedIndex : 2);

    private void QueryChanged()
    {
        clearSearch.Visibility = search.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        list.IsEnabled = rendered == CurrentQuery;
        if (!list.IsEnabled)
        {
            count.Text = "正在查找…";
            empty.Visibility = Visibility.Collapsed;
        }
        refresh.Request();
    }

    private async Task Refresh()
    {
        var query = CurrentQuery;
        try
        {
            bool? done = query.Filter == 2 ? null : query.Filter == 1;
            var next = await Task.Run(() => Host.Store.List(kind == "trash" ? null : kind,
                query.Text, kind == "trash", done: done, summaryOnly: true)
                .Select(e => new CollectionItem(this, e, e.Assets.Where(id => !Host.Store.Assets.FileExists(id)).ToArray())).ToList());
            if (!IsVisible || query != CurrentQuery) return;
            if (openMenu?.IsOpen == true)
            {
                refresh.Request();
                return;
            }

            items = RecordList.Update(list, items, next, item => item.Value.Id,
                (previous, current) => RecordList.SameEntry(previous.Value, current.Value)
                    && previous.MissingImages.SequenceEqual(current.MissingImages),
                rendered != query, search);
            rendered = query;
            list.IsEnabled = true;
            list.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            count.Text = items.Count == 0 ? "" : kind switch
            {
                "todo" => $"{items.Count} 项待办",
                "memo" => $"{items.Count} 条备忘",
                _ => $"{items.Count} 条已删除的记录"
            };
            if (items.Count == 0) ShowEmpty(query);
        }
        catch (Exception ex)
        {
            refresh.RetryOnShow();
            if (IsVisible) Feedback("读取记录失败，" + ex.Message);
        }
    }

    private void ShowEmpty(Query query)
    {
        if (query.Text.Length > 0)
        {
            emptyTitle.Text = "没有找到匹配的记录";
            emptyHint.Text = kind == "todo" && query.Filter != 2
                ? "试试更短的关键词，或将待办状态切换为「全部」。"
                : "试试更短的关键词，或点击「清除」。";
            return;
        }

        (emptyTitle.Text, emptyHint.Text) = kind switch
        {
            "todo" when query.Filter == 1 => ("没有已完成的待办", "在待办上点击「标为完成」后，可在这里查看。"),
            "todo" when query.Filter == 0 => ("没有未完成的待办", "点击「新建待办」，或从剪贴记录的「操作」中加入待办。"),
            "todo" => ("还没有待办", "点击「新建待办」，或从剪贴记录的「操作」中加入待办。"),
            "memo" => ("还没有备忘", "点击「新建备忘」记下文字或图片，也可以从剪贴记录中保存备忘。"),
            _ => ("回收站为空", "移入回收站的剪贴、待办和备忘会显示在这里。")
        };
    }

    private void Feedback(string message)
    {
        status.Text = message;
        status.Visibility = Visibility.Visible;
        if (System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(status) is { } peer)
            peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private FrameworkElement BuildItem(CollectionItem item)
    {
        var entry = item.Value;
        var card = new StackPanel();
        if (kind == "trash") card.Children.Add(Ui.Text(KindName(entry.Kind), 11.5, Ui.Muted));

        var title = entry.Title;
        if (string.IsNullOrWhiteSpace(title))
            title = entry.Assets.Count > 0 ? (kind == "memo" ? "图片备忘" : "图片") : "";
        var body = entry.Body.Trim();
        if (title.Length > 0)
        {
            var label = Ui.Text(Ui.PreviewText(title, 120), 14.5);
            label.FontWeight = FontWeights.SemiBold;
            label.Margin = new Thickness(0, 1, 0, 1);
            if (entry.Kind == "todo" && entry.Done)
            {
                label.TextDecorations = TextDecorations.Strikethrough;
                label.Foreground = Ui.Muted;
            }
            if (kind == "todo")
            {
                var headRow = new DockPanel();
                var check = new CheckBox
                {
                    IsChecked = entry.Done,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 1, 6, 0),
                    ToolTip = entry.Done ? "恢复为未完成" : "标为完成"
                };
                check.Click += (_, e) =>
                {
                    e.Handled = true;
                    Host.Store.SetDone(entry.Id, !entry.Done);
                    Feedback(entry.Done ? "已恢复为未完成" : "已标为完成");
                };
                DockPanel.SetDock(check, Dock.Left);
                headRow.Children.Add(check);
                headRow.Children.Add(label);
                card.Children.Add(headRow);
            }
            else
            {
                card.Children.Add(label);
            }
        }
        if (body.Length > 0 && body != title)
        {
            var preview = Ui.Text(Ui.PreviewText(body, 260), 13, title.Length == 0 ? Ui.Ink : Ui.Muted);
            preview.MaxHeight = 56;
            preview.Margin = new Thickness(0, 1, 0, 2);
            preview.TextTrimming = TextTrimming.CharacterEllipsis;
            card.Children.Add(preview);
        }

        if (entry.Kind == "todo")
        {
            var metaRow = new WrapPanel { Margin = new Thickness(0, 1, 0, 1) };
            if (!string.IsNullOrWhiteSpace(entry.Due) &&
                DateTime.TryParseExact(entry.Due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDate))
            {
                if (!entry.Done && dueDate.Date < DateTime.Today)
                    metaRow.Children.Add(Ui.Badge("已逾期 · " + entry.Due, Ui.Danger, Ui.DangerSoft));
                else if (!entry.Done && dueDate.Date == DateTime.Today)
                    metaRow.Children.Add(Ui.Badge("今天到期 · " + entry.Due, Ui.Warning, Ui.WarningSoft));
                else if (!entry.Done && dueDate.Date == DateTime.Today.AddDays(1))
                    metaRow.Children.Add(Ui.Badge("明天到期 · " + entry.Due, Ui.Accent, Ui.AccentSoft));
                else
                    metaRow.Children.Add(Ui.Text((entry.Done ? "已完成" : "未完成") + " · 到期 " + entry.Due, 11.5, Ui.Muted));
            }
            else
            {
                metaRow.Children.Add(Ui.Text((entry.Done ? "已完成" : "未完成") + (entry.Due == null ? "" : " · 到期 " + entry.Due), 11.5, Ui.Muted));
            }
            card.Children.Add(metaRow);
        }

        if (entry.Assets.Count > 0)
        {
            var previews = new WrapPanel();
            foreach (var id in entry.Assets.Take(2))
            {
                if (item.MissingImages.Contains(id)) continue;
                var image = Ui.Thumbnail(Host.Store.Assets, id, 180, 62);
                image.MaxWidth = 112;
                image.Margin = new Thickness(0, 2, 8, 2);
                previews.Children.Add(image);
            }
            if (previews.Children.Count > 0) card.Children.Add(previews);
            if (entry.Assets.Count > 2) card.Children.Add(Ui.Text($"共 {entry.Assets.Count} 张图片", 11.5, Ui.Muted));
            if (item.MissingImages.Length > 0) card.Children.Add(Ui.Text($"{item.MissingImages.Length} 张图片待下载", 11.5, Ui.Muted));
        }

        var controls = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        var menu = CreateItemMenu();
        if (kind == "trash")
        {
            AddAction(controls, Ui.Button("恢复", () =>
            {
                Host.Store.Restore(entry.Id);
                Feedback("已恢复到" + KindName(entry.Kind) + (entry.Kind == "todo" && entry.Done ? "的「已完成」列表" : ""));
            }, true), "Restore");
        }
        else
        {
            if (kind == "todo")
            {
                var complete = Ui.Button(entry.Done ? "恢复为未完成" : "标为完成", () =>
                {
                    Host.Store.SetDone(entry.Id, !entry.Done);
                    Feedback(entry.Done ? "已恢复为未完成" : "已标为完成");
                }, !entry.Done);
                AutomationProperties.SetName(complete, $"{complete.Content} {Ui.PreviewText(entry.Title, 70)}");
                AddAction(controls, complete, "Complete");
            }
            AddAction(controls, Ui.Button("编辑", () => Host.Edit(Host.Store.Get(entry.Id) ?? entry), kind == "memo"), "Edit");
            var hasText = body.Length > 0 || !string.IsNullOrWhiteSpace(entry.Title);
            if (hasText)
            {
                if (kind == "memo") AddAction(controls, Ui.AsyncButton("复制文字", () => Copy(entry, plain: true)), "CopyText");
                else AddMenuAction(menu, "复制文字", () => Copy(entry, plain: true));
            }
            if (entry.Assets.Count > 0)
            {
                var imageReady = !item.MissingImages.Contains(entry.Assets[0]);
                if (kind == "memo" && !hasText)
                {
                    var copyImage = Ui.AsyncButton("复制原图", () => Copy(entry, plain: false));
                    copyImage.IsEnabled = imageReady;
                    copyImage.ToolTip = ImageCopyHint(entry, imageReady);
                    ToolTipService.SetShowOnDisabled(copyImage, true);
                    AddAction(controls, copyImage, "CopyImage");
                }
                else
                {
                    var copyImage = AddMenuAction(menu, "复制原图", () => Copy(entry, plain: false));
                    copyImage.IsEnabled = imageReady;
                    copyImage.ToolTip = ImageCopyHint(entry, imageReady);
                    ToolTipService.SetShowOnDisabled(copyImage, true);
                }
            }
        }
        if (item.MissingImages.Length > 0)
        {
            var download = Ui.Button("下载图片", () =>
            {
                if (Host.Store.Settings.Sync.Enabled)
                {
                    Host.RequestSync();
                    Feedback("已请求同步");
                }
                else
                {
                    Feedback("开启同步后可以下载图片");
                    Host.ShowSettings();
                }
            });
            download.ToolTip = "下载图片需要开启同步";
            AddAction(controls, download, "Sync");
        }
        AddMenuAction(menu, "详情和版本", () => { Host.Details(Host.Store.Get(entry.Id) ?? entry); return Task.CompletedTask; });
        if (kind != "trash")
        {
            AddMenuAction(menu, "移入回收站", () =>
            {
                Host.Store.Delete(entry.Id);
                Feedback("已移入回收站。可从剪贴记录的「更多」打开回收站。");
                return Task.CompletedTask;
            });
        }
        var actions = new Button { Content = "操作", ContextMenu = menu, ToolTip = "查看更多条目操作" };
        actions.Click += (_, e) =>
        {
            e.Handled = true;
            openMenu = menu;
            menu.PlacementTarget = actions;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        AddAction(controls, actions, "Actions");
        card.Children.Add(controls);
        var border = Ui.Card(card);
        border.Padding = new Thickness(9, 6, 9, 6);
        return border;
    }

    private ContextMenu CreateItemMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => openMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(openMenu, menu)) openMenu = null;
            RequestAutoHide();
        };
        return menu;
    }

    private MenuItem AddMenuAction(ContextMenu menu, string label, Func<Task> action)
    {
        var item = new MenuItem { Header = label };
        item.Click += async (_, e) =>
        {
            e.Handled = true;
            if (!item.IsEnabled) return;
            item.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { Feedback("操作未完成，" + ex.Message); }
            finally { item.IsEnabled = true; }
        };
        menu.Items.Add(item);
        return item;
    }

    private static string ImageCopyHint(Entry entry, bool imageReady) => !imageReady
        ? "图片待下载，完成同步后可复制"
        : entry.Assets.Count > 1 ? "复制第一张图片的原图" : "复制图片原图";

    private async Task Copy(Entry entry, bool plain)
    {
        copiesInProgress++;
        try
        {
            var full = await Task.Run(() => Host.Store.Get(entry.Id) ?? entry);
            await Host.Clipboard.Copy(full, plain);
            Feedback(plain ? "文字已复制" : "原图已复制");
        }
        finally
        {
            copiesInProgress--;
        }
    }

    private static void AddAction(Panel panel, Button button, string id)
    {
        button.Padding = new Thickness(8, 3, 8, 3);
        button.FontSize = 12;
        AutomationProperties.SetAutomationId(button, id);
        panel.Children.Add(button);
    }

    private static string KindName(string kind) => kind switch
    {
        "todo" => "待办",
        "memo" => "备忘",
        "trash" => "回收站",
        _ => "剪贴记录"
    };

    private readonly record struct Query(string Text, int Filter);

    private sealed class CollectionItem(CollectionWindow owner, Entry value, string[] missingImages)
    {
        internal CollectionWindow Owner { get; } = owner;
        internal Entry Value { get; } = value;
        internal string[] MissingImages { get; } = missingImages;
    }

    private sealed class ItemView : Decorator
    {
        private CollectionItem? rendered;
        public ItemView()
        {
            DataContextChanged += (_, e) =>
            {
                if (ReferenceEquals(rendered, e.NewValue)) return;
                rendered = e.NewValue as CollectionItem;
                Child = rendered != null ? rendered.Owner.BuildItem(rendered) : null;
            };
        }
    }
}

internal static class RecordList
{
    internal static ListBox Create(Type itemView, string name)
    {
        var list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(itemView) }
        };
        var itemTemplate = new ControlTemplate(typeof(ListBoxItem));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        itemTemplate.VisualTree = presenter;

        var containerStyle = new Style(typeof(ListBoxItem));
        containerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        containerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        containerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        containerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        containerStyle.Setters.Add(new Setter(Control.TemplateProperty, itemTemplate));
        containerStyle.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        list.ItemContainerStyle = containerStyle;
        KeyboardNavigation.SetTabNavigation(list, KeyboardNavigationMode.Continue);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(list, name);
        return list;
    }

    internal static bool SameEntry(Entry previous, Entry current) =>
        previous.UpdatedAt == current.UpdatedAt && previous.Assets.SequenceEqual(current.Assets)
            && previous == (current with { Assets = previous.Assets });

    internal static ObservableCollection<T> Update<T>(ListBox list, ObservableCollection<T> current, List<T> next,
        Func<T, string> key, Func<T, T, bool> retain, bool reset, TextBox fallback) where T : class
    {
        var selectedId = list.SelectedItem is T selected ? key(selected) : null;
        var selectedIndex = list.SelectedIndex;
        var focused = list.IsKeyboardFocusWithin ? Keyboard.FocusedElement as DependencyObject : null;
        var focusedRow = focused == null ? null : ItemsControl.ContainerFromElement(list, focused) as ListBoxItem;
        var focusedId = focusedRow?.Content is T focusedItem ? key(focusedItem) : null;
        var action = focused == null ? "" : AutomationProperties.GetAutomationId(focused);

        if (reset || current.Count == 0)
        {
            current = new ObservableCollection<T>(next);
            list.ItemsSource = current;
        }
        else
        {
            var nextIds = next.Select(key).ToHashSet(StringComparer.Ordinal);
            for (var i = current.Count - 1; i >= 0; i--)
                if (!nextIds.Contains(key(current[i]))) current.RemoveAt(i);

            var existing = current.ToDictionary(key, StringComparer.Ordinal);
            for (var i = 0; i < next.Count; i++)
            {
                var item = next[i];
                if (!existing.TryGetValue(key(item), out var previous))
                {
                    current.Insert(i, item);
                    continue;
                }
                if (!ReferenceEquals(current[i], previous)) current.Move(current.IndexOf(previous), i);
                if (!retain(previous, item)) current[i] = item;
            }
        }

        if (current.Count == 0)
        {
            if (focused != null && Window.GetWindow(list)?.IsActive == true) fallback.Focus();
            return current;
        }

        var selection = reset ? current[0] : current.FirstOrDefault(item => key(item) == selectedId)
            ?? current[Math.Clamp(selectedIndex, 0, current.Count - 1)];
        if (!ReferenceEquals(list.SelectedItem, selection)) list.SelectedItem = selection;
        if (reset) list.ScrollIntoView(selection);

        if (focusedId != null && !ReferenceEquals(Keyboard.FocusedElement, focused) && Window.GetWindow(list)?.IsActive == true)
        {
            var row = current.FirstOrDefault(item => key(item) == focusedId) ?? selection;
            list.ScrollIntoView(row);
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem container)
                (FindAction(container, action) ?? container).Focus();
        }
        return current;
    }

    private static Control? FindAction(DependencyObject root, string id)
    {
        if (id.Length == 0) return null;
        if (root is Control control && control.IsEnabled && AutomationProperties.GetAutomationId(control) == id) return control;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindAction(VisualTreeHelper.GetChild(root, i), id) is { } found) return found;
        return null;
    }
}

internal sealed class RecordRefresh
{
    private readonly Window owner;
    private readonly Func<Task> refresh;
    private readonly Func<bool>? ready;
    private readonly DispatcherTimer timer;
    private bool pending = true, running, retryOnShow;
    private int posted;

    internal RecordRefresh(Window owner, Func<Task> refresh, Func<bool>? ready = null)
    {
        this.owner = owner;
        this.refresh = refresh;
        this.ready = ready;
        timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(75)
        };
        timer.Tick += (_, _) => _ = RunPending();
        owner.IsVisibleChanged += (_, _) =>
        {
            if (!owner.IsVisible)
            {
                timer.Stop();
                // An in-flight read may be discarded while the window is hidden.
                if (running) pending = true;
                return;
            }
            pending |= retryOnShow;
            retryOnShow = false;
            _ = RunPending();
        };
    }

    internal void Request()
    {
        if (!owner.Dispatcher.CheckAccess())
        {
            if (owner.Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref posted, 1) != 0) return;
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref posted, 0);
                Request();
            }));
            return;
        }
        pending = true;
        if (owner.IsVisible && !running) timer.Start();
    }

    internal void RetryOnShow() => retryOnShow = true;

    internal Task Warm() => RunPending(warm: true);

    private async Task RunPending(bool warm = false)
    {
        if (running || !pending || !owner.IsVisible && !warm)
        {
            timer.Stop();
            return;
        }
        if (ready?.Invoke() == false)
        {
            timer.Start();
            return;
        }
        timer.Stop();
        pending = false;
        running = true;
        try { await refresh(); }
        finally
        {
            running = false;
            if (pending && owner.IsVisible) timer.Start();
        }
    }
}
