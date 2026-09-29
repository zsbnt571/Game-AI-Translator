using System.Drawing.Imaging;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly GalleryCanvas _galleryCanvas=new(){Dock=DockStyle.Fill};
    private readonly TextBox _gallerySearch=new(){PlaceholderText="搜索原文或译文",Width=190};
    private readonly ComboBox _galleryDate=new GamePlanBox(){Width=95};
    private readonly Label _galleryCaption=new(){AutoSize=true};
    private readonly TextBox _galleryText=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.None,BorderStyle=BorderStyle.None,Dock=DockStyle.Fill};
    private readonly TextBox _gallerySourceText=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.None,BorderStyle=BorderStyle.None,Dock=DockStyle.Fill};
    private readonly Label _galleryTitle=new(){AutoSize=true,Font=new Font("Microsoft YaHei UI",12,FontStyle.Bold)};
    private readonly Label _galleryRecordCount=new(){AutoSize=true};
    private FlowLayoutPanel? _galleryPager;
    private readonly List<Button> _galleryViews=[];
    private readonly List<Button> _galleryActions=[];
    private SessionHistoryItem? _gallerySelected;
    private int _galleryEpoch,_galleryPage;
    private bool _galleryLoading,_galleryRefreshPending;
    private IReadOnlyList<SessionHistoryItem>? _gallerySnapshot;
    private int _galleryHistoryVersion,_galleryLoadedVersion=-1;
    private readonly System.Windows.Forms.Timer _gallerySearchTimer=new(){Interval=200};

    private void BuildScreenshotWorkspace(Panel page)
    {
        page.AutoScroll=false;var root=WorkspaceColumn();root.Padding=new(0,15,6,0);
        var capture=IconButton("开始截图","camera",(_,_)=>BeginCapture(),true,112);
        var import=IconButton("导入图片","image",(_,_)=>{
            using var dialog=new OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp",Title="打开需要翻译的图片"};if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            try{using var image=new Bitmap(dialog.FileName);CreateAndShowPreview(new CaptureResult(new Bitmap(image),PreviewMode.OcrAndTranslate));}catch(Exception ex){ShowFusionError(ex);}});
        _screenshotCaptureButton=capture;_screenshotImportButton=import;capture.Name="ScreenshotCapture";import.Name="ScreenshotImport";
        capture.Enabled=import.Enabled=false;
        var top=new TableLayoutPanel{ColumnCount=2,AutoSize=true,Padding=new(20,0,6,0),Margin=new(0,0,0,4)};top.ColumnStyles.Add(new(SizeType.Percent,100));top.ColumnStyles.Add(new(SizeType.AutoSize));
        var heading=WorkspaceRow(WorkspaceLabel("截图翻译",true),WorkspaceLabel("截图与记录，都在这里"));heading.WrapContents=false;top.Controls.Add(heading,0,0);
        var topActions=WorkspaceRow(capture,import);topActions.WrapContents=false;topActions.Dock=DockStyle.None;topActions.Anchor=AnchorStyles.Right;top.Controls.Add(topActions,1,0);WorkspaceAdd(root,top,SizeType.Absolute,48);
        _shotProfile.Width=168;var edit=IconButton("","settings",(_,_)=>EditModeProfile(false),false,34);edit.AccessibleName="编辑截图翻译方案";
        var plan=WorkspaceRow(WorkspaceLabel("翻译方案"),_shotProfile,edit);plan.Padding=new(20,0,0,4);WorkspaceAdd(root,plan);
        _homeOcrStatus=new Label{AutoSize=true,MaximumSize=new(1050,0),Name="ScreenshotOcrAvailability",Text="OCR — Missing：请在设置 → 运行依赖中检查本地环境。"};_homeApiStatus=new Label();
        var dependencyRow=WorkspaceRow(_homeOcrStatus,IconButton("检查 OCR","refresh",async(_,_)=>await RefreshOcrAvailabilityAsync(),false,112));dependencyRow.Padding=new(20,0,0,4);WorkspaceAdd(root,dependencyRow);
        page.VisibleChanged+=async(_,_)=>{if(page.Visible)await RefreshOcrAvailabilityAsync();};
        var body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};body.ColumnStyles.Add(new(SizeType.Percent,38));body.ColumnStyles.Add(new(SizeType.Percent,62));
        var leftHost=new WorkspaceSurface{Dock=DockStyle.Fill,Padding=new(18,12,14,8),Margin=new(0,0,5,0)};var left=WorkspaceColumn();
        var listTitle=WorkspaceLabel("翻译记录");listTitle.Font=new Font("Microsoft YaHei UI",12,FontStyle.Bold);WorkspaceAdd(left,WorkspaceRow(listTitle,_galleryRecordCount));
        _galleryDate.Items.AddRange(["全部日期","今天","近七天"]);_galleryDate.SelectedIndex=0;
        var search=new TableLayoutPanel{ColumnCount=2,RowCount=1,Height=37,Margin=new(0,4,0,10)};search.RowStyles.Add(new(SizeType.Percent,100));search.ColumnStyles.Add(new(SizeType.Percent,65));search.ColumnStyles.Add(new(SizeType.Percent,35));var searchBox=SearchField(_gallerySearch);searchBox.Dock=DockStyle.Fill;searchBox.Margin=new(0,0,8,0);search.Controls.Add(searchBox,0,0);_galleryDate.Dock=DockStyle.Fill;_galleryDate.Margin=Padding.Empty;search.Controls.Add(_galleryDate,1,0);WorkspaceAdd(left,search,SizeType.Absolute,39);
        _historyList.Dock=DockStyle.Fill;_historyList.Compact=true;_historyList.Margin=Padding.Empty;WorkspaceAdd(left,_historyList,SizeType.Percent,100);
        var previous=WorkspaceButton("上一页",(_,_)=>{if(_galleryPage>0){_galleryPage--;RefreshHistory();}});var next=WorkspaceButton("下一页",(_,_)=>{_galleryPage++;RefreshHistory();});_galleryPager=WorkspaceRow(previous,next);WorkspaceAdd(left,_galleryPager);
        var footer=WorkspaceLabel("图片与译文自动保存");footer.TextAlign=ContentAlignment.MiddleCenter;footer.AutoSize=false;footer.Height=32;WorkspaceAdd(left,footer,SizeType.Absolute,34);leftHost.Controls.Add(left);body.Controls.Add(leftHost,0,0);
        var rightHost=new WorkspaceSurface{Dock=DockStyle.Fill,Padding=new(14,12,14,12),Margin=Padding.Empty};var right=WorkspaceColumn();
        var previewHeader=new TableLayoutPanel{ColumnCount=2,RowCount=1,AutoSize=true,Margin=Padding.Empty};previewHeader.ColumnStyles.Add(new(SizeType.Percent,100));previewHeader.ColumnStyles.Add(new(SizeType.Absolute,34));var nameColumn=WorkspaceColumn();nameColumn.AutoSize=true;WorkspaceAdd(nameColumn,_galleryTitle);WorkspaceAdd(nameColumn,_galleryCaption);previewHeader.Controls.Add(nameColumn,0,0);
        var expand=IconButton("","expand",(_,_)=>{try{OpenHistorySelection();}catch(Exception ex){ShowFusionError(ex);}},false,30);expand.AccessibleName="在独立窗口打开";previewHeader.Controls.Add(expand,1,0);WorkspaceAdd(right,previewHeader);
        for(int i=0;i<3;i++){int mode=i;var b=IconButton(new[]{"原图","译图","对照"}[i],"",(_,_)=>{_galleryCanvas.Mode=mode;_galleryCanvas.Invalidate();UpdateGalleryChrome();},false,78);b.Margin=Padding.Empty;_galleryViews.Add(b);}
        var segments=WorkspaceRow(_galleryViews.Cast<Control>().ToArray());foreach(var b in _galleryViews)b.Margin=Padding.Empty;segments.WrapContents=false;segments.Dock=DockStyle.None;segments.Anchor=AnchorStyles.None;
        var segmentsHost=new TableLayoutPanel{ColumnCount=1,RowCount=1,AutoSize=true,Margin=new(0,0,0,7)};segmentsHost.ColumnStyles.Add(new(SizeType.Percent,100));segmentsHost.Controls.Add(segments);WorkspaceAdd(right,segmentsHost);
        WorkspaceAdd(right,_galleryCanvas,SizeType.Percent,100);
        var minus=IconButton("","minus",(_,_)=>{_galleryCanvas.Zoom=Math.Max(.3f,_galleryCanvas.Zoom-.2f);_galleryCanvas.Invalidate();},false,26);
        var fit=IconButton("适应窗口","",(_,_)=>{_galleryCanvas.Zoom=1;_galleryCanvas.Invalidate();},false,70);
        var plus=IconButton("","plus",(_,_)=>{_galleryCanvas.Zoom=Math.Min(4,_galleryCanvas.Zoom+.2f);_galleryCanvas.Invalidate();},false,26);
        foreach(var b in new[]{minus,fit,plus}){b.LogicalHeight=25;b.RefreshMetrics();}var zoom=WorkspaceRow(minus,fit,plus);zoom.WrapContents=false;zoom.Dock=DockStyle.None;zoom.Anchor=AnchorStyles.Right;var zoomHost=new TableLayoutPanel{AutoSize=true,ColumnCount=1,Margin=Padding.Empty};zoomHost.ColumnStyles.Add(new(SizeType.Percent,100));zoomHost.Controls.Add(zoom);WorkspaceAdd(right,zoomHost);
        var redo=IconButton("重新翻译","refresh",(_,_)=>{try{if(_gallerySelected is {OriginalPng.Length:>0} item)OpenHistoryItem(item).TranslateFromWorkspace();else _statusLabel.Text="此记录缺少原图，无法重新翻译。";}catch(Exception ex){ShowFusionError(ex);}});
        var open=IconButton("详细编辑","edit",(_,_)=>{try{OpenHistorySelection();}catch(Exception ex){ShowFusionError(ex);}});
        var copy=IconButton("复制文字","copy",(_,_)=>CopyGalleryText(false));var export=IconButton("导出图片","export",(_,_)=>ExportGalleryImage());
        _galleryActions.AddRange([redo,open,copy,export]);WorkspaceAdd(right,EqualRow(redo,open,copy,export));
        var textTitle=WorkspaceLabel("文字内容");textTitle.Font=new Font("Microsoft YaHei UI",10,FontStyle.Bold);textTitle.Margin=new(0,10,0,4);WorkspaceAdd(right,textTitle);
        Control TextRow(string label,TextBox field,bool source)
        {var border=new WorkspaceSurface{Padding=new(8,6,6,6),Margin=new(0,3,0,2)};var row=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty};row.RowStyles.Add(new(SizeType.Percent,100));row.ColumnStyles.Add(new(SizeType.Absolute,46));row.ColumnStyles.Add(new(SizeType.Percent,100));row.ColumnStyles.Add(new(SizeType.Absolute,25));var caption=WorkspaceLabel(label);caption.Margin=Padding.Empty;row.Controls.Add(caption,0,0);field.MinimumSize=Size.Empty;field.AutoSize=false;field.Margin=Padding.Empty;row.Controls.Add(field,1,0);var button=IconButton("","copy",(_,_)=>CopyGalleryText(source),false,22);button.LogicalHeight=22;button.RefreshMetrics();button.AccessibleName="复制"+label;button.Dock=DockStyle.Fill;button.Margin=Padding.Empty;row.Controls.Add(button,2,0);border.Controls.Add(row);return border;}
        WorkspaceAdd(right,TextRow("原文",_gallerySourceText,true),SizeType.Absolute,49);WorkspaceAdd(right,TextRow("译文",_galleryText,false),SizeType.Absolute,49);
        rightHost.Controls.Add(right);body.Controls.Add(rightHost,1,0);WorkspaceAdd(root,body,SizeType.Percent,100);page.Controls.Add(root);
        _historyList.SelectionChanged+=async(_,_)=>await LoadGallerySelectionAsync();_historyList.DoubleClick+=(_,_)=>{try{OpenHistorySelection();}catch(Exception ex){ShowFusionError(ex);}};
        _gallerySearch.TextChanged+=(_,_)=>{_galleryPage=0;_gallerySearchTimer.Stop();_gallerySearchTimer.Start();};_galleryDate.SelectedIndexChanged+=(_,_)=>{_galleryPage=0;RefreshHistory();};
        _gallerySearchTimer.Tick+=(_,_)=>{_gallerySearchTimer.Stop();RefreshHistory();};_session.HistoryChanged+=GalleryHistoryChanged;
        FormClosed+=(_,_)=>{_session.HistoryChanged-=GalleryHistoryChanged;_gallerySearchTimer.Stop();_gallerySearchTimer.Dispose();_galleryEpoch++;};UpdateGalleryChrome();
    }
    private void GalleryHistoryChanged()
    {
        Interlocked.Increment(ref _galleryHistoryVersion);
        if(IsDisposed||Disposing||!IsHandleCreated)return;
        try{BeginInvoke((Action)(()=>{if(_currentMainPage=="主页"){_galleryPage=0;RefreshHistory();}}));}catch(InvalidOperationException){}
    }
    private async Task RefreshGalleryAsync()
    {
        if(IsDisposed)return;if(_galleryLoading){_galleryRefreshPending=true;return;}_galleryLoading=true;
        try
        {
            if(_gallerySnapshot is null||_galleryLoadedVersion!=Volatile.Read(ref _galleryHistoryVersion))
            {
                var version=Volatile.Read(ref _galleryHistoryVersion);var snapshot=await Task.Run(_session.SnapshotHistory);if(IsDisposed)return;
                _gallerySnapshot=snapshot;_galleryLoadedVersion=version;
                if(version!=Volatile.Read(ref _galleryHistoryVersion))_galleryRefreshPending=true;
            }
            var all=_gallerySnapshot;
            IEnumerable<SessionHistoryItem> found=all;var query=_gallerySearch.Text.Trim();
            if(query.Length>0)found=found.Where(x=>(x.SourceText+" "+x.SourceSummary+" "+x.TranslationText+" "+x.TranslationSummary).Contains(query,StringComparison.OrdinalIgnoreCase));
            if(_galleryDate.SelectedIndex==1)found=found.Where(x=>x.Timestamp.LocalDateTime.Date==DateTime.Today);
            if(_galleryDate.SelectedIndex==2)found=found.Where(x=>x.Timestamp>=DateTimeOffset.Now.AddDays(-7));
            var records=found.OrderByDescending(x=>x.Timestamp).ToArray();int pages=Math.Max(1,(records.Length+29)/30);_galleryPage=Math.Clamp(_galleryPage,0,pages-1);
            _historyThumbnailSide=ResolveHistoryThumbnailSide();_historyList.SetRecords(records.Skip(_galleryPage*30).Take(30).ToArray(),_historyThumbnailSide,CurrentSettings.HistoryTextSize);
            _historyThumbnailDecodeCount=_historyList.DecodeCount;_historyUsage.Text=$"{records.Length} 条记录 · 第 {_galleryPage+1} / {pages} 页";_galleryRecordCount.Text=$"{records.Length} 条";if(_galleryPager is not null)_galleryPager.Visible=pages>1;
            if(_historyList.SelectedIndex<0&&_historyList.Count>0)_historyList.SelectedIndex=0;else await LoadGallerySelectionAsync();
        }
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
        finally{_galleryLoading=false;if(_galleryRefreshPending&&!IsDisposed){_galleryRefreshPending=false;_=RefreshGalleryAsync();}}
    }
    private async Task LoadGallerySelectionAsync()
    {
        var item=_historyList.SelectedRecord;int epoch=++_galleryEpoch;_gallerySelected=item;
        _galleryText.Text=item?.TranslationText??item?.TranslationSummary??"";_gallerySourceText.Text=item?.SourceText??item?.SourceSummary??"";_galleryTitle.Text=item is null?"选择一条记录":GalleryItemTitle(item);_galleryCanvas.Zoom=1;
        _galleryCaption.Text=item is null?"截图历史只收纳截图翻译记录":item.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm")+"  ·  "+UiTargetLanguageDisplay.DisplayFromCode(item.TargetLanguage);
        _galleryCanvas.SetImages(null,null);UpdateGalleryChrome();if(item is null)return;
        Bitmap? source=null,result=null;
        try
        {
            await Task.Run(()=>{if(item.OriginalPng.Length>0)source=DecodeOwnedBitmap(item.OriginalPng,out _);if(item.TranslatedImage is {Length:>0})result=DecodeOwnedBitmap(item.TranslatedImage,out _);});
            if(IsDisposed||epoch!=_galleryEpoch)return;
            if(_galleryCanvas.Mode==1&&result is null)_galleryCanvas.Mode=0;
            _galleryCanvas.SetImages(source,result);source=null;result=null;UpdateGalleryChrome();
        }
        catch(Exception ex){if(!IsDisposed&&epoch==_galleryEpoch)_galleryCaption.Text="图片读取失败，文字记录仍保留："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{source?.Dispose();result?.Dispose();}
    }
    private void UpdateGalleryChrome()
    {
        var p=UiTheme.Current;
        for(int i=0;i<_galleryViews.Count;i++){if(_galleryViews[i] is GameActionButton b)b.Primary=i==_galleryCanvas.Mode;_galleryViews[i].Invalidate();}
        foreach(var action in _galleryActions)action.Enabled=_gallerySelected is not null;
        _galleryCanvas.Invalidate();
    }
    internal static string GalleryItemTitle(SessionHistoryItem item)
    {var title=(string.IsNullOrWhiteSpace(item.TranslationSummary)?item.SourceSummary:item.TranslationSummary).Replace('\r',' ').Replace('\n',' ').Trim();return title.Length==0?"截图翻译":title.Length>22?title[..22]+"…":title;}
    private void CopyGalleryText(bool source)
    {try{var text=source?_gallerySourceText.Text:_galleryText.Text;if(!string.IsNullOrWhiteSpace(text))Clipboard.SetText(text);}catch(Exception ex){ShowFusionError(ex);}}
    private void ExportGalleryImage()
    {
        if(_gallerySelected is not { } item)return;
        var bytes=_galleryCanvas.Mode==0?item.OriginalPng:item.TranslatedImage;
        if(bytes is null||bytes.Length==0){_statusLabel.Text="当前记录没有可导出的这张图片。";return;}
        using var dialog=new SaveFileDialog{Filter="PNG 图片|*.png",FileName=( _galleryCanvas.Mode==0?"原图-":"译图-")+item.Timestamp.ToString("yyyyMMdd-HHmmss")+".png"};if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try{using var bitmap=DecodeOwnedBitmap(bytes,out _);bitmap.Save(dialog.FileName,ImageFormat.Png);_statusLabel.Text="图片已导出。";}catch(Exception ex){ShowFusionError(ex);}
    }
}
