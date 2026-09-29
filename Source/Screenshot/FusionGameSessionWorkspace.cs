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
    private readonly Label _sessionStatus = WorkspaceLabel("");
    private readonly Label _sessionTranslationState = WorkspaceLabel("");
    private readonly Label _sessionActionFeedback = WorkspaceLabel("");
    private Control? _sessionTranslationPanel, _sessionModificationPanel;
    private GameActionButton? _sessionTranslationChoice, _sessionModificationChoice;
    private Button? _sessionRetryTranslation;
    private Button? _sessionRuntimeText;
    private Button? _sessionEnableTranslation, _sessionLaunchArrow, _sessionReturnGame;
    private readonly Dictionary<string, GameWorkspaceSession> _gameRootOperations = new(StringComparer.OrdinalIgnoreCase);
    private TableLayoutPanel? _sessionContent;
    private Control? _sessionHero,_sessionFilesRow,_sessionPathField,_sessionStatusRow,_sessionFileDetails;
    private readonly Label _sessionCompactTitle=WorkspaceLabel("");
    private GameActionButton? _sessionCompactReturn,_sessionCompactMore,_sessionRestart,_sessionCompactRestart;
    private GameActionButton? _sessionRename,_sessionCompactRename,_sessionCoverEdit,_sessionFolderOpen;

    private void BuildGameSessionWorkspace(Panel page)
    {
        FormClosed+=(_,_)=>{foreach(var session in _scriptTranslationSessions.Values)session.Dispose();};
        FormClosed+=(_,_)=>{foreach(var session in _embeddedTranslationSessions.Values)session.Dispose();};
        FormClosed+=(_,_)=>_runtimeTranslationWindow?.Close();
        _gameSessionPage=new WorkspaceScrollView{Dock=DockStyle.Fill,AutoScroll=false,Padding=new(24,14,24,12),Visible=false};
        var content=new WorkspaceContentColumn();content.Dock=DockStyle.Top;content.AutoSize=true;content.AutoSizeMode=AutoSizeMode.GrowAndShrink;
        _sessionContent=content;
        var compactMore=IconButton("…","",(_,_)=>{},false,42);compactMore.Click+=(_,_)=>ShowSessionMenu(compactMore);
        _sessionCompactMore=compactMore;compactMore.Visible=false;compactMore.AccessibleName="游戏操作";
        var back=IconButton("返回游戏库","back",(_,_)=>ShowLibraryHome());back.LogicalHeight=28;back.RefreshMetrics();_sessionCompactReturn=IconButton("返回游戏","play",async(_,_)=>await ReturnToSelectedGameAsync(),false,98);
        _sessionCompactReturn.LogicalHeight=28;_sessionCompactReturn.RefreshMetrics();_sessionCompactReturn.Visible=false;
        _sessionCompactTitle.AutoEllipsis=true;_sessionCompactTitle.AutoSize=false;_sessionCompactTitle.Height=28;
        _sessionCompactRestart=IconButton("重启游戏","refresh",async(_,_)=>await RestartSelectedGameAsync(),false,98);_sessionCompactRestart.LogicalHeight=28;_sessionCompactRestart.RefreshMetrics();_sessionCompactRestart.Visible=false;
        _sessionCompactRename=IconButton("编辑名称","edit",async(_,_)=>await EditLibraryNoteAsync(),false,90);_sessionCompactRename.LogicalHeight=28;_sessionCompactRename.RefreshMetrics();_sessionCompactRename.Visible=false;
        var backRow=WorkspaceRow(back,_sessionCompactTitle,_sessionCompactRename,_sessionCompactReturn,_sessionCompactRestart,compactMore);backRow.Name="SessionCompactHeader";backRow.Margin=Padding.Empty;_sessionCompactTitle.Visible=false;
        backRow.SizeChanged+=(_,_)=>{int width=Math.Clamp(backRow.ClientSize.Width-360*DeviceDpi/96,120*DeviceDpi/96,360*DeviceDpi/96);if(_sessionCompactTitle.Width!=width)_sessionCompactTitle.Width=width;};WorkspaceAdd(content,backRow);
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
            int titleWidth=Math.Min(TextRenderer.MeasureText(_gameTitle.Text,title).Width,Math.Max(0,r.Width-_sessionRename.Width-62*d/96));
            _sessionRename.Location=new(22*d/96+titleWidth+12*d/96,top+Math.Max(0,(title.Height-_sessionRename.Height)/2));
            _sessionCoverEdit.Location=new(Math.Max(0,r.Width-_sessionCoverEdit.Width-14*d/96),Math.Max(0,r.Height-_sessionCoverEdit.Height-12*d/96));
        }
        _libraryCover.Layout+=(_,_)=>PlaceHeroActions();_gameTitle.TextChanged+=(_,_)=>PlaceHeroActions();
        ((WorkspaceCover)_libraryCover).OverlayPaint+=(_,e)=>{
            var r=_libraryCover.ClientRectangle;
            using var title=new Font("Microsoft YaHei UI",20,FontStyle.Bold);using var sub=new Font("Microsoft YaHei UI",14);
            int d=DeviceDpi,gap=5*d/96,top=Math.Max(4*d/96,(r.Height-title.Height-sub.Height-gap)/2);
            WorkspaceDrawing.Text(e.Graphics,_gameTitle.Text,title,new(22*d/96,top,Math.Max(0,_sessionRename.Left-34*d/96),title.Height),Color.White);
            WorkspaceDrawing.Text(e.Graphics,_libraryChineseName.Text,sub,new(24*d/96,top+title.Height+gap,Math.Max(0,r.Width-_sessionCoverEdit.Width-62*d/96),sub.Height),Color.FromArgb(225,231,240));
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
        var more=IconButton("···","",(_,_)=>{},false,36);more.AccessibleName="游戏操作";
        more.Click+=(_,_)=>ShowSessionMenu(more);
        var launchRow=CompactRow(_plainLaunchButton,_sessionLaunchArrow,_sessionReturnGame,_sessionRestart,more);launchRow.Name="SessionLaunchRow";
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
        _sessionRetryTranslation=IconButton("重试失败","refresh",(_,_)=>SelectedTranslationSession()?.Translation?.RetryFailures(),false,108);_sessionRetryTranslation.Visible=false;
        _installGameButton=IconButton("应用翻译配置","refresh",async(_,_)=>await RunGameActionAsync("install"),false,128);
        WorkspaceAdd(left,WorkspaceRow(_sessionEnableTranslation,_launchGameButton,_sessionRetryTranslation,_installGameButton));WorkspaceAdd(left,_sessionTranslationState);
        var leftSurface=new WorkspaceSurface{Dock=DockStyle.Fill,AutoSize=true,Padding=new(1),Margin=new(0,0,8,0)};leftSurface.Controls.Add(left);translation.Controls.Add(leftSurface,0,0);
        var display=WorkspaceColumn();display.Dock=DockStyle.Top;display.AutoSize=true;display.Padding=new(16);
        var displayHeading=WorkspaceLabel("显示设置");displayHeading.Font=new Font("Microsoft YaHei UI",12,FontStyle.Bold);WorkspaceAdd(display,displayHeading);
        Control DisplayRow(string label,Control control){var caption=WorkspaceLabel(label);var row=new WorkspaceAlignedRow(caption,control){Name="DisplayRow:"+label,Margin=new(0,8,0,4)};caption.Margin=new(0,0,24,0);return row;}
        _sessionFontCapability=WorkspaceLabel("由游戏适配自动控制");_sessionRenderingCapability=WorkspaceLabel("内嵌替换");
        WorkspaceAdd(display,DisplayRow("字体大小",_sessionFontCapability));
        WorkspaceAdd(display,DisplayRow("译文显示",_sessionRenderingCapability));
        _sessionScanKey=WorkspaceLabel("F8");_sessionScanKeyEdit=IconButton("编辑","edit",(_,_)=>OpenKeysSettings(),false,72);WorkspaceAdd(display,DisplayRow("扫描开关",new WorkspaceAlignedRow(_sessionScanKey,_sessionScanKeyEdit){Name="ScanKeyEditRow"}));
        _sessionRuntimeText=IconButton("打开备用译文窗","translate",(_,_)=>OpenRuntimeTranslationWindow(),false,150);_sessionRuntimeText.Visible=false;
        WorkspaceAdd(display,_sessionRuntimeText);
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
                int width=Math.Max(120,translation.ClientSize.Width/(stacked?1:2)-42*DeviceDpi/96);foreach(var label in new[]{_embeddedEffective,_sessionTranslationState})label.MaximumSize=new(width,0);
            }
            finally{stackingTranslation=false;}
        };
        _sessionTranslationPanel=translation;WorkspaceAdd(content,translation);
        _sessionModificationPanel=BuildGameModificationWorkspace();WorkspaceAdd(content,_sessionModificationPanel);
        _renpyModificationPanel=BuildRenpyModificationWorkspace();WorkspaceAdd(content,_renpyModificationPanel);WorkspaceAdd(content,_unsupportedModificationPanel!);
        WorkspaceAdd(content,BuildGameDiagnosticRow());
        _chooseGameButton=WorkspaceButton("更换游戏",(_,_)=>ChooseLibraryGame());_restoreGameButton=WorkspaceButton("恢复安装前状态",async(_,_)=>await RunGameActionAsync("restore"));
        var details=WorkspaceColumn();details.AutoSize=true;details.Dock=DockStyle.Top;details.Visible=false;_sessionFileDetails=details;
        WorkspaceAdd(details,WorkspaceLabel("存档位置识别尚未接入，可先打开游戏文件夹。"));
        foreach(var label in new[]{_gameInfo,_gameQueryDetails,_gameStatus,_mediaStatus,_coverDetails,_gameFeedback,_gameConfiguration}){label.MaximumSize=new(760,0);WorkspaceAdd(details,label);}
        var files=IconButton("游戏文件与存档","folder",(_,_)=>{},false,180);files.NavigationStyle=true;
        files.Click+=(_,_)=>{details.Visible=!details.Visible;files.Text=details.Visible?"收起游戏文件与存档":"游戏文件与存档";};_sessionFilesRow=WorkspaceRow(files);WorkspaceAdd(content,_sessionFilesRow);WorkspaceAdd(content,details);
        _gamePath.BorderStyle=BorderStyle.None;_gamePath.AccessibleName="游戏程序路径";var path=SearchField(_gamePath,"folder");
        _sessionFolderOpen=IconButton("打开游戏文件夹","folder",(_,_)=>_folderButton.PerformClick(),false,148);
        var pathRow=new WorkspaceAlignedRow(path,_sessionFolderOpen){Name="SessionGamePath",Margin=Padding.Empty};pathRow.FlexibleColumn(0,4096);WorkspaceAdd(content,pathRow,SizeType.Absolute,44);
        _sessionPathField=pathRow;
        _gameSessionPage.Controls.Add(content);page.Controls.Add(_gameSessionPage);_sessionTranslationPanel.Visible=true;_sessionModificationPanel.Visible=false;
    }
    private Label? _sessionScanKey,_sessionFontCapability,_sessionRenderingCapability;
    private Button? _sessionScanKeyEdit;
    private void OpenKeysSettings(){ShowPage("设置");for(int i=0;i<_settingsNavigation.Items.Count;i++)if(_settingsNavigation.Items[i]?.ToString()=="按键"){_settingsNavigation.SelectedIndex=i;break;}}
    private void ShowSessionMenu(Control anchor)
    {
        _libraryContextMenu.ClearActions();_libraryContextMenu.Font=Font;
        _libraryContextMenu.AddAction("恢复安装前状态","back",async(_,_)=>await RunGameActionAsync("restore"),_restoreGameButton?.Enabled==true);
        _libraryContextMenu.AddAction("查看最近诊断报告","",(_,_)=>ShowGameDiagnosticReport(),_selectedGame is not null);
        _libraryContextMenu.AddAction("打开诊断报告目录","folder",(_,_)=>OpenGameDiagnosticsFolder(),_selectedGame is not null);
        _libraryContextMenu.Show(anchor,new Point(0,anchor.Height));
    }
    private async Task StartLibraryGameAsync()
    {
        if(_selectedGame is not { } game||_gameDetecting||_gameActionRunning)return;
        if(SameGame(_desktopGameState?.Game.ExePath,game.ExePath)&&_desktopGameState!.Process==FusionGameProcessState.Running){await ReturnToSelectedGameAsync();return;}
        await RunGameActionAsync(GameCapabilities.DefaultTranslationLaunch(game,_fusion?.State.DefaultGameLaunchWithTranslation==true)?"launch":"plain");
    }

    private void OpenGameSession(GameInfo game,bool withTranslation=false,bool observed=false)
    {
        if(_gameSessionPage is null||!SameGame(game.ExePath,_selectedGame?.ExePath))return;
        var session=_gameSessions.Get(game);
        if(observed)session.Observe(FusionGameProcessState.Running);
        // Navigation never resets another game or implies a launch/activation.
        _sessionGamePath=game.ExePath;
        WorkspacePages.Show(_gameSessionPage.Parent!,_gameSessionPage);ScheduleLibraryRefresh();
        if(withTranslation)session.Section=1;
        SelectGameSessionSection(session.Section);
    }

    private static bool SameGame(string? a,string? b)=>a is not null&&b is not null&&StringComparer.OrdinalIgnoreCase.Equals(a,b);
    private void ShowLibraryHome()
    {
        if(_gameSessionPage is null)return;
        if(_libraryBody!.Visible&&!_gameSessionPage.Visible)return;
        WorkspacePages.Show(_gameSessionPage.Parent!,_libraryBody!);ScheduleLibraryRefresh();
    }

    private void SelectGameSessionSection(int section)
    {
        if(_selectedGame is not { } game)return;
        _gameSessions.Get(game).Section=section==2?2:1;
        RenderGameSessionState();
        if(section==2&&SelectedDataSession() is {Connection.Connected:true} data)
        {data.EnableOnEntry=true;_=RefreshGameDataAsync();}
        if(section==2&&game.AdapterId=="renpy"&&SelectedScriptTranslationSession()?.Connection.Connected==true)_=RefreshRenpyDataAsync();
    }

    private void RequestGameSessionTranslation()
    {
        if(_selectedGame is not { } game||!SameGame(game.ExePath,_sessionGamePath)||_gameActionRunning||!GameCapabilities.EmbeddedTranslation(game))return;
        if(SelectedTranslationSession()?.Connection.Connected==true){_=ToggleRpgTranslationAsync();return;}
        var session=_gameSessions.Get(game);
        if(session.Process!=FusionGameProcessState.Running)return;
        // Existing adapters load at startup. Preserve this game's pending choice;
        // never terminate its process or rewrite running-game files.
        session.TranslationRequested=true; RenderGameSessionState();
    }

    private GameTranslationPresentation TranslationPresentation(GameInfo? game)
    {
        bool selected=SameGame(game?.ExePath,_selectedGame?.ExePath);
        bool running=SameGame(game?.ExePath,_desktopGameState?.Game.ExePath)&&_desktopGameState?.Process==FusionGameProcessState.Running;
        bool connected=selected&&running&&SelectedTranslationSession()?.Connection.Connected==true;
        return GameTranslationPresentation.Resolve(game,connected,game is null?null:_gameSessions.Get(game).TranslationBlockedReason);
    }

    private void RenderGameSessionState()
    {
        if(_gameSessionPage is null||_plainLaunchButton is null)return;
        var game=_selectedGame; var state=_desktopGameState;
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
        if(_sessionRuntimeText is not null)
        {
            var liveTranslation=SelectedTranslationSession()?.Translation;
            _sessionRuntimeText.Visible=liveTranslation is {Enabled:true}&&liveTranslation.ReadOnlyTexts.Length>0;
            _sessionRuntimeText.Enabled=!busy;
        }
        SetLabelText(_gameBadge,translationPresentation.Badge);
        if(_sessionFontCapability is not null)SetLabelText(_sessionFontCapability,translationPresentation.Font);
        if(_sessionRenderingCapability is not null)SetLabelText(_sessionRenderingCapability,translationPresentation.Rendering);
        bool ready=GameTranslationReady(game);
        if(_gameSessionPage.Visible&&game is not null)_sessionGamePath=game.ExePath;
        bool defaultTranslation=GameCapabilities.DefaultTranslationLaunch(game,_fusion?.State.DefaultGameLaunchWithTranslation==true);
        _plainLaunchButton.Text=defaultTranslation?"翻译并启动":"启动游戏";
        _plainLaunchButton.Visible=!running; _plainLaunchButton.Enabled=game is not null&&stopped&&!busy&&(!defaultTranslation||ready);
        _sessionLaunchArrow!.Visible=!running; _sessionLaunchArrow.Enabled=stopped&&!busy;
        bool restarting=IsGameRestartPending(game);
        _sessionReturnGame!.Visible=running; _sessionReturnGame.Enabled=!busy||restarting;
        _sessionRestart!.Visible=running||restarting;_sessionRestart.Enabled=restarting||running&&!busy;_sessionRestart.Text=restarting?"取消重启":"重启游戏";
        foreach(var button in new[]{_sessionRename,_sessionCoverEdit,_sessionFolderOpen})if(button is not null)button.Enabled=game is not null&&!busy;
        _librarySelectionLabel.Text=game is null?"双击游戏进入详细主页":game.Name+(_libraryChineseName.Text.Length>0?" · "+_libraryChineseName.Text:"");
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
            _sessionCompactReturn!.Visible=modifying&&running;
            _sessionCompactReturn.Enabled=!busy||restarting;
            _sessionCompactRestart!.Visible=modifying&&(running||restarting);_sessionCompactRestart.Enabled=restarting||running&&!busy;_sessionCompactRestart.Text=restarting?"取消重启":"重启游戏";
            _sessionCompactRename!.Visible=modifying;_sessionCompactRename.Enabled=game is not null&&!busy;
            _sessionCompactMore!.Visible=modifying;
        }
        _sessionTranslationPanel!.Visible=section==1;
        _sessionModificationPanel!.Visible=section==2&&rpg;
        _renpyModificationPanel!.Visible=section==2&&renpy;
        _unsupportedModificationPanel!.Visible=section==2&&!rpg&&!renpy;
        if(_sessionTranslationChoice.Primary!=(section==1)){_sessionTranslationChoice.Primary=section==1;_sessionTranslationChoice.Invalidate();}
        if(_sessionModificationChoice.Primary!=(section==2)){_sessionModificationChoice.Primary=section==2;_sessionModificationChoice.Invalidate();}
        bool prepared=session?.LaunchMode==GameLaunchMode.Translation;
        bool requested=session?.TranslationRequested==true;
        if(!rpg&&!renpy&&!embedded)
        {
        _sessionEnableTranslation!.Visible=running&&!prepared;
        _sessionEnableTranslation.Enabled=supported&&!busy&&!requested;
        _sessionEnableTranslation.Text=requested?"等待退出后启用":"开启翻译";
        _sessionTranslationState.Text=!supported?"此游戏暂不支持内嵌翻译。":prepared&&(running||session?.LaunchPending==true)?
            "已随本次启动配置翻译 · 实际效果以游戏内文字为准。":requested?
            (running?"当前适配需要重启才能启用。请保存并自行退出游戏，再点击“翻译并启动”。":"已保留开启翻译的选择，可以翻译并启动。"):
            running?(session?.LaunchMode==GameLaunchMode.Ordinary?"翻译未开启 · 当前适配需退出游戏后启用。":"本次启动方式未确认 · 当前适配需退出游戏后应用翻译。"):
            "按需选择翻译方案，普通启动不会开启翻译。";
        if(prepared&&(running||session?.LaunchPending==true)&&session!.AppliedProfileName is { } applied)
        {
            _sessionTranslationState.Text="本次启动方案："+applied+" · 实际效果以游戏内文字为准。";
            if(session.AppliedSettingsIdentity!=FusionConfiguration.EditIdentity(EmbeddedSettings))
                _sessionTranslationState.Text+="\n当前所选方案将在下次翻译启动时应用。";
        }
        }
        _launchGameButton!.Visible=!running; _launchGameButton.Enabled=stopped&&supported&&ready&&!busy;
        _restoreGameButton!.Enabled=(rpg||current&&state?.HasRecord==true)&&stopped&&!busy;
        _installGameButton!.Enabled=supported&&ready&&stopped&&!busy;
        _installGameButton.Visible=supported&&stopped;
        _sessionRetryTranslation!.Visible=rpg&&SelectedDataSession()?.Translation?.Enabled==true&&SelectedDataSession()?.Translation?.FailedCount>0;
        if(rpg||renpy||embedded)
        {
            var data=SelectedTranslationSession();bool connected=running&&data?.Connection.Connected==true;
            _sessionEnableTranslation!.Visible=running;
            _sessionEnableTranslation.Enabled=translationPresentation.CanToggle&&!busy&&(ready||data?.Translation?.Enabled==true);
            _sessionEnableTranslation.Text=connected?(data?.Translation?.Enabled==true?"关闭翻译":"开启翻译"):"翻译未接入";
            _sessionTranslationState.Text=connected?(data!.Translation?.Status??"游戏已连接，翻译关闭。可在游玩时开启，无需重启。"):
                !string.IsNullOrEmpty(session?.TranslationBlockedReason)?"本次翻译未接入："+session.TranslationBlockedReason:
                running?(busy?"正在连接游戏…":"此游戏尚未建立翻译连接。保存退出后，可从本软件重新启动并检查连接结果。"):
                translationPresentation.PendingVerification?"待接入验证。可尝试翻译并启动，连接成功后再开启翻译；实际效果以游戏内显示为准。":
                "启动游戏后可随时开启翻译，开启后提取文本并后台预译；已译内容直接显示译文，未译内容保留游戏原文。";
            _sessionRetryTranslation!.Visible=connected&&data?.Translation?.FailedCount>0;
            _restoreGameButton!.Enabled=stopped&&!busy&&(embedded?state?.HasRecord==true:!renpy||RenpyGameAdapter.HasRecord(game!.ExePath));
        }
        _sessionActionFeedback.Text=session?.Feedback??""; _sessionActionFeedback.Visible=_sessionActionFeedback.Text.Length>0;
        if(_sessionScanKey is not null)_sessionScanKey.Text=rpg||renpy||embedded?
            translationPresentation.CanToggle?"使用本页翻译开关":translationPresentation.Blocked?"当前无法开关翻译":"接入后可用":
            GameCapabilities.TranslationToggleKey(game)?_fusion?.State.EmbeddedToggleKey??"F8":"当前引擎尚无内嵌翻译开关";
        if(_sessionScanKeyEdit is not null)_sessionScanKeyEdit.Visible=GameCapabilities.TranslationToggleKey(game);
        var runningPaths=_gameSessions.Snapshot().Where(x=>x.Process==FusionGameProcessState.Running).Select(x=>x.Game.ExePath).ToArray();
        _libraryGrid.UpdateRunning(runningPaths);_recentShelf.UpdateRunning(runningPaths);_libraryNavigation.UpdateRunning(runningPaths);
        RenderGameModificationState();
        RenderRenpyModificationState();
        RenderGameDiagnosticState();
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
            if(!SetForegroundWindow(window))_statusLabel.Text="游戏窗口已找到，请从任务栏切回。";
        }
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
    }
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr window);
}
