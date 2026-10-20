using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

// Derive columns from the parent's constraint, never from child DesiredSize or
// the panel's previous ActualWidth. This avoids WrapPanel width feedback during
// asynchronous label updates, DPI rounding, and viewport scrollbar changes.
internal sealed class ResponsiveEqualColumnsPanel(double preferredWidth, int maximumColumns) : WC.Panel
{
    internal int Columns(double width) => Math.Clamp((int)Math.Floor(Math.Max(0, width) / preferredWidth), 1, maximumColumns);
    protected override W.Size MeasureOverride(W.Size available)
    {
        double width = double.IsFinite(available.Width) ? available.Width : preferredWidth;
        int columns = Columns(width);
        double cell = width / columns, height = 0, rowHeight = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i]; child.Measure(new(Math.Max(0, cell), double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (i % columns == columns - 1 || i == InternalChildren.Count - 1) { height += rowHeight; rowHeight = 0; }
        }
        return new(Math.Max(0, width), height);
    }
    protected override W.Size ArrangeOverride(W.Size finalSize)
    {
        int columns = Columns(finalSize.Width);
        double cell = finalSize.Width / columns, y = 0;
        for (int first = 0; first < InternalChildren.Count; first += columns)
        {
            int count = Math.Min(columns, InternalChildren.Count - first);
            double height = 0;
            for (int i = 0; i < count; i++) height = Math.Max(height, InternalChildren[first + i].DesiredSize.Height);
            for (int i = 0; i < count; i++) InternalChildren[first + i].Arrange(new W.Rect(i * cell, y, Math.Max(0, cell), height));
            y += height;
        }
        return finalSize;
    }
}
