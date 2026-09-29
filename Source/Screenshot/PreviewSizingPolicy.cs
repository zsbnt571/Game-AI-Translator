namespace ScreenshotTranslationUiTester;

public static class PreviewSizingPolicy
{
    public static Size Resolve(PreviewWindowSizingMode mode, Size image, Size workingArea,
        Size fixedSize, Size previousSize, Size minimum)
    {
        var maximum = new Size(Math.Max(minimum.Width, (int)(workingArea.Width * .9)),
            Math.Max(minimum.Height, (int)(workingArea.Height * .9)));
        Size Clamp(Size value) => new(Math.Clamp(value.Width, minimum.Width, maximum.Width),
            Math.Clamp(value.Height, minimum.Height, maximum.Height));
        if (mode == PreviewWindowSizingMode.CurrentAuto)
        {
            var targetHeight=Math.Clamp((int)(workingArea.Height*.76),minimum.Height,maximum.Height);
            var imageWidth=image.Height<=0?700:(int)(targetHeight*(image.Width/(float)image.Height));
            return Clamp(new(Math.Max(minimum.Width,imageWidth+390),targetHeight));
        }
        if (mode == PreviewWindowSizingMode.Fixed) return Clamp(fixedSize);
        return Clamp(previousSize);
    }
}
