using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

// One finite viewport and one vertical offset. The native canvas remains
// viewport-sized; only nearby cards are drawn/decoded even in a very large library.
internal sealed class WpfLibraryPageViewport : WC.Panel, IDisposable
{
    private readonly W.FrameworkElement canvas, header, footer;
    private readonly NativeGameLibraryGrid grid;
    private readonly WM.TranslateTransform headerOffset = new(), footerOffset = new();
    private bool disposed;
    internal double VerticalOffset => grid.ScrollOffset.Y * 96d / grid.DeviceDpi;
    internal double ExtentHeight => grid.PageExtentHeight * 96d / grid.DeviceDpi;
    internal double ViewportHeight => RenderSize.Height;
    internal double HeaderHeight => grid.PageLeadingHeight * 96d / grid.DeviceDpi;
    internal double FooterHeight => grid.PageTrailingHeight * 96d / grid.DeviceDpi;
    internal WpfLibraryPageViewport(W.FrameworkElement mainCanvas, W.FrameworkElement header, W.FrameworkElement footer)
    {
        canvas = mainCanvas; this.header = header; this.footer = footer;
        grid = NativeGameLibraryGrid.GridFromNativeView(canvas)
            ?? throw new ArgumentException("A native library canvas is required.", nameof(mainCanvas));
        ClipToBounds = true; UseLayoutRounding = true; SnapsToDevicePixels = true;
        W.Automation.AutomationProperties.SetAutomationId(this, "LibraryPageViewport");
        W.Automation.AutomationProperties.SetName(this, UiLanguage.Text("游戏库页面"));
        Children.Add(canvas); Children.Add(header); Children.Add(footer);
        header.RenderTransform = headerOffset; footer.RenderTransform = footerOffset;
        grid.PageScrollChanged += UpdateOffsets;
        grid.EnablePageScroll();
        PreviewMouseWheel += PagePreviewMouseWheel;
        RequestBringIntoView += PageRequestBringIntoView;
        GotKeyboardFocus += PageGotKeyboardFocus;
    }
    internal void ScrollToVerticalOffset(double offset)
        => grid.ScrollPageTo((int)Math.Round(Math.Clamp(offset, 0, int.MaxValue * 96d / grid.DeviceDpi) * grid.DeviceDpi / 96d));
    internal void ScrollToTop() => grid.ScrollToStart();
    private void UpdateOffsets()
    {
        if (disposed) return;
        headerOffset.Y = -VerticalOffset;
        footerOffset.Y = HeaderHeight + grid.PageContentHeight * 96d / grid.DeviceDpi - VerticalOffset;
    }
    protected override W.Size MeasureOverride(W.Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? ActualWidth : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? ActualHeight : availableSize.Height;
        width = Math.Max(0, width); height = Math.Max(0, height);
        double lane = Math.Round(16 * grid.DeviceDpi / 96d) * 96d / grid.DeviceDpi;
        header.Measure(new(Math.Max(0, width - lane), double.PositiveInfinity));
        footer.Measure(new(Math.Max(0, width - lane), double.PositiveInfinity));
        canvas.Measure(new(width, height));
        return new(width, height);
    }
    protected override W.Size ArrangeOverride(W.Size finalSize)
    {
        double lane = Math.Round(16 * grid.DeviceDpi / 96d) * 96d / grid.DeviceDpi;
        double width = Math.Max(0, finalSize.Width - lane);
        canvas.Arrange(new W.Rect(new W.Point(), finalSize));
        header.Arrange(new(0, 0, width, header.DesiredSize.Height));
        footer.Arrange(new(0, 0, width, footer.DesiredSize.Height));
        grid.SetPageChrome(header.DesiredSize.Height, footer.DesiredSize.Height);
        UpdateOffsets();
        return finalSize;
    }
    private void PagePreviewMouseWheel(object sender, WI.MouseWheelEventArgs e)
    {
        if ((WI.Keyboard.Modifiers & WI.ModifierKeys.Shift) != 0) return;
        for (W.DependencyObject? node = e.OriginalSource as W.DependencyObject; node is not null && node != this; node = Parent(node))
            if (node is WC.ComboBox { IsDropDownOpen: true }) return;
        grid.ScrollPageWheel(e.Delta);
        e.Handled = true;
    }
    private static W.DependencyObject? Parent(W.DependencyObject node)
        => node is WM.Visual or System.Windows.Media.Media3D.Visual3D ? WM.VisualTreeHelper.GetParent(node) : W.LogicalTreeHelper.GetParent(node);
    private bool IsChrome(W.DependencyObject node)
    {
        for (W.DependencyObject? parent = node; parent is not null && parent != this; parent = Parent(parent))
            if (parent == header || parent == footer) return true;
        return false;
    }
    private void BringChromeIntoView(W.DependencyObject? target)
    {
        if (target is not W.FrameworkElement element || !IsChrome(element) || !element.IsVisible) return;
        W.Rect bounds = element.TransformToAncestor(this).TransformBounds(new W.Rect(element.RenderSize));
        if (bounds.Top < 0) ScrollToVerticalOffset(VerticalOffset + bounds.Top);
        else if (bounds.Bottom > ViewportHeight) ScrollToVerticalOffset(VerticalOffset + bounds.Bottom - ViewportHeight);
    }
    private void PageRequestBringIntoView(object sender, W.RequestBringIntoViewEventArgs e)
    {
        if (!IsChrome(e.TargetObject)) return;
        BringChromeIntoView(e.TargetObject); e.Handled = true;
    }
    private void PageGotKeyboardFocus(object sender, WI.KeyboardFocusChangedEventArgs e)
        => BringChromeIntoView(e.NewFocus as W.DependencyObject);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; grid.PageScrollChanged -= UpdateOffsets; grid.StopPageScroll();
        PreviewMouseWheel -= PagePreviewMouseWheel; RequestBringIntoView -= PageRequestBringIntoView; GotKeyboardFocus -= PageGotKeyboardFocus;
    }
}
