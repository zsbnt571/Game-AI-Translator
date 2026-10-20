using W = System.Windows;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

// Vector drawing adapter used by the migrated diagnostic overlays. It emits
// WPF drawing commands directly; it never paints a WinForms control to a bitmap.
internal sealed class WpfCanvasGraphics(WM.DrawingContext context)
{
    private static WM.Brush Brush(Brush value) => WpfDrawing.Brush(value is SolidBrush solid ? solid.Color : Color.White);
    private static WM.Pen Pen(Pen value)
    {
        var pen = new WM.Pen(WpfDrawing.Brush(value.Color), value.Width);
        pen.DashStyle = value.DashStyle switch { System.Drawing.Drawing2D.DashStyle.Dash => WM.DashStyles.Dash, System.Drawing.Drawing2D.DashStyle.Dot => WM.DashStyles.Dot, System.Drawing.Drawing2D.DashStyle.DashDot => WM.DashStyles.DashDot, _ => WM.DashStyles.Solid }; pen.Freeze(); return pen;
    }
    private static WM.FormattedText Text(string text, Font font, WM.Brush brush) => new(text, System.Globalization.CultureInfo.CurrentUICulture, W.FlowDirection.LeftToRight, new WM.Typeface(new WM.FontFamily(font.Name), font.Italic ? W.FontStyles.Italic : W.FontStyles.Normal, font.Bold ? W.FontWeights.Bold : W.FontWeights.Normal, W.FontStretches.Normal), font.Unit == GraphicsUnit.Pixel ? font.Size : font.SizeInPoints * 96 / 72, brush, 1);
    internal SizeF MeasureString(string text, Font font) { var value = Text(text, font, WM.Brushes.Black); return new((float)value.WidthIncludingTrailingWhitespace, (float)value.Height); }
    internal void DrawString(string text, Font font, Brush brush, float x, float y) => context.DrawText(Text(text, font, Brush(brush)), new(x, y));
    internal void FillRectangle(Brush brush, float x, float y, float width, float height) { if (width >= 0 && height >= 0) context.DrawRectangle(Brush(brush), null, new(x, y, width, height)); }
    internal void FillRectangle(Brush brush, RectangleF rectangle) => FillRectangle(brush, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
    internal void DrawRectangle(Pen pen, Rectangle rectangle) => DrawRectangle(pen, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
    internal void DrawRectangle(Pen pen, float x, float y, float width, float height) { if (width >= 0 && height >= 0) context.DrawRectangle(null, Pen(pen), new(x, y, width, height)); }
    internal void FillEllipse(Brush brush, float x, float y, float width, float height) => context.DrawEllipse(Brush(brush), null, new(x + width / 2, y + height / 2), width / 2, height / 2);
    internal void DrawPolygon(Pen pen, PointF[] points)
    {
        if (points.Length < 2) return; var geometry = new WM.StreamGeometry();
        using (var builder = geometry.Open()) { builder.BeginFigure(new(points[0].X, points[0].Y), false, true); builder.PolyLineTo(points.Skip(1).Select(p => new W.Point(p.X, p.Y)).ToArray(), true, false); }
        geometry.Freeze(); context.DrawGeometry(null, Pen(pen), geometry);
    }
}

internal static class WpfNativeVisuals
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, Func<W.FrameworkElement>> Factories = new();
    internal static void Register(Control model, Func<W.FrameworkElement> factory) { Factories.Remove(model); Factories.Add(model, factory); }
    internal static W.FrameworkElement? Create(Control model) => Factories.TryGetValue(model, out var factory) ? factory() : null;
    internal static W.FrameworkElement Picture(PictureBox model, Action<WpfCanvasGraphics> overlay)
    {
        return new WpfControllerCanvas(model, (dc, size) =>
        {
            dc.DrawRectangle(WpfDrawing.Brush(model.BackColor), null, new(size));
            if (model.Image is Bitmap bitmap)
            {
                double factor = model.SizeMode == PictureBoxSizeMode.Zoom ? Math.Min(size.Width / bitmap.Width, size.Height / bitmap.Height) : 1;
                var target = model.SizeMode == PictureBoxSizeMode.StretchImage ? new W.Rect(size) : new((size.Width - bitmap.Width * factor) / 2, (size.Height - bitmap.Height * factor) / 2, bitmap.Width * factor, bitmap.Height * factor);
                dc.DrawImage(WpfDrawing.Image(bitmap), target);
            }
            overlay(new(dc));
        });
    }
}
