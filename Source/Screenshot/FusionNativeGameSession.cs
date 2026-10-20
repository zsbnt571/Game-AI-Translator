namespace ScreenshotTranslationUiTester;

// State consumed by the native game workspace. These values never hold HWNDs
// or perform layout while a game status/translation task is being updated.
internal class DesktopLabelState
{
    internal string Text { get; set; } = "";
    internal bool Visible { get; set; } = true;
    internal Color ForeColor { get; set; }
}
internal sealed class DesktopActionState(string text) : DesktopLabelState
{
    internal bool Enabled { get; set; } = true;
    internal bool Primary { get; set; }
    internal string InitialText { get; } = text;
}
internal sealed class GameDetailPresentation
{
    internal bool SelectionPending;
    internal bool Modifying, RpgModification, RenpyModification, NovelModification, UnsupportedModification, FileDetails;
    internal DesktopActionState Launch { get; } = new("启动游戏") { Text = "启动游戏" };
    internal DesktopActionState LaunchArrow { get; } = new("") { Text = "" };
    internal DesktopActionState Return { get; } = new("返回游戏") { Text = "返回游戏" };
    internal DesktopActionState Restart { get; } = new("重启游戏") { Text = "重启游戏" };
    internal DesktopActionState Rename { get; } = new("编辑名称") { Text = "编辑名称" };
    internal DesktopActionState CoverEdit { get; } = new("更换封面") { Text = "更换封面" };
    internal DesktopActionState FolderOpen { get; } = new("打开游戏文件夹") { Text = "打开游戏文件夹" };
    internal DesktopActionState TranslationTab { get; } = new("游戏翻译") { Text = "游戏翻译" };
    internal DesktopActionState ModificationTab { get; } = new("游戏修改") { Text = "游戏修改" };
    internal DesktopActionState CompactReturn { get; } = new("返回游戏") { Text = "返回游戏" };
    internal DesktopActionState CompactRestart { get; } = new("重启游戏") { Text = "重启游戏" };
    internal DesktopActionState CompactRename { get; } = new("编辑名称") { Text = "编辑名称" };
    internal DesktopActionState CompactMore { get; } = new("···") { Text = "···" };
    internal DesktopActionState ToggleTranslation { get; } = new("开启翻译") { Text = "开启翻译" };
    internal DesktopActionState TranslateLaunch { get; } = new("翻译并启动") { Text = "翻译并启动" };
    internal DesktopActionState PrepareText { get; } = new("读取文本与准备译文") { Text = "读取文本与准备译文" };
    internal DesktopActionState Restore { get; } = new("恢复游戏文件（保留译文）") { Text = "恢复游戏文件（保留译文）" };
    internal DesktopActionState Install { get; } = new("应用翻译") { Text = "应用翻译" };
    internal DesktopActionState Retry { get; } = new("重试失败项") { Text = "重试失败项" };
    internal DesktopActionState ScanKeyEdit { get; } = new("编辑") { Text = "编辑" };
    internal DesktopActionState ScreenTranslation { get; } = new("屏幕翻译并启动") { Text = "屏幕翻译并启动" };
    internal DesktopLabelState Badge { get; } = new();
    internal DesktopLabelState FontCapability { get; } = new();
    internal DesktopLabelState RenderingCapability { get; } = new();
    internal DesktopLabelState SelectionLabel { get; } = new();
    internal DesktopLabelState Status { get; } = new();
    internal DesktopLabelState CompactTitle { get; } = new();
    internal DesktopLabelState TranslationStatus { get; } = new();
    internal DesktopLabelState ActionFeedback { get; } = new();
    internal DesktopLabelState ScanKey { get; } = new();
    internal DesktopLabelState ScreenStatus { get; } = new();
    internal DesktopLabelState Diagnostic { get; } = new();
    internal readonly DesktopLabelState Info = new(), FileStatus = new(), Media = new(), CoverDetails = new(), Query = new(), Feedback = new(), Configuration = new(), Hint = new();
    internal bool CanOpenDiagnostic;
}

public sealed partial class MainForm
{
    internal GameDetailPresentation GameDetail { get; } = new();
    private static void SetLabelText(DesktopLabelState label,string value) => label.Text=value;
    private void RenderNativeGameSessionState()
    {
        if(_gameDetecting){GameDetail.SelectionPending=true;NotifyNativeGameDetailChanged();return;}
        GameDetail.SelectionPending=false;
        var game=_selectedGame; var state=_desktopGameState;
        GameDetail.Install.Text=state?.Action??"应用翻译";
        var session=game is null?null:_gameSessions.Get(game);
        bool current=SameGame(game?.ExePath,state?.Game.ExePath);
        if(current)session!.Observe(state!.Process);
        bool running=current&&state!.Process==FusionGameProcessState.Running;
        bool stopped=current&&state!.Process==FusionGameProcessState.Stopped&&session?.LaunchPending!=true;
        bool busy=_gameActionRunning||_gameDetecting;
        bool rpg=game?.AdapterId is "rpg-maker-mv" or "rpg-maker-mz";
        bool renpy=game?.AdapterId=="renpy";
        bool embedded=EmbeddedGameAdapters.Handles(game);
        bool supported=GameCapabilities.EmbeddedTranslation(game);
        var translationPresentation=TranslationPresentation(game);
        SetLabelText(GameDetail.Badge,translationPresentation.Badge);
        if(GameDetail.FontCapability is not null)SetLabelText(GameDetail.FontCapability,translationPresentation.Font);
        if(GameDetail.RenderingCapability is not null)SetLabelText(GameDetail.RenderingCapability,translationPresentation.Rendering);
        bool ready=GameTranslationReady(game);
        RenderNativeScreenTranslationState(game,running,stopped,busy,ready);
        if(_wpfDetails&&game is not null)_sessionGamePath=game.ExePath;
        bool defaultTranslation=GameCapabilities.DefaultTranslationLaunch(game,_fusion?.State.DefaultGameLaunchWithTranslation==true);
        GameDetail.Launch.Text=defaultTranslation?"翻译并启动":"启动游戏";
        GameDetail.Launch.Visible=!running; GameDetail.Launch.Enabled=game is not null&&stopped&&!busy&&(!defaultTranslation||ready);
        GameDetail.LaunchArrow!.Visible=!running; GameDetail.LaunchArrow.Enabled=stopped&&!busy;
        bool restarting=IsGameRestartPending(game);
        GameDetail.Return!.Visible=running; GameDetail.Return.Enabled=!busy||restarting;
        GameDetail.Restart!.Visible=running||restarting;GameDetail.Restart.Enabled=restarting||running&&!busy;GameDetail.Restart.Text=restarting?"取消重启":"重启游戏";
        foreach(var button in new[]{GameDetail.Rename,GameDetail.CoverEdit,GameDetail.FolderOpen})if(button is not null)button.Enabled=game is not null&&!busy;
        GameDetail.SelectionLabel.Text=game is null?"双击游戏进入详细主页":game.Name+(GameChineseNameText.Length>0?" · "+GameChineseNameText:"");
        GameDetail.Status.Text=_gameDetecting?"正在识别游戏…":busy?"正在处理此游戏…":session?.LaunchPending==true?"正在等待游戏启动…":running?"游戏运行中":stopped?"游戏未运行":"正在核对游戏状态…";
        if(!busy&&!current&&_gameQueryError.Length>0) GameDetail.Status.Text=_gameQueryError;
        GameDetail.Status.ForeColor=running?Color.SeaGreen:UiTheme.Current.SecondaryText;
        GameDetail.TranslationTab!.Enabled=GameDetail.ModificationTab!.Enabled=game is not null&&!_gameDetecting;
        int section=session?.Section??1;
        var modifying=section==2;
        GameDetail.Modifying=modifying;
        GameDetail.CompactTitle.Visible=modifying;GameDetail.CompactTitle.Text=game?.Name??"";
        GameDetail.CompactReturn.Visible=modifying&&running;GameDetail.CompactReturn.Enabled=!busy||restarting;
        GameDetail.CompactRestart.Visible=modifying&&(running||restarting);GameDetail.CompactRestart.Enabled=restarting||running&&!busy;GameDetail.CompactRestart.Text=restarting?"取消重启":"重启游戏";
        GameDetail.CompactRename.Visible=modifying;GameDetail.CompactRename.Enabled=game is not null&&!busy;
        GameDetail.CompactMore.Visible=modifying;
        GameDetail.TranslationTab.Primary=section==1;GameDetail.ModificationTab.Primary=section==2;
        GameDetail.RpgModification=modifying&&rpg;GameDetail.RenpyModification=modifying&&renpy;
        GameDetail.NovelModification=modifying&&NovelModification(game);GameDetail.UnsupportedModification=modifying&&!rpg&&!renpy&&!NovelModification(game);
        bool prepared=session?.LaunchMode==GameLaunchMode.Translation;
        bool requested=session?.TranslationRequested==true;
        if(!rpg&&!renpy&&!embedded)
        {
        GameDetail.ToggleTranslation!.Visible=running&&!prepared;
        GameDetail.ToggleTranslation.Enabled=supported&&!busy&&!requested;
        GameDetail.ToggleTranslation.Text=requested?"等待退出后启用":"开启翻译";
        GameDetail.TranslationStatus.Text=!supported?"此游戏暂不支持内嵌翻译。":prepared&&(running||session?.LaunchPending==true)?
            "已随本次启动配置翻译 · 实际效果以游戏内文字为准。":requested?
            (running?"当前适配需要重启才能启用。请保存并自行退出游戏，再点击“翻译并启动”。":"已保留开启翻译的选择，可以翻译并启动。"):
            running?(session?.LaunchMode==GameLaunchMode.Ordinary?"翻译未开启 · 当前适配需退出游戏后启用。":"本次启动方式未确认 · 当前适配需退出游戏后应用翻译。"):
            "按需选择翻译方案，普通启动不会开启翻译。";
        if(prepared&&(running||session?.LaunchPending==true)&&session!.AppliedProfileName is { } applied)
        {
            GameDetail.TranslationStatus.Text="本次启动方案："+applied+" · 实际效果以游戏内文字为准。";
            if(session.AppliedSettingsIdentity!=FusionConfiguration.EditIdentity(EmbeddedSettings))
                GameDetail.TranslationStatus.Text+="\n当前所选方案将在下次翻译启动时应用。";
        }
        }
        GameDetail.TranslateLaunch!.Visible=!running; GameDetail.TranslateLaunch.Enabled=stopped&&supported&&ready&&!busy;
        bool canPrepare=game is not null&&CatalogTranslationPreparation.Supports(game.Engine);
        if(GameDetail.PrepareText is not null){GameDetail.PrepareText.Visible=canPrepare;GameDetail.PrepareText.Enabled=!busy;}
        if(canPrepare&&!supported&&game is not null){GameDetail.TranslateLaunch.Visible=false;GameDetail.ToggleTranslation!.Visible=false;GameDetail.TranslationStatus!.Text=game.Engine+" · 可读取文本并准备译文；游戏内显示尚未接入。";}
        GameDetail.Restore!.Enabled=(rpg||current&&state?.HasRecord==true)&&stopped&&!busy;
        GameDetail.Install!.Enabled=supported&&ready&&stopped&&!busy;
        GameDetail.Install.Visible=supported&&stopped;
        GameDetail.Retry!.Visible=rpg&&SelectedDataSession()?.Translation?.Enabled==true&&SelectedDataSession()?.Translation?.FailedCount>0;
        if(rpg||renpy||embedded)
        {
            var data=SelectedTranslationSession();bool connected=running&&data?.Connection.Connected==true;
            GameDetail.ToggleTranslation!.Visible=running;
            bool restorePending=data?.Translation?.RestorePending==true;
            bool reconnect=renpy&&running&&!connected&&RenpyReconnect.Peer(game!.ExePath) is not null;
            GameDetail.ToggleTranslation.Enabled=!busy&&(reconnect&&ready|| (restorePending?connected:translationPresentation.CanToggle&&(ready||data?.Translation?.Enabled==true)));
            GameDetail.ToggleTranslation.Text=connected?TranslationToggleDispatcher.ButtonText(data?.Translation):reconnect?"重新连接翻译":"翻译未接入";
            GameDetail.TranslationStatus.Text=restorePending?data!.Translation!.Status:connected?(data!.Translation?.Status??"游戏已连接，翻译关闭。可在游玩时开启，无需重启。"):
                !string.IsNullOrEmpty(session?.TranslationBlockedReason)?"本次翻译未接入："+session.TranslationBlockedReason:
                reconnect?"翻译连接已断开，已有译文保留。点击“重新连接翻译”即可继续，无需退出游戏。":
                running?(busy?"正在连接游戏…":"此游戏尚未建立翻译连接。保存退出后，可从本软件重新启动并检查连接结果。"):
                translationPresentation.PendingVerification?"已识别到适配方式。点击“翻译并启动”，连接后会自动开启翻译；首次使用请进入菜单或对话查看效果。":
                "点击“翻译并启动”可连接游戏并开启翻译；普通启动后，也可在连接成功时手动开启。";
            if(connected&&data?.Translation is {Enabled:true,RestorePending:false} activeTranslation)
            {
                var progress=TranslationProgressPresentation.Describe(activeTranslation.Snapshot);
                GameDetail.TranslationStatus.Text=TranslationProgressPresentation.Overview(activeTranslation.Snapshot);
                if(activeTranslation.CacheWarning.Length>0)GameDetail.TranslationStatus.Text+="\n"+activeTranslation.CacheWarning;
                if(activeTranslation.Snapshot.RuntimeNote.Length>0)GameDetail.TranslationStatus.Text+="\n"+activeTranslation.Snapshot.RuntimeNote;
            }
            if(!ready&&data?.Translation?.Enabled!=true)
                GameDetail.TranslationStatus.Text="翻译方案尚未配置完整。请点击方案旁的“编辑”，补齐提示的必填项。\n"+GameDetail.TranslationStatus.Text;
            GameDetail.Retry!.Visible=connected&&data?.Translation?.Enabled==true&&data.Translation.FailedCount>0;
            GameDetail.Retry.Enabled=!busy&&data?.Translation?.RestorePending!=true;
            GameDetail.Restore!.Enabled=stopped&&!busy&&(embedded?state?.HasRecord==true:!renpy||RenpyGameAdapter.HasRecord(game!.ExePath));
        }
        GameDetail.ActionFeedback.Text=session?.Feedback??""; GameDetail.ActionFeedback.Visible=GameDetail.ActionFeedback.Text.Length>0;
        if(GameDetail.ScanKey is not null)GameDetail.ScanKey.Text=rpg||renpy||embedded?
            translationPresentation.CanToggle?"使用本页翻译开关":translationPresentation.Blocked?"当前无法开关翻译":"接入后可用":
            GameCapabilities.TranslationToggleKey(game)?_fusion?.State.EmbeddedToggleKey??"F8":"当前引擎尚无内嵌翻译开关";
        if(GameDetail.ScanKeyEdit is not null)GameDetail.ScanKeyEdit.Visible=GameCapabilities.TranslationToggleKey(game);
        var runningPaths=_gameSessions.Snapshot().Where(x=>x.Process==FusionGameProcessState.Running).Select(x=>x.Game.ExePath).ToArray();
        _libraryGrid.UpdateRunning(runningPaths);_recentShelf.UpdateRunning(runningPaths);_libraryNavigation.UpdateRunning(runningPaths);
        RenderGameModificationState();
        RenderNovelModificationState();
        RenderRenpyModificationState();
        RenderNativeGameDiagnosticState();
        NotifyNativeGameDetailChanged();
    }

    private void RenderNativeScreenTranslationState(GameInfo? game,bool running,bool stopped,bool busy,bool ready)
    {
        if(GameDetail.ScreenTranslation is null)return;
        var active=game is null?null:_manualScreenSessions.GetValueOrDefault(game.ExePath);
        bool changing=game is not null&&_manualScreenOperations.Contains(game.ExePath);
        GameDetail.ScreenTranslation.Visible=game is not null;
        GameDetail.ScreenTranslation.Enabled=game is not null&&!changing&&(active is not null||!busy&&ready&&(running||stopped));
        GameDetail.ScreenTranslation.Text=active is not null?"关闭屏幕翻译":running?"开启屏幕翻译":"屏幕翻译并启动";
        string message=game is null?"":_manualScreenFeedback.GetValueOrDefault(game.ExePath,"手动开启后，在游戏画面上显示译文。");
        if(active is not null)
        {
            var translation=active.Data.Translation!;
            message=translation.Enabled?"屏幕翻译已开启 · "+active.ProfileName+" · "+((ScreenTranslationConnection)active.Data.Connection).Status:translation.Status;
            if(translation.Enabled&&translation.FailedCount>0)message+=$" · {translation.FailedCount} 条翻译失败，可关闭后重新开启重试";
            if(translation.CacheWarning.Length>0)message+=" · "+translation.CacheWarning;
        }
        SetLabelText(GameDetail.ScreenStatus,message);
    }
    private void RenderNativeGameDiagnosticState()
    {
        if(_selectedGame is not { } game) { GameDetail.Diagnostic.Text=""; GameDetail.CanOpenDiagnostic=false; return; }
        try
        {
            var report=CrashMonitor.Latest(game.ExePath);bool tracking=CrashMonitor.IsTracking(game.ExePath);
            GameDetail.Diagnostic.Text=(tracking?"退出监测中。 ":"")+(report is null ? tracking?"退出后自动保存诊断报告。":"从本软件启动游戏后，自动监测退出并保存诊断报告。" : "最近报告："+report.Summary);
            GameDetail.CanOpenDiagnostic=report is not null;
        }
        catch { GameDetail.Diagnostic.Text="诊断记录暂不可读取。";GameDetail.CanOpenDiagnostic=false; }
    }
}
