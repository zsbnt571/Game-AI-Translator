namespace ScreenshotTranslationUiTester;

public static class RegionDisplayMapping
{
    public static RectangleF? Resolve(RecognitionRegion region, ImageViewMode mode) =>
        mode==ImageViewMode.Translated ? region.TranslationRenderRect : region.BoundingBox;
}
