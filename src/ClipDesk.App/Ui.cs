using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;

namespace ClipDesk.App;

internal static class Ui
{
    internal static readonly Brush Ink = MakeBrush(24, 34, 48);
    internal static readonly Brush Muted = MakeBrush(100, 116, 139);
    internal static readonly Brush Accent = MakeBrush(79, 70, 229);
    internal static readonly Brush AccentSoft = MakeBrush(244, 243, 255);
    internal static readonly Brush HoverBorder = MakeBrush(165, 180, 252);
    internal static readonly Brush Background = MakeBrush(246, 248, 252);
    internal static readonly Brush Line = MakeBrush(226, 232, 240);
    internal static readonly Brush Danger = MakeBrush(220, 38, 38);
    internal static readonly Brush DangerSoft = MakeBrush(254, 242, 242);
    internal static readonly Brush Warning = MakeBrush(217, 119, 6);
    internal static readonly Brush WarningSoft = MakeBrush(255, 251, 235);
    private static readonly Style CardBorderStyle = CreateCardStyle();
    private const int ThumbnailLimit = 128;
    private const long ThumbnailByteLimit = 32 * 1024 * 1024;
    private static readonly SemaphoreSlim thumbnailSlots = new(2);
    private static readonly Dictionary<(string Path, int Width, int Height), (BitmapSource Bitmap, long Bytes, long Used)> thumbnails = [];
    private static long thumbnailBytes, thumbnailUse;

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Style CreateCardStyle()
    {
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.White));
        style.Setters.Add(new Setter(Border.BorderBrushProperty, Line));
        style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(8)));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, HoverBorder));
        style.Triggers.Add(hover);

        var selected = new DataTrigger
        {
            Binding = new Binding("IsSelected")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
            },
            Value = true
        };
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, Accent));
        selected.Setters.Add(new Setter(Border.BackgroundProperty, AccentSoft));
        style.Triggers.Add(selected);
        style.Seal();
        return style;
    }

    internal static void Theme(Application app)
    {
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 10, 5)));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        button.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        button.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2)));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new(RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new(RelativeSourceMode.TemplatedParent) });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(FrameworkElement.MarginProperty, new Binding("Padding") { RelativeSource = new(RelativeSourceMode.TemplatedParent) });
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Setters.Add(new Setter(Control.TemplateProperty, template));

        var focusRing = new FrameworkElementFactory(typeof(Rectangle));
        focusRing.SetValue(Shape.StrokeProperty, Ink);
        focusRing.SetValue(Shape.StrokeThicknessProperty, 2.0);
        focusRing.SetValue(Rectangle.RadiusXProperty, 7.0);
        focusRing.SetValue(Rectangle.RadiusYProperty, 7.0);
        focusRing.SetValue(FrameworkElement.MarginProperty, new Thickness(-2));
        var focusStyle = new Style(typeof(Control));
        focusStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = focusRing }));
        button.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, focusStyle));

        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Control.BorderBrushProperty, Accent));
        button.Triggers.Add(over);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45));
        button.Triggers.Add(disabled);
        app.Resources[typeof(Button)] = button;

        var input = new Style(typeof(TextBox));
        input.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 6, 9, 6)));
        input.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        input.Setters.Add(new Setter(Control.FontSizeProperty, 13.5));
        input.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        input.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        app.Resources[typeof(TextBox)] = input;

        var password = new Style(typeof(PasswordBox));
        password.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 6, 9, 6)));
        password.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        password.Setters.Add(new Setter(Control.FontSizeProperty, 13.5));
        password.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        password.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        app.Resources[typeof(PasswordBox)] = password;

        var combo = new Style(typeof(ComboBox));
        combo.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 3, 8, 3)));
        combo.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        combo.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
        combo.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        combo.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        app.Resources[typeof(ComboBox)] = combo;

        var tabItem = new Style(typeof(TabItem));
        tabItem.Setters.Add(new Setter(Control.FontSizeProperty, 13.5));
        tabItem.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 7, 14, 7)));
        tabItem.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        app.Resources[typeof(TabItem)] = tabItem;
    }
    internal static TextBlock Text(string text, double size = 13, Brush? color = null) => new() { Text = text, FontSize = size, Foreground = color ?? Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
    internal static Border Badge(string text, Brush foreground, Brush background) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(6, 1.5, 6, 1.5),
        Margin = new Thickness(0, 1, 6, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center
        }
    };
    internal static Button Button(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text }; if (primary) { b.Background = Accent; b.Foreground = Brushes.White; b.BorderBrush = Accent; }
        b.Click += (_, e) => { e.Handled = true; try { click(); } catch (Exception ex) { Error(ex.Message); } }; return b;
    }
    internal static Button AsyncButton(string text, Func<Task> click, bool primary = false)
    {
        var b = new Button { Content = text }; if (primary) { b.Background = Accent; b.Foreground = Brushes.White; b.BorderBrush = Accent; }
        b.Click += async (_, e) => { e.Handled = true; if (!b.IsEnabled) return; b.IsEnabled = false; try { await click(); } catch (Exception ex) { Error(ex.Message); } finally { b.IsEnabled = true; } }; return b;
    }
    private static Window? ActiveWindow() => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w.IsActive);
    internal static void Error(string text)
    {
        var owner = ActiveWindow();
        if (owner != null) MessageBox.Show(owner, text, "ClipDesk", MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(text, "ClipDesk", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    internal static bool Confirm(string text)
    {
        var owner = ActiveWindow();
        return (owner == null ? MessageBox.Show(text, "ClipDesk", MessageBoxButton.OKCancel, MessageBoxImage.Question) : MessageBox.Show(owner, text, "ClipDesk", MessageBoxButton.OKCancel, MessageBoxImage.Question)) == MessageBoxResult.OK;
    }
    internal static void PresentTransient(Window window)
    { var owner = ActiveWindow(); if (owner != null) { window.Owner = owner; window.Topmost = owner.Topmost; } window.Show(); }
    internal static StackPanel Row(params UIElement[] children) { var p = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var c in children) p.Children.Add(c); return p; }
    internal static Border Card(UIElement child) => new() { Child = child, Style = CardBorderStyle, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 2.5, 0, 2.5) };
    internal static TextBox Input(string value = "", bool multi = false) => new() { Text = value, AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = multi ? 130 : 34, Margin = new Thickness(0, 3, 0, 6) };
    internal static Grid SearchBox(TextBox input, string placeholder = "搜索文字、链接或标题")
    {
        input.MinHeight = 32;
        input.Margin = new Thickness(0, 2, 0, 2);
        input.Padding = new Thickness(9, 4, 9, 4);
        input.VerticalContentAlignment = VerticalAlignment.Center;
        var panel = new Grid();
        panel.Children.Add(input);
        var hint = Text(placeholder, 12.5, Muted);
        hint.Margin = new Thickness(11, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
        if (string.IsNullOrEmpty(AutomationProperties.GetName(input)))
            AutomationProperties.SetName(input, placeholder);
        panel.Children.Add(hint);
        void UpdateHint() => hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) => UpdateHint();
        UpdateHint();
        return panel;
    }
    internal static void Field(Panel panel, string label, UIElement input)
    {
        var caption = Text(label, 12, Muted);
        AutomationProperties.SetLabeledBy(input, caption);
        panel.Children.Add(caption);
        panel.Children.Add(input);
    }
    internal static string PreviewText(string text, int length)
    {
        if (text.Length <= length) return text;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;
        return text[..length] + "…";
    }
    internal static FrameworkElement Thumbnail(AssetStore assets, string id, int width, double maxHeight)
    {
        // Reserve the same space during loading so images do not move the row under a click.
        var holder = new Grid { Height = maxHeight };
        var status = Text("正在加载图片…", 12, Muted);
        status.VerticalAlignment = VerticalAlignment.Center;
        var image = new Image
        {
            MaxWidth = width,
            MaxHeight = maxHeight,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(image, "图片附件");
        holder.Children.Add(status);
        holder.Children.Add(image);
        CancellationTokenSource? request = null;

        holder.Loaded += async (_, _) =>
        {
            if (request != null || image.Source != null) return;
            var current = new CancellationTokenSource();
            request = current;
            var dpi = VisualTreeHelper.GetDpi(holder);
            var pixelWidth = (int)Math.Ceiling(width * Math.Min(2, dpi.DpiScaleX));
            var pixelHeight = (int)Math.Ceiling(maxHeight * Math.Min(2, dpi.DpiScaleY));
            try
            {
                await thumbnailSlots.WaitAsync(current.Token);
                BitmapSource bitmap;
                try
                {
                    bitmap = await Task.Run(() => ReadThumbnail(assets.PathFor(id), pixelWidth, pixelHeight), current.Token);
                }
                finally { thumbnailSlots.Release(); }
                if (current.IsCancellationRequested) return;
                image.Source = bitmap;
                image.Visibility = Visibility.Visible;
                status.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested) { }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (!current.IsCancellationRequested) status.Text = "图片待下载 · 同步后可用";
            }
            catch (Exception)
            {
                if (!current.IsCancellationRequested) status.Text = "图片读取失败";
            }
            finally
            {
                if (ReferenceEquals(request, current)) request = null;
                current.Dispose();
            }
        };
        holder.Unloaded += (_, _) =>
        {
            request?.Cancel();
            request = null;
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
            status.Text = "正在加载图片…";
            status.Visibility = Visibility.Visible;
        };
        return holder;
    }

    private static BitmapSource ReadThumbnail(string path, int width, int height)
    {
        var key = (path, width, height);
        lock (thumbnails)
        {
            if (thumbnails.TryGetValue(key, out var cached))
            {
                thumbnails[key] = (cached.Bitmap, cached.Bytes, ++thumbnailUse);
                return cached.Bitmap;
            }
        }

        using var file = File.OpenRead(path);
        var frame = BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand).Frames[0];
        int originalWidth = frame.PixelWidth, originalHeight = frame.PixelHeight;
        if ((long)originalWidth * originalHeight > 64_000_000)
            throw new InvalidDataException("图片超过 6400 万像素");
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (originalWidth > width || originalHeight > height)
        {
            var scale = Math.Min((double)width / originalWidth, (double)height / originalHeight);
            bitmap.DecodePixelWidth = Math.Clamp((int)Math.Round(originalWidth * scale), 1, width);
            bitmap.DecodePixelHeight = Math.Clamp((int)Math.Round(originalHeight * scale), 1, height);
        }
        file.Position = 0;
        bitmap.StreamSource = file;
        bitmap.EndInit();
        bitmap.Freeze();

        var bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(4, (bitmap.Format.BitsPerPixel + 7) / 8);
        lock (thumbnails)
        {
            if (thumbnails.TryGetValue(key, out var existing))
            {
                thumbnails[key] = (existing.Bitmap, existing.Bytes, ++thumbnailUse);
                return existing.Bitmap;
            }
            if (bytes > ThumbnailByteLimit) return bitmap;
            while (thumbnails.Count >= ThumbnailLimit || thumbnailBytes + bytes > ThumbnailByteLimit)
            {
                var oldest = thumbnails.MinBy(pair => pair.Value.Used);
                thumbnails.Remove(oldest.Key);
                thumbnailBytes -= oldest.Value.Bytes;
            }
            thumbnails.Add(key, (bitmap, bytes, ++thumbnailUse));
            thumbnailBytes += bytes;
        }
        return bitmap;
    }
    internal static bool FromButton(DependencyObject? obj)
    { while (obj != null) { if (obj is ButtonBase) return true; obj = obj is Visual ? VisualTreeHelper.GetParent(obj) : null; } return false; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out ScreenRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenRect
    {
        public int Left, Top, Right, Bottom;
    }

    internal static (int X, int Y)? ScreenPosition(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != 0 && GetWindowRect(hwnd, out var rect) ? (rect.Left, rect.Top) : null;
    }
    internal static void MoveToScreenPosition(Window window, int x, int y)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        const uint noSize = 0x0001, noZOrder = 0x0004, noActivate = 0x0010;
        if (hwnd != 0) Native.SetWindowPos(hwnd, 0, x, y, 0, 0, noSize | noZOrder | noActivate);
    }
    internal static void MakeVisible(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        const uint noZOrder = 0x0004, noActivate = 0x0010;
        // A DPI change can resize the window during the move. Clamp once more using the new bounds.
        for (var pass = 0; pass < 2; pass++)
        {
            if (!GetWindowRect(hwnd, out var rect)) return;
            var work = Screen.FromHandle(hwnd).WorkingArea;
            var oldWidth = rect.Right - rect.Left;
            var oldHeight = rect.Bottom - rect.Top;
            var width = Math.Min(oldWidth, work.Width);
            var height = Math.Min(oldHeight, work.Height);
            if (width <= 0 || height <= 0) return;
            var x = Math.Clamp(rect.Left, work.Left, work.Right - width);
            var y = Math.Clamp(rect.Top, work.Top, work.Bottom - height);
            if (x == rect.Left && y == rect.Top && width == oldWidth && height == oldHeight) return;
            if (!Native.SetWindowPos(hwnd, 0, x, y, width, height, noZOrder | noActivate)) return;
        }
    }
    internal static void PositionNewPanel(Window window, string key)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !GetWindowRect(hwnd, out var rect)) return;
        var work = Screen.FromHandle(hwnd).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        var marginX = (int)Math.Round(14 * dpi.DpiScaleX);
        var marginY = (int)Math.Round(14 * dpi.DpiScaleY);
        var x = key == "pinned" ? work.Left + marginX : work.Right - (rect.Right - rect.Left) - marginX;
        var y = key == "todo" ? work.Bottom - (rect.Bottom - rect.Top) - marginY : work.Top + marginY;
        MoveToScreenPosition(window, x, y);
    }
}

internal class DeskWindow : Window
{
    private const double MinimumOpacity = .45;
    private const double CaptionBarHeight = 34, ResizeBorderSize = 6;
    private static readonly ControlTemplate captionButtonTemplate = CreateCaptionButtonTemplate();
    protected readonly App Host;
    private readonly DispatcherTimer layoutTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool closePermanently, defaultPanelPosition, optionsOpen, autoHideEnabled, autoHideQueued, temporaryTopmost;
    private Func<bool>? suppressAutoHide;
    private (int X, int Y)? startupPosition;
    private double idleOpacity;

    internal string LayoutKey { get; }
    internal bool KeepVisible { get; private set; }

    internal DeskWindow(App host, string key, string title, double width = 620)
    {
        Host = host;
        LayoutKey = key;
        Title = title;
        var panel = key is "todo" or "memo" or "pinned";
        Width = width;
        Height = panel ? 330 : 720;
        MinWidth = panel ? 300 : 420;
        MinHeight = key == "pinned" ? 320 : panel ? 250 : 400;
        Background = Ui.Background;
        Foreground = Ui.Ink;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 13;
        if (key == "clipboard" || panel) ConfigurePanelChrome();

        var layout = host.Store.Settings.Windows.GetValueOrDefault(key);
        KeepVisible = layout?.KeepVisible ?? (key != "clipboard");
        Topmost = layout?.Topmost ?? (key == "clipboard");
        idleOpacity = NormalizeOpacity(layout?.Opacity ?? 1);
        if (layout != null)
        {
            Left = layout.Left;
            Top = layout.Top;
            Width = Math.Max(MinWidth, layout.Width);
            Height = Math.Max(MinHeight, layout.Height);
            if (layout.ScreenX is int x && layout.ScreenY is int y) startupPosition = (x, y);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            defaultPanelPosition = panel;
        }
        SourceInitialized += (_, _) =>
        {
            if (startupPosition is { } position) Ui.MoveToScreenPosition(this, position.X, position.Y);
        };
        Closing += (_, e) =>
        {
            layoutTimer.Stop();
            if (host.Exiting || closePermanently) return;
            e.Cancel = true;
            Dismiss();
        };
        layoutTimer.Tick += (_, _) =>
        {
            layoutTimer.Stop();
            if (!Host.Exiting)
            {
                try { Persist(IsVisible); }
                catch (Exception) { Host.Notify("窗口位置和显示状态未能保存"); }
            }
        };
        LocationChanged += (_, _) => ScheduleLayoutSave();
        SizeChanged += (_, _) => ScheduleLayoutSave();
        Activated += (_, _) => UpdateOpacity(animate: true);
        Deactivated += (_, _) =>
        {
            RestoreTopmostPreference();
            UpdateOpacity(animate: true);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) RestoreTopmostPreference();
        };
        MouseEnter += (_, _) => UpdateOpacity(animate: true);
        MouseLeave += (_, _) => UpdateOpacity(animate: true);
    }

    private void ConfigurePanelChrome()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            // The caption starts below the top resize border.
            CaptionHeight = CaptionBarHeight - ResizeBorderSize,
            ResizeBorderThickness = new Thickness(ResizeBorderSize),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });

        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetBinding(Border.BackgroundProperty, new Binding(nameof(Background)) { RelativeSource = RelativeSource.TemplatedParent });
        frame.SetValue(Border.BorderBrushProperty, Ui.Line);
        frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var layout = new FrameworkElementFactory(typeof(DockPanel));
        frame.AppendChild(layout);

        var caption = new FrameworkElementFactory(typeof(DockPanel));
        caption.SetValue(DockPanel.DockProperty, Dock.Top);
        caption.SetValue(FrameworkElement.HeightProperty, CaptionBarHeight);
        caption.SetValue(Panel.BackgroundProperty, Brushes.White);
        caption.AppendChild(CaptionButton("ClosePanel", "×", "关闭窗口", (_, _) => SystemCommands.CloseWindow(this)));
        caption.AppendChild(CaptionButton("MaximizePanel", "□", "最大化窗口", (_, _) =>
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }));
        caption.AppendChild(CaptionButton("MinimizePanel", "−", "最小化窗口", (_, _) => SystemCommands.MinimizeWindow(this)));

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { RelativeSource = RelativeSource.TemplatedParent });
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 6, 0));
        caption.AppendChild(label);
        layout.AppendChild(caption);
        layout.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

        var template = new ControlTemplate(typeof(Window)) { VisualTree = frame };
        var maximized = new Trigger { Property = WindowStateProperty, Value = WindowState.Maximized };
        maximized.Setters.Add(new Setter(ContentControl.ContentProperty, "❐", "MaximizePanel"));
        maximized.Setters.Add(new Setter(AutomationProperties.NameProperty, "还原窗口", "MaximizePanel"));
        maximized.Setters.Add(new Setter(ToolTipProperty, "还原窗口", "MaximizePanel"));
        template.Triggers.Add(maximized);
        Template = template;
    }

    private static ControlTemplate CreateCaptionButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "CaptionButtonBackground");
        border.SetValue(Border.BackgroundProperty, Brushes.White);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Ui.Background, "CaptionButtonBackground"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, Ui.Line, "CaptionButtonBackground"));
        template.Triggers.Add(pressed);
        return template;
    }

    private static FrameworkElementFactory CaptionButton(string id, string glyph, string name, RoutedEventHandler click)
    {
        var button = new FrameworkElementFactory(typeof(Button), id);
        button.SetValue(ContentControl.ContentProperty, glyph);
        button.SetValue(Control.TemplateProperty, captionButtonTemplate);
        button.SetValue(FrameworkElement.WidthProperty, 36.0);
        button.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 4, 3, 3));
        button.SetValue(ToolTipProperty, name);
        button.SetValue(AutomationProperties.NameProperty, name);
        button.SetValue(WindowChrome.IsHitTestVisibleInChromeProperty, true);
        button.SetValue(DockPanel.DockProperty, Dock.Right);
        button.AddHandler(ButtonBase.ClickEvent, click);
        return button;
    }

    internal void Open(bool activate = true)
    {
        if (!IsVisible)
        {
            ShowActivated = activate;
            Show();
        }
        if (activate && WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (startupPosition is { } position)
        {
            Ui.MoveToScreenPosition(this, position.X, position.Y);
            startupPosition = null;
        }
        else if (defaultPanelPosition)
        {
            Ui.PositionNewPanel(this, LayoutKey);
            defaultPanelPosition = false;
        }
        Ui.MakeVisible(this);
        if (activate)
        {
            Activate();
            LiftAbovePanels();
        }
        UpdateOpacity();
        if (LayoutKey == "clipboard") ScheduleLayoutSave();
        else Persist(true);
    }

    private void LiftAbovePanels()
    {
        var anotherPanelIsTopmost = Application.Current?.Windows.OfType<DeskWindow>().Any(window =>
            window != this && window.IsVisible && window.Topmost
            && (window.LayoutKey is "clipboard" or "todo" or "memo" or "pinned")) == true;
        if (!Topmost && (!IsActive || !anotherPanelIsTopmost)) return;
        if (PositionInTopmostOrder(new nint(-1)) && !Topmost) temporaryTopmost = true;
    }

    private void RestoreTopmostPreference()
    {
        if (!temporaryTopmost) return;
        temporaryTopmost = false;
        if (!Topmost) PositionInTopmostOrder(new nint(-2));
    }

    private bool PositionInTopmostOrder(nint after)
    {
        const uint noMove = 0x0002, noSize = 0x0001, noActivate = 0x0010;
        var hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != 0 && Native.SetWindowPos(hwnd, after, 0, 0, 0, 0, noMove | noSize | noActivate);
    }

    internal void Dismiss()
    {
        layoutTimer.Stop();
        Hide();
        if (LayoutKey == "clipboard") layoutTimer.Start();
        else
        {
            try { Persist(false); }
            catch (Exception) { Host.Notify("窗口位置和显示状态未能保存"); }
        }
    }

    internal void ClosePermanently()
    {
        closePermanently = true;
        Close();
    }

    private void ScheduleLayoutSave()
    {
        if (!IsLoaded || Host.Exiting) return;
        layoutTimer.Stop();
        layoutTimer.Start();
    }

    internal void Persist(bool open)
    {
        var settings = Host.Store.Settings;
        var previous = settings.Windows.GetValueOrDefault(LayoutKey);
        WindowLayout next;
        if (WindowState != WindowState.Normal)
        {
            if (previous == null) return;
            next = previous with { Topmost = Topmost, Open = open, Opacity = idleOpacity, KeepVisible = KeepVisible };
        }
        else
        {
            if (!double.IsFinite(Left) || !double.IsFinite(Top)) return;
            var position = Ui.ScreenPosition(this);
            next = new WindowLayout(Left, Top, Width, Height, Topmost, open, position?.X, position?.Y, idleOpacity, KeepVisible);
        }
        if (next == previous) return;
        var windows = new Dictionary<string, WindowLayout>(settings.Windows) { [LayoutKey] = next };
        Host.Store.SaveSettings(settings with { Windows = windows });
    }

    protected void EnableAutoHide(Func<bool>? suppress = null)
    {
        suppressAutoHide = suppress;
        if (autoHideEnabled) return;
        autoHideEnabled = true;
        void OnDeactivation(object? sender, EventArgs e) => RequestAutoHide();
        Deactivated += OnDeactivation;
        if (Application.Current is { } application)
        {
            application.Deactivated += OnDeactivation;
            Closed += (_, _) => application.Deactivated -= OnDeactivation;
        }
    }

    protected void RequestAutoHide()
    {
        if (!autoHideEnabled || KeepVisible || autoHideQueued || Host.Exiting) return;
        autoHideQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            autoHideQueued = false;
            if (KeepVisible || !IsVisible || IsActive || Host.Exiting || optionsOpen || suppressAutoHide?.Invoke() == true
                || (LayoutKey != "clipboard" && OwnedWindows.OfType<Window>().Any(window => window.IsVisible && window.IsActive))) return;
            Dismiss();
        }));
    }

    protected Button WindowOptionsButton()
    {
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        var keep = new MenuItem { Header = "保持显示", IsCheckable = true, ToolTip = "切换到其他应用时继续显示" };
        keep.Click += (_, _) =>
        {
            KeepVisible = keep.IsChecked;
            Persist(IsVisible);
            RequestAutoHide();
        };
        var top = new MenuItem { Header = "始终最前", IsCheckable = true, ToolTip = "显示时保持在其他窗口前面" };
        top.Click += (_, _) =>
        {
            Topmost = top.IsChecked;
            Persist(IsVisible);
        };
        const string opacityHelp = "数值越小越透明，操作时恢复清晰";
        var opacity = new MenuItem { Header = OpacityCaption(), ToolTip = opacityHelp };
        AutomationProperties.SetHelpText(opacity, opacityHelp);
        var levels = new Dictionary<int, MenuItem>();
        for (var percent = 45; percent <= 100; percent += 5)
        {
            var value = percent;
            var level = new MenuItem { Header = value + "%", IsCheckable = true };
            level.Click += (_, _) => SetIdleOpacity(value / 100.0);
            levels.Add(value, level);
            opacity.Items.Add(level);
        }
        menu.Items.Add(keep);
        menu.Items.Add(top);
        if (AllowsTransparency) menu.Items.Add(opacity);
        menu.Opened += (_, _) =>
        {
            optionsOpen = true;
            keep.IsChecked = KeepVisible;
            top.IsChecked = Topmost;
            var currentPercent = (int)Math.Round(idleOpacity * 100);
            opacity.Header = OpacityCaption();
            foreach (var level in levels) level.Value.IsChecked = level.Key == currentPercent;
            UpdateOpacity();
        };
        menu.Closed += (_, _) =>
        {
            optionsOpen = false;
            UpdateOpacity();
            RequestAutoHide();
        };
        var button = new Button { Content = "窗口选项", ContextMenu = menu };
        button.Click += (_, e) =>
        {
            e.Handled = true;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        };
        return button;
    }

    private string OpacityCaption() => $"未激活时不透明度 · {(int)Math.Round(idleOpacity * 100)}%";

    private static double NormalizeOpacity(double value) => double.IsFinite(value) ? Math.Clamp(value, MinimumOpacity, 1) : 1;

    private void SetIdleOpacity(double value)
    {
        idleOpacity = NormalizeOpacity(value);
        UpdateOpacity();
        Persist(IsVisible);
    }

    private void UpdateOpacity(bool animate = false)
    {
        if (!AllowsTransparency) return;
        var target = IsActive || IsMouseOver || optionsOpen ? 1.0 : idleOpacity;
        if (!animate || !IsVisible || !IsLoaded)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = target;
            return;
        }
        if (Math.Abs(Opacity - target) < 0.01) return;
        var animation = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        BeginAnimation(OpacityProperty, animation);
    }

    protected CheckBox TopSwitch()
    {
        var box = new CheckBox { Content = "始终最前", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
        box.SetBinding(ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding(nameof(Topmost)) { Source = this, Mode = System.Windows.Data.BindingMode.TwoWay });
        box.Click += (_, _) => Persist(IsVisible);
        return box;
    }
}
