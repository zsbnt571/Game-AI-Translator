using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly GameWorkspaceSessions _gameSessions = new();
    private Control? _libraryBody;
    private WorkspaceScrollView? _gameSessionPage;
    private string? _sessionGamePath;
    private bool _sessionPolling;
    private readonly Label _sessionStatus = WpfWorkspace.Enabled ? null! : WorkspaceLabel("");
    private readonly Label _sessionTranslationState = WpfWorkspace.Enabled ? null! : WorkspaceLabel("");
    private readonly Label _sessionActionFeedback = WpfWorkspace.Enabled ? null! : WorkspaceLabel("");
    private Control? _sessionTranslationPanel, _sessionModificationPanel;
    private GameActionButton? _sessionTranslationChoice, _sessionModificationChoice;
    private Button? _sessionRetryTranslation;
    private Button? _sessionScreenTranslation;
    private readonly Label _sessionScreenTranslationState=WpfWorkspace.Enabled ? null! : WorkspaceLabel("");
    private Button? _sessionEnableTranslation, _sessionLaunchArrow, _sessionReturnGame;
    private readonly Dictionary<string, GameWorkspaceSession> _gameRootOperations = new(StringComparer.OrdinalIgnoreCase);
    private TableLayoutPanel? _sessionContent;
    private Control? _sessionHero,_sessionFilesRow,_sessionPathField,_sessionStatusRow,_sessionFileDetails;
    private readonly Label _sessionCompactTitle=WpfWorkspace.Enabled ? null! : WorkspaceLabel("");
    private GameActionButton? _sessionCompactReturn,_sessionCompactMore,_sessionRestart,_sessionCompactRestart,_sessionExit,_sessionCompactExit,_sessionCompactLaunch,_sessionCompactLaunchArrow;
    private GameActionButton? _sessionRename,_sessionCompactRename,_sessionCoverEdit,_sessionFolderOpen;

    private void BuildGameSessionWorkspace(Panel page)
    {
        FormClosed+=(_,_)=>_independentSpeedRuntime?.Dispose();
        FormClosed+=(_,_)=>{foreach(var session in _scriptTranslationSessions.Values)session.Dispose();};
        FormClosed+=(_,_)=>{foreach(var session in _embeddedTranslationSessions.Values)session.Dispose();};
        FormClosed+=(_,_)=>{_runtimeTranslationWindow?.Close();_nativeRuntimeTranslationWindow?.Close();};
        FormClosed+=(_,_)=>DisposeManualScreenSessions();
        if(WpfWorkspace.Enabled)
        {
            nativeRpg=new(this);
            FormClosed+=(_,_)=>{nativeRpg.Dispose();foreach(var session in _dataSessions.Values)session.Dispose();};
            FormClosed+=(_,_)=>_gameCrashMonitor?.Dispose();
            return;
        }
        _gameSessionPage=new WorkspaceScrollView{Dock=DockStyle.Fill,AutoScroll=false,Padding=new(24,14,24,12),Visible=false};
        var content=new WorkspaceContentColumn();content.Dock=DockStyle.Top;content.AutoSize=true;content.AutoSizeMode=AutoSizeMode.GrowAndShrink;
        _sessionContent=content;
        var compactMore=IconButton("…","",(_,_)=>{},false,42);compactMore.Click+=(_,_)=>ShowSessionMenu(compactMore);
        _sessionCompactMore=compactMore;compactMore.Visible=false;compactMore.AccessibleName="游戏操作";
        var back=IconButton("返回游戏库","back",(_,_)=>ShowLibraryHome());back.LogicalHeight=28;back.RefreshMetrics();_sessionCompactReturn=IconButton("返回游戏","play",async(_,_)=>await ReturnToSelectedGameAsync(),false,98);
        _sessionCompactReturn.LogicalHeight=28;_sessionCompactReturn.RefreshMetrics();_sessionCompactReturn.Visible=false;
        _sessionCompactTitle.AutoEllipsis=true;_sessionCompactTitle.AutoSize=false;_sessionCompactTitle.Height=28;
        _sessionCompactLaunch=IconButton("启动游戏","play",async(_,_)=>await StartLibraryGameAsync(),true,120);_sessionCompactLaunch.LogicalHeight=28;_sessionCompactLaunch.RefreshMetrics();_sessionCompactLaunch.Visible=false;
        _sessionCompactLaunchArrow=IconButton("","down",(_,_)=>ShowGameLaunchMenu(_sessionCompactLaunchArrow!),true,32);_sessionCompactLaunchArrow.LogicalHeight=28;_sessionCompactLaunchArrow.RefreshMetrics();_sessionCompactLaunchArrow.Visible=false;
        _sessionCompactRestart=IconButton("重启游戏","refresh",async(_,_)=>await RestartSelectedGameAsync(),false,98);_sessionCompactRestart.LogicalHeight=28;_sessionCompactRestart.RefreshMetrics();_sessionCompactRestart.Visible=false;
        _sessionCompactExit=IconButton("退出游戏","close",async(_,_)=>{if(_selectedGame is {} game)await RequestGameExitAsync(game);},false,98);_sessionCompactExit.LogicalHeight=28;_sessionCompactExit.RefreshMetrics();_sessionCompactExit.Visible=false;
        _sessionCompactRename=IconButton("编辑名称","edit",async(_,_)=>await EditLibraryNoteAsync(),false,90);_sessionCompactRename.LogicalHeight=28;_sessionCompactRename.RefreshMetrics();_sessionCompactRename.Visible=false;
        var compactActions=WorkspaceRow(_sessionCompactLaunch,_sessionCompactLaunchArrow,_sessionCompactReturn,_sessionCompactRestart,_sessionCompactExit,compactMore);compactActions.Name="SessionCompactActions";compactActions.Dock=DockStyle.None;compactActions.WrapContents=false;compactActions.Margin=Padding.Empty;
        var backRow=WorkspaceRow(back,_sessionCompactTitle,_sessionCompactRename,compactActions);backRow.Name="SessionCompactHeader";backRow.Margin=Padding.Empty;_sessionCompactTitle.Visible=false;
        backRow.SizeChanged+=(_,_)=>{int width=Math.Clamp(backRow.ClientSize.Width-500*DeviceDpi/96,120*DeviceDpi/96,360*DeviceDpi/96);if(_sessionCompactTitle.Width!=width)_sessionCompactTitle.Width=width;};WorkspaceAdd(content,backRow);
        var hero=new WorkspaceSurface{Height=166,Padding=new(1),Margin=new(0,8,0,12)};hero.Controls.Add(_libraryCover);WorkspaceAdd(content,hero,SizeType.Absolute,166);
        _sessionHero=hero;
        _sessionRename=IconButton("编辑名称","edit",async(_,_)=>await EditLibraryNoteAsync(),false,90);
        _sessionCoverEdit=IconButton("更换封面","image",async(_,_)=>await EditGameArtworkAsync(),false,108);
        _sessionRename.CoverOverlay=true;_sessionCoverEdit.CoverOverlay=true;
        _libraryCover.Controls.AddRange([_sessionRename,_sessionCoverEdit]);
        void PlaceHeroActions()
        {
            int d=DeviceDpi;var r=_libraryCover.ClientRectangle;
            using var title=new Font("Microsoft YaHei UI",20,FontStyle.Bold);using var sub=new Font("Microsoft YaHei UI",14);
            int top=Math.Max(4*d/96,(r.Height-title.Height-sub.Height-5*d/96)/2);
            int titleWidth=Math.Min(TextRenderer.MeasureText(GameTitleText,title).Width,Math.Max(0,r.Width-_sessionRename.Width-62*d/96));
            _sessionRename.Location=new(22*d/96+titleWidth+12*d/96,top+Math.Max(0,(title.Height-_sessionRename.Height)/2));
            _sessionCoverEdit.Location=new(Math.Max(0,r.Width-_sessionCoverEdit.Width-14*d/96),Math.Max(0,r.Height-_sessionCoverEdit.Height-12*d/96));
        }
        _libraryCover.Layout+=(_,_)=>PlaceHeroActions();_gameTitle.TextChanged+=(_,_)=>PlaceHeroActions();
        ((WorkspaceCover)_libraryCover).OverlayPaint+=(_,e)=>{
            var r=_libraryCover.ClientRectangle;
            using var title=new Font("Microsoft YaHei UI",20,FontStyle.Bold);using var sub=new Font("Microsoft YaHei UI",14);
            int d=DeviceDpi,gap=5*d/96,top=Math.Max(4*d/96,(r.Height-title.Height-sub.Height-gap)/2);
            WorkspaceDrawing.Text(e.Graphics,GameTitleText,title,new(22*d/96,top,Math.Max(0,_sessionRename.Left-34*d/96),title.Height),Color.White);
            WorkspaceDrawing.Text(e.Graphics,GameChineseNameText,sub,new(24*d/96,top+title.Height+gap,Math.Max(0,r.Width-_sessionCoverEdit.Width-62*d/96),sub.Height),Color.FromArgb(225,231,240));
        };
        _gameTitle.TextChanged+=(_,_)=>_libraryCover.Invalidate();_libraryChineseName.TextChanged+=(_,_)=>_libraryCover.Invalidate();
        var status=new TableLayoutPanel{ColumnCount=2,RowCount=1,AutoSize=false,Height=44,Margin=Padding.Empty};status.ColumnStyles.Add(new(SizeType.Percent,100));status.ColumnStyles.Add(new(SizeType.AutoSize));
        status.RowStyles.Add(new(SizeType.Percent,100));
        status.Controls.Add(WorkspaceRow(_sessionStatus,_gameBadge),0,0);
        _plainLaunchButton=IconButton("启动游戏","play",async(_,_)=>await StartLibraryGameAsync(),true,120);
        _sessionLaunchArrow=IconButton("","down",(_,_)=>ShowGameLaunchMenu(_sessionLaunchArrow!),true,32);
        ((GameActionButton)_sessionLaunchArrow).LogicalHeight=36;((GameActionButton)_sessionLaunchArrow).RefreshMetrics();
        _sessionReturnGame=IconButton("返回游戏","play",async(_,_)=>await ReturnToSelectedGameAsync(),true,120);
        _sessionRestart=IconButton("重启游戏","refresh",async(_,_)=>await RestartSelectedGameAsync(),false,108);
        _sessionExit=IconButton("退出游戏","close",async(_,_)=>{if(_selectedGame is {} game)await RequestGameExitAsync(game);},false,108);
        var more=IconButton("···","",(_,_)=>{},false,36);more.AccessibleName="游戏操作";
        more.Click+=(_,_)=>ShowSessionMenu(more);
        var launchRow=CompactRow(_plainLaunchButton,_sessionLaunchArrow,_sessionReturnGame,_sessionRestart,_sessionExit,more);launchRow.Name="SessionLaunchRow";
        status.Controls.Add(launchRow,1,0);_sessionStatusRow=status;WorkspaceAdd(content,status,SizeType.Absolute,44);
        _sessionTranslationChoice=IconButton("游戏翻译","translate",(_,_)=>SelectGameSessionSection(1),false,115);
        _sessionModificationChoice=IconButton("游戏修改","tool",(_,_)=>SelectGameSessionSection(2),false,115);
        _sessionTranslationChoice.TabStyle=_sessionModificationChoice.TabStyle=true;var sessionTabs=WorkspaceRow(_sessionTranslationChoice,_sessionModificationChoice);sessionTabs.Margin=Padding.Empty;WorkspaceAdd(content,sessionTabs);
        // Session actions, including a pending restart, also belong to the
        // modification page. Keep one shared feedback row outside both tabs.
        WorkspaceAdd(content,_sessionActionFeedback);
        content.SizeChanged+=(_,_)=>_sessionActionFeedback.MaximumSize=new(Math.Max(120,content.ClientSize.Width),0);
        var translation=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,RowCount=1,Margin=new(0,5,0,12)};
        translation.ColumnStyles.Add(new(SizeType.Percent,50));translation.ColumnStyles.Add(new(SizeType.Percent,50));
        var left=WorkspaceColumn();left.Dock=DockStyle.Top;left.AutoSize=true;left.Padding=new(16);
        var heading=WorkspaceLabel("翻译设置");heading.Font=new Font("Microsoft YaHei UI",12,FontStyle.Bold);WorkspaceAdd(left,heading);
        var edit=IconButton("编辑","edit",(_,_)=>EditModeProfile(true),false,74);
        var profile=new WorkspaceAlignedRow(WorkspaceLabel("翻译方案"),_embeddedProfile,edit){Name="TranslationProfileRow",Margin=new(0,8,0,8)};profile.FlexibleColumn(1,280);
        WorkspaceAdd(left,profile);WorkspaceAdd(left,_embeddedEffective);
        _sessionEnableTranslation=IconButton("开启翻译","translate",(_,_)=>RequestGameSessionTranslation(),true,130);
        _launchGameButton=IconButton("翻译并启动","play",async(_,_)=>await RunGameActionAsync("launch"),true,130);
        _sessionRetryTranslation=IconButton("重试失败项","refresh",(_,_)=>RetrySessionTranslationFailures(),false,120);_sessionRetryTranslation.Visible=false;
        _installGameButton=IconButton("应用翻译配置","refresh",async(_,_)=>await RunGameActionAsync("install"),false,128);
        _sessionPrepareText=IconButton("读取文本与准备译文","translate",(_,_)=>{if(_selectedGame is {} game)OpenTextPreparation(game);},false,168);_sessionPrepareText.Visible=false;
        WorkspaceAdd(left,WorkspaceRow(_sessionEnableTranslation,_launchGameButton,_sessionRetryTranslation,_installGameButton,_sessionPrepareText));WorkspaceAdd(left,_sessionTranslationState);
        var manage=IconButton("译文与术语","edit",(_,_)=>OpenTranslationManager(),false,120);
        var diagnostics=IconButton("翻译状态","",(_,_)=>ShowTranslationDiagnostics(),false,108);
        diagnostics.Name="TranslationDiagnostics";
        WorkspaceAdd(left,WorkspaceRow(manage,diagnostics));
        var leftSurface=new WorkspaceSurface{Dock=DockStyle.Fill,AutoSize=true,Padding=new(1),Margin=new(0,0,8,0)};leftSurface.Controls.Add(left);translation.Controls.Add(leftSurface,0,0);
        var display=WorkspaceColumn();display.Dock=DockStyle.Top;display.AutoSize=true;display.Padding=new(16);
        var displayHeading=WorkspaceLabel("显示设置");displayHeading.Font=new Font("Microsoft YaHei UI",12,FontStyle.Bold);WorkspaceAdd(display,displayHeading);
        Control DisplayRow(string label,Control control){var caption=WorkspaceLabel(label);var row=new WorkspaceAlignedRow(caption,control){Name="DisplayRow:"+label,Margin=new(0,8,0,4)};caption.Margin=new(0,0,24,0);row.FlexibleColumn(1,4096);return row;}
        _sessionFontCapability=WorkspaceLabel("由游戏适配自动控制");_sessionRenderingCapability=WorkspaceLabel("内嵌替换");
        WorkspaceAdd(display,DisplayRow("字体大小",_sessionFontCapability));
        _sessionGameFontPreference=WorkspaceLabel(GameFontSettings.DefaultLabel);
        _sessionGameFontEdit=IconButton("更换字体","edit",(_,_)=>EditGameFont(),false,108);
        WorkspaceAdd(display,DisplayRow("游戏字体",new WorkspaceAlignedRow(_sessionGameFontPreference,_sessionGameFontEdit){Name="GameFontEditRow"}));
        WorkspaceAdd(display,DisplayRow("译文显示",_sessionRenderingCapability));
        _sessionScanKey=WorkspaceLabel("F8");_sessionScanKeyEdit=IconButton("编辑","edit",(_,_)=>OpenKeysSettings(),false,72);WorkspaceAdd(display,DisplayRow("扫描开关",new WorkspaceAlignedRow(_sessionScanKey,_sessionScanKeyEdit){Name="ScanKeyEditRow"}));
        _sessionScreenTranslation=IconButton("屏幕翻译并启动","translate",async(_,_)=>await RunGameActionAsync("screen"),false,150);
        WorkspaceAdd(display,_sessionScreenTranslation);WorkspaceAdd(display,_sessionScreenTranslationState);
        var displaySurface=new WorkspaceSurface{Dock=DockStyle.Fill,AutoSize=true,Padding=new(1),Margin=new(8,0,0,0)};displaySurface.Controls.Add(display);translation.Controls.Add(displaySurface,1,0);
        bool stackingTranslation=false,translationStacked=false;
        translation.SizeChanged+=(_,_)=>{
            if(stackingTranslation)return;stackingTranslation=true;
            try
            {
                bool stacked=translation.ClientSize.Width<760*DeviceDpi/96;
                if(stacked!=translationStacked)
                {
                    translation.SuspendLayout();translationStacked=stacked;
                    translation.SetCellPosition(displaySurface,new TableLayoutPanelCellPosition(0,stacked?1:0));
                    translation.ColumnCount=stacked?1:2;translation.RowCount=stacked?2:1;translation.ColumnStyles.Clear();translation.RowStyles.Clear();
                    translation.ColumnStyles.Add(new(SizeType.Percent,stacked?100:50));if(!stacked)translation.ColumnStyles.Add(new(SizeType.Percent,50));
                    translation.RowStyles.Add(new(SizeType.AutoSize));if(stacked)translation.RowStyles.Add(new(SizeType.AutoSize));
                    translation.SetCellPosition(displaySurface,new TableLayoutPanelCellPosition(stacked?0:1,stacked?1:0));
                    leftSurface.Margin=stacked?new Padding(0,0,0,8):new Padding(0,0,8,0);displaySurface.Margin=stacked?Padding.Empty:new Padding(8,0,0,0);translation.ResumeLayout(true);
                }
                int width=Math.Max(120,translation.ClientSize.Width/(stacked?1:2)-42*DeviceDpi/96);foreach(var label in new[]{_embeddedEffective,_sessionTranslationState,_sessionScreenTranslationState})label.MaximumSize=new(width,0);
                foreach(var label in new[]{_sessionFontCapability,_sessionRenderingCapability,_sessionScanKey})label.MaximumSize=new(Math.Max(100*DeviceDpi/96,width-145*DeviceDpi/96),0);
            }
            finally{stackingTranslation=false;}
        };
        _sessionTranslationPanel=translation;WorkspaceAdd(content,translation);
        _sessionModificationPanel=BuildGameModificationWorkspace();WorkspaceAdd(content,_sessionModificationPanel);
        _renpyModificationPanel=BuildRenpyModificationWorkspace();WorkspaceAdd(content,_renpyModificationPanel);WorkspaceAdd(content,_unsupportedModificationPanel!);
        _novelModificationPanel=BuildNovelModificationWorkspace();WorkspaceAdd(content,_novelModificationPanel);
        WorkspaceAdd(content,BuildGameDiagnosticRow());
        _chooseGameButton=WorkspaceButton("更换游戏",(_,_)=>ChooseLibraryGame());_restoreGameButton=WorkspaceButton("恢复游戏文件（保留译文）",async(_,_)=>await RunGameActionAsync("restore"));
        var details=WorkspaceColumn();details.AutoSize=true;details.Dock=DockStyle.Top;details.Visible=false;_sessionFileDetails=details;
        WorkspaceAdd(details,WorkspaceLabel("存档位置识别尚未接入，可先打开游戏文件夹。"));
        foreach(var label in new[]{_gameInfo,_gameQueryDetails,_gameStatus,_mediaStatus,_coverDetails,_gameFeedback,_gameConfiguration}){label.MaximumSize=new(760,0);WorkspaceAdd(details,label);}
        var files=IconButton("游戏文件与存档","folder",(_,_)=>{},false,180);files.NavigationStyle=true;
        files.Click+=(_,_)=>{details.Visible=!details.Visible;files.Text=details.Visible?"收起游戏文件与存档":"游戏文件与存档";};_sessionFilesRow=WorkspaceRow(files);WorkspaceAdd(content,_sessionFilesRow);WorkspaceAdd(content,details);
        _gamePath.BorderStyle=BorderStyle.None;_gamePath.AccessibleName="游戏程序路径";var path=SearchField(_gamePath,"folder");
        _sessionFolderOpen=IconButton("打开游戏文件夹","folder",async(_,_)=>await OpenSelectedGameFolderAsync(),false,148);
        var pathRow=new WorkspaceAlignedRow(path,_sessionFolderOpen){Name="SessionGamePath",Margin=Padding.Empty};pathRow.FlexibleColumn(0,4096);WorkspaceAdd(content,pathRow,SizeType.Absolute,44);
        _sessionPathField=pathRow;
        _gameSessionPage.Controls.Add(content);page.Controls.Add(_gameSessionPage);_sessionTranslationPanel.Visible=true;_sessionModificationPanel.Visible=false;
    }
    private Label? _sessionScanKey,_sessionFontCapability,_sessionRenderingCapability,_sessionGameFontPreference;
    private Button? _sessionScanKeyEdit,_sessionPrepareText,_sessionGameFontEdit;
    private void OpenTextPreparation(GameInfo game)
    {
        try
        {
            var profile=_fusion?.GameSettings(UiSettings,game.ExePath)??EmbeddedSettings;
            if(WpfWorkspace.Enabled)new NativeExperimentalCatalog(game,UiSettings,profile,async(text,settings,token)=>(await _translationService.TranslatePlainAsync(text,settings,token)).Text).Show(this);
            else new ExperimentalCatalogForm(game,UiSettings,profile,async(text,settings,token)=>(await _translationService.TranslatePlainAsync(text,settings,token)).Text).Show(this);
        }
        catch(Exception ex){SetGameTranslationFeedback(SafeDiagnosticOutput.ExceptionSummary(ex));}
    }
    private void OpenKeysSettings(){if(WpfWorkspace.Enabled){ShowPage("设置");NativeSelectSettings(2);return;}ShowPage("设置");for(int i=0;i<_settingsNavigation.Items.Count;i++)if(_settingsNavigation.Items[i]?.ToString()=="按键"){_settingsNavigation.SelectedIndex=i;break;}}
    private void ShowSessionMenu(Control? anchor,System.Windows.FrameworkElement? nativeAnchor=null)
    {
        _libraryContextMenu.ClearActions();_libraryContextMenu.Font=Font;
        _libraryContextMenu.AddAction("恢复游戏文件（保留译文）","back",async(_,_)=>await RunGameActionAsync("restore"),WpfWorkspace.Enabled?GameDetail.Restore.Enabled:_restoreGameButton?.Enabled==true);
        if(_selectedGame is {} resetGame&&CanResetMachineTranslations(resetGame))
            _libraryContextMenu.AddAction("清空机器译文并重译","refresh",async(_,_)=>await ResetMachineTranslationsAsync(resetGame),!_gameSessions.Get(resetGame).Busy);
        _libraryContextMenu.AddAction("查看最近诊断报告","",(_,_)=>ShowGameDiagnosticReport(),_selectedGame is not null);
        _libraryContextMenu.AddAction("打开诊断报告目录","folder",(_,_)=>OpenGameDiagnosticsFolder(),_selectedGame is not null);
        if(_selectedGame is {} selected&&CatalogTranslationPreparation.Supports(selected))
            _libraryContextMenu.AddAction("读取文本与准备译文","translate",(_,_)=>OpenTextPreparation(selected));
        if(nativeAnchor is not null)WpfNativeMenus.Show(_libraryContextMenu,nativeAnchor:nativeAnchor);else if(anchor is not null)_libraryContextMenu.Show(anchor,new Point(0,anchor.Height));
    }
    private async Task StartLibraryGameAsync()
    {
        if(_selectedGame is not { } game||_gameDetecting||_gameActionRunning)return;
        if(SameGame(_desktopGameState?.Game.ExePath,game.ExePath)&&_desktopGameState!.Process==FusionGameProcessState.Running){await ReturnToSelectedGameAsync();return;}
        await RunGameActionAsync(GameCapabilities.DefaultTranslationLaunch(game,_fusion?.State.DefaultGameLaunchWithTranslation==true)?"launch":"plain");
    }

    private void OpenGameSession(GameInfo game,bool withTranslation=false,bool observed=false)
    {
        if((!WpfWorkspace.Enabled&&_gameSessionPage is null)||!SameGame(game.ExePath,_selectedGame?.ExePath))return;
        var session=_gameSessions.Get(game);
        if(observed)session.Observe(FusionGameProcessState.Running);
        // Navigation never resets another game or implies a launch/activation.
        _sessionGamePath=game.ExePath;
        _libraryHomePending=false;
        if(!WpfWorkspace.Enabled)WorkspacePages.Show(_gameSessionPage!.Parent!,_gameSessionPage);
        if(_libraryNavigation is NativeLibraryNavigation navigation)navigation.SelectGame(game.ExePath);
        _libraryGrid.SelectPath(game.ExePath);_recentShelf.SelectPath(game.ExePath);
        _wpfDetails=true;_wpfWindow?.RefreshWorkspace();
        if(withTranslation)session.Section=1;
        SelectGameSessionSection(session.Section);RecordPageLocation();
    }

    private static bool SameGame(string? a,string? b)=>a is not null&&b is not null&&StringComparer.OrdinalIgnoreCase.Equals(a,b);
    private void ShowLibraryHome()
    {
        _libraryNavigationEpoch++;
        _libraryHomePending=false;_wpfDetails=false;_wpfWindow?.RefreshWorkspace();
        if(WpfWorkspace.Enabled){RecordPageLocation();return;}
        if(_gameSessionPage is null)return;
        if(_libraryBody!.Visible&&!_gameSessionPage.Visible)return;
        WorkspacePages.Show(_gameSessionPage.Parent!,_libraryBody!);ScheduleLibraryRefresh();RecordPageLocation();
    }

    private void SelectGameSessionSection(int section)
    {
        if(_selectedGame is not { } game)return;
        _gameSessions.Get(game).Section=section==2?2:1;RecordPageLocation();
        RenderGameSessionState();
        if(WpfWorkspace.Enabled){if(section==2)_=EnterGameModificationPageAsync(game);return;}
        if(section==2&&SelectedDataSession() is {Enabled:true,Connection.Connected:true})_=RefreshGameDataAsync();
        if(section==2&&game.AdapterId=="renpy"&&SelectedScriptTranslationSession() is {Enabled:true,Connection.Connected:true})_=RefreshRenpyDataAsync();
    }

    private void RequestGameSessionTranslation()=>_=RequestGameSessionTranslationAsync();
    private async Task RequestGameSessionTranslationAsync()
    {
        if(_selectedGame is not { } game||!SameGame(game.ExePath,_sessionGamePath)||!GameCapabilities.EmbeddedTranslation(game))return;
        if(_gameRestartRequests.TryGetValue(game.ExePath,out var pending)){pending.Cancel();return;}
        var session=_gameSessions.Get(game);if(_gameActionRunning||session.Busy||session.LaunchPending)return;
        if(SelectedTranslationSession()?.Connection.Connected==true){await ToggleRpgTranslationAsync();return;}
        if(game.AdapterId=="renpy"&&RenpyReconnect.Peer(game.ExePath) is not null){await ReconnectRenpyAsync(game);return;}
        if(session.Process!=FusionGameProcessState.Running)return;
        try
        {
            // Validate this game's profile before asking it to exit. The captured
            // game remains the target even if the user browses to another one.
            _fusion!.RequireGameReady(game.ExePath);
            if(AppDialog.Show(this,GameTranslationActivation.RestartTitle,GameTranslationActivation.RestartConfirmation,AppDialogKind.Confirmation)!=DialogResult.Yes)return;
            if(IsDisposed||Disposing||session.Busy||session.LaunchPending)return;
            await RestartGameAsync(game,enableTranslation:true);
        }
        catch(Exception error){session.Feedback=SafeDiagnosticOutput.ExceptionSummary(error);if(!IsDisposed)RenderGameSessionState();}
    }

    private GameTranslationPresentation TranslationPresentation(GameInfo? game)
    {
        bool selected=SameGame(game?.ExePath,_selectedGame?.ExePath);
        bool running=SameGame(game?.ExePath,_desktopGameState?.Game.ExePath)&&_desktopGameState?.Process==FusionGameProcessState.Running;
        bool connected=selected&&running&&SelectedTranslationSession()?.Connection.Connected==true;
        var session=game is null?null:_gameSessions.Get(game);
        return GameTranslationPresentation.Resolve(game,connected,session?.TranslationBlockedReason,selected&&running&&session?.LaunchMode==GameLaunchMode.Ordinary&&session.LaunchedWithoutBridge,selected&&session?.TranslationRuntimePending==true);
    }

    private void RenderGameSessionState()
    {
        if(WpfWorkspace.Enabled){RenderNativeGameSessionState();return;}
        if(_gameSessionPage is null||_plainLaunchButton is null)return;
        var game=_selectedGame; var state=_desktopGameState;
        var session=game is null?null:_gameSessions.Get(game);
        bool current=SameGame(game?.ExePath,state?.Game.ExePath);
        if(current)session!.Observe(state!.Process);
        RenderCommonModificationAuthorization();
        bool running=current&&state!.Process==FusionGameProcessState.Running;
        bool stopped=current&&state!.Process==FusionGameProcessState.Stopped&&session?.LaunchPending!=true;
        bool busy=_gameActionRunning||_gameDetecting;
        bool rpg=game?.AdapterId is "rpg-maker-mv" or "rpg-maker-mz";
        bool renpy=game?.AdapterId=="renpy";
        bool embedded=EmbeddedGameAdapters.Handles(game);
        
        bool supported=GameCapabilities.EmbeddedTranslation(game);
        var translationPresentation=TranslationPresentation(game);
        SetLabelText(_gameBadge,translationPresentation.Badge);
        if(_sessionFontCapability is not null)SetLabelText(_sessionFontCapability,translationPresentation.Font);
        if(_sessionGameFontPreference is not null)SetLabelText(_sessionGameFontPreference,game is null?"":GameFontSettings.Supports(game)?GameFontSettings.Describe(game.ExePath):"此游戏暂不支持自定义内嵌字体。");
        if(_sessionGameFontEdit is not null)_sessionGameFontEdit.Enabled=game is not null&&GameFontSettings.Supports(game)&&!busy;
        if(_sessionRenderingCapability is not null)SetLabelText(_sessionRenderingCapability,translationPresentation.Rendering);
        bool ready=GameTranslationReady(game);
        RenderScreenTranslationState(game,running,stopped,busy,ready);
        if(_gameSessionPage.Visible&&game is not null)_sessionGamePath=game.ExePath;
        bool defaultTranslation=GameCapabilities.DefaultTranslationLaunch(game,_fusion?.State.DefaultGameLaunchWithTranslation==true);
        _plainLaunchButton.Text=defaultTranslation?"翻译并启动":"启动游戏";
        _plainLaunchButton.Visible=!running; _plainLaunchButton.Enabled=game is not null&&stopped&&!busy&&(!defaultTranslation||ready);
        _sessionLaunchArrow!.Visible=!running; _sessionLaunchArrow.Enabled=stopped&&!busy;
        bool restarting=IsGameRestartPending(game);
        _sessionReturnGame!.Visible=running; _sessionReturnGame.Enabled=!busy||restarting;
        _sessionRestart!.Visible=running||restarting;_sessionRestart.Enabled=restarting||running&&!busy;_sessionRestart.Text=restarting?"取消重启":"重启游戏";
        _sessionExit!.Visible=running;_sessionExit.Enabled=running&&!busy;
        foreach(var button in new[]{_sessionRename,_sessionCoverEdit,_sessionFolderOpen})if(button is not null)button.Enabled=game is not null&&!busy;
        _librarySelectionLabel.Text=game is null?"双击游戏进入详细主页":game.Name+(GameChineseNameText.Length>0?" · "+GameChineseNameText:"");
        _sessionStatus.Text=_gameDetecting?"正在识别游戏…":busy?"正在处理此游戏…":session?.LaunchPending==true?"正在等待游戏启动…":running?"游戏运行中":stopped?"游戏未运行":"正在核对游戏状态…";
        _sessionStatus.ForeColor=running?Color.SeaGreen:UiTheme.Current.SecondaryText;
        _sessionTranslationChoice!.Enabled=_sessionModificationChoice!.Enabled=game is not null&&!_gameDetecting;
        int section=session?.Section??1;
        if(_sessionContent is not null&&_sessionHero is not null)
        {
            bool modifying=section==2;
            void HeaderRow(Control control,int logicalHeight)
            {
                bool visible=!modifying;var row=_sessionContent.RowStyles[_sessionContent.GetRow(control)];float height=visible?logicalHeight*DeviceDpi/96f:0;
                if(control.Visible!=visible)control.Visible=visible;
                if(Math.Abs(row.Height-height)>1)row.Height=height;
            }
            HeaderRow(_sessionHero,166);HeaderRow(_sessionStatusRow!,44);HeaderRow(_sessionPathField!,44);
            _sessionFilesRow!.Visible=!modifying;
            if(modifying&&_sessionFileDetails!.Visible)_sessionFileDetails.Visible=false;
            _sessionCompactTitle.MinimumSize=new(0,28*DeviceDpi/96);_sessionCompactTitle.Visible=modifying;_sessionCompactTitle.Text=game?.Name??"";
            _sessionCompactLaunch!.Visible=modifying&&!running;_sessionCompactLaunch.Enabled=_plainLaunchButton.Enabled;_sessionCompactLaunch.Text=_plainLaunchButton.Text;
            _sessionCompactLaunchArrow!.Visible=modifying&&!running;_sessionCompactLaunchArrow.Enabled=_sessionLaunchArrow.Enabled;
            _sessionCompactReturn!.Visible=modifying&&running;
            _sessionCompactReturn.Enabled=!busy||restarting;
            _sessionCompactRestart!.Visible=modifying&&(running||restarting);_sessionCompactRestart.Enabled=restarting||running&&!busy;_sessionCompactRestart.Text=restarting?"取消重启":"重启游戏";
            _sessionCompactExit!.Visible=modifying&&running;_sessionCompactExit.Enabled=running&&!busy;
            _sessionCompactRename!.Visible=modifying;_sessionCompactRename.Enabled=game is not null&&!busy;
            _sessionCompactMore!.Visible=modifying;
        }
        _sessionTranslationPanel!.Visible=section==1;
        _sessionModificationPanel!.Visible=section==2&&rpg;
        _renpyModificationPanel!.Visible=section==2&&renpy;
        _unsupportedModificationPanel!.Visible=section==2&&!rpg&&!renpy&&!NovelModification(game);
        _novelModificationPanel!.Visible=section==2&&NovelModification(game);
        if(_sessionTranslationChoice.Primary!=(section==1)){_sessionTranslationChoice.Primary=section==1;_sessionTranslationChoice.Invalidate();}
        if(_sessionModificationChoice.Primary!=(section==2)){_sessionModificationChoice.Primary=section==2;_sessionModificationChoice.Invalidate();}
        bool prepared=session?.LaunchMode==GameLaunchMode.Translation;
        bool requested=session?.TranslationRequested==true;
        if(!rpg&&!renpy&&!embedded)
        {
        var activation=GameTranslationActivation.Resolve(false,false,ready,busy,restarting,null);
        _sessionEnableTranslation!.Visible=running&&!prepared||restarting;
        _sessionEnableTranslation.Enabled=supported&&activation.Enabled;
        _sessionEnableTranslation.Text=activation.Text;
        _sessionTranslationState.Text=!supported?"此游戏暂不支持内嵌翻译。":prepared&&(running||session?.LaunchPending==true)?
            "已随本次启动配置翻译 · 实际效果以游戏内文字为准。":requested?
            (running?"当前适配需要重启才能启用。请保存并自行退出游戏，再点击“翻译并启动”。":"已保留开启翻译的选择，可以翻译并启动。"):
            running?activation.Hint:
            "按需选择翻译方案，普通启动不会开启翻译。";
        if(prepared&&(running||session?.LaunchPending==true)&&session!.AppliedProfileName is { } applied)
        {
            _sessionTranslationState.Text="本次启动方案："+applied+" · 实际效果以游戏内文字为准。";
            if(session.AppliedSettingsIdentity!=FusionConfiguration.EditIdentity(EmbeddedSettings))
                _sessionTranslationState.Text+="\n当前所选方案将在下次翻译启动时应用。";
        }
        }
        _launchGameButton!.Visible=!running; _launchGameButton.Enabled=stopped&&supported&&ready&&!busy;
        bool canPrepare=game is not null&&CatalogTranslationPreparation.Supports(game);
        if(_sessionPrepareText is not null){_sessionPrepareText.Visible=canPrepare;_sessionPrepareText.Enabled=!busy;}
        if(canPrepare&&!supported&&game is not null){_launchGameButton.Visible=false;_sessionEnableTranslation!.Visible=false;_sessionTranslationState!.Text=game.Engine+" · 可读取文本并准备译文；游戏内显示尚未接入。";}
        _restoreGameButton!.Enabled=(rpg||current&&state?.HasRecord==true)&&stopped&&!busy;
        _installGameButton!.Enabled=supported&&ready&&stopped&&!busy;
        _installGameButton.Visible=supported&&stopped;
        _sessionRetryTranslation!.Visible=rpg&&SelectedDataSession()?.Translation?.Enabled==true&&SelectedDataSession()?.Translation?.FailedCount>0;
        if(rpg||renpy||embedded)
        {
            var data=SelectedTranslationSession();bool connected=running&&data?.Connection.Connected==true;
            _sessionEnableTranslation!.Visible=running||restarting;
            bool restorePending=data?.Translation?.RestorePending==true;
            bool reconnect=renpy&&running&&!connected&&RenpyReconnect.Peer(game!.ExePath) is not null;
            var activation=GameTranslationActivation.Resolve(connected,reconnect,ready,busy,restarting,data?.Translation,session?.TranslationRuntimePending==true);
            _sessionEnableTranslation.Enabled=activation.Enabled;
            _sessionEnableTranslation.Text=activation.Text;
            _sessionTranslationState.Text=restorePending?data!.Translation!.Status:connected?(data!.Translation?.Status??"游戏已连接，翻译关闭。可在游玩时开启，无需重启。"):
                running?(restarting?activation.Hint:(!string.IsNullOrEmpty(session?.TranslationBlockedReason)?"本次翻译未接入："+session.TranslationBlockedReason+"\n":"")+(busy?"正在连接游戏…":activation.Hint)):
                translationPresentation.PendingVerification?"已识别到适配方式。点击“翻译并启动”，连接后会自动开启翻译；首次使用请进入菜单或对话查看效果。":
                "点击“翻译并启动”可连接游戏并开启翻译；普通启动后，也可在连接成功时手动开启。";
            if(connected&&data?.Translation is {Enabled:true,RestorePending:false} activeTranslation)
            {
                var progress=TranslationProgressPresentation.Describe(activeTranslation.Snapshot);
                _sessionTranslationState.Text=TranslationProgressPresentation.Overview(activeTranslation.Snapshot);
                if(activeTranslation.CacheWarning.Length>0)_sessionTranslationState.Text+="\n"+activeTranslation.CacheWarning;
                if(activeTranslation.Snapshot.RuntimeNote.Length>0)_sessionTranslationState.Text+="\n"+activeTranslation.Snapshot.RuntimeNote;
            }
            if(!ready&&data?.Translation?.Enabled!=true)
                _sessionTranslationState.Text="翻译方案尚未配置完整。请点击方案旁的“编辑”，补齐提示的必填项。\n"+_sessionTranslationState.Text;
            _sessionRetryTranslation!.Visible=connected&&data?.Translation?.Enabled==true&&data.Translation.FailedCount>0;
            _sessionRetryTranslation.Enabled=!busy&&data?.Translation?.RestorePending!=true;
            _restoreGameButton!.Enabled=stopped&&!busy&&(embedded?state?.HasRecord==true:!renpy||RenpyGameAdapter.HasRecord(game!.ExePath));
        }
        _sessionActionFeedback.Text=session?.Feedback??""; _sessionActionFeedback.Visible=_sessionActionFeedback.Text.Length>0;
        if(_sessionScanKey is not null)_sessionScanKey.Text=rpg||renpy||embedded?
            translationPresentation.CanToggle?"使用本页翻译开关":translationPresentation.Blocked?"当前无法开关翻译":translationPresentation.OrdinaryLaunch?"翻译开启后可用":"接入后可用":
            GameCapabilities.TranslationToggleKey(game)?_fusion?.State.EmbeddedToggleKey??"F8":"当前引擎尚无内嵌翻译开关";
        if(_sessionScanKeyEdit is not null)_sessionScanKeyEdit.Visible=GameCapabilities.TranslationToggleKey(game);
        var runningPaths=_gameSessions.Snapshot().Where(x=>x.Process==FusionGameProcessState.Running).Select(x=>x.Game.ExePath).ToArray();
        _libraryGrid.UpdateRunning(runningPaths);_recentShelf.UpdateRunning(runningPaths);_libraryNavigation.UpdateRunning(runningPaths);
        RenderGameModificationState();
        RenderNovelModificationState();
        RenderRenpyModificationState();
        RenderGameDiagnosticState();
        NotifyNativeGameDetailChanged();
    }

    private async Task RefreshOtherGameSessionsAsync()
    {
        if(_sessionPolling||!_desktopReady||IsDisposed)return;
        _sessionPolling=true;
        var snapshots=_gameSessions.Snapshot().Where(x=>!x.Busy&&!SameGame(x.Game.ExePath,_selectedGame?.ExePath)).Select(x=>(Session:x,Revision:x.Revision,Game:x.Game)).ToArray();
        try
        {
            var states=await Task.Run(()=>snapshots.Select(x=>(x.Session,x.Revision,State:FusionAdapterInstaller.ProcessState(x.Game))).ToArray());
            if(IsDisposed||Disposing)return;
            foreach(var item in states)if(!item.Session.Busy&&item.Revision==item.Session.Revision)item.Session.Observe(item.State);
            RenderGameSessionState();
        }
        catch(Exception ex){if(!IsDisposed)AppLog.Write("game-session",SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally{_sessionPolling=false;}
    }

    private async Task ReturnToSelectedGameAsync()
    {
        if(_selectedGame is not { } game)return;
        try
        {
            var found=await Task.Run(()=>GameLibraryWindowIdentity.Find(game.ExePath,forControl:true));
            if(IsDisposed)return;
            if(found.Target is not { } target||!GameLibraryWindowIdentity.Matches(target,forControl:true))throw new InvalidOperationException("尚未找到此游戏的可用窗口，请从任务栏切回。"+found.Message);
            var window=target.Window;
            if(IsIconic(window))ShowWindow(window,9);
            if(!SetForegroundWindow(window))DesktopStatus="游戏窗口已找到，请从任务栏切回。";
        }
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
    }
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr window);
}
