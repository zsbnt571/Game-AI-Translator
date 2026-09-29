namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private async Task ToggleRpgTranslationAsync()
    {
        if(_selectedGame is not { } game||SelectedTranslationSession() is not { } data||!data.Connection.Connected)return;
        var session=_gameSessions.Get(game);if(session.Busy)return;session.Busy=true;session.Feedback="";RenderGameSessionState();
        try
        {
            if(data.Translation is {Enabled:true} active)
            {
                await active.StopAsync();
                session.Feedback=active.CacheWarning;
                active.Dispose();data.Translation=null;
            }
            else
            {
                _fusion!.RequireGameReady(game.ExePath);var settings=_fusion.GameSettings(UiSettings,game.ExePath);
                data.Translation?.Dispose();data.Translation=new(data.Connection,game.ExePath,settings,async(text,profile,token)=>(await _translationService.TranslatePlainAsync(text,profile,token)).Text,data.TranslationOptions);
                await data.Translation.StartAsync();session.Feedback="";
            }
        }
        catch(Exception ex)
        {
            string warning=data.Translation?.CacheWarning??"";
            data.Translation?.Dispose();data.Translation=null;
            session.Feedback=SafeDiagnosticOutput.ExceptionSummary(ex)+(warning.Length==0?"":" · "+warning);
        }
        finally{session.Busy=false;if(!IsDisposed)RenderGameSessionState();}
    }
}
