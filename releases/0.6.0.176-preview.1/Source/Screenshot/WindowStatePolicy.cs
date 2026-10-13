namespace ScreenshotTranslationUiTester;

public static class PreviewWindowPlacementPolicy
{
    public static Rectangle Normalize(Rectangle requested, IReadOnlyList<Rectangle> workingAreas, Size minimum)
    {
        var screens = workingAreas.Count == 0 ? [new Rectangle(0, 0, 1920, 1080)] : workingAreas;
        var width = Math.Max(minimum.Width, requested.Width);
        var height = Math.Max(minimum.Height, requested.Height);
        var target = screens.FirstOrDefault(x => Rectangle.Intersect(x, requested).Width >= 80 && Rectangle.Intersect(x, requested).Height >= 60);
        if (target.IsEmpty) target = screens[0];
        width = Math.Min(width, target.Width); height = Math.Min(height, target.Height);
        var x = Math.Clamp(requested.X, target.Left, target.Right - width);
        var y = Math.Clamp(requested.Y, target.Top, target.Bottom - height);
        return new(x, y, width, height);
    }
}

public static class WindowStateGuard
{
    public static void ShowPreviewWithoutRestoringMain(Form main, Form preview)
    {
        var minimized = main.WindowState == FormWindowState.Minimized;
        preview.Show();
        if (minimized && main.WindowState != FormWindowState.Minimized)
            main.WindowState = FormWindowState.Minimized;
    }
}
