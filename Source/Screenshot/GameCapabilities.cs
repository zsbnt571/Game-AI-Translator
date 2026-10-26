namespace ScreenshotTranslationUiTester;

// Engine detection is descriptive. Only a registered adapter path grants an
// action; a newly recognised engine must not inherit another engine's controls.
internal static class GameCapabilities
{
    internal static bool EmbeddedTranslation(GameInfo? game) => game?.AdapterId switch
    {
        "rpg-maker-mv" or "rpg-maker-mz" or "renpy" => true,
        "godot3" or "godot4" or "unity-mono" or "unity-il2cpp" or "unreal" or "gamemaker" or "web-rpg" or "web-construct" or "web-tyrano" or "web-vnmaker" or "web-choicescript" or "defold" or "wolf" or "yukar-bakin" or "rgss" => game.Support==SupportLevel.Candidate,
        _ => false
    };

    internal static bool TranslationToggleKey(GameInfo? game) => false;

    // These adapters have an authenticated host connection that can remain idle
    // until translation or modification is explicitly enabled.
    internal static bool DormantTranslationRuntime(GameInfo? game) => EmbeddedTranslation(game)
        && (game!.AdapterId is "rpg-maker-mv" or "rpg-maker-mz" or "renpy" || EmbeddedGameAdapters.Handles(game));

    internal static bool DefaultTranslationLaunch(GameInfo? game, bool requested)
        => requested && EmbeddedTranslation(game);
}

public sealed partial class MainForm
{
    private bool GameTranslationReady(GameInfo? game)
        => _fusion is not null && GameTranslationReady(_fusion, UiSettings, game);

    // Shared with guarded verification; this is the same readiness check used by the visible launch controls.
    internal static bool GameTranslationReady(FusionConfiguration? configuration, ApiSettings basis, GameInfo? game)
    {
        if (game is null || configuration is null) return false;
        try
        {
            var settings = configuration.GameSettings(basis, game.ExePath);
            return FusionConfiguration.Missing(settings).Count == 0;
        }
        catch (InvalidOperationException) { return false; }
        catch (KeyNotFoundException) { return false; }
    }
}

