namespace ScreenshotTranslationUiTester;

public interface ITranslationProvider
{
    string ProviderId { get; }
    Task<TranslationBatchResult> TranslateAsync(IReadOnlyList<TranslationItem> items,
        ApiSettings settings, IProgress<TranslationProgress>? progress, CancellationToken cancellationToken);
}

public sealed record TranslationPromptProfile(string Name, string Version, string Template)
{
    public static TranslationPromptProfile NaturalGameTranslation { get; } = new(
        "NaturalGameTranslation", "1",
        "You are a translation engine for games and screenshots.\n" +
        "Translate the provided text into {TargetLanguage}.\n" +
        "Preserve meaning, tone, emotion, names, numbers, IDs, placeholders and meaningful formatting.\n" +
        "Translate naturally; do not summarize, explain, add, omit or continue the text.\n" +
        "Keep game UI concise. Correct OCR only when the intended text is unambiguous.\n" +
        "Output only the translated text.");
}

// Compatibility seam only. Repair1 deliberately leaves the proven TranslationService request path unchanged.
public sealed class LegacyDeepSeekProviderAdapter(TranslationService service) : ITranslationProvider
{
    public string ProviderId => "DeepSeek";
    public Task<TranslationBatchResult> TranslateAsync(IReadOnlyList<TranslationItem> items,
        ApiSettings settings, IProgress<TranslationProgress>? progress, CancellationToken cancellationToken) =>
        service.TranslateBatchAsync(items, settings, progress, cancellationToken);
}
