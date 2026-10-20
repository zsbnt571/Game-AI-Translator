using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

internal static class WpfScrollRouting
{
    internal static void BubbleAtBoundary(WC.ScrollViewer outer,WI.MouseWheelEventArgs e)
    {
        var node=e.OriginalSource as W.DependencyObject;WC.ScrollViewer? target=null;
        for(;node is not null;node=Parent(node))
        {
            if(node is WC.ScrollViewer scroll&&scroll.ScrollableHeight>0&&(e.Delta>0?scroll.VerticalOffset>0:scroll.VerticalOffset<scroll.ScrollableHeight)){target=scroll;break;}
            if(ReferenceEquals(node,outer))break;
        }
        if(target is null)return;
        // Handle here: an empty DataGrid can consume the later bubbling event
        // before its containing ScrollViewer sees it. Line commands preserve
        // logical row scrolling as well as physical-pixel scrolling.
        if(W.SystemParameters.WheelScrollLines<0){if(e.Delta>0)target.PageUp();else target.PageDown();}
        else for(int i=0;i<Math.Clamp(Math.Abs(e.Delta)/120*Math.Max(1,W.SystemParameters.WheelScrollLines),1,100);i++)
        {if(e.Delta>0)target.LineUp();else target.LineDown();}
        e.Handled=true;
    }
    private static W.DependencyObject? Parent(W.DependencyObject node)=>node is WM.Visual or System.Windows.Media.Media3D.Visual3D
        ?WM.VisualTreeHelper.GetParent(node):W.LogicalTreeHelper.GetParent(node);
    internal static void RouteToParent(W.DependencyObject from, WI.MouseWheelEventArgs e)
    {
        for (var node = WM.VisualTreeHelper.GetParent(from); node is not null; node = WM.VisualTreeHelper.GetParent(node))
        {
            if (node is not WC.ScrollViewer scroll || scroll.ScrollableHeight <= 0) continue;
            double distance = W.SystemParameters.WheelScrollLines < 0 ? scroll.ViewportHeight : W.SystemParameters.WheelScrollLines * 16d;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 120d * distance);
            e.Handled = true;
            return;
        }
    }
}
