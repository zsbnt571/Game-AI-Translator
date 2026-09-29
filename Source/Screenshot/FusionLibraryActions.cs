namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly WorkspaceMenu _libraryContextMenu = new();
    private readonly WorkspaceMenu _gameLaunchMenu = new();
    private int _libraryCapabilityMenuEpoch;
    private Func<string,CancellationToken,Task<GameInfo>> _libraryCapabilityDetector =
        (path,token)=>Task.Run(()=>GameDetector.Detect(path),token);

    private async Task OpenLibraryGameAsync(string path)
    {
        await SelectFusionGameAsync(path,recordSelection:false);
        if(IsDisposed||_gameDetecting||!SameGame(path,_selectedGame?.ExePath))return;
        OpenGameSession(_selectedGame!);
        ScheduleLibraryRefresh();
    }

    private void ShowGameLaunchMenu(Control anchor)
    {
        if(_selectedGame is not { } game)return;
        _gameLaunchMenu.ClearActions();
        _gameLaunchMenu.Font=Font;
        var session=_gameSessions.Find(game.ExePath);
        bool stopped=SameGame(_desktopGameState?.Game.ExePath,game.ExePath)&&_desktopGameState?.Process==FusionGameProcessState.Stopped;
        bool busy=_gameDetecting||session?.Busy==true||session?.LaunchPending==true;
        _gameLaunchMenu.AddAction("普通启动","play",async(_,_)=>await RunLibraryActionAsync(game.ExePath,"plain"),stopped&&!busy);
        if(EmbeddedGameAdapters.Handles(game))_gameLaunchMenu.AddAction("仅启动（不接入翻译）","play",async(_,_)=>await RunLibraryActionAsync(game.ExePath,"plain-bare"),stopped&&!busy);
        var translation=_gameLaunchMenu.AddAction("翻译并启动","translate",async(_,_)=>await RunLibraryActionAsync(game.ExePath,"launch"));
        UpdateTranslationMenuItem(translation,game,stopped,busy);
        _gameLaunchMenu.Show(anchor,new Point(0,anchor.Height));
    }

    private void UpdateTranslationMenuItem(ToolStripItem item,GameInfo game,bool stopped,bool busy)
    {
        bool supported=GameCapabilities.EmbeddedTranslation(game),ready=GameTranslationReady(game);
        item.Text=!supported?"翻译并启动（暂不支持）":!ready?"翻译并启动（先设置方案）":"翻译并启动";
        item.Enabled=supported&&ready&&stopped&&!busy;
        item.ToolTipText=!supported?"当前游戏尚无内嵌翻译适配，可使用普通启动。":!ready?"请先在游戏详情页设置翻译方案。":"使用此游戏的翻译方案启动。";
    }

    private async Task ResolveLibraryTranslationMenuAsync(string path,ToolStripItem item,int epoch)
    {
        try
        {
            // Cached sidebar engine names are not evidence of an available adapter.
            var game=await _libraryCapabilityDetector(path,_desktopLifetime.Token);
            if(IsDisposed||epoch!=_libraryCapabilityMenuEpoch||!_libraryContextMenu.Items.Contains(item))return;
            var operations=_gameLaunchOperations;
            var process=await Task.Run(()=>operations.ProcessState(game),_desktopLifetime.Token);
            if(IsDisposed||epoch!=_libraryCapabilityMenuEpoch||!_libraryContextMenu.Items.Contains(item))return;
            var session=_gameSessions.Find(path);
            UpdateTranslationMenuItem(item,game,process==FusionGameProcessState.Stopped,session?.Busy==true||session?.LaunchPending==true);
        }
        catch(OperationCanceledException){}
        catch(Exception)
        {
            if(IsDisposed||epoch!=_libraryCapabilityMenuEpoch||!_libraryContextMenu.Items.Contains(item))return;
            item.Enabled=false;item.Text="翻译并启动（无法核对）";item.ToolTipText="请打开游戏详情页重新识别游戏。";
        }
    }

    private void ShowLibraryContextMenu(RecentGame game,Point point)
        =>ShowLibraryGameContextMenu(game,point,_libraryGrid,false);

    private void ShowLibraryGameContextMenu(RecentGame game,Point point,Control anchor,bool single)
    {
        _libraryContextMenu.ClearActions(); _libraryContextMenu.Font=Font;int capabilityEpoch=++_libraryCapabilityMenuEpoch;
        bool running=_gameSessions.Find(game.ExePath)?.Process==FusionGameProcessState.Running;
        bool busy=_gameSessions.Find(game.ExePath)?.Busy==true;
        ToolStripItem Add(string label,string action,string icon,bool enabled=true)
        {
            var item=_libraryContextMenu.AddAction(label,icon,async(_,_)=>await RunLibraryActionAsync(game.ExePath,action));
            item.Enabled=enabled;return item;
        }
        var source=_libraryContextSource??_libraryGrid;
        var selected=source.SelectedGames;
        var games=!single&&selected.Any(x=>x.Id==game.Id)?selected:[game];
        if(games.Length==1)
        {
            Add(running?"返回游戏":"普通启动",running?"return":"plain","play",!busy);
            var translation=Add("翻译并启动（核对适配中）","launch","translate",false);
            var known=SameGame(_selectedGame?.ExePath,game.ExePath)?_selectedGame:_gameSessions.Find(game.ExePath)?.Game;
            if(known is not null)
            {
                var session=_gameSessions.Find(game.ExePath);
                bool isSelected=SameGame(_desktopGameState?.Game.ExePath,game.ExePath);
                var process=isSelected?_desktopGameState!.Process:session?.Process;
                UpdateTranslationMenuItem(translation,known,process==FusionGameProcessState.Stopped,busy||session?.LaunchPending==true||isSelected&&_gameDetecting);
                if((process is null or FusionGameProcessState.Unknown)&&session?.LaunchPending!=true)
                    _=ResolveLibraryTranslationMenuAsync(game.ExePath,translation,capabilityEpoch);
            }
            else _=ResolveLibraryTranslationMenuAsync(game.ExePath,translation,capabilityEpoch);
            Add("进入详细主页","detail","game");
            _libraryContextMenu.Items.Add(new ToolStripSeparator());
        }
        AddLibraryOrganizingMenu(_libraryContextMenu,games,source);
        if(games.Length==1)
        {
            _libraryContextMenu.Items.Add(new ToolStripSeparator());
            Add("标签与便签","note","tag");Add("编辑名称","name","edit");
            Add("打开游戏文件夹","folder","folder");Add("更换图片","cover","image",!busy);
        }
        _libraryContextMenu.Items.Add(new ToolStripSeparator());
        _libraryContextMenu.AddAction(games.Length>1?$"从游戏库移除 {games.Length} 个游戏":"从游戏库移除","trash",async(_,_)=>await RemoveLibraryGamesAsync(games));
        _libraryContextMenu.Show(anchor,point);
    }

    private async Task RunLibraryActionAsync(string path,string action)
    {
        try
        {
            await SelectFusionGameAsync(path,recordSelection:false);
            if(IsDisposed||_gameDetecting||!SameGame(path,_selectedGame?.ExePath))return;
            switch(action)
            {
                case "detail":OpenGameSession(_selectedGame!);break;
                case "plain":case "plain-bare":await RunGameActionAsync(action);break;
                case "launch":
                    if(!GameCapabilities.EmbeddedTranslation(_selectedGame))
                    {
                        _gameSessions.Get(_selectedGame!).Feedback="当前游戏尚无内嵌翻译适配，请使用普通启动。";RenderGameSessionState();return;
                    }
                    await RunGameActionAsync(action);break;
                case "return":await ReturnToSelectedGameAsync();break;
                case "note":case "name":await EditLibraryNoteAsync();break;
                case "folder":_folderButton.PerformClick();break;
                case "cover":await EditGameArtworkAsync();break;
                case "remove":await RemoveSelectedLibraryGameAsync();break;
            }
        }
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
    }

    private async Task RemoveSelectedLibraryGameAsync()
    {
        if(_selectedGame is not { } game||_gameActionRunning)return;
        var epoch=_gameSelectionEpoch;
        await _rememberSelectedTask;
        var store=await RecentStore();
        if(IsDisposed||epoch!=_gameSelectionEpoch||!SameGame(game.ExePath,_selectedGame?.ExePath))return;
        if(store.Find(game.ExePath) is not { } record)return;
        // Only unregister the library entry. Runtime sessions and game files remain untouched.
        store.RemoveFromLibrary(record.Id);
        _gameSelectionEpoch++;_gameQueryEpoch++;
        CancelSelectedMedia();_selectedGame=null;_desktopGameState=null;_sessionGamePath=null;
        if(_fusion?.State.LastGame==game.ExePath)_fusion.Change(next=>next.LastGame="");
        _gamePath.Text="";_gameTitle.Text="选择一个游戏";_gameOperationResult="";
        ShowLibraryHome();RenderGameState();ScheduleLibraryRefresh();
        _statusLabel.Text="已从游戏库移除；游戏文件和正在运行的游戏保留。";
    }
}
