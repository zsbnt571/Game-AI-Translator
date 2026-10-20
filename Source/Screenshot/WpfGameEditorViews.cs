using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

internal static class WpfCanvasDrawing
{
    internal static WM.Pen Pen(Color color, double thickness = 1) { var p = new WM.Pen(WpfDrawing.Brush(color), thickness); p.Freeze(); return p; }
    internal static void Text(WM.DrawingContext dc, string text, Font font, Color color, W.Rect box)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        var value = new WM.FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, W.FlowDirection.LeftToRight,
            new WM.Typeface(new WM.FontFamily(font.Name), W.FontStyles.Normal, font.Bold ? W.FontWeights.Bold : W.FontWeights.Normal, W.FontStretches.Normal), font.SizeInPoints * 96 / 72, WpfDrawing.Brush(color), 1) { MaxTextWidth = Math.Max(1, box.Width), MaxTextHeight = Math.Max(1, box.Height), Trimming = W.TextTrimming.CharacterEllipsis };
        dc.DrawText(value, box.TopLeft);
    }
    internal static void Crop(WM.DrawingContext dc, Bitmap bitmap, RectangleF source, W.Rect target)
    {
        if (source.Width <= 0 || source.Height <= 0) return;
        dc.PushClip(new WM.RectangleGeometry(target));
        double sx = target.Width / source.Width, sy = target.Height / source.Height;
        dc.DrawImage(WpfDrawing.Image(bitmap), new(target.Left - source.Left * sx, target.Top - source.Top * sy, bitmap.Width * sx, bitmap.Height * sy)); dc.Pop();
    }
}

internal sealed partial class RpgMapGrid
{
    internal W.FrameworkElement CreateWpfView() => new WpfControllerCanvas(this, DrawNative);
    private void DrawNative(WM.DrawingContext dc, W.Size size)
    {
        var p = UiTheme.Current; dc.DrawRectangle(WpfDrawing.Brush(p.Secondary), null, new(size));
        if (map is null) { WpfCanvasDrawing.Text(dc, UiLanguage.Text("开启修改后选择地图"), Font, p.SecondaryText, new(20, 20, Math.Max(1, size.Width - 40), 40)); return; }
        int x0 = Math.Max(0, (int)Math.Floor(-panX / cell)), y0 = Math.Max(0, (int)Math.Floor(-panY / cell));
        int x1 = Math.Min(width, (int)Math.Ceiling((size.Width - panX) / cell)), y1 = Math.Min(height, (int)Math.Ceiling((size.Height - panY) / cell)); bool dark = p.Main.GetBrightness() < .4f;
        var ground = WpfDrawing.Brush(dark ? Color.FromArgb(54, 93, 84) : Color.FromArgb(202, 229, 218));
        var block = WpfDrawing.Brush(dark ? Color.FromArgb(39, 46, 59) : Color.FromArgb(207, 214, 225));
        var water = WpfDrawing.Brush(dark ? Color.FromArgb(39, 87, 128) : Color.FromArgb(133, 195, 236));
        var tagged = WpfDrawing.Brush(dark ? Color.FromArgb(92, 68, 123) : Color.FromArgb(210, 186, 229));
        var line = WpfCanvasDrawing.Pen(dark ? Color.FromArgb(96, 113, 134) : Color.FromArgb(145, 168, 191));
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = y * width + x, kind = kinds.ElementAtOrDefault(i); dc.DrawRectangle(kind == 2 ? water : kind == 3 ? tagged : cells[i] > 0 ? ground : block, null, new(panX + x * cell, panY + y * cell, cell, cell)); }
        for (int x = x0; x <= x1; x++) dc.DrawLine(line, new(panX + x * cell, panY + y0 * cell), new(panX + x * cell, panY + y1 * cell));
        for (int y = y0; y <= y1; y++) dc.DrawLine(line, new(panX + x0 * cell, panY + y * cell), new(panX + x1 * cell, panY + y * cell));
        foreach (var ev in events)
        {
            if (!TryPosition(ev, out var position) || position.X < x0 || position.X >= x1 || position.Y < y0 || position.Y >= y1) continue;
            var r = new W.Rect(panX + position.X * cell + 2, panY + position.Y * cell + 2, Math.Max(3, cell - 4), Math.Max(3, cell - 4)); dc.DrawEllipse(WpfDrawing.Brush(Color.FromArgb(234, 142, 39)), null, new(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
            if (cell >= 24 * DeviceDpi / 96f && ev.TryGetProperty("id", out var id)) WpfCanvasDrawing.Text(dc, id.ToString(), Font, Color.FromArgb(44, 28, 8), r);
        }
        if (player.X >= 0 && player.Y >= 0)
        {
            var shape = new WM.StreamGeometry(); using (var path = shape.Open()) { path.BeginFigure(new(panX + (player.X + .5f) * cell, panY + player.Y * cell + 1), true, true); path.LineTo(new(panX + (player.X + 1) * cell - 1, panY + (player.Y + 1) * cell - 1), true, false); path.LineTo(new(panX + player.X * cell + 1, panY + (player.Y + 1) * cell - 1), true, false); } shape.Freeze(); dc.DrawGeometry(WpfDrawing.Brush(Color.FromArgb(0, 120, 255)), null, shape);
        }
        if (SelectedTile is { } selected) dc.DrawRectangle(null, WpfCanvasDrawing.Pen(p.Accent, 2), new(panX + selected.X * cell, panY + selected.Y * cell, cell, cell));
    }
    private new bool Capture { get => WpfControlCoordinates.View(this)?.IsMouseCaptured ?? base.Capture; set { if (WpfControlCoordinates.View(this) is { } view) { if (value) view.CaptureMouse(); else view.ReleaseMouseCapture(); } else base.Capture = value; } }
}

internal sealed partial class ArtworkCropPreview
{
    internal W.FrameworkElement CreateWpfView() => new WpfControllerCanvas(this, (dc, size) =>
    {
        dc.DrawRectangle(WpfDrawing.Brush(UiTheme.Current.Control), null, new(size));
        if (image is null) { WpfCanvasDrawing.Text(dc, UiLanguage.Text("选择图片后在这里预览"), Font, UiTheme.Current.SecondaryText, new(16, 20, Math.Max(1, size.Width - 32), 60)); return; }
        var crop = CropRectangle(); var ratio = Math.Min(Math.Max(1, size.Width - 24) / crop.Width, Math.Max(1, size.Height - 24) / crop.Height); var dest = new W.Rect((size.Width - crop.Width * ratio) / 2, (size.Height - crop.Height * ratio) / 2, crop.Width * ratio, crop.Height * ratio);
        WpfCanvasDrawing.Crop(dc, image, crop, dest); dc.DrawRectangle(null, WpfCanvasDrawing.Pen(UiTheme.Current.Accent, 2), dest);
    });
    private new bool Capture { get => WpfControlCoordinates.View(this)?.IsMouseCaptured ?? base.Capture; set { if (WpfControlCoordinates.View(this) is { } view) { if (value) view.CaptureMouse(); else view.ReleaseMouseCapture(); } else base.Capture = value; } }
}

internal sealed partial class GameDataIcon
{
    internal W.FrameworkElement CreateWpfView() => new WpfControllerCanvas(this, (dc, size) => { if (Atlas is Bitmap bitmap && Index >= 0 && ((Index / 16) + 1) * 32 <= bitmap.Height) WpfCanvasDrawing.Crop(dc, bitmap, new(Index % 16 * 32, Index / 16 * 32, 32, 32), new(4, 4, Math.Max(1, size.Width - 8), Math.Max(1, size.Height - 8))); });
}

internal sealed partial class GameEventCommandList
{
    internal W.FrameworkElement CreateWpfView()
    {
        var list = new WC.ListBox { MaxHeight = 340, MinHeight = 78, HorizontalContentAlignment = W.HorizontalAlignment.Stretch }; bool updating = false; string[]? previous = null;
        var factory = new W.FrameworkElementFactory(typeof(WC.TextBlock)); factory.SetBinding(WC.TextBlock.TextProperty, new System.Windows.Data.Binding()); factory.SetValue(WC.TextBlock.TextWrappingProperty, W.TextWrapping.Wrap); list.ItemTemplate = new W.DataTemplate { VisualTree = factory };
        void Sync() { updating = true; if (!ReferenceEquals(previous, items)) { previous = items; list.ItemsSource = items; } list.SelectedIndex = selected; updating = false; }
        list.SelectionChanged += (_, _) => { if (!updating) SelectedIndex = list.SelectedIndex; };
        InvalidateEventHandler changed = (_, _) => Sync(); Invalidated += changed; list.Unloaded += (_, _) => Invalidated -= changed; list.Loaded += (_, _) => { Invalidated -= changed; Invalidated += changed; Sync(); }; Sync(); return list;
    }
}
