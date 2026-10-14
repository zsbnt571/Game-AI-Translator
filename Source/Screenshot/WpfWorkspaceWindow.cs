using System.ComponentModel;
using System.Runtime.CompilerServices;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WI = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

// The application keeps one existing service/session owner. Native WPF surfaces
// can replace its views without creating a second queue, settings store or API client.
internal static class WpfWorkspace
{
    internal static bool Enabled { get; set; }
    internal static void Run(MainForm main)
    {
        if (Environment.GetCommandLineArgs().Contains("--software-rendering", StringComparer.OrdinalIgnoreCase))
            WM.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Write("wpf-ui", "Unhandled desktop exception", e.Exception);
        };
        var window = new WpfWorkspaceWindow(main);
        System.Windows.Forms.Integration.WindowsFormsHost.EnableWindowsFormsInterop();
        app.Run(window);
    }
}

internal static class WpfDrawing
{
    private static readonly Dictionary<int, WM.SolidColorBrush> Brushes = [];
    private static readonly ConditionalWeakTable<Bitmap, WM.Imaging.BitmapSource> Images = new();
    private static readonly Dictionary<(string Key,int Color,int Size),WM.Imaging.BitmapSource> Icons=[];
    internal static WM.SolidColorBrush Brush(Color color)
    {
        int key = color.ToArgb();
        if (Brushes.TryGetValue(key, out var brush)) return brush;
        brush = new(WM.Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        if (Brushes.Count > 256) Brushes.Clear();
        return Brushes[key] = brush;
    }
    internal static WM.Imaging.BitmapSource Image(Bitmap bitmap) => Images.GetValue(bitmap, source =>
    {
        // One upload per bitmap lifetime; scrolling never encodes or rescales a cover.
        var rect = new Rectangle(0, 0, source.Width, source.Height);
        using var normalized = source.Clone(rect, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = normalized.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var image = WM.Imaging.BitmapSource.Create(source.Width, source.Height, 96, 96,
                WM.PixelFormats.Pbgra32, null, data.Scan0, data.Stride * source.Height, data.Stride);
            image.Freeze(); return image;
        }
        finally { normalized.UnlockBits(data); }
    });
    internal static WM.Imaging.BitmapSource Icon(string key, Color color, int size = 32)
    {
        var cacheKey=(key,color.ToArgb(),size);if(Icons.TryGetValue(cacheKey,out var cached))return cached;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap)) WorkspaceSkin.Icon(graphics, key, new(0, 0, size, size), color);
        if(Icons.Count>256)Icons.Clear();return Icons[cacheKey]=Image(bitmap);
    }
}

internal sealed class WpfWorkspaceWindow : W.Window
{
    private readonly MainForm main;
    private readonly System.Windows.Forms.Integration.WindowsFormsHost contentHost;
    private readonly WpfLibraryWorkspace library;
    private readonly WpfSettingsWorkspace settings;
    private readonly WpfScreenshotWorkspace screenshots;
    private readonly WC.Border border = new();
    private readonly WC.Grid caption = new() { Height = 40 };
    private readonly WC.StackPanel tabs = new() { Orientation = WC.Orientation.Horizontal };
    private readonly WC.Button theme = new(), minimize = new(), maximize = new(), close = new();
    private readonly Dictionary<string, (WC.Button Button, WC.Border Line)> navigation = [];
    private readonly WC.TextBlock product = new() { Text = "FUSION R1", FontFamily = new("Segoe UI"), FontSize = 12, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly WC.TextBlock version = new() { Text = BuildIdentity.BuildVersion, FontFamily = new("Segoe UI"), FontSize = 10.667, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly WC.Image brand = new() { Width = 24, Height = 24, VerticalAlignment = W.VerticalAlignment.Center };
    private bool legacyClosed, requestingClose;

    internal WpfWorkspaceWindow(MainForm main)
    {
        this.main = main;
        Resources.MergedDictionaries.Add(WpfThemeResources.Create());
        Title = main.Text;
        var workArea = W.SystemParameters.WorkArea;
        Width = Math.Min(1440, workArea.Width); Height = Math.Min(880, workArea.Height);
        MinWidth = Math.Min(900, Width); MinHeight = Math.Min(640, Height);
        WindowStartupLocation = W.WindowStartupLocation.CenterScreen;
        WindowStyle = W.WindowStyle.None;
        ResizeMode = W.ResizeMode.CanResize;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        WM.TextOptions.SetTextFormattingMode(this, WM.TextFormattingMode.Display);
        WM.TextOptions.SetTextRenderingMode(this, WM.TextRenderingMode.ClearType);
        W.Shell.WindowChrome.SetWindowChrome(this, new W.Shell.WindowChrome
        {
            CaptionHeight = 40, ResizeBorderThickness = new W.Thickness(5),
            GlassFrameThickness = new W.Thickness(0), CornerRadius = new W.CornerRadius(0),
            UseAeroCaptionButtons = false
        });
        SourceInitialized += (_, _) => System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(ConstrainMaximizedWindow);
        var root = new WC.Grid();
        root.RowDefinitions.Add(new() { Height = new W.GridLength(40) });
        root.RowDefinitions.Add(new() { Height = new W.GridLength(1, W.GridUnitType.Star) });
        border.BorderThickness = new W.Thickness(1); border.Child = root; Content = border;
        caption.ColumnDefinitions.Add(new() { Width = new W.GridLength(237) });
        caption.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        caption.ColumnDefinitions.Add(new() { Width = new W.GridLength(1, W.GridUnitType.Star) });
        caption.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        var identity = new WC.Canvas();
        identity.Children.Add(brand); WC.Canvas.SetLeft(brand, 8); WC.Canvas.SetTop(brand, 8);
        identity.Children.Add(product); WC.Canvas.SetLeft(product, 42); WC.Canvas.SetTop(product, 12);
        identity.Children.Add(version); WC.Canvas.SetLeft(version, 123); WC.Canvas.SetTop(version, 13);
        caption.Children.Add(identity);
        foreach (var pair in new[] { ("内嵌翻译", "游戏库"), ("主页", "截图翻译"), ("设置", "设置") })
        {
            var button = Button(pair.Item2, 88, () => main.WpfNavigate(pair.Item1));
            var line = new WC.Border { Height = 2, VerticalAlignment = W.VerticalAlignment.Bottom };
            var item = new WC.Grid(); item.Children.Add(button); item.Children.Add(line);
            line.IsHitTestVisible = false;
            tabs.Children.Add(item); navigation[pair.Item1] = (button, line);
        }
        WC.Grid.SetColumn(tabs, 1); caption.Children.Add(tabs);
        var commands = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        PrepareButton(theme, "切换主题", 44, main.WpfToggleTheme);
        PrepareButton(minimize, "最小化", 44, () => WindowState = W.WindowState.Minimized);
        PrepareButton(maximize, "最大化 / 还原", 44, () => WindowState = WindowState == W.WindowState.Maximized ? W.WindowState.Normal : W.WindowState.Maximized);
        PrepareButton(close, "关闭", 44, Close);
        commands.Children.Add(theme); commands.Children.Add(minimize); commands.Children.Add(maximize); commands.Children.Add(close);
        WC.Grid.SetColumn(commands, 3); caption.Children.Add(commands); root.Children.Add(caption);
        contentHost = new() { Focusable = true };
        var workspace=new WC.Grid();WC.Grid.SetRow(workspace,1);root.Children.Add(workspace);workspace.Children.Add(contentHost);
        main.AttachWpfWorkspace(this);
        contentHost.Child = main;
        main.WpfLibraryAttached();library=new(main);workspace.Children.Add(library);
        settings=new(main);workspace.Children.Add(settings);
        screenshots=new(main);workspace.Children.Add(screenshots);
        PreviewKeyDown += (_, e) => { if(e.Key==WI.Key.Escape && main.WpfShotEscape())e.Handled=true; };
        main.FormClosed += (_, _) => { legacyClosed = true; if (!requestingClose) Close(); };
        main.TextChanged += (_, _) => Title = main.Text;
        Loaded += (_, _) => { main.Show(); RefreshChrome(); RefreshWorkspace(); };
        Activated += (_, _) => { if (!main.IsDisposed) main.WpfWindowActivated(); };
        Deactivated += (_, _) => { if (!main.IsDisposed) main.WpfWindowDeactivated(); };
        StateChanged += (_, _) => { RefreshChrome(); if (WindowState != W.WindowState.Minimized) main.Invalidate(true); };
        Closed += (_, _) => { library.Dispose(); screenshots.Dispose(); settings.Dispose(); contentHost.Child = null; contentHost.Dispose(); if (!main.IsDisposed) main.Dispose(); };
        RefreshChrome();
    }
    private WC.Button Button(string text, double width, Action action)
    {
        var button = new WC.Button(); PrepareButton(button, text, width, action); return button;
    }
    private IntPtr ConstrainMaximizedWindow(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message!=0x0024)return IntPtr.Zero; // WM_GETMINMAXINFO
        var monitor=Screen.FromHandle(hwnd);var area=monitor.WorkingArea;var bounds=monitor.Bounds;
        var limits=System.Runtime.InteropServices.Marshal.PtrToStructure<WindowLimits>(lParam);
        limits.MaxPosition=new(area.Left-bounds.Left,area.Top-bounds.Top);
        limits.MaxSize=new(area.Width,area.Height);
        var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(this);
        limits.MinTrackSize=new((int)Math.Min(area.Width,MinWidth*dpi.DpiScaleX),(int)Math.Min(area.Height,MinHeight*dpi.DpiScaleY));
        System.Runtime.InteropServices.Marshal.StructureToPtr(limits,lParam,false);handled=true;return IntPtr.Zero;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WindowLimits { public Point Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize; }
    private static void PrepareButton(WC.Button button, string text, double width, Action action)
    {
        button.Content = text; button.Width = width; button.Height = 40;
        button.Padding = new W.Thickness(12, 0, 12, 0); button.BorderThickness = new W.Thickness(0);
        button.VerticalContentAlignment = W.VerticalAlignment.Center;
        button.HorizontalContentAlignment = W.HorizontalAlignment.Center;
        button.Cursor = WI.Cursors.Hand;
        var panel = new W.FrameworkElementFactory(typeof(WC.Border));
        panel.SetValue(WC.Border.BackgroundProperty, new W.TemplateBindingExtension(WC.Control.BackgroundProperty));
        var presenter = new W.FrameworkElementFactory(typeof(WC.ContentPresenter));
        presenter.SetValue(W.FrameworkElement.HorizontalAlignmentProperty, W.HorizontalAlignment.Center);
        presenter.SetValue(W.FrameworkElement.VerticalAlignmentProperty, W.VerticalAlignment.Center);
        panel.AppendChild(presenter);
        button.Template = new WC.ControlTemplate(typeof(WC.Button)) { VisualTree = panel };
        W.Automation.AutomationProperties.SetName(button, text);
        W.Shell.WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.MouseEnter += (_, _) => button.Background = WpfDrawing.Brush(UiTheme.Current.Control);
        button.MouseLeave += (_, _) => button.Background = WpfDrawing.Brush(UiTheme.Current.Main);
        button.Click += (_, _) => action();
    }
    internal void RefreshChrome()
    {
        if (main.IsDisposed) return;
        var p = UiTheme.Current;
        WpfThemeResources.Apply(Resources);
        Background = border.Background = WpfDrawing.Brush(p.Main);
        border.BorderBrush = WpfDrawing.Brush(p.Border);
        caption.Background = WpfDrawing.Brush(p.Main);
        FontFamily = new(main.Font.FontFamily.Name); FontSize = main.Font.SizeInPoints * 96 / 72;
        Foreground = product.Foreground = WpfDrawing.Brush(p.Text); version.Foreground = WpfDrawing.Brush(p.SecondaryText);
        brand.Source = WpfDrawing.Icon("brand", p.Accent);
        foreach (var pair in navigation)
        {
            var original = main.WpfNavigationButton(pair.Key);
            var button = pair.Value.Button;
            button.Content = original.Text;
            button.Width = Math.Max(88, TextRenderer.MeasureText(original.Text, original.Font).Width * 96d / main.DeviceDpi + 32);
            button.Background = WpfDrawing.Brush(p.Main);
            button.Foreground = WpfDrawing.Brush(main.WpfPage == pair.Key ? p.Accent : p.Text);
            pair.Value.Line.Background = WpfDrawing.Brush(main.WpfPage == pair.Key ? p.Accent : p.Border);
            W.Automation.AutomationProperties.SetName(button, original.Text);
        }
        foreach (var button in new[] { theme, minimize, maximize, close })
        { button.Background = WpfDrawing.Brush(p.Main); button.Foreground = WpfDrawing.Brush(p.SecondaryText); }
        void IconButton(WC.Button button, string icon) => button.Content = new WC.Image { Source = WpfDrawing.Icon(icon, p.SecondaryText), Width = 19, Height = 19 };
        IconButton(theme, p.Main.GetBrightness() < .5f ? "sun" : "moon");
        minimize.Content = "−"; maximize.Content = WindowState == W.WindowState.Maximized ? "❐" : "□"; IconButton(close, "close");
        RefreshWorkspace();
    }
    internal void RefreshWorkspace()
    {
        if(library is null)return;
        bool game=main.WpfPage=="内嵌翻译", configuration=main.WpfPage=="设置", shot=main.WpfPage=="主页";
        contentHost.Visibility=game||configuration||shot?W.Visibility.Collapsed:W.Visibility.Visible;
        library.Visibility=game?W.Visibility.Visible:W.Visibility.Collapsed;
        if(settings is not null) { settings.Visibility=configuration?W.Visibility.Visible:W.Visibility.Collapsed; if(configuration)settings.Refresh(); }
        if(screenshots is not null) { screenshots.Visibility=shot?W.Visibility.Visible:W.Visibility.Collapsed; if(shot)screenshots.Refresh(); }
        if(game)library.Refresh();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!legacyClosed)
        {
            requestingClose = true;
            try { main.Close(); } finally { requestingClose = false; }
            e.Cancel = !legacyClosed;
        }
        base.OnClosing(e);
    }
}

public sealed partial class MainForm
{
    private WpfWorkspaceWindow? _wpfWindow;
    internal string WpfPage => _currentMainPage;
    internal bool DesktopVisible => _wpfWindow is { } window ? window.IsVisible && window.WindowState!=W.WindowState.Minimized : Visible;
    internal bool DesktopContainsFocus => _wpfWindow?.IsActive ?? ContainsFocus;
    internal Button WpfNavigationButton(string page) => _navigationButtons[page];
    internal void WpfNavigate(string page) => NavigateWorkspace(page);
    private void NavigateWorkspace(string page)
    {
        ShowPage(page);
        if (_currentMainPage != page) return;
        if (page != "设置") ClearProfileReturn();
        if (page == "内嵌翻译") ShowLibraryHome();
    }
    internal void WpfToggleTheme() => ToggleTheme();
    internal void WpfWindowActivated() => ValidateUiInvariants("WpfActivate");
    internal void WpfWindowDeactivated() => CancelBindingCapture("WindowDeactivated");
    internal void AttachWpfWorkspace(WpfWorkspaceWindow window)
    {
        _wpfWindow = window;
        TopLevel = false; FormBorderStyle = FormBorderStyle.None;
        MinimumSize = Size.Empty; Padding = Padding.Empty; Dock = DockStyle.Fill;
        if (_workspaceCaption?.Parent is TableLayoutPanel frame)
        { _workspaceCaption.Visible = false; frame.RowStyles[0].Height = 0; }
    }
    public new void Hide() { base.Hide(); _wpfWindow?.Hide(); }
    public new void Show() { base.Show(); if (_wpfWindow is { IsVisible: false }) _wpfWindow.Show(); }
    public new void Activate() { if (_wpfWindow is null) base.Activate(); else _wpfWindow.Activate(); }
    public new void BringToFront() { if (_wpfWindow is null) base.BringToFront(); else _wpfWindow.Activate(); }
    public new bool ShowInTaskbar
    { get => _wpfWindow?.ShowInTaskbar ?? base.ShowInTaskbar; set { if (_wpfWindow is null) base.ShowInTaskbar = value; else _wpfWindow.ShowInTaskbar = value; } }
    public new FormWindowState WindowState
    {
        get => _wpfWindow is null ? base.WindowState : (FormWindowState)(int)_wpfWindow.WindowState;
        set { if (_wpfWindow is null) base.WindowState = value; else _wpfWindow.WindowState = (W.WindowState)(int)value; }
    }
}
