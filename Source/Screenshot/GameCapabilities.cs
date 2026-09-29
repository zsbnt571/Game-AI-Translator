namespace ScreenshotTranslationUiTester;

// Engine detection is descriptive. Only a registered adapter path grants an
// action; a newly recognised engine must not inherit another engine's controls.
internal static class GameCapabilities
{
    internal static bool EmbeddedTranslation(GameInfo? game) => game?.AdapterId switch
    {
        "rpg-maker-mv" or "rpg-maker-mz" or "renpy" => true,
        "godot3" or "godot4" or "unity-mono" or "unity-il2cpp" or "unreal" => game.Support==SupportLevel.Candidate,
        "mgi" or "cloud-meadow" => game.Engine == "Unity Mono"
            && (game.Support is SupportLevel.Supported or SupportLevel.Candidate),
        _ => false
    };

    internal static bool TranslationToggleKey(GameInfo? game) => EmbeddedTranslation(game)
        && (game!.AdapterId is "mgi" or "cloud-meadow");

    internal static bool DefaultTranslationLaunch(GameInfo? game, bool requested)
        => requested && EmbeddedTranslation(game);
}

public sealed partial class MainForm
{
    private bool GameTranslationReady(GameInfo? game)
    {
        if (game is null || _fusion is null) return false;
        try
        {
            var settings = _fusion.GameSettings(UiSettings, game.ExePath);
            return FusionConfiguration.Missing(settings).Count == 0 && !string.IsNullOrEmpty(settings.ApiKey);
        }
        catch (InvalidOperationException) { return false; }
        catch (KeyNotFoundException) { return false; }
    }
}
