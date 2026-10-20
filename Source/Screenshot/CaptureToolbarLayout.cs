namespace ScreenshotTranslationUiTester;

// All inputs are physical capture pixels, including monitors with negative origins.
internal static class CaptureToolbarLayout
{
    internal static Point Place(Rectangle selection, Point pointer, Size toolbar,
        Rectangle screen, CaptureToolbarPlacement placement, int gap)
    {
        int inset = Math.Max(4, gap / 2);
        int left = screen.Left + inset, top = screen.Top + inset;
        int right = Math.Max(left, screen.Right - toolbar.Width - inset);
        int bottom = Math.Max(top, screen.Bottom - toolbar.Height - inset);
        int x, y;
        if (placement == CaptureToolbarPlacement.NearPointer)
        {
            x = pointer.X + gap;
            y = pointer.Y + gap;
            if (y > bottom) y = pointer.Y - toolbar.Height - gap;
        }
        else
        {
            x = selection.Right - toolbar.Width;
            y = selection.Bottom + gap;
            if (y > bottom) y = selection.Top - toolbar.Height - gap;
        }
        return new(Math.Clamp(x, left, right), Math.Clamp(y, top, bottom));
    }
}
