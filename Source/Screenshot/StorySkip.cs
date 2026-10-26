namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private async Task StorySkipAsync(bool stop)
    {
        if(_selectedGame is not {} game||game.AdapterId!="renpy"||!ModificationAuthorized(game)||ModificationSession(game) is not {} session||!_independentSpeedBusy.Add(game.ExePath))return;
        var authorization=SpeedAuthorization(game);
        if(authorization is null){_independentSpeedBusy.Remove(game.ExePath);return;}
        NotifyNativeGameDetailChanged();
        try
        {
            var result=await session.Connection.RequestAsync(new(){["op"]=stop?"storySkipStop":"storySkip"},authorization.Cancellation.Token);
            if(!result.TryGetProperty(stop?"stopped":"accepted",out var accepted)||accepted.ValueKind!=System.Text.Json.JsonValueKind.True)
                throw new IOException("游戏未确认剧情快进，请重启游戏以加载新版组件。");
            if(CurrentSpeedAuthorization(game,authorization))_independentSpeedMessages[game.ExePath]=stop?"已请求停止剧情快进。":"已请求快进到下一处选择；游戏中的点击也可中止快进。";
        }
        catch(Exception ex){if(CurrentSpeedAuthorization(game,authorization))_independentSpeedMessages[game.ExePath]=SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{_independentSpeedBusy.Remove(game.ExePath);if(!IsDisposed&&!Disposing)NotifyNativeGameDetailChanged();}
    }
}
