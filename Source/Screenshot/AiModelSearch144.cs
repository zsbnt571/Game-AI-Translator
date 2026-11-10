namespace ScreenshotTranslationUiTester;

// Shared by the settings ComboBox and the workspace's window-contained model picker.
internal static class AiModelSearch144
{
    internal static IReadOnlyList<AiModelDescriptor> Filter(IEnumerable<AiModelDescriptor> catalog, string? query)
    {
        string text = (query ?? "").Trim();
        return catalog.Where(x => x.SupportsTextOutput != false && x.CatalogCategory is not ("audio" or "image" or "video" or "embedding" or "moderation") &&
            (text.Length == 0 || x.Id.Contains(text, StringComparison.OrdinalIgnoreCase) ||
             x.Name.Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
}
