namespace ScreenshotTranslationUiTester;

internal static class UnityEmbeddedPayload
{
    internal static Stream Open(string backend,string architecture) => RuntimePayloadProvider.OpenUnity(backend,architecture);
}
