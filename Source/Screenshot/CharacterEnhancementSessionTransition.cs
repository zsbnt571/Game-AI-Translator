namespace ScreenshotTranslationUiTester;

internal static class CharacterEnhancementSessionTransition
{
    internal static async Task<bool> ApplyAsync(GameDataSession data, bool enabled, Func<bool>? latestSelection=null)
    {
        var previous=data.Translation;
        if(previous is null||!previous.NeedsCharacterEnhancementTransition(enabled))return false;
        // Stop cancels provider waiters, confirms the bridge's original-text
        // restoration and drains the old cache writer before a new reader starts.
        // It does not restart the game, change fonts, or change modification state.
        previous.BeginCharacterEnhancementTransition();
        await previous.StopAsync();
        if(!ReferenceEquals(data.Translation,previous))throw new InvalidOperationException("翻译会话已改变，请重试。");
        var next=previous.WithCharacterEnhancement(latestSelection?.Invoke()??enabled);
        next.BeginCharacterEnhancementTransition();
        previous.Dispose();data.Translation=next;
        await next.StartAsync();
        next.CompleteCharacterEnhancementTransition();
        return true;
    }
}

public sealed partial class MainForm
{
    private readonly SemaphoreSlim characterEnhancementTransition=new(1,1);
    // Called after the shared translation-content setting was durably saved.
    // All active game/screen sessions are considered, not only the selected card.
    internal async Task<string> ApplyCharacterGenderEnhancementChangeAsync(bool enabled)
    {
        await characterEnhancementTransition.WaitAsync();
        try
        {
            if(_fusion?.State.TranslationContent?.CharacterGenderEnhancementEnabled!=enabled)return "";
            var targets=_dataSessions.Concat(_scriptTranslationSessions).Concat(_embeddedTranslationSessions)
                .Concat(_manualScreenSessions.Select(p=>new KeyValuePair<string,GameDataSession>(p.Key,p.Value.Data)))
                .GroupBy(p=>p.Value).Select(g=>g.First()).ToArray();
            var failures=new List<string>();
            foreach(var target in targets)
            {
                enabled=_fusion?.State.TranslationContent?.CharacterGenderEnhancementEnabled??false;
                var data=target.Value;
                if(data.Translation?.NeedsCharacterEnhancementTransition(enabled)!=true)continue;
                var state=_gameSessions.Find(target.Key);
                // Give an already-running connection operation a bounded chance
                // to finish. Never silently skip an active translated game.
                for(int count=0;state?.Busy==true&&count<100;count++)await Task.Delay(50);
                if(state?.Busy==true){failures.Add("游戏操作尚未完成，请稍后重试。");continue;}
                if(state is not null)state.Busy=true;
                try
                {
                    await CharacterEnhancementSessionTransition.ApplyAsync(data,enabled,()=>_fusion?.State.TranslationContent?.CharacterGenderEnhancementEnabled??false);
                    if(state is not null)state.Feedback=data.Translation?.CacheWarning??"";
                }
                catch(Exception error)
                {
                    string message=SafeDiagnosticOutput.ExceptionSummary(error);
                    failures.Add(message);if(state is not null)state.Feedback=message;
                }
                finally { if(state is not null)state.Busy=false; }
            }
            if(!IsDisposed&&!Disposing)RenderGameSessionState();
            return string.Join(" · ",failures.Distinct(StringComparer.Ordinal));
        }
        finally { characterEnhancementTransition.Release(); }
    }
}

internal sealed partial class RpgTranslationSession
{
    private CancellationTokenRegistration enhancementRevocation;
    private volatile bool runningWhenEnhancementRevoked;
    private volatile bool characterEnhancementTransitionPending;
    internal void BeginCharacterEnhancementTransition()=>characterEnhancementTransitionPending=true;
    internal void CompleteCharacterEnhancementTransition()=>characterEnhancementTransitionPending=false;
    internal bool NeedsCharacterEnhancementTransition(bool enabled)
        =>CharacterGenderEnhancementPolicy.SupportsLanguage(cacheSettings)
            &&(characterEnhancementTransitionPending||cacheSettings.CharacterGenderEnhancementEnabled!=enabled||CharacterGenderEnhancementPolicy.Revoked(cacheSettings))
            &&(Enabled||runningWhenEnhancementRevoked||characterEnhancementTransitionPending);
    internal RpgTranslationSession WithCharacterEnhancement(bool enabled)
    {
        var settings=ApiSettingsSnapshot.Copy(cacheSettings);
        settings.CharacterGenderEnhancementEnabled=enabled;
        settings.CharacterGenderEnhancementEpoch=CharacterGenderEnhancementPolicy.Current;
        return new(connection,exe,settings,translate,options);
    }
}
