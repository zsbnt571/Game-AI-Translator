namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private void RetrySessionTranslationFailures()
    {
        if(_selectedGame is not { } game||SelectedTranslationSession() is not { } data||!data.Connection.Connected||
            data.Translation is not {Enabled:true,RestorePending:false} translation||translation.FailedCount<=0)return;
        var session=_gameSessions.Get(game);if(session.Busy)return;
        int count=translation.FailedCount;
        translation.RetryFailures();
        session.Feedback=$"已安排重试 {count:N0} 条失败项。当前对话会优先处理；仍未通过检查的条目可在“翻译状态”查看原因。";
        RenderGameSessionState();
    }

    private async Task ToggleRpgTranslationAsync()
    {
        if(_selectedGame is not { } game||SelectedTranslationSession() is not { } data||!data.Connection.Connected)return;
        var session=_gameSessions.Get(game);if(session.Busy)return;session.Busy=true;session.Feedback="";RenderGameSessionState();
        try
        {
            session.Feedback=await TranslationToggleDispatcher.ToggleAsync(data,()=>
            {
                _fusion!.RequireGameReady(game.ExePath);var settings=_fusion.GameSettings(UiSettings,game.ExePath);
                return new(data.Connection,game.ExePath,settings,async(text,profile,token)=>(await _translationService.TranslatePlainAsync(text,profile,token)).Text,data.TranslationOptions);
            });
        }
        catch(Exception ex)
        {
            string warning=data.Translation?.CacheWarning??"";
            if(data.Translation?.RestorePending!=true){data.Translation?.Dispose();data.Translation=null;}
            session.Feedback=SafeDiagnosticOutput.ExceptionSummary(ex)+(warning.Length==0?"":" · "+warning);
        }
        finally{session.Busy=false;if(!IsDisposed)RenderGameSessionState();}
    }
}
