namespace ScreenshotTranslationUiTester;

public static class FontSettingsPolicy
{
    public static bool IsInstalled(string? family) => FontManager.IsInstalled(family);
    public static string ResolveFamily(string? requested, string baseline) => FontManager.IsInstalled(requested) ? requested!.Trim() : FontManager.BuildChain(baseline)[0];
    public static Font Create(string? requested, string baseline, float size, FontStyle style = FontStyle.Regular) => FontManager.Create(FontManager.IsInstalled(requested) ? requested : baseline, size, style);
    public static Font CreatePixel(string? requested,string baseline,float size,FontStyle style=FontStyle.Regular)=>FontManager.CreatePixel(FontManager.IsInstalled(requested)?requested:baseline,size,style);
    public static string[] Families() => FontManager.InstalledFamilies();
    public static void ApplyUi(Control root, ApiSettings settings) => FontManager.ApplyUi(root, settings);
    public static void ApplyPreviewText(RichTextBox text, ApiSettings settings) => FontManager.ApplyPreviewText(text, settings);
    public static void ApplyOverlay(RenderSettings render, ApiSettings settings)
    {
        var profile = FontManager.ResolveTranslationImageProfile(settings);
        render.FontFamily = profile.PrimaryFamily;
        if (settings.OverlayFontMode == OverlayFontMode.Custom)
        {
            if (settings.OverlayFontSize > 0) render.FontScale = Math.Clamp(settings.OverlayFontSize / 10F, .5F, 2F);
        }
        switch (settings.OverlayBackgroundStyle)
        {
            case TranslationOverlayBackgroundStyle.Automatic:
                render.BackgroundStrategy=TranslationOverlayBackgroundStyle.Automatic; render.Background=false; break;
            case TranslationOverlayBackgroundStyle.LightOverlay:
                render.BackgroundStrategy=TranslationOverlayBackgroundStyle.LightOverlay; render.Background=true; render.BackgroundOpacity=settings.OverlayBackgroundOpacity; break;
            case TranslationOverlayBackgroundStyle.Solid:
                render.BackgroundStrategy=TranslationOverlayBackgroundStyle.Solid; render.Background=true; render.BackgroundOpacity=255; break;
        }
    }
}
