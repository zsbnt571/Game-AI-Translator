namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private void ShowLibraryCategoryContext(LibraryNavigationTarget target,Point point)
    {
        _folderContext.ClearActions();_folderContext.Font=Font;
        _folderContext.AddAction("新建分类","plus",(_,_)=>AddLibraryCategory());
        if(target.Kind=="tag")
        {
            _folderContext.Items.Add(new ToolStripSeparator());
            _folderContext.AddAction("重命名分类","edit",(_,_)=>
            {
                var name=AskLibraryFolderName("重命名分类",target.Value,"分类名称");if(name is null)return;
                try{RenameLibraryCategory(target.Value,name);}catch(Exception ex){ShowFusionError(ex);}
            });
            _folderContext.AddAction("删除分类","trash",(_,_)=>
            {
                try{DeleteLibraryCategory(target.Value);}
                catch(Exception ex){ShowFusionError(ex);}
            });
        }
        _folderContext.Show(_libraryNavigation,point);
    }
    private void RenameLibraryCategory(string category,string name)
    {
        name=name.Trim();
        if(name.Length==0||name.Length>40||name.IndexOfAny([',','，',';','；'])>=0)throw new ArgumentException("分类名需为 1–40 个字，不能包含逗号或分号。");
        if(category==name)return;
        var current=LibraryPreferencesStore.Load();var metadata=_libraryMetadata??=new LibraryMetadata();metadata.Reload();
        if((current.Categories??[]).Contains(name,StringComparer.Ordinal))
            throw new ArgumentException("已有同名分类，请换一个名称。");
        var next=current with{
            Categories=(current.Categories??[]).Select(t=>t==category?name:t).Append(name).Distinct().ToArray(),
            RecentSection=current.RecentSection=="tag:"+category?"tag:"+name:current.RecentSection,
            MainSection=current.MainSection=="tag:"+category?"tag:"+name:current.MainSection};
        metadata.RenameCategory(category,name);LibraryPreferencesStore.Save(next);_libraryPreferences=next;
        if(_libraryTag==category)_libraryTag=name;
        ScheduleLibraryRefresh();_statusLabel.Text="分类已重命名，原有游戏归属保留。";
    }
    private void DeleteLibraryCategory(string category)
    {
        if(string.IsNullOrWhiteSpace(category))return;
        // Preserve preferences saved after this window last refreshed. Categories can
        // also come from game tags, so both sources must stop referring to this name.
        var current=LibraryPreferencesStore.Load();
        var next=current with{
            Categories=(current.Categories??[]).Where(name=>name!=category).ToArray(),
            RecentSection=current.RecentSection=="tag:"+category?"recent":current.RecentSection,
            MainSection=current.MainSection=="tag:"+category?"all":current.MainSection};
        (_libraryMetadata??=new LibraryMetadata()).RemoveCategory(category);
        LibraryPreferencesStore.Save(next);_libraryPreferences=next;
        if(_libraryTag==category){_libraryTag="";_libraryScope="home";_libraryPage=0;}
        ScheduleLibraryRefresh();
        if(_statusLabel is not null)_statusLabel.Text="已删除分类“"+category+"”，游戏和磁盘文件保留。";
    }
}
