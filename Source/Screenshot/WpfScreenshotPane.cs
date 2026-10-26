using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

// Bitmaps belong to the screenshot session. The view borrows immutable WPF
// sources; changing zoom/pan never resamples or duplicates the original bitmap.
internal sealed class WpfScreenshotPane : WC.Grid, IDisposable
{
    private readonly IScreenshotPaneSession model;
    private readonly WC.TextBlock caption = new(), dimensions = new(), ratio = new();
    private readonly WpfScreenshotModeSwitch mode = new() { Margin = new(0, 0, 10, 0) };
    private readonly WC.WrapPanel imageTools = new(), textTools = new();
    private readonly List<(WC.Button Button, string Label, string? Icon)> actions = [];
    private readonly WC.Grid viewport;
    private readonly WC.Image image = new() { Stretch = WM.Stretch.Fill, HorizontalAlignment = W.HorizontalAlignment.Left, VerticalAlignment = W.VerticalAlignment.Top };
    private readonly WC.TextBox text = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = W.TextWrapping.Wrap, VerticalContentAlignment = W.VerticalAlignment.Top, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, BorderThickness = new(0), Margin = new(12, 10, 12, 10) };
    private readonly WC.TextBlock empty = new() { HorizontalAlignment = W.HorizontalAlignment.Center, VerticalAlignment = W.VerticalAlignment.Center, TextWrapping = W.TextWrapping.Wrap, TextAlignment = W.TextAlignment.Center, Margin = new(20) };
    private readonly WM.ScaleTransform scale = new();
    private readonly WM.TranslateTransform translate = new();
    private readonly WC.Button maximize, copy, edit, smaller, larger;
    private readonly WC.TextBlock fontSize = new();
    private ScreenshotPaneViewState state = new();
    private Bitmap? current;
    private bool updating;
    private W.Point? drag;
    private double panX, panY;
    private int? labelDensity;

    internal WpfScreenshotPane(IScreenshotPaneSession model)
    {
        this.model = model; ClipToBounds = true;
        viewport = new ImageViewport(this) { ClipToBounds = true };
        foreach (var height in new[] { 54d, -1d, -2d, 30d }) RowDefinitions.Add(new() { Height = height == -2 ? new(1, W.GridUnitType.Star) : height == -1 ? W.GridLength.Auto : new(height) });
        var header = new WC.Grid { Margin = new(10, 4, 10, 4) };
        header.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        mode.SelectionChanged += value => { if (!updating && model.View != value) { model.NativeActivate(); model.View = value; } };
        header.Children.Add(mode);
        maximize = WpfThemeResources.Button("最大化", "expand", model.NativeMaximize); maximize.MinHeight = maximize.MinWidth = 36; maximize.Padding = new(6, 2, 6, 2);
        WC.Grid.SetColumn(maximize, 1); header.Children.Add(maximize); Children.Add(header);
        var tools = new WC.Grid { Margin = new(10, 4, 10, 8), MinHeight = 30 }; tools.Children.Add(imageTools); tools.Children.Add(textTools); WC.Grid.SetRow(tools, 1); Children.Add(tools);
        void ImageTool(string label, string? icon, Action action) { var b = WpfThemeResources.Button(label, icon, action); b.MinHeight = 36; b.MinWidth = 36; b.Padding = new(10, 5, 10, 5); b.Margin = new(0, 0, 5, 3); actions.Add((b, label, icon)); imageTools.Children.Add(b); }
        ImageTool("适应窗口", null, () => Fit(ScreenshotPaneFitMode.Window)); ImageTool("适应宽度", null, () => Fit(ScreenshotPaneFitMode.Width)); ImageTool("", "minus", () => Zoom(1 / 1.2));
        ratio.VerticalAlignment = W.VerticalAlignment.Center; ratio.Margin = new(6, 0, 10, 0); imageTools.Children.Add(ratio); ImageTool("", "plus", () => Zoom(1.2));
        copy = WpfThemeResources.Button("复制", "copy", model.NativeCopy); edit = WpfThemeResources.Button("详细编辑", "edit", model.NativeEdit);
        smaller = WpfThemeResources.Button("", "minus", () => model.FontSize--); larger = WpfThemeResources.Button("", "plus", () => model.FontSize++);
        actions.AddRange([(copy, "复制", "copy"), (edit, "详细编辑", "edit"), (smaller, "", "minus"), (larger, "", "plus")]);
        foreach (var b in new[] { copy, edit, smaller }) { b.MinHeight = 36; b.Margin = new(0, 0, 5, 3); b.MinWidth = 36; b.Padding = new(10, 5, 10, 5); textTools.Children.Add(b); }
        fontSize.VerticalAlignment = W.VerticalAlignment.Center; textTools.Children.Add(fontSize); larger.Margin = new(5, 0, 0, 3); larger.MinHeight = 36; larger.MinWidth = 36; textTools.Children.Add(larger);
        var transforms = new WM.TransformGroup(); transforms.Children.Add(scale); transforms.Children.Add(translate); image.RenderTransform = transforms;
        // Arrange the full source image before transforming it. Grid clips a
        // large child to its layout slot before RenderTransform is applied.
        var imageSurface = new WC.Canvas(); imageSurface.Children.Add(image);
        viewport.Children.Add(imageSurface); viewport.Children.Add(text); viewport.Children.Add(empty); WC.Grid.SetRow(viewport, 2); Children.Add(viewport);
        var footer = new WC.Grid { Margin = new(12, 0, 12, 0) }; footer.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        caption.VerticalAlignment = dimensions.VerticalAlignment = W.VerticalAlignment.Center; caption.TextTrimming = W.TextTrimming.CharacterEllipsis; footer.Children.Add(caption); WC.Grid.SetColumn(dimensions, 1); footer.Children.Add(dimensions); WC.Grid.SetRow(footer, 3); Children.Add(footer);
        viewport.PreviewMouseWheel += (_, e) => { if (model.IsImageView && current is not null) { Zoom(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(viewport)); e.Handled = true; } };
        viewport.MouseLeftButtonDown += (_, e) => { model.NativeActivate(); if (!model.IsImageView || current is null) return; drag = e.GetPosition(viewport); viewport.CaptureMouse(); viewport.Cursor = WI.Cursors.Hand; e.Handled = true; };
        viewport.MouseMove += (_, e) => { if (drag is not { } start) return; var point = e.GetPosition(viewport); panX += point.X - start.X; panY += point.Y - start.Y; drag = point; ArrangeImage(); SaveState(); };
        viewport.MouseLeftButtonUp += (_, _) => { drag = null; viewport.ReleaseMouseCapture(); viewport.Cursor = WI.Cursors.Arrow; };
        viewport.LostMouseCapture += (_, _) => drag = null;
        text.GotKeyboardFocus += (_, _) => model.NativeActivate(); text.SelectionChanged += (_, _) => model.NativeSelectedText = text.SelectedText;
        viewport.ContextMenuOpening += (_, _) => BuildContextMenu();
        state = model.CaptureViewState();
        model.NativeViewChanged += Refresh; model.NativeStateApplied = ApplyState; model.NativeStateRequested = () => state;
        model.NativeImageCommand = command => { if (command == "actual") { state = state with { FitMode = ScreenshotPaneFitMode.Manual, Zoom = 1 }; panX = panY = 0; ArrangeImage(); SaveState(); } else if (command == "width") Fit(ScreenshotPaneFitMode.Width); else if (command == "in") Zoom(1.2); else if (command == "out") Zoom(1 / 1.2); else Fit(ScreenshotPaneFitMode.Window); };
        W.Automation.AutomationProperties.SetName(text, "截图文字内容"); W.Automation.AutomationProperties.SetName(viewport, "截图图片查看器");
        WpfUiLanguage.Property(text,W.Automation.AutomationProperties.NameProperty,"截图文字内容");WpfUiLanguage.Property(viewport,W.Automation.AutomationProperties.NameProperty,"截图图片查看器");
        ApplyState(state); Refresh();
    }
    private void BuildContextMenu()
    {
        var menu = new WC.ContextMenu();
        void Add(string label, Action action, bool enabled) { var item = new WC.MenuItem { Header = UiLanguage.Text(label), IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("复制", model.NativeCopy, model.IsImageView ? current is not null : model.CurrentText.Length > 0);
        Add("导出", model.NativeExport, model.IsImageView ? current is not null : model.CurrentText.Length > 0); Add("详细编辑", model.NativeEdit, model.HasContent); viewport.ContextMenu = menu;
    }
    private void ApplyState(ScreenshotPaneViewState value)
    {
        state = value; panX = value.PanX - value.ScrollX; panY = value.PanY - value.ScrollY; ArrangeImage();
    }
    private void Fit(ScreenshotPaneFitMode mode) { state = state with { FitMode = mode, Zoom = 1 }; panX = panY = 0; ArrangeImage(); SaveState(); }
    private void Zoom(double factor, W.Point? at = null)
    {
        if (current is null) return;
        var p = at ?? new(viewport.ActualWidth / 2, viewport.ActualHeight / 2); double oldScale = Math.Max(.0001, scale.ScaleX);
        double ix = (p.X - translate.X) / oldScale, iy = (p.Y - translate.Y) / oldScale;
        state = state with { Zoom = (float)Math.Clamp(oldScale * factor, .05, 20), FitMode = ScreenshotPaneFitMode.Manual };
        ArrangeImage(); panX += p.X - (ix * scale.ScaleX + translate.X); panY += p.Y - (iy * scale.ScaleY + translate.Y); ArrangeImage(); SaveState();
    }
    private void SaveState()
    {
        state = state with { View = model.View, FontSize = model.FontSize, PanX = (int)Math.Round(panX), PanY = (int)Math.Round(panY), ScrollX = 0, ScrollY = 0 }; model.NativeStateChanged();
    }
    private void ArrangeImage(W.Size? viewportSize = null)
    {
        if (current is null) return;
        var size = viewportSize ?? viewport.RenderSize;
        double fit = state.FitMode == ScreenshotPaneFitMode.Manual ? 1 : state.FitMode == ScreenshotPaneFitMode.Width ? size.Width / current.Width : Math.Min(size.Width / current.Width, size.Height / current.Height);
        double zoom = Math.Max(.001, fit * state.Zoom); scale.ScaleX = scale.ScaleY = zoom;
        image.Width = current.Width; image.Height = current.Height;
        translate.X = (size.Width - current.Width * zoom) / 2 + panX; translate.Y = (size.Height - current.Height * zoom) / 2 + panY;
        ratio.Text = $"{zoom * 100:0}%";
    }
    private void RefreshLabels(double? availableWidth = null)
    {
        foreach (var action in actions) WpfThemeResources.SetButton(action.Button, action.Label, action.Icon);
        double width = availableWidth ?? ActualWidth; labelDensity = width < 420 ? 2 : width < 490 ? 1 : 0;
        bool compact = width < 490;
        W.Automation.AutomationProperties.SetName(this, UiLanguage.Text(model.PaneTitle));
        mode.Refresh(model.View);
        WpfThemeResources.SetButton(maximize, compact ? "" : model.Maximized ? "还原" : "最大化", model.Maximized ? "restore" : "expand");
        maximize.ToolTip = UiLanguage.Text(model.Maximized ? "还原" : "最大化");
        W.Automation.AutomationProperties.SetName(maximize, (string)maximize.ToolTip);
        caption.Text = UiLanguage.Text(ScreenshotRebuildPane.ViewTitle(model.View)); dimensions.Text = model.IsImageView ? current is null ? "" : $"{current.Width} × {current.Height}" : $"{model.CurrentText.Length:N0}";
    }
    protected override W.Size MeasureOverride(W.Size availableSize)
    {
        if (double.IsFinite(availableSize.Width))
        {
            int density = availableSize.Width < 420 ? 2 : availableSize.Width < 490 ? 1 : 0;
            if (density != labelDensity) RefreshLabels(availableSize.Width);
        }
        return base.MeasureOverride(availableSize);
    }
    private sealed class ImageViewport(WpfScreenshotPane owner) : WC.Grid
    {
        protected override W.Size ArrangeOverride(W.Size finalSize)
        {
            owner.ArrangeImage(finalSize);
            return base.ArrangeOverride(finalSize);
        }
    }
    internal void Refresh()
    {
        if (updating) return; updating = true;
        try
        {
            var p = UiTheme.Current; Background = WpfDrawing.Brush(p.Secondary); viewport.Background = WpfDrawing.Brush(p.Control);
            foreach (var label in new[] { caption, dimensions, ratio, fontSize, empty }) label.Foreground = WpfDrawing.Brush(p.SecondaryText);
            if (!ReferenceEquals(current, model.CurrentBitmap)) { current = model.CurrentBitmap; image.Source = current is null ? null : WpfDrawing.Image(current); panX = panY = 0; }
            if (text.Text != model.CurrentText) text.Text = model.CurrentText;
            text.FontSize = model.FontSize; fontSize.Text = $"{model.FontSize}px"; smaller.IsEnabled = model.FontSize > 12; larger.IsEnabled = model.FontSize < 28;
            text.Foreground = WpfDrawing.Brush(p.Text); text.Background = WpfDrawing.Brush(p.Secondary);
            image.Visibility = imageTools.Visibility = model.IsImageView ? W.Visibility.Visible : W.Visibility.Collapsed;
            text.Visibility = textTools.Visibility = model.IsImageView ? W.Visibility.Collapsed : W.Visibility.Visible;
            empty.Visibility = (model.IsImageView ? current is null : model.CurrentText.Length == 0) ? W.Visibility.Visible : W.Visibility.Collapsed;
            empty.Text = UiLanguage.Text(model.NativeEmptyText); copy.IsEnabled = model.CurrentText.Length > 0; edit.IsEnabled = model.HasContent;
            state = state with { View = model.View, FontSize = model.FontSize }; RefreshLabels(); ArrangeImage(); BuildContextMenu();
        }
        finally { updating = false; }
    }
    public void Dispose() { model.NativeViewChanged -= Refresh; model.NativeStateApplied = null; model.NativeStateRequested = null; model.NativeImageCommand = null; image.Source = null; }
}

internal sealed partial class ScreenshotRebuildPane
{
    internal event Action? NativeViewChanged;
    internal Func<ScreenshotPaneViewState>? NativeStateRequested;
    internal Action<ScreenshotPaneViewState>? NativeStateApplied;
    internal Action<string>? NativeImageCommand;
    internal string NativeSelectedText = "";
    internal string NativeEmptyText => IsImageView ? canvas.EmptyText : empty.Text;
    internal void NativeStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    internal void NativeActivate() => Activated?.Invoke(this, EventArgs.Empty);
    internal void NativeMaximize() => MaximizeRequested?.Invoke(this, EventArgs.Empty);
    internal void NativeCopy() => CopyRequested?.Invoke(this, EventArgs.Empty);
    internal void NativeExport() => ExportRequested?.Invoke(this, EventArgs.Empty);
    internal void NativeEdit() => EditRequested?.Invoke(this, EventArgs.Empty);
}
