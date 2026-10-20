using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

// Character hit testing and scroll positions must come from the visible text
// document, never from the hidden RichEdit controller's different line layout.
internal static class WpfRichTextBridge
{
    internal static int CharacterAt(RichTextBox model, Point point)
    {
        if (WpfControlCoordinates.View(model) is not WC.RichTextBox view) return model.GetCharIndexFromPosition(point);
        var position = view.GetPositionFromPoint(new(point.X * 96d / model.DeviceDpi, point.Y * 96d / model.DeviceDpi), true);
        return position is null ? 0 : Math.Min(model.TextLength, new System.Windows.Documents.TextRange(view.Document.ContentStart, position).Text.Replace("\r\n", "\n").Length);
    }
    private static WC.ScrollViewer? Scroll(W.DependencyObject root)
    {
        if (root is WC.ScrollViewer viewer) return viewer;
        for (int i = 0; i < WM.VisualTreeHelper.GetChildrenCount(root); i++)
            if (Scroll(WM.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    internal static Point? Position(RichTextBox model)
    {
        if (WpfControlCoordinates.View(model) is not WC.RichTextBox view || Scroll(view) is not { } scroll) return null;
        return new((int)Math.Round(scroll.HorizontalOffset * model.DeviceDpi / 96), (int)Math.Round(scroll.VerticalOffset * model.DeviceDpi / 96));
    }
    internal static bool Restore(RichTextBox model, Point point)
    {
        if (WpfControlCoordinates.View(model) is not WC.RichTextBox view || Scroll(view) is not { } scroll) return false;
        scroll.ScrollToHorizontalOffset(point.X * 96d / model.DeviceDpi);
        scroll.ScrollToVerticalOffset(point.Y * 96d / model.DeviceDpi);
        return true;
    }
}
