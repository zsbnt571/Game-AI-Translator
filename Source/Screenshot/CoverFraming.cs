using W = System.Windows;

namespace ScreenshotTranslationUiTester;

// The full source image is retained. This describes only the detail-page view.
internal sealed record CoverFraming(double X = .5, double Y = .5, double Zoom = 1)
{
    internal static CoverFraming Centered { get; } = new();
    internal CoverFraming Normalize() => new(double.IsFinite(X) ? Math.Clamp(X, 0, 1) : .5,
        double.IsFinite(Y) ? Math.Clamp(Y, 0, 1) : .5, double.IsFinite(Zoom) ? Math.Clamp(Zoom, 1, 5) : 1);
    internal W.Rect Crop(double imageWidth, double imageHeight, double aspect)
    {
        var frame = Normalize();
        if (imageWidth <= 0 || imageHeight <= 0 || aspect <= 0) return new(0, 0, 1, 1);
        double w = Math.Min(imageWidth, imageHeight * aspect) / frame.Zoom, h = w / aspect;
        return new(Math.Clamp(imageWidth * frame.X - w / 2, 0, imageWidth - w),
            Math.Clamp(imageHeight * frame.Y - h / 2, 0, imageHeight - h), w, h);
    }
    internal CoverFraming Pan(double dx, double dy, double width, double height, double imageWidth, double imageHeight)
    {
        var crop = Crop(imageWidth, imageHeight, width / Math.Max(1, height));
        double scale = width / crop.Width;
        // Start from the actual clamped center, so reversing a drag has no dead zone.
        return new CoverFraming((crop.X + crop.Width / 2 - dx / scale) / imageWidth,
            (crop.Y + crop.Height / 2 - dy / scale) / imageHeight, Zoom).Normalize();
    }
}
