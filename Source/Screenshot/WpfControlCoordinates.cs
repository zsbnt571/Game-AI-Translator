using System.Runtime.CompilerServices;
using W = System.Windows;

namespace ScreenshotTranslationUiTester;

internal static class WpfControlCoordinates
{
    private static readonly ConditionalWeakTable<Control, W.FrameworkElement> Views = new();
    internal static void Register(Control model, W.FrameworkElement view) { Views.Remove(model); Views.Add(model, view); }
    internal static bool HasView(Control model) => Views.TryGetValue(model, out var view) && view.IsVisible;
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
