using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfGameSessionWorkspace : WC.ScrollViewer, IDisposable
{
    private readonly MainForm main;
    private readonly WpfSettingsFields fields = new() { UseModelDimensions = true };
    private readonly WC.StackPanel content = new() { Margin = new(24, 14, 24, 16) };
    private readonly List<System.Windows.Forms.Integration.WindowsFormsHost> modules = [];
    private readonly WC.Image artwork = new() { Stretch = WM.Stretch.UniformToFill };
    private readonly WC.TextBlock title = new() { FontSize = 26.667, FontWeight = W.FontWeights.Bold, Foreground = WM.Brushes.White, TextTrimming = W.TextTrimming.CharacterEllipsis, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly WC.TextBlock subtitle = new() { FontSize = 18.667, Foreground = WM.Brushes.LightGray, TextTrimming = W.TextTrimming.CharacterEllipsis };
    private bool refreshing;

    internal WpfGameSessionWorkspace(MainForm main)
    {
        this.main = main; main.WpfGameSessionView = this;
        Content = content; VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled;
        foreach (var model in main.WpfSessionRows)
        {
            W.FrameworkElement view = ReferenceEquals(model, main.WpfSessionHero) ? Hero()
                : ReferenceEquals(model, main.WpfSessionTranslation) ? Translation((TableLayoutPanel)model)
                : ReferenceEquals(model, main.WpfSessionStatusRow) ? Status((TableLayoutPanel)model)
                : main.WpfSessionModules.Contains(model) ? Module(model)
                : fields.Create(model);
            view.Margin = new(0, 0, 0, 12); fields.Bind(model, view); content.Children.Add(view);
        }
        SizeChanged += (_, _) => { foreach (var module in modules) module.Height = Math.Max(430, ActualHeight - 115); };
    }
    private W.FrameworkElement Hero()
    {
        var grid = new WC.Grid { Height = 166, ClipToBounds = true }; grid.Children.Add(artwork);
        grid.Children.Add(new WC.Border { Background = new WM.LinearGradientBrush(WM.Color.FromArgb(5, 0, 0, 0), WM.Color.FromArgb(185, 0, 0, 0), 90) });
        var text = new WC.StackPanel { VerticalAlignment = W.VerticalAlignment.Center, Margin = new(22, 0, 22, 0) };
        var row = new WC.Grid(); row.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto, MaxWidth = 680 }); row.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        row.Children.Add(title); var rename = fields.Create(main.WpfSessionRename); rename.Margin = new(12, 0, 0, 0); WC.Grid.SetColumn(rename, 1); row.Children.Add(rename);
        row.SizeChanged += (_, _) => row.ColumnDefinitions[0].MaxWidth = Math.Max(60, row.ActualWidth - rename.ActualWidth - 16);
        text.Children.Add(row); text.Children.Add(subtitle); grid.Children.Add(text);
        var edit = fields.Create(main.WpfSessionCoverEdit); edit.HorizontalAlignment = W.HorizontalAlignment.Right; edit.VerticalAlignment = W.VerticalAlignment.Bottom; edit.Margin = new(0, 0, 14, 12); grid.Children.Add(edit);
        var border = new WC.Border { Child = grid, BorderThickness = new(1), CornerRadius = new(6) }; border.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border"); return border;
    }
    private W.FrameworkElement Status(TableLayoutPanel model)
    {
        var grid = new WC.Grid(); grid.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        foreach (Control child in model.Controls) { var view = fields.Create(child); WC.Grid.SetColumn(view, model.GetColumn(child)); grid.Children.Add(view); }
        return grid;
    }
    private W.FrameworkElement Translation(TableLayoutPanel model)
    {
        var grid = new WC.Grid(); grid.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = W.GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        var views = model.Controls.Cast<Control>().OrderBy(model.GetColumn).Select(fields.Create).ToArray(); foreach (var view in views) grid.Children.Add(view);
        void Arrange()
        {
            bool stack = grid.ActualWidth < 760;
            for (int i = 0; i < views.Length; i++) { WC.Grid.SetRow(views[i], stack ? i : 0); WC.Grid.SetColumn(views[i], stack ? 0 : i); WC.Grid.SetColumnSpan(views[i], stack ? 2 : 1); views[i].Margin = stack ? new(0, 0, 0, 10) : i == 0 ? new(0, 0, 8, 0) : new(8, 0, 0, 0); }
        }
        grid.SizeChanged += (_, _) => Arrange(); Arrange(); return grid;
    }
    private W.FrameworkElement Module(Control model)
    {
        // Existing engine-specific editors retain their own focus, menus,
        // dynamic rows, maps and validation, inside a single native leaf host.
        model.Parent?.Controls.Remove(model);
        // Reset docking before insertion. Changing Dock from OnControlAdded
        // during a reparent can run DefaultLayout with stale table anchors.
        model.Dock = DockStyle.None; model.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        var viewport = new WorkspaceScrollView { Dock = DockStyle.Fill };
        viewport.SuspendLayout(); try { viewport.Controls.Add(model); } finally { viewport.ResumeLayout(true); }
        var host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = viewport, Height = 500, Focusable = true }; modules.Add(host); return host;
    }
    internal void Refresh()
    {
        if (refreshing || main.IsDisposed || !IsVisible) return; refreshing = true;
        try
        {
            fields.Refresh(); title.Text = main.WpfSessionTitle; subtitle.Text = main.WpfSessionSubtitle;
            artwork.Source = main.WpfSessionArtwork is Bitmap bitmap ? WpfDrawing.Image(bitmap) : null;
            Background = WpfDrawing.Brush(UiTheme.Current.Main);
            foreach (var module in modules) if (module.Child is { } child) { child.BackColor = UiTheme.Current.Main; child.ForeColor = UiTheme.Current.Text; }
        }
        finally { refreshing = false; }
    }
    public void Dispose() { fields.Dispose(); foreach (var module in modules) module.Dispose(); }
}

public sealed partial class MainForm
{
    internal WpfGameSessionWorkspace? WpfGameSessionView { get; set; }
    internal Control[] WpfSessionRows => _sessionContent!.Controls.Cast<Control>().OrderBy(_sessionContent.GetRow).ToArray();
    internal Control WpfSessionHero => _sessionHero!;
    internal Control WpfSessionTranslation => _sessionTranslationPanel!;
    internal Control WpfSessionStatusRow => _sessionStatusRow!;
    internal Control[] WpfSessionModules => [_sessionModificationPanel!, _renpyModificationPanel!, _novelModificationPanel!];
    internal Control WpfSessionRename => _sessionRename!;
    internal Control WpfSessionCoverEdit => _sessionCoverEdit!;
    internal string WpfSessionTitle => _gameTitle.Text;
    internal string WpfSessionSubtitle => _libraryChineseName.Text;
    internal Image? WpfSessionArtwork => _libraryCover.Image;
}
