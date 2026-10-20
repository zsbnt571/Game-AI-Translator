namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private async Task EnterGameModificationPageAsync(GameInfo game)
    {
        if(SelectedDataSession() is {Connection.Connected:true} data&&nativeRpg is not null)
        {
            if(!data.ModificationEntered){data.ModificationEntered=true;data.EnableOnEntry=true;}
            await nativeRpg.Enter();
        }
        else if(game.AdapterId=="renpy"&&SelectedScriptTranslationSession()?.Connection.Connected==true)
            await RefreshRenpyDataAsync(preferCached:true);
        else if(NovelModification(game)&&NovelSession() is {Enabled:true,Connection.Connected:true})
            await NovelCommandAsync("navigate");
    }
}
