namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly GameLibraryGrid _libraryGrid=new(){Dock=DockStyle.Fill};
    private readonly TextBox _librarySearch=new(){PlaceholderText="搜索名称、标签或便签",Width=220};
    private readonly ComboBox _libraryFilter=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=125};
    private readonly ComboBox _libraryEngine=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=155};
    private readonly ComboBox _libraryFolder=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=155};
    private readonly Label _libraryCount=new(){AutoSize=true};
    private readonly LinkLabel _libraryRetention=new(){AutoSize=true,Text="保留数量",Visible=false};
    private readonly Label _libraryChineseName=new(){AutoSize=true};
    private readonly PictureBox _libraryCover=new WorkspaceCover {Dock=DockStyle.Fill,AccessibleName="所选游戏封面"};
    private readonly Label _libraryNote=new(){AutoSize=true};
    private readonly System.Windows.Forms.Timer _libraryRefreshTimer=new(){Interval=180};
    private readonly Dictionary<string,string> _libraryEngines=new(StringComparer.OrdinalIgnoreCase);
    private LibraryMetadata? _libraryMetadata;
    private Task<LibraryMetadata>? _libraryMetadataTask;
    private bool _libraryLoading,_libraryRefreshPending,_libraryFiltersLoading;
    private bool _libraryIdentifying;
    private int _libraryPage,_libraryDetailEpoch,_libraryViewRevision;
    private Button? _plainLaunchButton;
    private readonly Label _librarySelectionLabel=WorkspaceLabel("双击游戏进入详细主页");
    private RecentGameStore? _librarySubscribedStore;
    private const int LibraryPageSize=48;
    private FlowLayoutPanel? _libraryPager;
    private static GameActionButton IconButton(string text,string icon,EventHandler action,bool primary=false,int minWidth=84)
    {var button=new GameActionButton(text){IconKey=icon,Primary=primary,MinimumLogicalWidth=minWidth};if(text.Length==0)button.LogicalHeight=30;button.RefreshMetrics();button.Click+=action;return button;}
    private static Panel SearchField(TextBox field,string icon="search")
        =>new WorkspaceTextField(field,icon){Height=34,Margin=Padding.Empty};
    private static TableLayoutPanel EqualRow(params Control[] controls)
    {var row=new TableLayoutPanel{ColumnCount=controls.Length,RowCount=1,Dock=DockStyle.Top,AutoSize=true,Margin=Padding.Empty};row.RowStyles.Add(new(SizeType.AutoSize));foreach(var control in controls){row.ColumnStyles.Add(new(SizeType.Percent,100f/controls.Length));control.Dock=DockStyle.Fill;control.Margin=new(0,2,5,2);row.Controls.Add(control);}return row;}


    private static Label WorkspaceLabel(string text,bool title=false)=>new(){Text=text,AutoSize=true,Font=new Font("Microsoft YaHei UI",title?17:10,title?FontStyle.Bold:FontStyle.Regular),Margin=new(0,4,8,5)};
    private static FlowLayoutPanel WorkspaceRow(params Control[] controls)
    {
        var row=new WorkspaceControlRow{AutoSize=true,Dock=DockStyle.Top,WrapContents=true,Margin=new(0,0,0,8)};
        foreach(var control in controls){control.Margin=new(0,3,8,3);row.Controls.Add(control);}return row;
    }
    private static WorkspaceAlignedRow CompactRow(params Control[] controls)
    {
        return new WorkspaceAlignedRow(controls){Dock=DockStyle.None,Anchor=AnchorStyles.Right,Margin=Padding.Empty};
    }
    private static TableLayoutPanel WorkspaceColumn(){var table=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=0,Margin=Padding.Empty};table.ColumnStyles.Add(new(SizeType.Percent,100));return table;}
    private static void WorkspaceAdd(TableLayoutPanel table,Control control,SizeType size=SizeType.AutoSize,float height=0)
    {int row=table.RowCount++;table.RowStyles.Add(new(size,size==SizeType.Absolute?height+control.Margin.Vertical:height));control.Dock=DockStyle.Fill;table.Controls.Add(control,0,row);}
    private static Button WorkspaceButton(string text,EventHandler action){var b=new GameActionButton(text);b.Click+=action;return b;}

    private void BuildGameLibraryWorkspace(Panel page)
    {
        page.AutoScroll=false;
        var body=WorkspaceColumn();body.Padding=new(24,18,24,0);
        var top=new TableLayoutPanel{ColumnCount=2,RowCount=1,AutoSize=false,Height=48,Margin=new(0,0,0,12)};top.ColumnStyles.Add(new(SizeType.Percent,100));top.ColumnStyles.Add(new(SizeType.AutoSize));top.RowStyles.Add(new(SizeType.Percent,100));
        _libraryHeading=WorkspaceLabel("游戏库",true);_libraryHeading.Font=new Font("Microsoft YaHei UI",20,FontStyle.Bold);FontManager.MarkPreviewTextRoot(_libraryHeading);
        top.Controls.Add(new WorkspaceAlignedRow(_libraryHeading,_libraryCount){Name="LibraryHeadingRow",Dock=DockStyle.Fill},0,0);
        var add=IconButton("导入游戏","plus",(_,_)=>ChooseLibraryGame(),true,108);
        var arrow=IconButton("","down",(_,_)=>{},true,32);arrow.LogicalHeight=36;arrow.RefreshMetrics();
        var menu=new WorkspaceMenu();menu.AddAction("选择游戏程序（可多选）","game",(_,_)=>ChooseLibraryGame());menu.AddAction("扫描文件夹","folder",async(_,_)=>await ImportLibraryFolderAsync());
        arrow.Click+=(_,_)=>{menu.Font=Font;menu.Show(arrow,new Point(0,arrow.Height));};FormClosed+=(_,_)=>menu.Dispose();top.Controls.Add(CompactRow(add,arrow),1,0);WorkspaceAdd(body,top,SizeType.Absolute,48);
        _libraryFilter.Items.AddRange(["全部游戏","已保存","最近玩过","有书签"]);_libraryFilter.SelectedIndex=0;
        _libraryEngine.Items.Add("全部引擎");_libraryEngine.SelectedIndex=0;_libraryFolder.Items.Add("全部文件夹");_libraryFolder.SelectedIndex=0;
        var search=SearchField(_librarySearch);search.MinimumSize=new(180,34);
        var gridView=IconButton("","grid",(_,_)=>{},true,31);var listView=IconButton("","list",(_,_)=>{},false,31);gridView.AccessibleName="封面视图";listView.AccessibleName="列表视图";
        gridView.Click+=(_,_)=>{_libraryGrid.SetListMode(false);gridView.Primary=true;listView.Primary=false;gridView.Invalidate();listView.Invalidate();};
        listView.Click+=(_,_)=>{_libraryGrid.SetListMode(true);gridView.Primary=false;listView.Primary=true;gridView.Invalidate();listView.Invalidate();};
        var view=CompactRow(gridView,listView);view.Name="LibraryViewButtons";
        var filters=new WorkspaceAlignedRow(search,_libraryFilter,view){Name="LibraryFilterRow",Height=44,Margin=new(0,0,0,12)};filters.FlexibleColumn(0,430);
        WorkspaceAdd(body,filters,SizeType.Absolute,44);
        var shelf=WorkspaceColumn();shelf.AutoSize=false;shelf.Height=202;shelf.Margin=new(0,0,0,12);_recentShelfHost=shelf;
        WorkspaceAdd(shelf,_recentSectionHeading);_recentSectionHeading.Dock=DockStyle.None;_recentSectionHeading.Anchor=AnchorStyles.Left;
        _recentSectionHeading.Click+=(_,_)=>ShowLibrarySectionMenu(true);_mainSectionHeading.Click+=(_,_)=>ShowLibrarySectionMenu(false);
        FormClosed+=(_,_)=>_sectionMenu.Dispose();
        WorkspaceAdd(shelf,_recentShelf,SizeType.Percent,100);WorkspaceAdd(body,shelf,SizeType.Absolute,202);
        _recentShelf.SizeChanged+=(_,_)=>SizeRecentShelf();_recentShelf.FontChanged+=(_,_)=>SizeRecentShelf();
        WorkspaceAdd(body,_mainSectionHeading);_mainSectionHeading.Dock=DockStyle.None;_mainSectionHeading.Anchor=AnchorStyles.Left;
        _libraryGrid.Portrait=_libraryPreferences.Portrait;_libraryGrid.CoverWidth=_libraryPreferences.CoverWidth;WorkspaceAdd(body,_libraryGrid,SizeType.Percent,100);
        var previous=WorkspaceButton("上一页",(_,_)=>{if(_libraryPage>0){_libraryPage--;ScheduleLibraryRefresh();}});var next=WorkspaceButton("下一页",(_,_)=>{_libraryPage++;ScheduleLibraryRefresh();});
        _libraryPager=WorkspaceRow(previous,next,_libraryRetention);_libraryPager.Visible=false;WorkspaceAdd(body,_libraryPager);_libraryRetention.LinkClicked+=async(_,_)=>await EditLibraryRetentionAsync();
        page.Controls.Add(body);_libraryBody=body;BuildGameSessionWorkspace(page);
        foreach(var grid in new[]{_libraryGrid,_recentShelf})
        {
            grid.GameSelected+=game=>SelectFusionGame(game.ExePath,recordSelection:false);grid.GameActivated+=async game=>await OpenLibraryGameAsync(game.ExePath);
            grid.ContextRequested+=(game,point)=>{_libraryContextSource=grid;ShowLibraryContextMenu(game,_libraryGrid.PointToClient(grid.PointToScreen(point)));};
        }
        _librarySearch.TextChanged+=(_,_)=>{_libraryPage=0;ScheduleLibraryRefresh();};
        foreach(var box in new[]{_libraryFilter,_libraryEngine,_libraryFolder})box.SelectedIndexChanged+=(_,_)=>{if(!_libraryFiltersLoading){_libraryPage=0;ScheduleLibraryRefresh();}};
        _libraryRefreshTimer.Tick+=async(_,_)=>{_libraryRefreshTimer.Stop();await RefreshLibraryAsync();};
        void DropTarget(Control control)
        {
            control.AllowDrop=true;control.DragEnter+=(_,e)=>{if(e.Data?.GetDataPresent(DataFormats.FileDrop)==true){e.Effect=DragDropEffects.Copy;_statusLabel.Text="松开以导入游戏或扫描文件夹";}};
            control.DragLeave+=(_,_)=>_statusLabel.Text="就绪";
            control.DragDrop+=async(_,e)=>{if(e.Data?.GetData(DataFormats.FileDrop)is string[] paths)await ImportLibraryPathsAsync(paths,true);};
            foreach(Control child in control.Controls)DropTarget(child);
        }
        DropTarget(body);InitializeRecentUi();RenderGameState();Shown+=(_,_)=>ScheduleLibraryRefresh();
        FormClosed+=(_,_)=>{_libraryContextMenu.Dispose();_gameLaunchMenu.Dispose();_libraryRefreshTimer.Stop();_libraryRefreshTimer.Dispose();if(_librarySubscribedStore is not null)_librarySubscribedStore.Changed-=LibraryStoreChanged;_libraryCover.Image?.Dispose();_libraryCover.Image=null;};
    }
    private async void ChooseLibraryGame()
    {
        using var dialog=new OpenFileDialog{Filter="游戏程序|*.exe",Title="导入游戏 · 支持多选",Multiselect=true};
        if(dialog.ShowDialog(this)==DialogResult.OK)await ImportLibraryPathsAsync(dialog.FileNames,false);
    }
    private void LibraryStoreChanged()
    {
        if(IsDisposed||Disposing||!IsHandleCreated)return;
        try{BeginInvoke((Action)ScheduleLibraryRefresh);}catch(InvalidOperationException){}
    }
    private void ScheduleLibraryRefresh(){if(IsDisposed||Disposing||!IsHandleCreated)return;_libraryViewRevision++;_libraryRefreshTimer.Stop();_libraryRefreshTimer.Start();}
    private async Task RefreshLibraryAsync()
    {
        if(IsDisposed||Disposing)return;if(_libraryLoading){_libraryRefreshPending=true;return;}
        _libraryLoading=true;int revision=_libraryViewRevision;
        LibraryTile[]? tiles=null,recentTiles=null;Dictionary<string,Bitmap>? navIcons=null;
        bool Stale()=>IsDisposed||Disposing||revision!=_libraryViewRevision;
        try
        {
            var store=await RecentStore();_libraryMetadata=await (_libraryMetadataTask??=Task.Run(()=>new LibraryMetadata()));if(IsDisposed)return;
            _libraryMetadata.Reload();if(Stale())return;
            if(_librarySubscribedStore is null){_librarySubscribedStore=store;store.Changed+=LibraryStoreChanged;}
            _libraryRetention.Text=$"保留 {store.RecentLimit} 项";_libraryRetention.Visible=_libraryFilter.SelectedIndex==2;
            var all=await Task.Run(store.SnapshotAll);if(Stale())return;
            foreach(var entry in all){var engine=_libraryMetadata.Get(entry.Id).Engine;_libraryEngines.TryAdd(entry.ExePath,string.IsNullOrWhiteSpace(engine)?"待识别":engine);}
            if(_selectedGame is { } selected)_libraryEngines[selected.ExePath]=selected.Engine;
            _libraryFiltersLoading=true;
            try
            {
                void Refill(ComboBox box,IEnumerable<string> entries,string first){var chosen=box.Text;var items=new[]{first}.Concat(entries.Distinct().OrderBy(x=>x)).ToArray();if(box.Items.Cast<string>().SequenceEqual(items))return;box.Items.Clear();box.Items.AddRange(items);box.SelectedItem=items.Contains(chosen)?chosen:first;}
                Refill(_libraryEngine,_libraryEngines.Values,"全部引擎");Refill(_libraryFolder,LibraryFolders.All(_libraryPreferences).Where(f=>f.Root is not null).Select(f=>f.Root!),"全部文件夹");
            }
            finally{_libraryFiltersLoading=false;}
            var query=_librarySearch.Text.Trim();IEnumerable<RecentGame> filtered=all;
            bool homeSections=_libraryCollection.Length==0&&_libraryScope=="home"&&_libraryTag.Length==0&&query.Length==0&&_libraryFilter.SelectedIndex==0&&_libraryEngine.SelectedIndex<=0&&_libraryFolder.SelectedIndex<=0;
            if(homeSections)filtered=LibrarySections.Select(filtered,_libraryPreferences.MainSection,_libraryMetadata,_libraryEngines,store.RecentLimit,_libraryPreferences);
            var sectionTitle=homeSections?LibrarySections.Title(_libraryPreferences.MainSection,_libraryPreferences):query.Length>0?"搜索结果":"筛选结果";
            var running=_gameSessions.Snapshot().Where(x=>x.Process==FusionGameProcessState.Running).Select(x=>x.Game.ExePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(_libraryScope=="recent")filtered=filtered.Where(x=>x.RecentAt.HasValue);
            if(_libraryScope=="running")filtered=filtered.Where(x=>running.Contains(x.ExePath));
            if(_libraryScope=="favorite")filtered=filtered.Where(x=>_libraryMetadata.Get(x.Id).Bookmark);
            if(_libraryCollection.Length>0)filtered=LibrarySections.Select(filtered,"collection:"+_libraryCollection,_libraryMetadata,_libraryEngines,store.RecentLimit,_libraryPreferences);
            if(_libraryTag.Length>0)filtered=filtered.Where(x=>_libraryMetadata.Get(x.Id).Tags.Split([',','，',';','；'],StringSplitOptions.TrimEntries).Contains(_libraryTag));
            if(_libraryFilter.SelectedIndex==1)filtered=filtered.Where(x=>x.Saved);
            if(_libraryFilter.SelectedIndex==2){var recent=store.Snapshot().Select(x=>x.Id).ToHashSet();filtered=filtered.Where(x=>recent.Contains(x.Id));}
            if(_libraryFilter.SelectedIndex==3)filtered=filtered.Where(x=>_libraryMetadata.Get(x.Id).Bookmark);
            if(_libraryEngine.SelectedIndex>0)filtered=filtered.Where(x=>_libraryEngines.GetValueOrDefault(x.ExePath)==_libraryEngine.Text);
            if(_libraryFolder.SelectedIndex>0)filtered=filtered.Where(x=>LibraryFolders.Contains(_libraryFolder.Text,x.ExePath));
            if(query.Length>0)filtered=filtered.Where(x=>(x.Name+" "+_libraryMetadata.Get(x.Id).ChineseName+" "+_libraryMetadata.Get(x.Id).Note+" "+_libraryMetadata.Get(x.Id).Tags).Contains(query,StringComparison.OrdinalIgnoreCase));
            var found=filtered.ToArray();int pages=Math.Max(1,(found.Length+LibraryPageSize-1)/LibraryPageSize);_libraryPage=Math.Clamp(_libraryPage,0,pages-1);
            var batch=found.Skip(_libraryPage*LibraryPageSize).Take(LibraryPageSize).Select(x=>(Game:x,Note:_libraryMetadata.Get(x.Id),Engine:_libraryEngines.GetValueOrDefault(x.ExePath)??"待识别")).ToArray();
            tiles=await Task.Run(()=>batch.Select(x=>new LibraryTile(x.Game,x.Note,x.Engine,store.ReadImage(x.Game,true),store.ReadImage(x.Game,false))).ToArray());
            if(Stale())return;
            bool showShelf=all.Length>0&&homeSections;
            var sectionRecords=LibrarySections.Select(all,_libraryPreferences.RecentSection,_libraryMetadata,_libraryEngines,store.RecentLimit,_libraryPreferences).ToArray();
            var recentRecords=sectionRecords.Take(24).Select(x=>(Game:x,Note:_libraryMetadata.Get(x.Id),Engine:_libraryEngines.GetValueOrDefault(x.ExePath)??"")).ToArray();
            recentTiles=await Task.Run(()=>recentRecords.Select(x=>new LibraryTile(x.Game,x.Note,x.Engine,store.ReadImage(x.Game,true),store.ReadImage(x.Game,false))).ToArray());
            if(Stale())return;
            navIcons=await Task.Run(()=>all.Take(200).Select(x=>(x.ExePath,Image:store.ReadImage(x,false))).Where(x=>x.Image is not null).ToDictionary(x=>x.ExePath,x=>x.Image!,StringComparer.OrdinalIgnoreCase));
            if(Stale())return;
            string navigationKey=_libraryCollection.Length>0?"collection:"+_libraryCollection:_libraryTag.Length>0?"tag:"+_libraryTag:_libraryEngine.SelectedIndex>0?"engine:"+_libraryEngine.Text:_libraryFolder.SelectedIndex>0?"folder:"+_libraryFolder.Text:_libraryScope;
            // Commit a complete view without yielding between its heading, cards and navigation.
            string? selectedPath=_gameSessionPage?.Visible==true?_selectedGame?.ExePath:null;
            using(new UiLayoutBatch(_libraryBody!))
            {
                _mainSectionHeading.Text=sectionTitle;
                _libraryGrid.EmptyText=all.Length>0?"这个范围内暂无游戏\n可切换栏目、筛选或搜索":"把游戏拖到这里\n或点击右上角“导入游戏”";
                _libraryGrid.SetTiles(tiles,_selectedGame?.ExePath);tiles=null;_libraryGrid.UpdateRunning(running);
                _showRecentShelf=showShelf;if(_recentShelfHost is not null)_recentShelfHost.Visible=showShelf;
                _recentSectionTotal=sectionRecords.Length;_recentSectionHeading.Text=LibrarySections.Title(_libraryPreferences.RecentSection,_libraryPreferences)+(sectionRecords.Length>24?$"  24 / {sectionRecords.Length}":"");
                _recentShelf.EmptyText="这个范围内暂无游戏，可点击标题切换";
                _recentShelf.SetTiles(recentTiles,_selectedGame?.ExePath);recentTiles=null;_recentShelf.UpdateRunning(running);SizeRecentShelf();
                _libraryNavigation.UpdateEntries(all,_libraryMetadata,_libraryEngines,running,selectedPath,navigationKey,_libraryPreferences,navIcons);navIcons=null;
                if(_libraryHeading is not null)_libraryHeading.Text=_libraryCollection.Length>0?LibrarySections.Title("collection:"+_libraryCollection,_libraryPreferences):_libraryTag.Length>0?_libraryTag:_libraryEngine.SelectedIndex>0?_libraryEngine.Text:_libraryFolder.SelectedIndex>0?Path.GetFileName(_libraryFolder.Text):_libraryScope switch{"recent"=>"最近使用","running"=>"正在运行","favorite"=>"收藏",_=>"游戏库"};
                _libraryCount.Text=$"{found.Length} 款游戏";if(_libraryPager is not null)_libraryPager.Visible=pages>1||_libraryFilter.SelectedIndex==2;
            }
            if(_libraryMetadata.Notice.Length>0)_statusLabel.Text=_libraryMetadata.Notice;
            if(_selectedGame is { } game&&store.Find(game.ExePath) is { } record)await RefreshLibraryDetailAsync(store,record);
            if(!IsDisposed){_=IdentifyLibraryEnginesAsync(all);_=FillLibraryChineseNamesAsync(all);}
        }
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
        finally{if(tiles is not null)foreach(var tile in tiles)tile.Dispose();if(recentTiles is not null)foreach(var tile in recentTiles)tile.Dispose();if(navIcons is not null)foreach(var image in navIcons.Values)image.Dispose();_libraryLoading=false;if(_libraryRefreshPending&&!IsDisposed){_libraryRefreshPending=false;ScheduleLibraryRefresh();}}
    }
    private async Task RefreshLibraryDetailAsync(RecentGameStore store,RecentGame record)
    {
        int epoch=++_libraryDetailEpoch;
        var cover=await Task.Run(()=>store.ReadImage(record,true));
        if(IsDisposed||epoch!=_libraryDetailEpoch||!StringComparer.OrdinalIgnoreCase.Equals(record.ExePath,_selectedGame?.ExePath)){cover?.Dispose();return;}
        var note=_libraryMetadata?.Get(record.Id)??new();
        if(_libraryMetadata is not null&&_libraryMetadata.Notice.Length==0&&_selectedGame is { } active&&note.Engine!=active.Engine){_libraryMetadata.Save(record.Id,note with{Engine=active.Engine});note=_libraryMetadata.Get(record.Id);}
        _libraryChineseName.Text=note.ChineseName;_libraryChineseName.Visible=note.ChineseName.Length>0;_libraryNote.Text=note.Note;_libraryNote.Visible=note.Note.Length>0;
        var old=_libraryCover.Image;_libraryCover.Image=cover;old?.Dispose();_libraryCover.BackColor=UiTheme.Current.Main;RenderGameSessionState();
    }
    private async Task EditLibraryNoteAsync()
    {
        if(_selectedGame is not { } game)return;await _rememberSelectedTask;var store=await RecentStore();if(IsDisposed)return;
        var record=store.Find(game.ExePath);if(record is null)return;
        _libraryMetadata=await (_libraryMetadataTask??=Task.Run(()=>new LibraryMetadata()));if(IsDisposed)return;
        var note=_libraryMetadata.Get(record.Id);
        using var dialog=new Form{AutoScaleMode=AutoScaleMode.Dpi,AutoScaleDimensions=new(96,96),AutoScroll=true,Text="名称、标签与便签",StartPosition=FormStartPosition.CenterParent,ClientSize=new(560,460),MinimumSize=new(560,500),Font=Font,MinimizeBox=false,MaximizeBox=false};
        using var cancelTranslation=CancellationTokenSource.CreateLinkedTokenSource(_desktopLifetime.Token);dialog.FormClosed+=(_,_)=>cancelTranslation.Cancel();
        var table=WorkspaceColumn();table.Padding=new(18);table.Dock=DockStyle.Top;table.AutoSize=true;
        var originalName=new TextBox{Text=record.Name,MaxLength=160};
        var name=new TextBox{Text=note.ChineseName,PlaceholderText="添加中文显示名，保留原名",MaxLength=120};
        var text=new TextBox{Text=note.Note,PlaceholderText="写一条贴在游戏上的备注",MaxLength=120};
        var tags=new TextBox{Text=note.Tags,PlaceholderText="标签，例如：RPG, 像素, 待研究",MaxLength=160};
        var bookmark=new CheckBox{Text="在卡片上放置书签",Checked=note.Bookmark,AutoSize=true};
        var color=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};color.Items.AddRange(["浅黄","淡紫","薄荷"]);color.SelectedIndex=note.Color=="lavender"?1:note.Color=="mint"?2:0;
        var generate=WorkspaceButton("生成中文名",(_,_)=>{});var feedback=WorkspaceLabel("");
        WorkspaceAdd(table,WorkspaceLabel("游戏名称"));WorkspaceAdd(table,originalName);WorkspaceAdd(table,WorkspaceLabel("中文名称"));WorkspaceAdd(table,name);WorkspaceAdd(table,WorkspaceRow(generate));WorkspaceAdd(table,feedback);WorkspaceAdd(table,text);WorkspaceAdd(table,tags);WorkspaceAdd(table,WorkspaceRow(bookmark,color));
        var save=WorkspaceButton("保存",(_,_)=>dialog.DialogResult=DialogResult.OK);var cancel=WorkspaceButton("取消",(_,_)=>dialog.DialogResult=DialogResult.Cancel);WorkspaceAdd(table,WorkspaceRow(save,cancel));
        generate.Click+=async(_,_)=>{
            generate.Enabled=save.Enabled=false;name.ReadOnly=true;feedback.Text="正在用当前游戏翻译方案生成…";
            try
            {
                var settings=LibraryNameTranslation.Settings(_fusion!.GameSettings(UiSettings,game.ExePath));
                var translated=await _translationService.TranslatePlainAsync(originalName.Text.Trim(),settings,cancelTranslation.Token);
                if(dialog.IsDisposed||!dialog.Visible||cancelTranslation.IsCancellationRequested)return;
                var value=LibraryNameTranslation.Validate(translated.Text);
                name.Text=value;feedback.Text="机器译名，可修改后保存。";
            }
            catch(OperationCanceledException){}
            catch(Exception ex){if(!dialog.IsDisposed&&dialog.Visible)feedback.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
            finally{if(!dialog.IsDisposed){generate.Enabled=save.Enabled=true;name.ReadOnly=false;}}
        };
        dialog.AcceptButton=save;dialog.CancelButton=cancel;dialog.Controls.Add(table);UiTheme.Apply(dialog,_appliedDesktopSettings);
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try{if(originalName.Text.Trim()!=record.Name){store.SetDisplayName(record.Id,originalName.Text,true);_selectedGame=game with{Name=originalName.Text.Trim()};_gameTitle.Text=originalName.Text.Trim();}var value=_libraryMetadata.Get(record.Id) with{ChineseName=name.Text.Trim(),Note=text.Text.Trim(),Color=color.SelectedIndex==1?"lavender":color.SelectedIndex==2?"mint":"yellow",Bookmark=bookmark.Checked,Tags=tags.Text.Trim()};_libraryMetadata.Save(record.Id,value);ScheduleLibraryRefresh();}
        catch(Exception ex){ShowFusionError(ex);}
    }
    private async Task ImportLibraryFolderAsync(string? selectedFolder=null)
    {
        using var folder=new FolderBrowserDialog{Description="选择游戏库文件夹，自动识别并导入游戏主程序"};
        if(selectedFolder is null){if(folder.ShowDialog(this)!=DialogResult.OK)return;selectedFolder=folder.SelectedPath;}
        await ImportLibraryPathsAsync([selectedFolder],true);
    }
    private async Task ImportLibraryPathsAsync(string[] paths,bool review)
    {
        try
        {
            var scan=await Task.Run(()=>LibraryImport.Scan(paths));if(IsDisposed)return;
            var chosen=scan.Games;
            if(chosen.Length==0){_statusLabel.Text="未找到游戏主程序"+(scan.Skipped>0?$" · 已过滤 {scan.Skipped} 个辅助或重复程序":"")+"。";return;}
            var store=await RecentStore();int added=0,duplicates=0;var errors=new List<string>();
            foreach(var path in chosen)
            {
                if(IsDisposed||_desktopLifetime.IsCancellationRequested)return;
                if(store.Find(path) is not null){duplicates++;continue;}
                try{var game=await Task.Run(()=>GameDetector.Detect(path));await Task.Run(()=>{var record=store.Select(game.Name,game.ExePath,usage:false);store.SetSaved(record.Id,true);using var icon=store.IconFor(record,_desktopLifetime.Token);});_libraryEngines[game.ExePath]=game.Engine;added++;}
                catch(Exception ex){errors.Add(Path.GetFileName(path)+"："+SafeDiagnosticOutput.ExceptionSummary(ex));}
            }
            if(chosen.Length>0)RegisterLibraryRoots(paths.Where(Directory.Exists));
            if(!IsDisposed){_statusLabel.Text=$"已导入 {added} 个游戏"+(scan.Skipped>0?$" · 过滤 {scan.Skipped} 个辅助或重复入口":"")+(duplicates>0?$" · 跳过 {duplicates} 个已有记录":"")+(errors.Count>0?$" · {errors.Count} 项失败：{errors[0]}":"")+(scan.Limited?" · 已达到本次扫描上限，其余游戏可分批导入":"");ScheduleLibraryRefresh();_=FillDefaultArtworkAsync(chosen);}
        }
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
    }
    private async Task IdentifyLibraryEnginesAsync(RecentGame[] records)
    {
        if(_libraryIdentifying||_libraryMetadata is null||_libraryMetadata.Notice.Length>0)return;
        _libraryIdentifying=true;bool changed=false;
        try
        {
            foreach(var record in records)
            {
                if(IsDisposed||_desktopLifetime.IsCancellationRequested)return;
                if(!EngineStructureDetection.NeedsRefresh(_libraryMetadata.Get(record.Id).Engine)&&!(ToolStructureDetection.IsKnownHelper(record.ExePath)&&_libraryMetadata.Get(record.Id).Engine!="辅助工具"))continue;
                string engine;
                try{engine=await Task.Run(()=>GameDetector.Detect(record.ExePath).Engine,_desktopLifetime.Token);}
                catch(OperationCanceledException){return;}
                catch{engine="待重新定位";}
                if(IsDisposed||_desktopLifetime.IsCancellationRequested)return;
                if(_libraryEngines.GetValueOrDefault(record.ExePath)!=engine){_libraryEngines[record.ExePath]=engine;changed=true;}
                // Re-read after awaiting detection: names, notes and categories may
                // have changed meanwhile. An unchanged unknown result must not start
                // another save/refresh cycle.
                var latest=_libraryMetadata.Get(record.Id);
                if(latest.Engine!=engine){_libraryMetadata.Save(record.Id,latest with{Engine=engine});changed=true;}
            }
        }
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
        finally{_libraryIdentifying=false;if(changed&&!IsDisposed)ScheduleLibraryRefresh();}
    }
    private async Task EditLibraryRetentionAsync()
    {
        try
        {
            var store=await RecentStore();if(IsDisposed)return;
            using var dialog=new Form{AutoScaleMode=AutoScaleMode.Dpi,AutoScaleDimensions=new(96,96),AutoScroll=true,Text="最近玩过",StartPosition=FormStartPosition.CenterParent,ClientSize=new(410,180),MinimumSize=new(410,210),Font=Font,MinimizeBox=false,MaximizeBox=false};
            var table=WorkspaceColumn();table.Padding=new(16);table.Dock=DockStyle.Top;table.AutoSize=true;
            WorkspaceAdd(table,WorkspaceLabel("保留最近多少项？"));
            var number=new NumericUpDown{Minimum=1,Maximum=1000,Value=store.RecentLimit,Width=120};WorkspaceAdd(table,WorkspaceRow(number));
            WorkspaceAdd(table,WorkspaceLabel("只调整最近列表，游戏库中的记录仍保留。"));
            var save=WorkspaceButton("保存",(_,_)=>dialog.DialogResult=DialogResult.OK);var cancel=WorkspaceButton("取消",(_,_)=>dialog.DialogResult=DialogResult.Cancel);WorkspaceAdd(table,WorkspaceRow(save,cancel));
            dialog.Controls.Add(table);dialog.AcceptButton=save;dialog.CancelButton=cancel;UiTheme.Apply(dialog,_appliedDesktopSettings);
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            int limit=(int)number.Value;await Task.Run(()=>store.SetRecentLimit(limit));
        }
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
    }
}
