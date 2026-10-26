namespace ScreenshotTranslationUiTester;

internal enum GameDetailAction { Back, Rename, Cover, Folder, Launch, Return, Restart, Toggle, TranslateLaunch, Install, Retry, PrepareText, Manage, Diagnostics, Report, Keys, Screen, Profile, Files, TranslationTab, ModificationTab, CaptureCover, Exit, Font }

public sealed partial class MainForm
{
    internal async void NativeGameAction(GameDetailAction action)
    {
        try
        {
            switch(action)
            {
                case GameDetailAction.Back: ShowLibraryHome(); break;
                case GameDetailAction.Rename: await EditLibraryNoteAsync(); break;
                case GameDetailAction.Cover: await EditGameArtworkAsync(); break;
                case GameDetailAction.CaptureCover: await EditGameArtworkAsync(capture:true); break;
                case GameDetailAction.Folder: await OpenSelectedGameFolderAsync(); break;
                case GameDetailAction.Launch:
                    if(!GameDetail.SelectionPending&&GameDetail.Launch.Visible&&GameDetail.Launch.Enabled)
                        await StartLibraryGameAsync();
                    break;
                case GameDetailAction.Return: await ReturnToSelectedGameAsync(); break;
                case GameDetailAction.Restart: await RestartSelectedGameAsync(); break;
                case GameDetailAction.Exit:
                    if(!GameDetail.SelectionPending&&GameDetail.Exit.Visible&&GameDetail.Exit.Enabled&&_selectedGame is {} exitGame)
                        await RequestGameExitAsync(exitGame);
                    break;
                case GameDetailAction.Toggle: await RequestGameSessionTranslationAsync(); break;
                case GameDetailAction.TranslateLaunch: await RunGameActionAsync("launch"); break;
                case GameDetailAction.Install: await RunGameActionAsync("install"); break;
                case GameDetailAction.Retry: RetrySessionTranslationFailures(); break;
                case GameDetailAction.PrepareText: if(_selectedGame is {} game)OpenTextPreparation(game); break;
                case GameDetailAction.Manage: OpenTranslationManager(); break;
                case GameDetailAction.Diagnostics: ShowTranslationDiagnostics(); break;
                case GameDetailAction.Report: ShowGameDiagnosticReport(); break;
                case GameDetailAction.Keys: OpenKeysSettings(); break;
                case GameDetailAction.Screen: await RunGameActionAsync("screen"); break;
                case GameDetailAction.Profile: EditModeProfile(true); break;
                case GameDetailAction.Font: if(GameDetail.FontEdit.Enabled)EditGameFont(); break;
                case GameDetailAction.Files: GameDetail.FileDetails=!GameDetail.FileDetails; break;
                case GameDetailAction.TranslationTab: SelectGameSessionSection(1); break;
                case GameDetailAction.ModificationTab: SelectGameSessionSection(2); break;
            }
        }
        catch(Exception ex){SetGameActionFeedback(SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally { NotifyNativeGameDetailChanged(); }
    }
    internal void NativeGameMenu(string menu,System.Windows.FrameworkElement anchor)
    {
        if(menu=="launch")ShowGameLaunchMenu(null,anchor);
        else ShowSessionMenu(null,anchor);
    }
    internal string NativeGameProfileSummary => _fusion is null ? "" : EmbeddedSettings.Model+" · "+TargetLanguageDisplay.DisplayFromCode(EmbeddedSettings.TargetLanguage);
    private void SetGameActionFeedback(string text)
    {
        if(!WpfWorkspace.Enabled){_sessionActionFeedback.Text=text;return;}
        GameDetail.ActionFeedback.Text=text;GameDetail.ActionFeedback.Visible=text.Length>0;NotifyNativeGameDetailChanged();
    }
    private void SetGameTranslationFeedback(string text)
    { if(!WpfWorkspace.Enabled)_sessionTranslationState.Text=text;else{GameDetail.TranslationStatus.Text=text;NotifyNativeGameDetailChanged();} }

    private void RenderNativeGameState()
    {
        var game=_selectedGame;var s=_desktopGameState;
        var status=game is null?"尚未选择游戏":s is null?"正在核对插件与配置…":(s.Installed?"已安装":s.HasRecord?"安装需核对":"未安装")+" · "+(s.Process switch{
            FusionGameProcessState.Stopped=>"未检测到运行",FusionGameProcessState.Running=>"游戏运行中",_=>"同名进程路径无法确认，暂不能安装、启动或恢复"});
        if(_gameDetecting)status="正在识别游戏…";else if(_gameActionRunning)status="正在处理，请稍候…";
        if(_launchFeedbackUntil!=default&&(DateTime.UtcNow>=_launchFeedbackUntil||s?.Process==FusionGameProcessState.Running)){_gameOperationResult=game is null?"":_gameSessions.Get(game).Feedback;_launchFeedbackUntil=default;}
        SetGameFeedbackText(_gameOperationResult);
        if(_gameQueryError.Length>0)status+="\n"+_gameQueryError;
        SetGameFileStatusText(status);
        SetGameConfigurationText(s?.ConfigurationMatches switch{
            true=>"方案已写入 · 运行时加载尚未确认。",false=>"有更改待应用 · 写入后在游戏下次启动时生效。",
            _=>game is null?"方案保存在软件中，选择游戏后可应用。":"方案保存在软件中；游戏配置尚未确认。"});
        SetGameQueryText(s is not null?"实际插件："+s.Plugin+"\n"+s.Detail:"尚无完整核对结果。");
        var hint=s?.ActionHint??"";var missing=_desktopReady?FusionConfiguration.Missing(EmbeddedSettings):new List<string>();
        if(missing.Count>0)hint+=(hint.Length>0?"\n":"")+"方案尚缺："+string.Join("、",missing)+"。请在“编辑”中补全后应用。";
        SetGameHintText(hint);RenderNativeGameSessionState();
    }
}
