using W = System.Windows;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

public sealed partial class CaptureOverlay
{
    internal override bool HasNativeBackground => true;
    internal override bool NativeSnapshotPresentation => true;
    internal override void PrepareNativeShow()
    {
        // Upload both images and build the first drawing while still hidden.
        // The transparent host contributes no black/old background before that
        // completed frame reaches the compositor.
        NativeWindow?.PrepareSnapshotFrame(_virtualBounds);
    }
    internal override void RenderNativeBackground(WM.DrawingContext dc, W.Size size)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_firstPaintAt == 0) _firstPaintAt = started; _paintCount++;
        var desktop = new W.Rect(0, 0, _frozenScreen.Width, _frozenScreen.Height);
        dc.DrawImage(WpfDrawing.Image(_staticFreezeLayer ?? _frozenScreen), desktop);
        var active = _pendingCreate && !_mouseDownWindow.IsEmpty ? _mouseDownWindow : _dragging || _hasSelection ? _selection : _hoveredWindow;
        if (!active.IsEmpty)
        {
            var rectangle = new W.Rect(active.X, active.Y, active.Width, active.Height);
            dc.PushClip(new WM.RectangleGeometry(rectangle)); dc.DrawImage(WpfDrawing.Image(_frozenScreen), desktop); dc.Pop();
            dc.DrawRectangle(null, WpfCanvasDrawing.Pen(_selectionBorderPen.Color, _selectionBorderPen.Width), rectangle);
            var label = GetSizeLabelBounds(active); dc.DrawRectangle(WpfDrawing.Brush(Color.FromArgb(220, 20, 23, 31)), null, new(label.X, label.Y, label.Width, label.Height));
            WpfCanvasDrawing.Text(dc, _cachedSizeText, _sizeFont, Color.White, new(label.X + 6, label.Y + 2, Math.Max(1, label.Width - 12), label.Height));
        }
        if ((!_dragging || _pendingCreate) && !_hasSelection)
        {
            const string prompt = "移动鼠标识别窗口并单击；拖动选择区域；空白处单击选择当前屏幕；Esc 取消";
            double width = Math.Min(size.Width - 40, 850 * DeviceDpi / 96d); var box = new W.Rect((size.Width - width) / 2, size.Height - 70, width, 46);
            dc.DrawRoundedRectangle(WpfDrawing.Brush(Color.FromArgb(210, 20, 23, 31)), null, box, 8, 8); WpfCanvasDrawing.Text(dc, UiLanguage.Text(prompt), _promptFont, Color.White, new(box.X + 16, box.Y + 9, Math.Max(1, box.Width - 32), box.Height));
        }
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds; _overlayPaintMs.Add(elapsed); _diagnosticLastPaintMs = elapsed;
    }
    internal override bool NativeProcessKey(System.Windows.Input.Key key)
    {
        if (key != System.Windows.Input.Key.Escape) return false; CancelCapture(); return true;
    }
}

internal sealed partial class GameScreenTranslationOverlay
{
    internal override bool HasNativeBackground => true;
    internal override bool NativeClickThrough => true;
    internal override void RenderNativeBackground(WM.DrawingContext dc, W.Size size)
    {
        var frame = current; if (frame is null || !ReferenceEquals(frame, connection.Frame) || data.Translation?.Enabled != true || !ScreenTranslationConnection.CanDisplay(frame)) return;
        float sx = (float)size.Width / frame.Pixels.Width, sy = (float)size.Height / frame.Pixels.Height;
        foreach (var block in frame.Blocks)
        {
            string translated = data.Translation?.Lookup(block.Source) ?? ""; if (translated.Length == 0 || translated == block.Source) continue;
            var r = Rectangle.Round(new RectangleF(block.Bounds.X * sx, block.Bounds.Y * sy, block.Bounds.Width * sx, block.Bounds.Height * sy)); r.Inflate(4, 3); r = Rectangle.Intersect(r, ClientRectangle); if (r.Width < 8 || r.Height < 8) continue;
            using var font = new Font("Microsoft YaHei UI", Math.Clamp(r.Height * .64f, 10, 48), FontStyle.Regular, GraphicsUnit.Pixel);
            var measured = TextRenderer.MeasureText(translated, font, new(r.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding); r.Height = Math.Min(ClientSize.Height - r.Top, Math.Max(r.Height, measured.Height + 6));
            dc.DrawRectangle(WpfDrawing.Brush(Color.FromArgb(22, 25, 31)), null, new(r.X, r.Y, r.Width, r.Height)); WpfCanvasDrawing.Text(dc, translated, font, Color.White, new(r.X, r.Y, r.Width, r.Height));
        }
    }
}
