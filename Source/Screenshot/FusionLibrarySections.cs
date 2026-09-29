namespace ScreenshotTranslationUiTester;

internal static class LibrarySections
{
    internal static string Normalize(string? key,string fallback)=>key is "all" or "recent" or "favorite"?key:
        key is {Length:>0 and <=2048}&&(key.StartsWith("collection:")||key.StartsWith("folder:")||key.StartsWith("engine:")||key.StartsWith("tag:"))?key:fallback;
    internal static string Title(string key,LibraryPreferences? preferences=null)=>key switch
    {
        "all"=>"我的游戏","recent"=>"最近使用","favorite"=>"收藏",
        _ when key.StartsWith("collection:")=>preferences?.Folders?.FirstOrDefault(f=>f.Id==key[11..])?.Name??"文件夹",
        _ when key.StartsWith("folder:")=>FolderName(key[7..]),
        _ when key.StartsWith("engine:")=>key[7..],
        _ when key.StartsWith("tag:")=>key[4..],_=>"我的游戏"
    };
    internal static string FolderName(string path){var name=Path.GetFileName(Path.TrimEndingDirectorySeparator(path));return name.Length==0?path:name;}
    internal static IEnumerable<RecentGame> Select(IEnumerable<RecentGame> games,string key,LibraryMetadata metadata,Dictionary<string,string> engines,int recentLimit,LibraryPreferences? preferences=null)
    {
        return key switch
        {
            "recent"=>games.Where(x=>x.RecentAt.HasValue).OrderByDescending(x=>x.RecentAt).Take(recentLimit),
            "favorite"=>games.Where(x=>metadata.Get(x.Id).Bookmark),
            _ when key.StartsWith("folder:")=>games.Where(x=>LibraryFolders.Contains(key[7..],x.ExePath)),
            _ when key.StartsWith("collection:")=>games.Where(x=>preferences?.Folders?.FirstOrDefault(f=>f.Id==key[11..]) is { } folder&&LibraryFolders.Contains(folder,x)),
            _ when key.StartsWith("engine:")=>games.Where(x=>(engines.GetValueOrDefault(x.ExePath)??"待识别")==key[7..]),
            _ when key.StartsWith("tag:")=>games.Where(x=>metadata.Get(x.Id).Tags.Split([',','，',';','；'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Contains(key[4..])),
            _=>games
        };
    }
}

internal sealed class LibrarySectionHeader:Button
{
    private bool hover;
    internal LibrarySectionHeader(string text)
    {
        Text=text;AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;
        Margin=new(0,4,8,5);AccessibleDescription="选择这个栏目的游戏范围";
        SetStyle(ControlStyles.OptimizedDoubleBuffer,true);
    }
    public override Size GetPreferredSize(Size proposedSize)=>new(Math.Min(360*DeviceDpi/96,TextRenderer.MeasureText(Text,Font).Width+34*DeviceDpi/96),Font.Height+8*DeviceDpi/96);
    protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hover=true;Invalidate();}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=false;Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int n)=>n*DeviceDpi/96;var color=hover?UiTheme.Current.Accent:UiTheme.Current.Text;
        e.Graphics.Clear(Parent?.BackColor??UiTheme.Current.Main);
        WorkspaceDrawing.Text(e.Graphics,Text,Font,new(0,0,Width-S(26),Height),color);
        WorkspaceSkin.Icon(e.Graphics,"down",new(Width-S(20),(Height-S(14))/2,S(14),S(14)),color);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-1,-1));
    }
}

public sealed partial class MainForm
{
    private readonly LibrarySectionHeader _recentSectionHeading=new("最近使用");
    private readonly LibrarySectionHeader _mainSectionHeading=new("我的游戏");
    private readonly WorkspaceMenu _sectionMenu=new();
    private int _recentSectionTotal;

    private async void ShowLibrarySectionMenu(bool upper)
    {
        try
        {
            var store=await RecentStore();_libraryMetadata=await(_libraryMetadataTask??=Task.Run(()=>new LibraryMetadata()));
            var games=await Task.Run(store.SnapshotAll);if(IsDisposed)return;
            ConfigureLibrarySectionMenu(upper,games);
            var anchor=upper?_recentSectionHeading:_mainSectionHeading;
            if(anchor.Visible)_sectionMenu.Show(anchor,new Point(0,anchor.Height));
        }
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
    }
    private void ConfigureLibrarySectionMenu(bool upper,RecentGame[] games)
    {
        _sectionMenu.ClearActions();_sectionMenu.Font=Font;
        string chosen=upper?_libraryPreferences.RecentSection:_libraryPreferences.MainSection;
        void Add(WorkspaceMenu menu,string label,string key,string icon,string? detail=null)
        {
            var item=menu.AddAction(label,key==chosen?"check":icon,(_,_)=>SetLibrarySection(upper,key));
            item.ToolTipText=detail??label;
        }
        Add(_sectionMenu,"全部游戏","all","grid");Add(_sectionMenu,"最近使用","recent","calendar");Add(_sectionMenu,"收藏","favorite","tag");
        _sectionMenu.Items.Add(new ToolStripSeparator());
        var folders=_sectionMenu.AddGroup("按文件夹","folder");
        foreach(var folder in LibraryFolders.All(_libraryPreferences))
            Add(folders,folder.Name+"  ·  "+games.Count(g=>LibraryFolders.Contains(folder,g)),"collection:"+folder.Id,"folder",folder.Root);
        folders.AddAction("新建文件夹…","plus",(_,_)=>CreateLibraryFolder());
        folders.AddAction("添加游戏总目录…","folder",async(_,_)=>await ChooseLibraryRootAsync());
        var engines=_sectionMenu.AddGroup("按引擎","game");
        foreach(var group in games.GroupBy(x=>_libraryEngines.GetValueOrDefault(x.ExePath)??"待识别").OrderBy(x=>x.Key))
            Add(engines,group.Key+"  ·  "+group.Count(),"engine:"+group.Key,"game");
        var categories=_sectionMenu.AddGroup("我的分类","tag");
        foreach(var tag in (_libraryPreferences.Categories??[]).Concat(games.SelectMany(x=>_libraryMetadata!.Get(x.Id).Tags.Split([',','，',';','；'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))).Distinct().OrderBy(x=>x))
            Add(categories,tag,"tag:"+tag,"tag");
        if(categories.Items.Count==0)categories.AddAction("暂无分类，可在左侧新建","tag",(_,_)=>{},false);
        if(folders.Items.Count==0)folders.AddAction("导入游戏后显示文件夹","folder",(_,_)=>{},false);
        if(engines.Items.Count==0)engines.AddAction("暂无游戏","game",(_,_)=>{},false);
        if(upper&&_recentSectionTotal>24)
        {
            _sectionMenu.Items.Add(new ToolStripSeparator());
            _sectionMenu.AddAction($"在下方查看全部 {_recentSectionTotal} 项","list",(_,_)=>SetLibrarySection(false,chosen));
        }
    }
    private void SetLibrarySection(bool upper,string key)
    {
        try
        {
            key=LibrarySections.Normalize(key,upper?"recent":"all");
            var next=upper?_libraryPreferences with{RecentSection=key}:_libraryPreferences with{MainSection=key};
            LibraryPreferencesStore.Save(next);_libraryPreferences=next;
            if(!upper)
            {
                _libraryFiltersLoading=true;
                try{_libraryCollection="";_libraryScope="home";_libraryTag="";_libraryEngine.SelectedIndex=0;_libraryFolder.SelectedIndex=0;_libraryFilter.SelectedIndex=0;_librarySearch.Clear();}
                finally{_libraryFiltersLoading=false;}
            }
            _libraryPage=0;ScheduleLibraryRefresh();
        }
        catch(Exception ex){ShowFusionError(ex);}
    }
}
