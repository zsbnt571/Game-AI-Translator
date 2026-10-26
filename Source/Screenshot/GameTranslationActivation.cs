namespace ScreenshotTranslationUiTester;

// One action policy for all desktop presentations and every registered adapter.
// A pending handshake is distinct from an absent or disconnected live bridge.
internal sealed record GameTranslationActivation(string Text,bool Enabled,string Hint)
{
    internal const string RestartHint="本次游戏尚未连接翻译。请先保存进度，再点击“重启并开启翻译”；软件会正常退出游戏后重新接入。";
    internal const string RestartTitle="重启并开启翻译";
    internal const string ConnectingTitle="正在连接游戏";
    internal const string ConnectingHint="正在等待游戏连接完成；请保持游戏运行。";
    internal const string RestartConfirmation="请先在游戏中保存进度。确定后将正常退出当前游戏，并重新启动以开启翻译。若游戏需要退出确认，请返回游戏处理；等待期间可以取消重启。";
    internal static GameTranslationActivation Resolve(bool connected,bool reconnect,bool ready,bool busy,bool restarting,RpgTranslationSession? translation,bool connecting=false)
    {
        if(restarting)return new("取消重启",true,"正在等待游戏正常退出，退出后将开启翻译。可返回游戏处理退出确认，也可以取消重启。");
        if(connected)return new(TranslationToggleDispatcher.ButtonText(translation),!busy&&(ready||translation?.Enabled==true||translation?.RestorePending==true),"");
        if(connecting)return new(ConnectingTitle,false,ConnectingHint);
        if(reconnect)return new("重新连接翻译",!busy&&ready,"翻译连接已断开，已有译文保留。点击“重新连接翻译”即可继续，无需退出游戏。");
        return new(RestartTitle,!busy&&ready,RestartHint);
    }
}
