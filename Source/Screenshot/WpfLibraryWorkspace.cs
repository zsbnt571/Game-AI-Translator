using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfLibraryWorkspace : WC.Grid, IDisposable
{
    private readonly MainForm main;
    private readonly WC.TextBlock heading=new(),count=new(),status=new(),navigationTitle=new(),placeholder=new(){IsHitTestVisible=false,VerticalAlignment=W.VerticalAlignment.Center,Margin=new W.Thickness(34,0,10,0)};
    private readonly WC.TextBox search=new(){Height=34,MaxWidth=430,HorizontalAlignment=W.HorizontalAlignment.Stretch};
    private readonly WC.ComboBox filter=new(){Width=125,Height=34};
    private readonly WC.Button import,importMenu,covers,list,upperTitle,lowerTitle,retention;
    private readonly WC.Grid body=new(),shelf=new();
    private readonly WC.ColumnDefinition sidebarColumn;
    private readonly WC.Border sidebar;
    private readonly WC.Grid right;
    private WpfGameSessionWorkspace? detail;
    private bool refreshing;
    internal WpfLibraryWorkspace(MainForm main)
    {
        this.main=main;ClipToBounds=true;
        sidebarColumn=new(){Width=new W.GridLength(main.WpfSidebarWidth),MinWidth=190,MaxWidth=460};
        ColumnDefinitions.Add(sidebarColumn);ColumnDefinitions.Add(new(){Width=new W.GridLength(4)});ColumnDefinitions.Add(new(){Width=new W.GridLength(1,W.GridUnitType.Star)});
        var side=new WC.Grid{Margin=new W.Thickness(0,12,0,0)};
        side.RowDefinitions.Add(new(){Height=new W.GridLength(30)});side.RowDefinitions.Add(new(){Height=new W.GridLength(1,W.GridUnitType.Star)});
        navigationTitle.Margin=new W.Thickness(14,4,0,4);side.Children.Add(navigationTitle);
        var tree=main.WpfLibraryTree;WC.Grid.SetRow(tree,1);side.Children.Add(tree);
        sidebar=new WC.Border{Child=side,BorderThickness=new W.Thickness(0,0,1,0)};Children.Add(sidebar);
        var split=new WC.GridSplitter{Width=4,Background=WM.Brushes.Transparent,HorizontalAlignment=W.HorizontalAlignment.Stretch,VerticalAlignment=W.VerticalAlignment.Stretch,ResizeDirection=WC.GridResizeDirection.Columns,ResizeBehavior=WC.GridResizeBehavior.PreviousAndNext};
        split.DragCompleted+=(_,_)=>main.WpfSaveSidebar((int)Math.Round(sidebarColumn.ActualWidth));WC.Grid.SetColumn(split,1);Children.Add(split);
        right=new WC.Grid();right.RowDefinitions.Add(new(){Height=new W.GridLength(1,W.GridUnitType.Star)});right.RowDefinitions.Add(new(){Height=new W.GridLength(22)});WC.Grid.SetColumn(right,2);Children.Add(right);
        body.Margin=new W.Thickness(24,18,24,0);right.Children.Add(body);
        foreach(var height in new[]{60d,56d,-1d,42d,-2d,-3d})body.RowDefinitions.Add(new(){Height=height>=0?new W.GridLength(height):height==-2?new W.GridLength(1,W.GridUnitType.Star):W.GridLength.Auto});
        var top=new WC.Grid{Margin=new W.Thickness(0,0,0,12)};top.ColumnDefinitions.Add(new(){Width=new W.GridLength(1,W.GridUnitType.Star)});top.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});
        var title=new WC.StackPanel{Orientation=WC.Orientation.Horizontal,VerticalAlignment=W.VerticalAlignment.Center};heading.FontSize=26.667;heading.FontWeight=W.FontWeights.Bold;count.Margin=new W.Thickness(20,0,0,0);count.VerticalAlignment=W.VerticalAlignment.Center;title.Children.Add(heading);title.Children.Add(count);top.Children.Add(title);
        var actions=new WC.StackPanel{Orientation=WC.Orientation.Horizontal,VerticalAlignment=W.VerticalAlignment.Center};
        import=WpfThemeResources.Button("导入游戏","plus",main.WpfImport,true);importMenu=WpfThemeResources.Button("","down",main.WpfImportMenu,true);importMenu.Margin=new W.Thickness(8,0,0,0);importMenu.Padding=new W.Thickness(7);
        actions.Children.Add(import);actions.Children.Add(importMenu);WC.Grid.SetColumn(actions,1);top.Children.Add(actions);body.Children.Add(top);
        var filters=new WC.Grid{Margin=new W.Thickness(0,0,0,12)};filters.ColumnDefinitions.Add(new(){Width=new W.GridLength(1,W.GridUnitType.Star)});filters.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});
        var searchFrame=new WC.Grid{MaxWidth=430,HorizontalAlignment=W.HorizontalAlignment.Left,MinWidth=180};
        searchFrame.SetBinding(WidthProperty,new System.Windows.Data.Binding("ActualWidth"){Source=filters,Converter=new SearchWidthConverter()});
        searchFrame.Children.Add(search);search.Padding=new W.Thickness(34,6,10,6);
        searchFrame.Children.Add(placeholder);
        var searchIcon=new WC.Image{Source=WpfDrawing.Icon("search",UiTheme.Current.SecondaryText),Width=18,Height=18,HorizontalAlignment=W.HorizontalAlignment.Left,Margin=new W.Thickness(10,0,0,0),IsHitTestVisible=false};searchFrame.Children.Add(searchIcon);filters.Children.Add(searchFrame);
        W.Automation.AutomationProperties.SetName(search,UiLanguage.Text("搜索名称、标签或便签"));search.ToolTip=UiLanguage.Text("搜索名称、标签或便签");
        var view=new WC.StackPanel{Orientation=WC.Orientation.Horizontal,VerticalAlignment=W.VerticalAlignment.Center};
        view.Children.Add(filter);covers=WpfThemeResources.Button("","grid",()=>{main.WpfSetLibraryList(false);Refresh();},true);list=WpfThemeResources.Button("","list",()=>{main.WpfSetLibraryList(true);Refresh();});
        covers.Margin=list.Margin=new W.Thickness(10,0,0,0);covers.Padding=list.Padding=new W.Thickness(7);view.Children.Add(covers);view.Children.Add(list);WC.Grid.SetColumn(view,1);filters.Children.Add(view);WC.Grid.SetRow(filters,1);body.Children.Add(filters);
        shelf.RowDefinitions.Add(new(){Height=new W.GridLength(40)});shelf.RowDefinitions.Add(new(){Height=W.GridLength.Auto});shelf.Margin=new W.Thickness(0,0,0,12);
        upperTitle=WpfThemeResources.Button("我的游戏","down",()=>main.WpfSectionMenu(true));upperTitle.HorizontalAlignment=W.HorizontalAlignment.Left;upperTitle.BorderThickness=new W.Thickness(0);upperTitle.Padding=new W.Thickness(0,4,8,4);shelf.Children.Add(upperTitle);
        var recent=main.WpfRecentGrid;WC.Grid.SetRow(recent,1);shelf.Children.Add(recent);WC.Grid.SetRow(shelf,2);body.Children.Add(shelf);
        lowerTitle=WpfThemeResources.Button("我的游戏","down",()=>main.WpfSectionMenu(false));lowerTitle.HorizontalAlignment=W.HorizontalAlignment.Left;lowerTitle.BorderThickness=new W.Thickness(0);lowerTitle.Padding=new W.Thickness(0,4,8,4);WC.Grid.SetRow(lowerTitle,3);body.Children.Add(lowerTitle);
        var grid=main.WpfMainGrid;WC.Grid.SetRow(grid,4);body.Children.Add(grid);
        retention=WpfThemeResources.Button("保留数量",null,main.WpfEditRetention);retention.HorizontalAlignment=W.HorizontalAlignment.Left;WC.Grid.SetRow(retention,5);body.Children.Add(retention);
        status.FontSize=10.667;status.VerticalAlignment=W.VerticalAlignment.Center;status.Margin=new W.Thickness(4,0,0,0);WC.Grid.SetRow(status,1);right.Children.Add(status);
        WpfControlCoordinates.Register(main.WpfUpperHeader,upperTitle);WpfControlCoordinates.Register(main.WpfLowerHeader,lowerTitle);
        search.TextChanged+=(_,_)=>{placeholder.Visibility=search.Text.Length==0?W.Visibility.Visible:W.Visibility.Collapsed;if(!refreshing)main.WpfSearchText=search.Text;};
        filter.SelectionChanged+=(_,_)=>{if(!refreshing)main.WpfFilterIndex=filter.SelectedIndex;};
        filter.PreviewMouseWheel+=(_,e)=>{if(!filter.IsDropDownOpen)e.Handled=true;};
        AllowDrop=true;DragOver+=(_,e)=>{e.Effects=e.Data.GetDataPresent(W.DataFormats.FileDrop)?W.DragDropEffects.Copy:W.DragDropEffects.None;e.Handled=true;};
        Drop+=async(_,e)=>{if(e.Data.GetData(W.DataFormats.FileDrop) is string[] files)await main.WpfImportPaths(files);};
        main.WpfStatusChanged+=RefreshStatus;
        Refresh();
    }
    private sealed class SearchWidthConverter:System.Windows.Data.IValueConverter
    {
        public object Convert(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>Math.Clamp((double)value-250,180,430);
        public object ConvertBack(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>W.DependencyProperty.UnsetValue;
    }
    private void RefreshStatus()=>status.Text=main.WpfStatusText;
    internal void Refresh()
    {
        refreshing=true;
        try
        {
            bool home=main.WpfLibraryHome;
            if(!home && detail is null) { detail=new(main); WC.Grid.SetColumn(detail,2);Children.Add(detail); }
            right.Visibility=home?W.Visibility.Visible:W.Visibility.Collapsed;
            if(detail is not null) { detail.Visibility=home?W.Visibility.Collapsed:W.Visibility.Visible; if(!home)detail.Refresh(); }
            var p=UiTheme.Current;Background=WpfDrawing.Brush(p.Main);sidebar.Background=WpfDrawing.Brush(UiTheme.Sidebar);sidebar.BorderBrush=WpfDrawing.Brush(p.Border);
            foreach(var text in new[]{heading,count,status,navigationTitle})text.Foreground=WpfDrawing.Brush(p.Text);
            navigationTitle.Text=UiLanguage.Text("游戏导航");heading.Text=main.WpfLibraryHeading;count.Text=main.WpfLibraryCount;RefreshStatus();
            if(search.Text!=main.WpfSearchText)search.Text=main.WpfSearchText;
            placeholder.Text=UiLanguage.Text("搜索名称、标签或便签");placeholder.Foreground=WpfDrawing.Brush(p.SecondaryText);placeholder.Visibility=search.Text.Length==0?W.Visibility.Visible:W.Visibility.Collapsed;
            var items=main.WpfFilterItems.Select(UiLanguage.Text).ToArray();if(!filter.Items.Cast<string>().SequenceEqual(items)){filter.Items.Clear();foreach(var item in items)filter.Items.Add(item);}filter.SelectedIndex=main.WpfFilterIndex;
            shelf.Visibility=main.WpfShowShelf?W.Visibility.Visible:W.Visibility.Collapsed;
            main.WpfRecentGrid.Height=main.WpfShelfHeight;
            WpfThemeResources.SetButton(import,"导入游戏","plus",true);WpfThemeResources.SetButton(importMenu,"","down",true);
            WpfThemeResources.SetButton(upperTitle,main.WpfUpperHeader.Text,"down",trailingIcon:true);WpfThemeResources.SetButton(lowerTitle,main.WpfLowerHeader.Text,"down",trailingIcon:true);
            WpfThemeResources.SetButton(covers,"","grid",!main.WpfListMode);WpfThemeResources.SetButton(list,"","list",main.WpfListMode);
            W.Automation.AutomationProperties.SetName(covers,UiLanguage.Text("封面视图"));W.Automation.AutomationProperties.SetName(list,UiLanguage.Text("列表视图"));W.Automation.AutomationProperties.SetName(importMenu,UiLanguage.Text("更多导入方式"));
            retention.Visibility=main.WpfFilterIndex==2?W.Visibility.Visible:W.Visibility.Collapsed;WpfThemeResources.SetButton(retention,main.WpfRetentionText,null);
            main.WpfMainGrid.InvalidateVisual();main.WpfRecentGrid.InvalidateVisual();main.WpfLibraryTree.InvalidateVisual();
        }
        finally{refreshing=false;}
    }
    public void Dispose() { main.WpfStatusChanged-=RefreshStatus; detail?.Dispose(); }
}

public sealed partial class MainForm
{
    private bool _wpfDetails;
    internal bool WpfLibraryHome=>_currentMainPage=="内嵌翻译"&&!_wpfDetails;
    internal W.FrameworkElement WpfMainGrid=>_libraryGrid.NativeView;
    internal W.FrameworkElement WpfRecentGrid=>_recentShelf.NativeView;
    internal W.FrameworkElement WpfLibraryTree=>_libraryNavigation.NativeView;
    internal int WpfSidebarWidth=>_libraryPreferences.SidebarWidth;
    internal string WpfLibraryHeading=>_libraryHeading?.Text??UiLanguage.Text("游戏库");
    internal string WpfLibraryCount=>_libraryCount.Text;
    internal string WpfStatusText=>_statusLabel.Text;
    internal string WpfRetentionText=>_libraryRetention.Text;
    internal event Action? WpfStatusChanged;
    internal string WpfSearchText{get=>_librarySearch.Text;set=>_librarySearch.Text=value;}
    internal int WpfFilterIndex{get=>_libraryFilter.SelectedIndex;set{if(value>=0)_libraryFilter.SelectedIndex=value;}}
    internal string[] WpfFilterItems=>_libraryFilter.Items.Cast<string>().ToArray();
    internal bool WpfShowShelf=>_showRecentShelf;
    internal bool WpfListMode=>_libraryGrid.ListMode;
    internal double WpfShelfHeight=>_recentShelf.ShelfContentHeight*96d/_recentShelf.DeviceDpi;
    internal Control WpfUpperHeader=>_recentSectionHeading;
    internal Control WpfLowerHeader=>_mainSectionHeading;
    internal void WpfImport()=>ChooseLibraryGame();
    internal async void WpfEditRetention()=>await EditLibraryRetentionAsync();
    internal Task WpfImportPaths(string[] files)=>ImportLibraryPathsAsync(files,true);
    internal void WpfSetLibraryList(bool list)=>_libraryGrid.SetListMode(list);
    internal void WpfSectionMenu(bool upper)=>ShowLibrarySectionMenu(upper);
    internal void WpfSaveSidebar(int width)
    {
        try{_libraryPreferences=LibraryPreferencesStore.Load() with{SidebarWidth=Math.Clamp(width,190,460)};LibraryPreferencesStore.Save(_libraryPreferences);}
        catch(Exception ex){ShowFusionError(ex);}
    }
    internal void WpfImportMenu()
    {
        var menu=new WorkspaceMenu{Font=Font};menu.AddAction("选择游戏程序（可多选）","game",(_,_)=>ChooseLibraryGame());menu.AddAction("扫描文件夹","folder",async(_,_)=>await ImportLibraryFolderAsync());
        // Keep the popup alive through its click callback; dispose on the next UI turn.
        menu.Closed+=(_,_)=>BeginInvoke((Action)(()=>menu.Dispose()));menu.Show(Cursor.Position);
    }
    internal void WpfLibraryAttached()
    {
        _libraryGrid.Dock=_recentShelf.Dock=_libraryNavigation.Dock=DockStyle.None;
        _=_libraryGrid.Handle;_=_recentShelf.Handle;_=_libraryNavigation.Handle;
        _statusLabel.TextChanged+=(_,_)=>WpfStatusChanged?.Invoke();
    }
}
