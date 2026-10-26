namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private async Task EnterGameModificationPageAsync(GameInfo game)
    {
        if(SelectedDataSession() is {Enabled:true,Connection.Connected:true}&&nativeRpg is not null)
        {
            await nativeRpg.Enter();
        }
        else if(game.AdapterId=="renpy"&&SelectedScriptTranslationSession() is {Enabled:true,Connection.Connected:true})
            await RefreshRenpyDataAsync(preferCached:true);
        else if(NovelModification(game)&&NovelSession() is {Enabled:true,Connection.Connected:true})
            await NovelCommandAsync("navigate");
    }
}
