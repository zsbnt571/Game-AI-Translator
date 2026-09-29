namespace ScreenshotTranslationUiTester;

internal static class LibraryFolders
{
    internal static bool Contains(string root,string exe)
    {
        try{return Path.GetFullPath(exe).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);}catch{return false;}
    }
    internal static bool Contains(LibraryFolder folder,RecentGame game)=>folder.Root is {Length:>0} root?Contains(root,game.ExePath)&&!(folder.ExcludedGames??[]).Contains(game.Id,StringComparer.OrdinalIgnoreCase):(folder.Games??[]).Contains(game.Id,StringComparer.OrdinalIgnoreCase);
    internal static LibraryFolder[] All(LibraryPreferences preferences)=>preferences.Folders??[];
}

public sealed partial class MainForm
{
    private string _libraryCollection="";
    private readonly WorkspaceMenu _folderContext=new();
    private string? AskLibraryFolderName(string title,string initial="",string label="文件夹名称")
    {
        int S(int value)=>value*DeviceDpi/96;
        using var dialog=new Form{Text=title,StartPosition=FormStartPosition.CenterParent,AutoScaleMode=AutoScaleMode.None,ClientSize=new(S(400),S(180)),Font=Font,FormBorderStyle=FormBorderStyle.FixedDialog,MinimizeBox=false,MaximizeBox=false};
        var input=new TextBox{Text=initial,MaxLength=64,Width=330,PlaceholderText="例如：剧情游戏、正在玩、待整理"};
        var layout=WorkspaceColumn();layout.Dock=DockStyle.Top;layout.AutoSize=true;layout.Padding=new(S(18));
        WorkspaceAdd(layout,WorkspaceLabel(label));WorkspaceAdd(layout,SearchField(input,label=="分类名称"?"tag":"folder"),SizeType.Absolute,S(36));
        var save=WorkspaceButton("保存",(_,_)=>{if(input.Text.Trim().Length>0)dialog.DialogResult=DialogResult.OK;});
        var cancel=WorkspaceButton("取消",(_,_)=>dialog.DialogResult=DialogResult.Cancel);
        WorkspaceAdd(layout,new WorkspaceAlignedRow(save,cancel){Margin=new(0,S(12),0,0)});dialog.Controls.Add(layout);dialog.AcceptButton=save;dialog.CancelButton=cancel;UiTheme.Apply(dialog,_appliedDesktopSettings);
        return dialog.ShowDialog(this)==DialogResult.OK?input.Text.Trim():null;
    }
    private void SaveLibraryFolders(IEnumerable<LibraryFolder> folders)
    {
        var next=_libraryPreferences with{Folders=folders.ToArray()};LibraryPreferencesStore.Save(next);_libraryPreferences=next;ScheduleLibraryRefresh();
    }
    private void CreateLibraryFolder()
    {
        var name=AskLibraryFolderName("新建游戏库文件夹");if(name is null)return;
        try{var folder=new LibraryFolder(Guid.NewGuid().ToString("N"),name);SaveLibraryFolders(LibraryFolders.All(_libraryPreferences).Append(folder));NavigateLibrary(new("collection",folder.Id));}
        catch(Exception ex){ShowFusionError(ex);}
    }
    private void RegisterLibraryRoots(IEnumerable<string> roots)
    {
        var folders=LibraryFolders.All(_libraryPreferences).ToList();bool changed=false;
        foreach(var root in roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(folders.Any(f=>string.Equals(f.Root,root,StringComparison.OrdinalIgnoreCase)))continue;
            folders.Add(new(Guid.NewGuid().ToString("N"),LibrarySections.FolderName(root),root));changed=true;
        }
        if(changed)SaveLibraryFolders(folders);
    }
    private async Task ChooseLibraryRootAsync()
    {
        using var dialog=new FolderBrowserDialog{Description="选择存放多个游戏的总目录；包括其中所有子文件夹",UseDescriptionForTitle=true};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try{RegisterLibraryRoots([dialog.SelectedPath]);await ImportLibraryFolderAsync(dialog.SelectedPath);}catch(Exception ex){ShowFusionError(ex);}
    }
    private async void ShowLibraryFolderContext(LibraryNavigationTarget target,Point point)
    {
        if(target.Kind=="game")
        {
            try{var store=await RecentStore();if(!IsDisposed&&store.Find(target.Value) is {} game)ShowLibraryGameContextMenu(game,point,_libraryNavigation,true);}
            catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
            return;
        }
        if(target.Kind is "tag" or "new-category"||target is {Kind:"heading",Value:"categories"})
        {ShowLibraryCategoryContext(target,point);return;}
        _folderContext.ClearActions();_folderContext.Font=Font;
        _folderContext.AddAction("新建游戏库文件夹","plus",(_,_)=>CreateLibraryFolder());
        _folderContext.AddAction("添加游戏总目录…","folder",async(_,_)=>await ChooseLibraryRootAsync());
        if(target.Kind=="collection"&&LibraryFolders.All(_libraryPreferences).FirstOrDefault(f=>f.Id==target.Value) is { } folder)
        {
            _folderContext.Items.Add(new ToolStripSeparator());
            _folderContext.AddAction("重命名","edit",(_,_)=>{var name=AskLibraryFolderName("重命名文件夹",folder.Name);if(name is null)return;try{SaveLibraryFolders(LibraryFolders.All(_libraryPreferences).Select(f=>f.Id==folder.Id?f with{Name=name}:f));}catch(Exception ex){ShowFusionError(ex);}});
            if(folder.Root is not null)_folderContext.AddAction("扫描此目录","search",async(_,_)=>await ImportLibraryFolderAsync(folder.Root));
            _folderContext.AddAction("删除游戏库文件夹","trash",(_,_)=>
            {
                try
                {
                    SaveLibraryFolders(LibraryFolders.All(_libraryPreferences).Where(f=>f.Id!=folder.Id));
                    var next=_libraryPreferences with{RecentSection=_libraryPreferences.RecentSection=="collection:"+folder.Id?"recent":_libraryPreferences.RecentSection,MainSection=_libraryPreferences.MainSection=="collection:"+folder.Id?"all":_libraryPreferences.MainSection};
                    LibraryPreferencesStore.Save(next);_libraryPreferences=next;if(_libraryCollection==folder.Id)NavigateLibrary(new("scope","home"));
                    _statusLabel.Text="已删除库内文件夹，游戏和磁盘文件保留。";
                }catch(Exception ex){ShowFusionError(ex);}
            });
        }
        _folderContext.Show(_libraryNavigation,point);
    }
    private void AddLibraryFolderMembershipMenu(WorkspaceMenu menu,RecentGame game)
    {
        var group=menu.AddGroup("收纳到文件夹","folder");
        foreach(var folder in LibraryFolders.All(_libraryPreferences).Where(f=>f.Root is null))
        {
            bool member=LibraryFolders.Contains(folder,game);
            group.AddAction(folder.Name,member?"check":"folder",(_,_)=>
            {
                try{var games=(folder.Games??[]).ToHashSet(StringComparer.OrdinalIgnoreCase);if(!games.Add(game.Id))games.Remove(game.Id);
                    SaveLibraryFolders(LibraryFolders.All(_libraryPreferences).Select(f=>f.Id==folder.Id?f with{Games=games.ToArray()}:f));}
                catch(Exception ex){ShowFusionError(ex);}
            });
        }
        group.Items.Add(new ToolStripSeparator());group.AddAction("新建文件夹…","plus",(_,_)=>
        {var name=AskLibraryFolderName("新建文件夹并收纳此游戏");if(name is null)return;try{SaveLibraryFolders(LibraryFolders.All(_libraryPreferences).Append(new(Guid.NewGuid().ToString("N"),name,Games:[game.Id])));}catch(Exception ex){ShowFusionError(ex);}});
    }
}
