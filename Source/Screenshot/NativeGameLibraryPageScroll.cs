namespace ScreenshotTranslationUiTester;

internal sealed partial class NativeGameLibraryGrid
{
    private bool pageScrollEnabled;
    private int pageLeadingHeight, pageTrailingHeight;
    private int? pendingPageRestore;
    private double? pageHeaderResizeAnchor;
    internal event Action? PageScrollChanged;
    internal int PageLeadingHeight => pageLeadingHeight;
    internal int PageTrailingHeight => pageTrailingHeight;
    internal int PageContentHeight
    {
        get
        {
            if (tiles.Length == 0)
                return pageScrollEnabled ? Math.Max(S(160), ViewportSize.Height - pageLeadingHeight - pageTrailingHeight) : 0;
            int rows = (int)Math.Ceiling((double)tiles.Length / Columns);
            return S(6) + rows * Cell.Height + Math.Max(0, rows - 1) * S(12);
        }
    }
    internal int PageExtentHeight => pageLeadingHeight + PageContentHeight + pageTrailingHeight;
    internal void EnablePageScroll()
    {
        if (ShelfMode) throw new InvalidOperationException("The horizontal shelf cannot own the vertical page.");
        pageScrollEnabled = true;
        Reflow();
    }
    internal void SetPageChrome(double leadingDip, double trailingDip)
    {
        int leading = Math.Max(0, (int)Math.Round(leadingDip * DeviceDpi / 96d));
        int trailing = Math.Max(0, (int)Math.Round(trailingDip * DeviceDpi / 96d));
        bool changed = leading != pageLeadingHeight || trailing != pageTrailingHeight;
        int previous = ScrollValue, oldLeading = pageLeadingHeight;
        pageLeadingHeight = leading; pageTrailingHeight = trailing;
        UpdateScrollRange();
        if (pendingPageRestore is { } restored)
        {
            pendingPageRestore = null;
            SetScrollValue(restored);
        }
        else if (changed)
        {
            // A resized/reflowed header must not move the game currently at the
            // top. When still within the header, keep its own page position.
            int desired = previous >= oldLeading && previous > 0 ? previous + leading - oldLeading : previous;
            scrollOffset = new(0, Math.Clamp(desired, 0, scrollMaximum));
        }
        if (changed) Invalidate();
        NotifyPageScroll();
    }
    internal void ScrollPageTo(int physicalOffset) => SetScrollValue(physicalOffset);
    internal void ScrollPageWheel(int delta) => wpfCanvas?.ScrollWheel(delta);
    internal void StopPageScroll() => wpfCanvas?.StopWheelMotion();
    private void NotifyPageScroll() { if (pageScrollEnabled) PageScrollChanged?.Invoke(); }
    internal static NativeGameLibraryGrid? GridFromNativeView(System.Windows.FrameworkElement view)
        => view is WpfLibraryCanvas canvas ? canvas.Owner : null;
}
