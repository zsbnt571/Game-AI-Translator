namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private GameLibraryGrid? _libraryContextSource;
    private static string[] CategoryTokens(string value)=>value.Split([',','，',';','；'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
    private void UpdateLibraryAnnotations(RecentGame[] games,Func<LibraryAnnotation,LibraryAnnotation> update,string message)
    {
        try{_libraryMetadata!.UpdateMany(games.Select(g=>g.Id),update);ScheduleLibraryRefresh();_statusLabel.Text=message;}
        catch(Exception ex){ShowFusionError(ex);}
    }
    private void SetLibraryMembership(RecentGame[] games,string id,bool member)
    {
        var keys=games.Select(g=>g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var latest=LibraryPreferencesStore.Load();
        var folders=LibraryFolders.All(latest).Select(f=>
        {
            if(f.Id!=id)return f;
            var included=(f.Games??[]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excluded=(f.ExcludedGames??[]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(member){included.UnionWith(keys);excluded.ExceptWith(keys);}else{included.ExceptWith(keys);excluded.UnionWith(keys);}
            return f with{Games=included.ToArray(),ExcludedGames=excluded.ToArray()};
        }).ToArray();
        var next=latest with{Folders=folders};LibraryPreferencesStore.Save(next);_libraryPreferences=next;ScheduleLibraryRefresh();
    }
    private void AddLibraryOrganizingMenu(WorkspaceMenu menu,RecentGame[] games,GameLibraryGrid source)
    {
        bool allFavorite=games.All(g=>_libraryMetadata!.Get(g.Id).Bookmark);
        menu.AddAction(allFavorite?"取消收藏":"收藏","tag",(_,_)=>UpdateLibraryAnnotations(games,a=>a with{Bookmark=!allFavorite},allFavorite?"已取消收藏。":"已收藏所选游戏。"));
        if(!allFavorite&&games.Any(g=>_libraryMetadata!.Get(g.Id).Bookmark))
            menu.AddAction("取消所选游戏的收藏","tag",(_,_)=>UpdateLibraryAnnotations(games,a=>a with{Bookmark=false},"已取消所选游戏的收藏。"));
        var categories=menu.AddGroup("添加到分类","tag");
        foreach(var tag in (_libraryPreferences.Categories??[]).Concat(games.SelectMany(g=>CategoryTokens(_libraryMetadata!.Get(g.Id).Tags))).Distinct().OrderBy(x=>x))
            categories.AddAction(tag,"tag",(_,_)=>UpdateLibraryAnnotations(games,a=>a with{Tags=string.Join(", ",CategoryTokens(a.Tags).Append(tag).Distinct())},$"已添加到“{tag}”。"));
        if(categories.Items.Count==0)categories.AddAction("请先在左侧新建分类","plus",(_,_)=>{},false);
        var folders=menu.AddGroup("收纳到文件夹","folder");
        foreach(var folder in LibraryFolders.All(_libraryPreferences).Where(f=>f.Root is null))
            folders.AddAction(folder.Name,"folder",(_,_)=>{try{SetLibraryMembership(games,folder.Id,true);}catch(Exception ex){ShowFusionError(ex);}});
        folders.AddAction("新建文件夹并收纳…","plus",(_,_)=>
        {
            var name=AskLibraryFolderName("新建文件夹并收纳所选游戏");if(name is null)return;
            try{SaveLibraryFolders(LibraryFolders.All(_libraryPreferences).Append(new(Guid.NewGuid().ToString("N"),name,Games:games.Select(g=>g.Id).ToArray())));}catch(Exception ex){ShowFusionError(ex);}
        });
        string scope=_libraryTag.Length>0?"tag:"+_libraryTag:_libraryCollection.Length>0?"collection:"+_libraryCollection:
            source==_recentShelf?_libraryPreferences.RecentSection:_libraryPreferences.MainSection;
        if(scope.StartsWith("tag:"))
        {
            string tag=scope[4..];menu.AddAction("移出当前分类","minus",(_,_)=>UpdateLibraryAnnotations(games,a=>a with{Tags=string.Join(", ",CategoryTokens(a.Tags).Where(t=>t!=tag))},$"已移出“{tag}”，游戏仍在库中。"));
        }
        else if(scope.StartsWith("collection:"))
            menu.AddAction("移出当前文件夹","minus",(_,_)=>{try{SetLibraryMembership(games,scope[11..],false);_statusLabel.Text="已移出当前文件夹，游戏仍在库中。";}catch(Exception ex){ShowFusionError(ex);}});
    }
    private async Task RemoveLibraryGamesAsync(RecentGame[] games)
    {
        try
        {
            await _rememberSelectedTask;var store=await RecentStore();if(IsDisposed)return;
            store.RemoveFromLibrary(games.Select(g=>g.Id));
            if(_selectedGame is {} selected&&games.Any(g=>SameGame(g.ExePath,selected.ExePath)))
            {
                _gameSelectionEpoch++;_gameQueryEpoch++;CancelSelectedMedia();_selectedGame=null;_desktopGameState=null;_sessionGamePath=null;
                _fusion?.Change(next=>next.LastGame="");_gamePath.Text="";_gameTitle.Text="选择一个游戏";_gameOperationResult="";ShowLibraryHome();RenderGameState();
            }
            ScheduleLibraryRefresh();_statusLabel.Text=$"已从游戏库移除 {games.Length} 个游戏，磁盘文件和运行中的游戏保留。";
        }
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
    }
}
