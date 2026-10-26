using W=System.Windows;
using WC=System.Windows.Controls;
using WM=System.Windows.Media;
namespace ScreenshotTranslationUiTester;

internal sealed class NativeWolfMapDialog : NativeDialogWindow
{
    internal NativeWolfMapDialog(string exe,Func<int?,int,string[],Task> selectVariables,bool eventView=false):base(eventView?"事件":"地图",1080,760)
    {
        var view=new NativeWolfMapWorkspace(exe,async(map,eventId,ids)=>{await selectVariables(map,eventId,ids);Close();});
        view.Margin=new(20);view.SetMode(eventView);Content=view;Closed+=(_,_)=>view.Dispose();
    }
}

internal sealed class NativeWolfMapWorkspace : WC.Grid,IDisposable
{
    private readonly string exe;
    private readonly CancellationTokenSource lifetime=new();
    private readonly WC.ListBox maps=new(){MinWidth=150},events=new(){MinWidth=200};
    // Map and event names are game data. Only product-owned labels are
    // localized; never run the complete mixed status through UI translation.
    private readonly NativeLocalizedTextBlock status=new(){Localize=false,TextWrapping=W.TextWrapping.Wrap,Margin=new(0,4,0,12)};
    private readonly WC.TextBox detail=NativeDialogControls.Input("",true,true);
    private readonly MapCanvas canvas=new();
    private readonly Func<int?,int,string[],Task> selectVariables;
    private int generation,detailRevision;private bool loaded,disposed,mapLoading=true;
    private int? loadedMapCount;
    private string? errorMessage;
    private WolfMapCatalog.Entry? displayedEntry;
    private WolfTextCatalog.MapStructure? displayedMap;
    private WolfTextCatalog.MapEvent? displayedEvent;
    private bool IsDisposed=>disposed;
    private readonly WC.Grid body=new();
    internal NativeWolfMapWorkspace(string exe,Func<int?,int,string[],Task> selectVariables)
    {
        this.exe=exe;this.selectVariables=selectVariables;
        UiLanguage.LanguageChanged+=RefreshLanguage;RefreshStatus();
        MinHeight=480;
        detail.FontSize=13;detail.MinHeight=160;
        body.ColumnDefinitions.Add(new(){Width=new(160)});body.ColumnDefinitions.Add(new());body.ColumnDefinitions.Add(new(){Width=new(230)});
        body.Children.Add(maps);WC.Grid.SetColumn(canvas,1);body.Children.Add(canvas);WC.Grid.SetColumn(events,2);body.Children.Add(events);
        canvas.Margin=new(12,0,12,0);
        var layout=this;layout.RowDefinitions.Add(new(){Height=W.GridLength.Auto});layout.RowDefinitions.Add(new());layout.RowDefinitions.Add(new(){Height=new(200)});layout.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        layout.Children.Add(status);WC.Grid.SetRow(body,1);layout.Children.Add(body);WC.Grid.SetRow(detail,2);layout.Children.Add(detail);
        var actions=NativeDialogControls.Row(NativeDialogControls.Text("这是所选资源的事件位置示意，不是游戏当前画面。请在左侧选择地图，再选择事件查看内容及相关变量。"),
            NativeDialogControls.Button("查看相关变量",async()=>{if(events.SelectedItem is WolfTextCatalog.MapEvent e&&maps.SelectedItem is WolfMapCatalog.Entry m){await selectVariables(m.MapId,e.Id,WolfMapCatalog.VariableIds(e));}}));
        WC.Grid.SetRow(actions,3);layout.Children.Add(actions);
        maps.SelectionChanged+=async(_,_)=>{if(maps.SelectedItem is WolfMapCatalog.Entry entry)await LoadMap(entry);};
        events.SelectionChanged+=(_,_)=>SelectEvent();canvas.Selected+=e=>events.SelectedItem=e;
        Loaded+=async(_,_)=>
        {
            if(loaded)return;loaded=true;
            try{var items=await Task.Run(()=>WolfMapCatalog.List(exe,lifetime.Token),lifetime.Token);if(IsDisposed)return;loadedMapCount=items.Length;mapLoading=false;errorMessage=null;maps.ItemsSource=items;RefreshStatus();if(items.Length>0)maps.SelectedIndex=0;}
            catch(OperationCanceledException){}catch(Exception e){if(!IsDisposed){mapLoading=false;errorMessage=SafeDiagnosticOutput.ExceptionSummary(e);RefreshStatus();}}
        };
        foreach(var list in new[]{maps,events}){list.SetResourceReference(WC.Control.BackgroundProperty,"Fusion.Secondary");list.SetResourceReference(WC.Control.ForegroundProperty,"Fusion.Text");list.SetResourceReference(WC.Control.BorderBrushProperty,"Fusion.Border");}
    }
    internal void SetMode(bool eventView)
    {
        canvas.Visibility=eventView?W.Visibility.Collapsed:W.Visibility.Visible;
        WC.Grid.SetColumn(events,eventView?1:2);body.ColumnDefinitions[2].Width=eventView?new W.GridLength(0):new W.GridLength(230);
    }
    public void Dispose(){if(disposed)return;disposed=true;UiLanguage.LanguageChanged-=RefreshLanguage;detailRevision++;lifetime.Cancel();}
    private async Task LoadMap(WolfMapCatalog.Entry entry)
    {
        int request=++generation;mapLoading=true;errorMessage=null;displayedEntry=null;displayedMap=null;displayedEvent=null;detailRevision++;RefreshStatus();canvas.Map=null;events.ItemsSource=null;detail.Text="";
        try
        {
            var map=await Task.Run(()=>WolfMapCatalog.Read(exe,entry,lifetime.Token),lifetime.Token);
            if(IsDisposed||request!=generation)return;
            displayedEntry=entry;displayedMap=map;mapLoading=false;canvas.Map=map;events.ItemsSource=map.Events;RefreshStatus();
            if(map.Events.Length>0)events.SelectedIndex=0;
        }
        catch(OperationCanceledException){}catch(Exception e){if(!IsDisposed&&request==generation){mapLoading=false;errorMessage=SafeDiagnosticOutput.ExceptionSummary(e);RefreshStatus();}}
    }
    private void SelectEvent()
    {
        displayedEvent=events.SelectedItem as WolfTextCatalog.MapEvent;detailRevision++;
        if(displayedEvent is not {} item){detail.Text="";return;}canvas.Highlight=item.Id;
        RenderEvent(false);
    }
    private void RefreshLanguage(object? sender,EventArgs args)
    {
        if(IsDisposed)return;
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(()=>RefreshLanguage(sender,args));return;}
        RefreshStatus();RenderEvent(true);
    }
    private void RefreshStatus()
    {
        if(errorMessage is {} error){status.Text=UiLanguage.Text(error);return;}
        if(mapLoading){status.Text=UiLanguage.Text("正在读取地图…");return;}
        if(displayedEntry is {} entry&&displayedMap is {} map)
            status.Text=UiLanguage.Text("资源地图")+$" · {entry} · {map.Name} · {map.Width} × {map.Height} · "+UiLanguage.Format($"{map.Events.Length} 个事件");
        else status.Text=loadedMapCount is {} count?UiLanguage.Format($"已读取 {count} 张地图。"):"";
    }
    private void RenderEvent(bool preservePosition)
    {
        if(displayedEvent is not {} item)return;
        var lines=new List<string>();
        foreach(var page in item.Pages)
        {
            lines.Add(UiLanguage.Format($"第 {page.Index+1} 页"));
            foreach(var c in page.Commands.Take(2000))
                lines.Add($"{c.Index:000} · "+UiLanguage.Text(c.Code switch{101=>"对白",102=>"选项",111=>"条件分支",121=>"变量操作",122=>"文字变量",150=>"图片 / 文字",201=>"移动路线",250=>"数据库操作",_=>"指令"})+
                    $" [{c.Code}] "+string.Join(" / ",c.Texts.Select(t=>t.Replace("\n"," ↵ ")))+(c.Arguments.Length>0?"  ("+string.Join(", ",c.Arguments)+")":""));
            lines.Add("");
        }
        string text=string.Join("\n",lines);if(detail.Text==text)return;
        int start=detail.SelectionStart,length=detail.SelectionLength;double vertical=detail.VerticalOffset,horizontal=detail.HorizontalOffset;
        int revision=++detailRevision;
        detail.Text=text;
        if(!preservePosition)return;
        detail.Select(Math.Min(start,text.Length),Math.Min(length,Math.Max(0,text.Length-start)));
        // Restore after the translated lines have been measured. A later event
        // selection or disposal must not receive this old scroll position.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(()=>
        {
            if(IsDisposed||revision!=detailRevision||!ReferenceEquals(displayedEvent,item))return;
            detail.ScrollToHorizontalOffset(horizontal);detail.ScrollToVerticalOffset(vertical);
        }));
    }
    private sealed class MapCanvas : W.FrameworkElement
    {
        private WolfTextCatalog.MapStructure? map;private int highlight=-1;
        internal event Action<WolfTextCatalog.MapEvent>? Selected;
        internal WolfTextCatalog.MapStructure? Map {get=>map;set{map=value;InvalidateVisual();}}
        internal int Highlight {set{highlight=value;InvalidateVisual();}}
        private (double Cell,double X,double Y) View()=>map is null?(1,0,0):(Math.Max(.02,Math.Min((ActualWidth-24)/Math.Max(1,map.Width),(ActualHeight-24)/Math.Max(1,map.Height))),12,12);
        internal MapCanvas(){ClipToBounds=true;MinHeight=200;MouseLeftButtonDown+=(_,e)=>{if(map is null)return;var p=e.GetPosition(this);var v=View();var hit=map.Events.OrderBy(t=>Math.Pow(v.X+(t.X+.5)*v.Cell-p.X,2)+Math.Pow(v.Y+(t.Y+.5)*v.Cell-p.Y,2)).FirstOrDefault();if(hit is not null)Selected?.Invoke(hit);};}
        protected override void OnRender(WM.DrawingContext dc)
        {
            dc.DrawRectangle(WpfDrawing.Brush(UiTheme.Current.Control),null,new W.Rect(RenderSize));if(map is null)return;
            var v=View();var pen=new WM.Pen(WpfDrawing.Brush(UiTheme.Current.Border),.5);
            if(v.Cell>=4){for(int x=0;x<=map.Width;x++)dc.DrawLine(pen,new(v.X+x*v.Cell,v.Y),new(v.X+x*v.Cell,v.Y+map.Height*v.Cell));for(int y=0;y<=map.Height;y++)dc.DrawLine(pen,new(v.X,v.Y+y*v.Cell),new(v.X+map.Width*v.Cell,v.Y+y*v.Cell));}
            foreach(var e in map.Events.Where(e=>e.X>=0&&e.X<map.Width&&e.Y>=0&&e.Y<map.Height))dc.DrawEllipse(WpfDrawing.Brush(e.Id==highlight?Color.Orange:UiTheme.Current.Accent),null,new(v.X+(e.X+.5)*v.Cell,v.Y+(e.Y+.5)*v.Cell),Math.Clamp(v.Cell*.32,3,8),Math.Clamp(v.Cell*.32,3,8));
        }
    }
}
