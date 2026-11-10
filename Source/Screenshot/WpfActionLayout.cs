using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

// Shared geometry for ordinary desktop actions. Navigation, steppers and compact
// icon controls can keep their explicit metrics; spacing belongs to the row.
internal static class WpfActionLayout
{
    internal const double ControlHeight = 36, Gap = 10;
    internal static WC.Button NormalButton(WC.Button button)
    {
        button.MinHeight = ControlHeight;
        button.Padding = new(12, 5, 12, 5);
        button.VerticalAlignment = W.VerticalAlignment.Center;
        button.VerticalContentAlignment = W.VerticalAlignment.Center;
        return button;
    }
    internal static void CenterRowItem(W.FrameworkElement item)
        => item.VerticalAlignment = W.VerticalAlignment.Center;

    internal static WC.WrapPanel Row(params W.FrameworkElement[] children)
    {
        var row = new WC.WrapPanel { VerticalAlignment = W.VerticalAlignment.Center };
        foreach (var child in children)
        {
            CenterRowItem(child);
            // Nested action groups already own the gaps between their children.
            child.Margin = child is WC.WrapPanel ? new(0) : new(0, 0, Gap, 4);
            row.Children.Add(child);
        }
        return row;
    }

    internal static WC.Grid AlignedRow(params W.FrameworkElement[] children)
    {
        var row = new WC.Grid { VerticalAlignment = W.VerticalAlignment.Center };
        for (int i = 0; i < children.Length; i++)
        {
            var child = children[i];
            row.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
            child.Margin = new(0, 0, i + 1 < children.Length ? Gap : 0, 0);
            // The shared grid row matches input/action bounds to its tallest item;
            // labels keep their natural height and share that row's centre.
            child.VerticalAlignment = child is WC.TextBlock ? W.VerticalAlignment.Center : W.VerticalAlignment.Stretch;
            WC.Grid.SetColumn(child, i); row.Children.Add(child);
        }
        return row;
    }
}
