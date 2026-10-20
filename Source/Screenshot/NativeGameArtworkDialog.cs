using System.ComponentModel;
using System.Net.Http;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
namespace ScreenshotTranslationUiTester;

internal sealed class ArtworkCandidateView : INotifyPropertyChanged
{
    internal ArtworkCandidate Item { get; }
    private readonly HashSet<ArtworkProvider> discoveries=[];
    internal void AddDiscovery(ArtworkProvider provider)=>discoveries.Add(provider);
    internal bool BelongsTo(ArtworkProvider provider)=>Item.BelongsTo(provider)||discoveries.Contains(provider);
    public string Name=>Item.Name;
    private bool previewOnly;
    internal void SetThumbnail(ArtworkThumbnail value){previewOnly=value.PreviewOnly;Thumbnail=value.Image;PropertyChanged?.Invoke(this,new(nameof(Caption)));}
    public string Caption=>Item.SourceLabel+(Item.PreviewOnly||previewOnly?" · "+UiLanguage.Text("索引预览图"):Item.Width>0?$" · {Item.Width} × {Item.Height}":"")+(Item.TitleNeedsReview?" · "+UiLanguage.Text("标题待确认"):"");
    private WM.ImageSource? thumbnail;
    public WM.ImageSource? Thumbnail {get=>thumbnail;set{thumbnail=value;PropertyChanged?.Invoke(this,new(nameof(Thumbnail)));}}
    internal ArtworkCandidateView(ArtworkCandidate item)=>Item=item;
    public event PropertyChangedEventHandler? PropertyChanged;
}
internal sealed class ArtworkResultsPanel : WC.WrapPanel
{
    protected override W.Size MeasureOverride(W.Size size)
    {
        double width=double.IsFinite(size.Width)?Math.Max(1,size.Width):480;
        ItemWidth=Math.Max(1,(width-4)/Math.Max(1,(int)(width/210)));ItemHeight=182;
        return base.MeasureOverride(size);
    }
}
internal sealed class NativeGameArtworkDialog : NativeDialogWindow
{
    private readonly RecentGameStore store;
    private readonly string gamePath;
    private readonly ArtworkSearchService service;
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource? searchRequest,previewRequest;
    private readonly WC.TextBox query=NativeDialogControls.Input();
    private readonly WC.ComboBox kind=NativeDialogControls.Choice(["封面","图标"]),ratio=NativeDialogControls.Choice(["横幅取景","完整原图"]),steamKind=NativeDialogControls.Choice(["自动选择高清图","宽幅背景","横版封面","竖版封面","透明标志"]);
    private readonly WC.TextBox imageAddress=NativeDialogControls.Input(),sourceAddress=NativeDialogControls.Input(readOnly:true);
    private readonly WC.ListBox matches=new(){BorderThickness=new(0),HorizontalContentAlignment=W.HorizontalAlignment.Stretch,FocusVisualStyle=null};
    private readonly NativeArtworkCrop preview=new(){MinHeight=140,ShowOverview=true};
    private readonly WC.TextBlock status=NativeDialogControls.Text(""),sourceStatus=NativeDialogControls.Text(""),quality=NativeDialogControls.Text(""),previewStatus=NativeDialogControls.Text("");
    private readonly WC.Button save,search,stop;
    private readonly Dictionary<ArtworkProvider,ArtworkSearchBatch> batches=[];
    private readonly List<ArtworkCandidateView> candidates=[];
    private readonly SemaphoreSlim thumbnailSlots=new(2);
    private readonly Dictionary<ArtworkCandidateView,Task> thumbnailJobs=[];
    private bool thumbnailScheduled;
    private readonly List<(ArtworkProvider? Provider,WC.Button Button,string Label)> filters=[];
    private ArtworkProvider? selectedProvider;
    private bool suppressSelection,loading,saving,canApply,indexPreview;
    private int searchVersion,previewVersion;
    private string origin="file";
    private string? sourceUrl;
    private RecentGame? stagedAgainst;
    private bool Cover=>kind.SelectedIndex==0;
    private RecentGame Current=>store.Find(gamePath)??throw new IOException("该游戏已从游戏库移除。");
    internal Task SearchCompletion {get;private set;}=Task.CompletedTask;
    internal NativeGameArtworkDialog(RecentGameStore store,RecentGame game,bool autoSearch=true,ArtworkSearchService? service=null,bool captureOnOpen=false):base("查找游戏封面 · "+game.Name,1180,820)
    {
        this.store=store;this.service=service??new();gamePath=game.ExePath;MinWidth=800;MinHeight=620;
        query.Text=ArtworkWebSources.SearchTitle(game.Name);query.Margin=new(0,0,12,0);
        search=NativeDialogControls.Button("重新搜索",()=>SearchCompletion=SearchAsync(),true);
        stop=NativeDialogControls.Button("停止搜索",()=>searchRequest?.Cancel());stop.Visibility=W.Visibility.Collapsed;
        save=NativeDialogControls.Button("使用这张图片",async()=>await Run(SaveAsync),true);
        var heading=NativeDialogControls.Text(game.Name,21);heading.FontWeight=W.FontWeights.SemiBold;
        var searchRow=new WC.Grid();searchRow.ColumnDefinitions.Add(new());searchRow.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});searchRow.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});
        searchRow.Children.Add(query);WC.Grid.SetColumn(search,1);searchRow.Children.Add(search);WC.Grid.SetColumn(stop,2);searchRow.Children.Add(stop);
        var filterRow=NativeDialogControls.Row();
        void Filter(string label,ArtworkProvider? provider)
        {
            var button=NativeDialogControls.Button(label,()=>{selectedProvider=provider;RefreshResults();});button.MinWidth=56;
            filterRow.Children.Add(button);filters.Add((provider,button,label));
        }
        Filter("全部",null);foreach(var provider in new[]{ArtworkProvider.Google,ArtworkProvider.F95,ArtworkProvider.DLsite,ArtworkProvider.Steam,ArtworkProvider.Bing})Filter(ArtworkSearchService.Label(provider),provider);
        matches.SetResourceReference(BackgroundProperty,"Fusion.Main");
        matches.ItemsPanel=new WC.ItemsPanelTemplate(new W.FrameworkElementFactory(typeof(ArtworkResultsPanel)));
        WC.ScrollViewer.SetHorizontalScrollBarVisibility(matches,WC.ScrollBarVisibility.Disabled);
        WC.ScrollViewer.SetVerticalScrollBarVisibility(matches,WC.ScrollBarVisibility.Auto);
        matches.ItemTemplate=(W.DataTemplate)W.Markup.XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><StackPanel Margin="5">
              <Border Height="116" CornerRadius="5" Background="{DynamicResource Fusion.Secondary}" ClipToBounds="True"><Image Source="{Binding Thumbnail}" Stretch="Uniform"/></Border>
              <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" Margin="0,6,0,3" ToolTip="{Binding Name}"/>
              <TextBlock Text="{Binding Caption}" FontSize="12" Foreground="{DynamicResource Fusion.Muted}"/>
            </StackPanel></DataTemplate>
            """);
        matches.ItemContainerStyle=(W.Style)W.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem">
              <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem"><Border x:Name="Tile" CornerRadius="6" BorderThickness="2" BorderBrush="Transparent" Padding="2" Margin="0,0,8,8"><ContentPresenter/></Border>
                <ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter TargetName="Tile" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/><Setter TargetName="Tile" Property="Background" Value="{DynamicResource Fusion.Secondary}"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Tile" Property="Background" Value="{DynamicResource Fusion.Secondary}"/></Trigger></ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        var left=new WC.Grid();left.RowDefinitions.Add(new(){Height=W.GridLength.Auto});left.RowDefinitions.Add(new(){Height=W.GridLength.Auto});left.RowDefinitions.Add(new());
        left.Children.Add(filterRow);WC.Grid.SetRow(sourceStatus,1);left.Children.Add(sourceStatus);WC.Grid.SetRow(matches,2);left.Children.Add(matches);
        var right=new WC.Grid{Margin=new(20,0,0,0)};right.RowDefinitions.Add(new(){Height=W.GridLength.Auto});right.RowDefinitions.Add(new(){Height=W.GridLength.Auto});right.RowDefinitions.Add(new(){Height=W.GridLength.Auto});right.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        right.Children.Add(NativeDialogControls.Text("原图与取景范围",18));preview.Height=200;
        var captureButton=NativeDialogControls.Button("截取游戏画面",async()=>await Run(CaptureAsync));
        W.Automation.AutomationProperties.SetAutomationId(captureButton,"ArtworkCapture");
        var picture=NativeDialogControls.Column(captureButton,previewStatus,preview,NativeDialogControls.Text("横幅效果"),preview.BannerPreview);WC.Grid.SetRow(picture,1);right.Children.Add(picture);
        var pictureTools=NativeDialogControls.Column(ratio,NativeDialogControls.Row(
            NativeDialogControls.Button("居中",preview.Center),NativeDialogControls.Button("−",()=>preview.Zoom(-.15)),NativeDialogControls.Button("+",()=>preview.Zoom(.15))),
            NativeDialogControls.Text("拖动取景框调整位置，下方查看横幅效果。保留原图，保存后应用。"),quality,steamKind);
        WC.Grid.SetRow(pictureTools,2);right.Children.Add(pictureTools);
        sourceAddress.MinHeight=30;sourceAddress.FontSize=11;sourceAddress.Margin=new(0,0,0,4);
        var sourceTools=NativeDialogControls.Column(NativeDialogControls.Text("图片来源"),sourceAddress,NativeDialogControls.Row(NativeDialogControls.Button("打开图片来源",()=>{if(ArtworkWebSources.PublicUri(sourceUrl,out var uri))ArtworkWebSources.Open(uri!);})));WC.Grid.SetRow(sourceTools,3);right.Children.Add(sourceTools);
        var body=new WC.Grid();body.ColumnDefinitions.Add(new(){Width=new(1.25,W.GridUnitType.Star)});body.ColumnDefinitions.Add(new());body.Children.Add(left);var previewScroll=new WC.ScrollViewer{Content=right,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=WC.ScrollBarVisibility.Disabled};WC.Grid.SetColumn(previewScroll,1);body.Children.Add(previewScroll);
        var alternate=NativeDialogControls.Column(NativeDialogControls.Row(
            NativeDialogControls.Button("从文件选择",async()=>await Run(ChooseFileAsync)),NativeDialogControls.Button("粘贴图片",async()=>await Run(PasteImageAsync)),NativeDialogControls.Button("截取游戏画面",async()=>await Run(CaptureAsync))),
            NativeDialogControls.Row(NativeDialogControls.Text("编辑图片"),kind),imageAddress,NativeDialogControls.Row(NativeDialogControls.Button("读取图片地址",async()=>await Run(LoadAddressAsync))));
        var more=new WC.Expander{Header=UiLanguage.Text("本地图片与其他方式"),Content=new WC.ScrollViewer{Content=alternate,MaxHeight=170,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto},Margin=new(0,0,0,8)};
        more.SetResourceReference(WC.Control.ForegroundProperty,"Fusion.Text");
        more.Template=(WC.ControlTemplate)W.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Expander"><StackPanel>
            <ToggleButton IsChecked="{Binding IsExpanded,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}" Foreground="{TemplateBinding Foreground}"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent" Padding="2,8"><StackPanel Orientation="Horizontal"><Path Data="M 0,0 L 4,4 L 8,0" Stroke="{TemplateBinding Foreground}" StrokeThickness="1.5" Width="8" Height="4" VerticalAlignment="Center" Margin="0,0,8,0"/><ContentPresenter/></StackPanel></Border></ControlTemplate></ToggleButton.Template><ContentPresenter ContentSource="Header"/></ToggleButton>
            <ContentPresenter x:Name="Body" ContentSource="Content" Visibility="Collapsed"/>
            </StackPanel><ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="Body" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers></ControlTemplate>
            """);
        var footer=new WC.Grid();footer.ColumnDefinitions.Add(new());footer.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});footer.Children.Add(more);
        var buttons=NativeDialogControls.Row(NativeDialogControls.Button("取消",Close),save);buttons.VerticalAlignment=W.VerticalAlignment.Bottom;WC.Grid.SetColumn(buttons,1);footer.Children.Add(buttons);
        var layout=new WC.Grid{Margin=new(24)};layout.RowDefinitions.Add(new(){Height=W.GridLength.Auto});layout.RowDefinitions.Add(new());layout.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        layout.Children.Add(NativeDialogControls.Column(heading,searchRow,NativeDialogControls.Text("自动按游戏名称查找，点击候选图片预览。")));
        WC.Grid.SetRow(body,1);layout.Children.Add(body);var bottom=NativeDialogControls.Column(status,footer);WC.Grid.SetRow(bottom,2);layout.Children.Add(bottom);Content=layout;
        W.Automation.AutomationProperties.SetAutomationId(matches,"ArtworkCandidates");W.Automation.AutomationProperties.SetAutomationId(save,"ArtworkApply");
        matches.SelectionChanged+=async(_,_)=>{if(!suppressSelection&&matches.SelectedItem is ArtworkCandidateView item)await SelectAsync(item);};
        matches.AddHandler(WC.ScrollViewer.ScrollChangedEvent,new WC.ScrollChangedEventHandler((_,_)=>ScheduleThumbnails()));
        matches.SizeChanged+=(_,_)=>ScheduleThumbnails();
        matches.Loaded+=(_,_)=>ScheduleThumbnails();
        matches.ItemContainerGenerator.StatusChanged+=(_,_)=>
        {if(matches.ItemContainerGenerator.Status==WC.Primitives.GeneratorStatus.ContainersGenerated)ScheduleThumbnails();};
        steamKind.SelectionChanged+=async(_,_)=>{if(!suppressSelection&&matches.SelectedItem is ArtworkCandidateView item)await SelectAsync(item);};
        kind.SelectionChanged+=(_,_)=>{CancelPreview();LoadCurrent();};ratio.SelectionChanged+=(_,_)=>{preview.Aspect=Cover?(ratio.SelectedIndex==0?3.2:0):1;RefreshQuality();};preview.FramingChanged+=RefreshQuality;
        query.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter){e.Handled=true;SearchCompletion=SearchAsync();}};
        LoadCurrent();Loaded+=async(_,_)=>{if(captureOnOpen)await Run(CaptureAsync);else if(autoSearch)SearchCompletion=SearchAsync();};
        Closed+=(_,_)=>{lifetime.Cancel();searchRequest?.Cancel();previewRequest?.Cancel();preview.Dispose();};
    }
    private void LoadCurrent()
    {
        var game=Current;stagedAgainst=game;origin=Cover?game.CoverOrigin:game.IconOrigin;sourceUrl=Cover?game.CoverSourceUrl:game.IconSourceUrl;sourceAddress.Text=sourceUrl??"";
        preview.SetImage(store.ReadImage(game,Cover));ratio.SelectedIndex=0;preview.ShowOverview=Cover;preview.BannerPreview.Visibility=Cover?W.Visibility.Visible:W.Visibility.Collapsed;preview.Aspect=Cover?3.2:1;preview.Framing=game.CoverFrame??CoverFraming.Centered;
        indexPreview=false;RefreshQuality();steamKind.Visibility=W.Visibility.Collapsed;canApply=preview.HasImage;save.IsEnabled=canApply;status.Text="选择图片后可调整取景，应用后才会替换当前图片。";
        previewStatus.Text=UiLanguage.Text(preview.HasImage?"当前已保存的图片":"尚未载入图片。可选择候选图或截取游戏画面。");
    }
    private void CancelPreview(){previewVersion++;previewRequest?.Cancel();loading=false;}
    internal async Task SearchAsync()
    {
        string title;try{title=ArtworkSearchService.Query(query.Text);}catch(Exception ex){status.Text=ex.Message;return;}
        searchRequest?.Cancel();searchRequest=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=searchRequest.Token;int version=++searchVersion;
        CancelPreview();batches.Clear();candidates.Clear();thumbnailJobs.Clear();RefreshResults();stop.Visibility=W.Visibility.Visible;search.Content=UiLanguage.Text("重新搜索");status.Text="正在查找图片…";
        try
        {
            await Task.WhenAll(Enum.GetValues<ArtworkProvider>().Select(async provider=>
            {
                var batch=await Task.Run(()=>service.SearchAsync(provider,title,token),token);if(IsDisposed||version!=searchVersion||token.IsCancellationRequested)return;
                batches[provider]=batch;
                foreach(var item in batch.Items)
                {
                    var existing=candidates.FirstOrDefault(x=>x.Item.Identity==item.Identity);
                    if(existing is not null){existing.AddDiscovery(provider);continue;}
                    var view=new ArtworkCandidateView(item);view.AddDiscovery(provider);candidates.Add(view);
                }
                RefreshResults();status.Text=UiLanguage.Format($"已找到 {candidates.Count} 张候选图片，点击图片查看。");
                await LoadVisibleThumbnailsAsync();
            }));
        }
        catch(OperationCanceledException){if(!IsDisposed&&version==searchVersion)status.Text="搜索已停止，已找到的图片仍可选择。";}
        catch(Exception ex){if(!IsDisposed&&version==searchVersion)status.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{if(!IsDisposed&&version==searchVersion){stop.Visibility=W.Visibility.Collapsed;save.IsEnabled=preview.HasImage&&!loading&&canApply;if(!token.IsCancellationRequested&&candidates.Count==0)status.Text="未取得候选图片。请查看各来源的状态，或调整游戏名称后重试。";}}
    }
    private void ScheduleThumbnails()
    {
        if(thumbnailScheduled||IsDisposed||lifetime.IsCancellationRequested)return;
        thumbnailScheduled=true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>
        {thumbnailScheduled=false;if(!IsDisposed)_=LoadVisibleThumbnailsAsync();}));
    }
    private Task LoadVisibleThumbnailsAsync()
    {
        if(IsDisposed||searchRequest is null||searchRequest.IsCancellationRequested)return Task.CompletedTask;
        var token=searchRequest.Token;int version=searchVersion;
        var shown=matches.Items.Cast<ArtworkCandidateView>().ToArray();
        var visible=new List<ArtworkCandidateView>();
        for(int i=0;i<shown.Length;i++)
        {
            bool near=i<6;
            if(matches.ItemContainerGenerator.ContainerFromIndex(i) is W.FrameworkElement element&&element.ActualHeight>0)
            {
                var bounds=element.TransformToAncestor(matches).TransformBounds(new W.Rect(element.RenderSize));
                near|=bounds.Bottom>=0&&bounds.Top<=matches.ActualHeight+182;
            }
            if(!near)continue;
            var view=shown[i];visible.Add(view);
            if(thumbnailJobs.ContainsKey(view))continue;
            if(service.TryGetThumbnail(view.Item,out var ready))
            {view.SetThumbnail(new(ready!,false));thumbnailJobs[view]=Task.CompletedTask;}
            else thumbnailJobs[view]=Load(view);
        }
        return Task.WhenAll(visible.Select(x=>thumbnailJobs[x]));
        async Task Load(ArtworkCandidateView view)
        {
            bool acquired=false;
            try
            {
                await thumbnailSlots.WaitAsync(token);acquired=true;
                for(int attempt=0;attempt<3;attempt++)
                {
                    try
                    {
                        var thumbnail=await service.LoadThumbnailAsync(view.Item,token);
                        if(!IsDisposed&&version==searchVersion&&!token.IsCancellationRequested)view.SetThumbnail(thumbnail);
                        break;
                    }
                    catch(Exception ex)when(attempt<2&&ex is IOException or HttpRequestException)
                    {await Task.Delay(400*(attempt+1),token);}
                }
            }
            catch(Exception ex)when(ex is IOException or HttpRequestException or ArgumentException or OperationCanceledException){}
            finally{if(acquired)thumbnailSlots.Release();}
        }
    }
    private void RefreshResults()
    {
        foreach(var filter in filters)
        {
            filter.Button.BorderThickness=new(filter.Provider==selectedProvider?2:1);
            filter.Button.SetResourceReference(WC.Control.BorderBrushProperty,filter.Provider==selectedProvider?"Fusion.Accent":"Fusion.Border");
        }
        suppressSelection=true;var selected=matches.SelectedItem;
        var shown=candidates.Where(x=>selectedProvider is null||x.BelongsTo(selectedProvider.Value)).OrderBy(x=>ArtworkSearchService.DisplayRank(x.Item,query.Text,Cover)).ToArray();
        matches.ItemsSource=shown;
        if(selected is ArtworkCandidateView v&&shown.Contains(v))matches.SelectedItem=v;suppressSelection=false;
        ScheduleThumbnails();
        int Count(ArtworkProvider p)=>candidates.Count(x=>x.BelongsTo(p));
        string FullStatus(ArtworkProvider p)
        {
            var found=candidates.Where(x=>x.BelongsTo(p)).ToArray();
            string indexes=string.Join(", ",found.Select(x=>x.Item.SearchIndex??x.Item.Provider.ToString()).Distinct());
            return UiLanguage.Text(ArtworkSearchService.Label(p))+" · "+(found.Length>0?UiLanguage.Format($"{found.Length} 张图片")+" · "+indexes:batches.TryGetValue(p,out var b)?UiLanguage.Text(b.Message):UiLanguage.Text("正在查找…"));
        }
        sourceStatus.ToolTip=string.Join("\n",Enum.GetValues<ArtworkProvider>().Select(FullStatus));
        sourceStatus.Text=selectedProvider is {} selectedSource?FullStatus(selectedSource):string.Join("  ·  ",Enum.GetValues<ArtworkProvider>().Select(p=>
            (p==ArtworkProvider.F95?"F95":p.ToString())+": "+(Count(p)>0?Count(p).ToString():batches.TryGetValue(p,out var b)?UiLanguage.Text(b.Message=="未找到与游戏名匹配的图片。"?"暂无图片":"暂不可用"):"…")));

    }
    private async Task SelectAsync(ArtworkCandidateView item)
    {
        CancelPreview();previewRequest=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);int version=previewVersion;var token=previewRequest.Token;
        loading=true;canApply=false;save.IsEnabled=false;status.Text="正在载入所选图片…";previewStatus.Text=UiLanguage.Text(preview.HasImage?"正在载入新图，当前仍显示上一张图片。":"正在载入所选图片…");
        try
        {
            Bitmap image;bool previewOnly=item.Item.PreviewOnly;
            try
            {
                var selectedKind=steamKind.SelectedIndex switch{1=>SteamArtworkKind.Hero,2=>SteamArtworkKind.Header,3=>SteamArtworkKind.Portrait,4=>SteamArtworkKind.Logo,_=>(SteamArtworkKind?)null};
                image=await service.LoadAsync(item.Item,false,token,selectedKind);
            }
            catch(Exception ex)when(item.Item.Steam is null&&ex is IOException or HttpRequestException or ArgumentException)
            {image=await service.LoadAsync(item.Item,true,token);previewOnly=true;}
            if(IsDisposed||version!=previewVersion||token.IsCancellationRequested){image.Dispose();return;}
            // Once the original has loaded, use that same image for its tile.
            // Do not keep showing a lower-quality search preview beside it.
            if(!previewOnly){using var thumbnail=GameWindowCover.Thumbnail(image,640,440);item.Thumbnail=WpfDrawing.Image(thumbnail);}
            StageImage(image,"online",item.Item.PageUrl,previewOnly);steamKind.Visibility=item.Item.Steam is null?W.Visibility.Collapsed:W.Visibility.Visible;
            if(previewOnly)status.Text="此来源仅取得索引预览图，清晰度有限。可选择其他来源的原图。";
        }
        catch(OperationCanceledException){if(!IsDisposed&&version==previewVersion&&!token.IsCancellationRequested)status.Text="所选图片暂时无法读取，请选择其他候选。";}
        catch(Exception ex){if(!IsDisposed&&version==previewVersion)status.Text=ex is HttpRequestException?"所选图片暂时无法读取，请选择其他候选。":SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{if(!IsDisposed&&version==previewVersion){loading=false;save.IsEnabled=preview.HasImage&&canApply;if(!canApply)previewStatus.Text=UiLanguage.Text(preview.HasImage?"新图未载入，仍显示上一张图片。":"图片未载入，请重新选择或重试。");}}
    }
    internal void StageImage(Bitmap image,string imageOrigin,string? page,bool previewOnly=false)
    {
        indexPreview=previewOnly;previewStatus.Text=UiLanguage.Text(imageOrigin=="screenshot"?"刚截取的游戏画面 · 尚未保存":"新图片已载入 · 尚未保存");
        stagedAgainst=Current;preview.SetImage(image);origin=imageOrigin;sourceUrl=ArtworkWebSources.SourcePage(page);sourceAddress.Text=sourceUrl??"";
        preview.Aspect=Cover?(ratio.SelectedIndex==0?3.2:0):1;status.Text=UiLanguage.Format($"已载入 {image.Width} × {image.Height} 图片；保存后才会替换。");canApply=true;save.IsEnabled=true;RefreshQuality();
    }
    private void RefreshQuality()
    {
        var size=preview.PixelSize;
        save.Content=UiLanguage.Text(indexPreview?"使用预览图":"使用这张图片");
        if(size.IsEmpty){quality.Text="";return;}
        var crop=preview.Framing.Crop(size.Width,size.Height,Cover?3.2:1);
        quality.Text=indexPreview?UiLanguage.Format($"仅取得搜索预览（{size.Width} × {size.Height}），原图不可用。建议选择其他图片。")
            :Cover&&crop.Width<1600?UiLanguage.Format($"图片 {size.Width} × {size.Height}；当前取景宽约 {(int)crop.Width} 像素，铺满横幅可能模糊。")
            :UiLanguage.Format($"原图 {size.Width} × {size.Height}；保存时保留完整尺寸。");
    }
    private async Task Run(Func<Task> action)
    {
        if(saving)return;saving=true;save.IsEnabled=false;
        try{await action();}catch(OperationCanceledException){}catch(Exception ex){if(!IsDisposed)status.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{saving=false;if(!IsDisposed)save.IsEnabled=preview.HasImage&&!loading&&canApply;}
    }
    private async Task SaveAsync()
    {
        using var image=Cover?preview.Original():preview.Export(256);var game=stagedAgainst??Current;
        var cover=Cover;var frame=preview.Framing;var imageOrigin=origin;var page=sourceUrl;
        if(!await Task.Run(()=>store.SaveImage(game,image,cover,null,lifetime.Token,manual:true,origin:imageOrigin,sourceUrl:page,frame:frame),lifetime.Token))throw new IOException("图片记录已改变，请重新选择。");
        if(!IsDisposed){stagedAgainst=Current;Finish(System.Windows.Forms.DialogResult.OK);}
    }
    private Task ChooseFileAsync()
    {
        using var dialog=new OpenFileDialog{Filter=UiLanguage.Text("图片|*.png;*.jpg;*.jpeg;*.bmp;*.ico"),Title=UiLanguage.Text(Cover?"选择封面":"选择图标")};
        if(dialog.ShowDialog(this)!=System.Windows.Forms.DialogResult.OK)return Task.CompletedTask;
        CancelPreview();if(new FileInfo(dialog.FileName).Length>24_000_000)throw new IOException("请选择小于 24 MB 的图片。");
        using var image=Image.FromFile(dialog.FileName);ArtworkWebSources.ValidateImage(image);StageImage(new Bitmap(image),"file",null);return Task.CompletedTask;
    }
    private Task PasteImageAsync()
    {
        var sourceImage=W.Clipboard.GetImage();if(sourceImage is null){status.Text="剪贴板中没有图片。";return Task.CompletedTask;}
        if(sourceImage.PixelWidth>8192||sourceImage.PixelHeight>8192||(long)sourceImage.PixelWidth*sourceImage.PixelHeight>32_000_000)throw new IOException("图片尺寸过大。");
        CancelPreview();var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(sourceImage));using var buffer=new MemoryStream();encoder.Save(buffer);buffer.Position=0;using var image=Image.FromStream(buffer);
        var html=W.Clipboard.GetData(W.DataFormats.Html) as string;StageImage(new Bitmap(image),"clipboard",html is {Length:<1_000_000}?ArtworkWebSources.ClipboardSource(html):null);return Task.CompletedTask;
    }
    private async Task LoadAddressAsync(){CancelPreview();var image=await ArtworkWebSources.ReadImageAsync(imageAddress.Text.Trim(),lifetime.Token);if(IsDisposed){image.Dispose();return;}StageImage(image,"online",imageAddress.Text.Trim());}
    private async Task CaptureAsync()
    {
        CancelPreview();previewRequest=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=previewRequest.Token;int version=previewVersion;
        loading=true;canApply=false;save.IsEnabled=false;
        status.Text=UiLanguage.Text("正在截取游戏画面…");previewStatus.Text=UiLanguage.Text(preview.HasImage?"正在取图，当前仍显示上一张图片。":"正在截取游戏画面…");
        try
        {
            var result=await GameWindowCover.CaptureAsync(gamePath,false,token,_=>{});
            if(IsDisposed||version!=previewVersion||token.IsCancellationRequested){result.Image?.Dispose();return;}
            if(result.Image is null){status.Text=result.Message;previewStatus.Text=UiLanguage.Text(preview.HasImage?"截图失败，仍显示上一张图片。":"截图失败，暂无新预览。");return;}
            StageImage(result.Image,"screenshot",null);
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception ex)
        {
            if(IsDisposed||version!=previewVersion)return;
            status.Text=UiLanguage.Text("截图未完成，请重试。");
            previewStatus.Text=UiLanguage.Text(preview.HasImage?"截图失败，仍显示上一张图片。":"截图失败，暂无新预览。");
            AppLog.Write("game-cover","manual-preview-failed "+SafeDiagnosticOutput.ExceptionSummary(ex));
        }
        finally{if(!IsDisposed&&version==previewVersion){loading=false;save.IsEnabled=preview.HasImage&&canApply;}}
    }
}
