using System.Runtime.CompilerServices;
using W = System.Windows;

namespace ScreenshotTranslationUiTester;

internal static class WpfControlCoordinates
{
    internal static System.Windows.Input.Cursor Cursor(System.Windows.Forms.Cursor cursor) => cursor == Cursors.Cross ? System.Windows.Input.Cursors.Cross : cursor == Cursors.Hand ? System.Windows.Input.Cursors.Hand : cursor == Cursors.IBeam ? System.Windows.Input.Cursors.IBeam : cursor == Cursors.SizeAll ? System.Windows.Input.Cursors.SizeAll : cursor == Cursors.SizeWE ? System.Windows.Input.Cursors.SizeWE : cursor == Cursors.SizeNS ? System.Windows.Input.Cursors.SizeNS : cursor == Cursors.SizeNWSE ? System.Windows.Input.Cursors.SizeNWSE : cursor == Cursors.SizeNESW ? System.Windows.Input.Cursors.SizeNESW : System.Windows.Input.Cursors.Arrow;
    private static readonly ConditionalWeakTable<Control, W.FrameworkElement> Views = new();
    internal static W.FrameworkElement? View(Control model) => Views.TryGetValue(model, out var view) ? view : null;
    internal static void Register(Control model, W.FrameworkElement view) { Views.Remove(model); Views.Add(model, view); }
    internal static bool HasView(Control model) => Views.TryGetValue(model, out var view) && view.IsVisible;
    internal static Rectangle ScreenBounds(Control model)
    {
        if (Views.TryGetValue(model, out var view) && view.IsVisible)
        {
            var first = view.PointToScreen(new(0, 0)); var last = view.PointToScreen(new(view.ActualWidth, view.ActualHeight));
            return Rectangle.FromLTRB((int)Math.Floor(first.X), (int)Math.Floor(first.Y), (int)Math.Ceiling(last.X), (int)Math.Ceiling(last.Y));
        }
        return model.RectangleToScreen(model.ClientRectangle);
    }
    internal static Point ToScreen(Control model, Point point)
    {
        if (!Views.TryGetValue(model, out var view) || !view.IsVisible) return model.PointToScreen(point);
        var p = view.PointToScreen(new W.Point(point.X * 96d / model.DeviceDpi, point.Y * 96d / model.DeviceDpi));
        return new((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }
    internal static Point FromScreen(Control model, Point point)
    {
        if (!Views.TryGetValue(model, out var view) || !view.IsVisible) return model.PointToClient(point);
        var p = view.PointFromScreen(new W.Point(point.X, point.Y));
        return new((int)Math.Round(p.X * model.DeviceDpi / 96d), (int)Math.Round(p.Y * model.DeviceDpi / 96d));
    }
}
