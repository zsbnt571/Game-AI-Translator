namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private ILibraryGridSession? _libraryContextSource;
    private static string[] CategoryTokens(string value)=>value.Split([',','，',';','；'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
    private void UpdateLibraryAnnotations(RecentGame[] games,Func<LibraryAnnotation,LibraryAnnotation> update,string message)
    {
        try{_libraryMetadata!.UpdateMany(games.Select(g=>g.Id),update);ScheduleLibraryRefresh();DesktopStatus=message;}
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
    private void AddLibraryOrganizingMenu(WorkspaceMenu menu,RecentGame[] games,ILibraryGridSession source)
    {
        menu.AddAction("编辑分类标签","tag",(_,_)=>EditLibraryClassificationTags(games));
        var categories=menu.AddGroup("添加到分类","layers");
        foreach(var tag in LibraryCategories.Names(_libraryPreferences,games,_libraryMetadata!))
            categories.AddAction(tag,"layers",(_,_)=>SetLibraryCategoryMembership(games,tag,true));
        if(categories.Items.Count==0)categories.AddAction("请先在左侧新建分类","plus",(_,_)=>{},false);
        bool allFavorite=games.All(g=>_libraryMetadata!.Get(g.Id).Bookmark);
        menu.AddAction(allFavorite?"取消收藏":"收藏","star",(_,_)=>UpdateLibraryAnnotations(games,a=>a with{Bookmark=!allFavorite},allFavorite?"已取消收藏。":"已收藏所选游戏。"));
        if(!allFavorite&&games.Any(g=>_libraryMetadata!.Get(g.Id).Bookmark))
            menu.AddAction("取消所选游戏的收藏","star",(_,_)=>UpdateLibraryAnnotations(games,a=>a with{Bookmark=false},"已取消所选游戏的收藏。"));
        string scope=_libraryTag.Length>0?"tag:"+_libraryTag:_libraryCollection.Length>0?"collection:"+_libraryCollection:
            source==_recentShelf?_libraryPreferences.RecentSection:_libraryPreferences.MainSection;
        if(scope.StartsWith("collection:")&&LibraryCategories.LegacyCollections(_libraryPreferences).FirstOrDefault(f=>f.Id==scope[11..]) is {} legacy)scope="tag:"+legacy.Name;
        if(scope.StartsWith("tag:"))
        {
            string tag=scope[4..];menu.AddAction("移出当前分类","minus",(_,_)=>SetLibraryCategoryMembership(games,tag,false));
        }
        else if(scope.StartsWith("collection:"))
            menu.AddAction("移出当前文件夹","minus",(_,_)=>{try{SetLibraryMembership(games,scope[11..],false);DesktopStatus="已移出当前文件夹，游戏仍在库中。";}catch(Exception ex){ShowFusionError(ex);}});
    }
    private void SetLibraryCategoryMembership(RecentGame[] games,string category,bool member)
    {
        try
        {
            var current=LibraryPreferencesStore.Load();
            var legacy=LibraryCategories.LegacyCollections(current).Where(f=>f.Name==category).ToArray();
            // Preserve names containing punctuation in old collections without
            // splitting them into the older comma-delimited annotation format.
            if(member&&legacy.Length>0)SetLibraryMembership(games,legacy[0].Id,true);
            else
            {
                _libraryMetadata!.UpdateMany(games.Select(g=>g.Id),a=>a with{Tags=string.Join(", ",member?CategoryTokens(a.Tags).Append(category).Distinct():CategoryTokens(a.Tags).Where(t=>t!=category))});
                if(!member)foreach(var collection in legacy)SetLibraryMembership(games,collection.Id,false);
            }
            ScheduleLibraryRefresh();DesktopStatus=member?$"已添加到“{category}”。":$"已移出“{category}”，游戏仍在库中。";
        }
        catch(Exception ex){ShowFusionError(ex);}
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
                _fusion?.Change(next=>next.LastGame="");GamePathText="";GameTitleText="选择一个游戏";_gameOperationResult="";ShowLibraryHome();RenderGameState();
            }
            ScheduleLibraryRefresh();DesktopStatus=$"已从游戏库移除 {games.Length} 个游戏，磁盘文件和运行中的游戏保留。";
        }
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
    }
}
