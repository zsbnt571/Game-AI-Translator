namespace ScreenshotTranslationUiTester;

public enum UiIconColorMode { Monochrome, Original }

// Shared preference for app-owned icons. New icon families can opt in without
// adding per-page appearance settings; user-provided game artwork is unchanged.
internal static class UiIconAppearance
{
    internal static UiIconColorMode Current { get; private set; }
    internal static int Size { get; private set; } = 17;
    internal static void Apply(UiIconColorMode mode,int size=17)
    {Current=Enum.IsDefined(mode)?mode:UiIconColorMode.Monochrome;Size=Math.Clamp(size,16,28);}
}
