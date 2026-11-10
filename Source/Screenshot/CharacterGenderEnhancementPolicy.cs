namespace ScreenshotTranslationUiTester;

// The switch controls request assistance, not the identity of ordinary cached
// translations. Each enabled request keeps its original generation so that
// off -> on never revives an older in-flight result.
internal static class CharacterGenderEnhancementPolicy
{
    internal sealed class Epoch(long number)
    {
        internal readonly long Number = number;
        internal readonly CancellationTokenSource Cancellation = new();
    }
    private static readonly object gate = new();
    private static Epoch current = new(0);
    internal static Epoch Current { get { lock (gate) return current; } }
    internal static bool SupportsLanguage(ApiSettings settings)
        => TranslationLanguagePolicy.Canonical(settings.TargetLanguage) is "en" or "es" or "fr" or "de" or "ru";
    internal static bool Selected(ApiSettings settings) => settings.CharacterGenderEnhancementEnabled && SupportsLanguage(settings);
    internal static bool Revoked(ApiSettings settings) => Selected(settings) && settings.CharacterGenderEnhancementEpoch.Cancellation.IsCancellationRequested;
    internal static bool Applies(ApiSettings settings) => Selected(settings) && !Revoked(settings);
    internal static void ThrowIfRevoked(ApiSettings settings)
    {
        if(Revoked(settings))throw new OperationCanceledException(settings.CharacterGenderEnhancementEpoch.Cancellation.Token);
    }
    internal static CancellationTokenSource Link(ApiSettings settings, CancellationToken token)
        => Selected(settings)
            ? CancellationTokenSource.CreateLinkedTokenSource(token, settings.CharacterGenderEnhancementEpoch.Cancellation.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(token);
    internal static void InvalidateEnabledRequests()
    {
        Epoch previous;
        lock (gate) { previous = current; current = new(previous.Number + 1); }
        // Do not dispose an epoch still referenced by immutable snapshots.
        // Cancellation callbacks belong to request infrastructure, not UI state.
        try { previous.Cancellation.Cancel(); }
        catch (AggregateException error) { AppLog.Write("character-enhancement", "Request cancellation callbacks failed", error); }
    }
}
