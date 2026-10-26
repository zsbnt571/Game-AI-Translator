namespace ScreenshotTranslationUiTester;

// One queue/session owns the pause. A bad key in game A never pauses game B.
// Cached display updates continue while provider work waits.
internal sealed class TranslationProviderPause
{
    private DateTime until;
    private bool authentication;
    internal void Observe(string code,DateTime now)
    {
        if(code=="provider-authentication")authentication=true;
        if(code=="provider-rate-limited"&&until<now.AddSeconds(30))until=now.AddSeconds(30);
    }
    internal bool Blocked(DateTime now)=>authentication||now<until;
    internal void Retry(){authentication=false;until=default;}
    internal string Status(DateTime now)=>authentication?"API 认证失败，已暂停新请求；检查方案后点击重试。":
        now<until?$"API 暂时限流，{Math.Max(1,(int)Math.Ceiling((until-now).TotalSeconds))} 秒后自动继续；已有译文仍可显示。":"";
}
