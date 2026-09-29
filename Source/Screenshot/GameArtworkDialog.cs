using System.Drawing.Drawing2D;
using System.Net.Http;

namespace ScreenshotTranslationUiTester;

internal sealed class ArtworkCropPreview:Control
{
    private Bitmap? image;
    internal double Aspect {get;set;}=16d/9;
    private float zoom=1,centerX=.5f,centerY=.5f;
    private Point? dragging;
    internal ArtworkCropPreview(){DoubleBuffered=true;TabStop=true;Cursor=Cursors.Hand;AccessibleName="图片裁剪预览，可拖动位置并用滚轮缩放";}
    internal void SetImage(Bitmap? value){var old=image;image=value;old?.Dispose();zoom=1;centerX=centerY=.5f;Invalidate();}
    internal bool HasImage=>image is not null;
    internal RectangleF CropRectangle()
    {
        if(image is null)return RectangleF.Empty;double aspect=Aspect>0?Aspect:(double)image.Width/image.Height;
        float w=image.Width,h=(float)(w/aspect);if(h>image.Height){h=image.Height;w=(float)(h*aspect);}w/=zoom;h/=zoom;
        return new(Math.Clamp(image.Width*centerX-w/2,0,image.Width-w),Math.Clamp(image.Height*centerY-h/2,0,image.Height-h),w,h);
    }
    internal Bitmap Export(int maxSide)
    {
        if(image is null)throw new InvalidOperationException("请先选择图片。");var crop=CropRectangle();var scale=Math.Min(1f,maxSide/Math.Max(crop.Width,crop.Height));
        var result=new Bitmap(Math.Max(1,(int)(crop.Width*scale)),Math.Max(1,(int)(crop.Height*scale)));
        using var g=Graphics.FromImage(result);g.Clear(UiTheme.Current.Secondary);g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(image,new Rectangle(0,0,result.Width,result.Height),crop,GraphicsUnit.Pixel);return result;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var p=UiTheme.Current;e.Graphics.Clear(p.Control);var bounds=Rectangle.Inflate(ClientRectangle,-12,-12);
        if(image is null){WorkspaceDrawing.Text(e.Graphics,"选择图片后在这里预览",Font,bounds,p.SecondaryText,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);return;}
        var crop=CropRectangle();var dest=WorkspaceDrawing.Fit(new(Math.Max(1,(int)crop.Width),Math.Max(1,(int)crop.Height)),bounds);
        e.Graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;e.Graphics.DrawImage(image,dest,crop,GraphicsUnit.Pixel);using var pen=new Pen(p.Accent,2);e.Graphics.DrawRectangle(pen,dest);
    }
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left){Focus();dragging=e.Location;Capture=true;}}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging is not { } old||image is null)return;centerX=Math.Clamp(centerX-(e.X-old.X)/(float)Math.Max(1,Width)/zoom,0,1);centerY=Math.Clamp(centerY-(e.Y-old.Y)/(float)Math.Max(1,Height)/zoom,0,1);dragging=e.Location;Invalidate();}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragging=null;Capture=false;}
    protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);zoom=Math.Clamp(zoom+(e.Delta>0?.15f:-.15f),1,5);Invalidate();}
    protected override void Dispose(bool disposing){if(disposing)image?.Dispose();base.Dispose(disposing);}
}

internal sealed class GameArtworkDialog:Form
{
    private readonly RecentGameStore store;
    private readonly string gamePath;
    private readonly CancellationTokenSource lifetime=new();
    private readonly ComboBox kind=new GamePlanBox(){DropDownStyle=ComboBoxStyle.DropDownList,Width=110};
    private readonly ComboBox source=new GamePlanBox(){DropDownStyle=ComboBoxStyle.DropDownList,Width=220};
    private readonly ComboBox ratio=new GamePlanBox(){DropDownStyle=ComboBoxStyle.DropDownList,Width=160};
    private readonly TextBox query=new(){Width=198,PlaceholderText="游戏名称"};
    private readonly ListBox matches=new(){Height=108,Width=300,DisplayMember="Name"};
    private readonly ArtworkCropPreview preview=new(){Dock=DockStyle.Fill};
    private readonly Label status=new(){AutoSize=true,MaximumSize=new(520,0)};
    private readonly Label captureAvailability=new(){AutoSize=true,MaximumSize=new(290,0),Name="CoverCaptureAvailability"};
    private Button captureButton=null!;
    private readonly Button save=new GameActionButton("保存图片"){Primary=true};
    private readonly List<Control> busyControls=[];
    private bool loading,busy,dirty;
    private string origin="file";
    private bool Cover=>kind.SelectedIndex==0;
    private RecentGame Current=>store.Find(gamePath)??throw new IOException("该游戏已从游戏库移除。");
    internal GameArtworkDialog(RecentGameStore store,RecentGame game,ApiSettings appearance)
    {
        this.store=store;gamePath=game.ExePath;SuspendLayout();
        Text="图标与封面 · "+game.Name;StartPosition=FormStartPosition.CenterParent;ClientSize=new(940,670);MinimumSize=new(840,650);AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new(96,96);Font=new Font("Microsoft YaHei UI",10);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Padding=new(18)};layout.ColumnStyles.Add(new(SizeType.Absolute,330));layout.ColumnStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.AutoSize));
        var controls=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new(0,0,16,0)};
        void Label(string value)=>controls.Controls.Add(new Label{Text=value,AutoSize=true,Margin=new(0,6,0,4)});
        void Row(params Control[] items)
        {
            var row=new FlowLayoutPanel{AutoSize=true,WrapContents=true,MaximumSize=new(306,0),Margin=new(0,0,0,6)};
            row.Controls.AddRange(items);controls.Controls.Add(row);
            void SizeRow()=>row.MinimumSize=new(0,items.Max(x=>x.Height+x.Margin.Vertical));
            foreach(var item in items)item.SizeChanged+=(_,_)=>SizeRow();SizeRow();
        }
        Button Action(string text,Func<Task> action){var b=new GameActionButton(text);b.Click+=async(_,_)=>await Run(action);busyControls.Add(b);return b;}
        kind.Items.AddRange(["封面","图标"]);source.Items.AddRange(["跟随全局默认","本地自动","网上匹配"]);ratio.Items.AddRange(["原始比例","横版 16:9","竖版 2:3","正方形"]);
        Label("编辑图片");Row(kind);Label("默认来源");Row(source);
        var apply=Action("应用来源",ApplySourceAsync);Row(apply);Label("手动选择");
        captureButton=Action("截取游戏画面",CaptureAsync);captureButton.Name="CoverCaptureAction";
        Row(Action("从文件选择",ChooseFileAsync),captureButton);controls.Controls.Add(captureAvailability);RefreshCaptureAvailability();
        Label("网上查找 · Steam 图片");query.Text=game.Name;Row(query,Action("查找",SearchAsync));controls.Controls.Add(matches);
        Row(Action("预览所选图片",LoadMatchAsync));
        var picture=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};picture.ColumnStyles.Add(new(SizeType.Percent,100));picture.RowStyles.Add(new(SizeType.Percent,100));picture.RowStyles.Add(new(SizeType.AutoSize));picture.RowStyles.Add(new(SizeType.AutoSize));
        picture.Controls.Add(preview,0,0);var cropRow=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,Margin=new(0,10,0,4)};cropRow.Controls.Add(new Label{Text="裁剪比例",AutoSize=true,Margin=new(0,8,8,0)});cropRow.Controls.Add(ratio);picture.Controls.Add(cropRow,0,1);
        ratio.SizeChanged+=(_,_)=>cropRow.MinimumSize=new(0,ratio.Height+ratio.Margin.Vertical);
        cropRow.MinimumSize=new(0,ratio.Height+ratio.Margin.Vertical);
        picture.Controls.Add(new Label{Text="拖动调整位置，滚轮放大。手动保存后不会被自动覆盖。",AutoSize=true,Dock=DockStyle.Fill,Margin=new(0,4,0,8)},0,2);
        layout.Controls.Add(controls,0,0);layout.Controls.Add(picture,1,0);
        var footer=new TableLayoutPanel{ColumnCount=2,AutoSize=true,Dock=DockStyle.Fill,Padding=new(0,12,0,0)};footer.ColumnStyles.Add(new(SizeType.Percent,100));footer.ColumnStyles.Add(new(SizeType.AutoSize));footer.Controls.Add(status,0,0);
        var buttons=new FlowLayoutPanel{AutoSize=true,WrapContents=false};var cancel=new GameActionButton("关闭");cancel.Click+=(_,_)=>Close();buttons.Controls.Add(save);buttons.Controls.Add(cancel);footer.Controls.Add(buttons,1,0);layout.Controls.Add(footer,0,1);layout.SetColumnSpan(footer,2);
        Controls.Add(layout);CancelButton=cancel;save.Click+=async(_,_)=>await Run(SaveAsync);
        busyControls.AddRange([kind,source,ratio,query,matches,save]);kind.SelectedIndexChanged+=(_,_)=>LoadCurrent();ratio.SelectedIndexChanged+=(_,_)=>{preview.Aspect=ratio.SelectedIndex switch{1=>16d/9,2=>2d/3,3=>1,_=>0};preview.Invalidate();};
        matches.SelectedIndexChanged+=async(_,_)=>{if(!busy)await Run(LoadMatchAsync);};
        Shown+=(_,_)=>{loading=true;kind.SelectedIndex=0;loading=false;LoadCurrent();};
        FormClosing+=(_,e)=>{if(dirty&&!busy&&MessageBox.Show(this,"未保存的裁剪将放弃，继续关闭？","图标与封面",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)e.Cancel=true;};
        FormClosed+=(_,_)=>lifetime.Cancel();UiTheme.Apply(this,appearance);MainForm.ConfigureFusionInputs(this);
        float scale=DeviceDpi/96f;layout.Scale(new SizeF(scale,scale));ClientSize=new((int)(940*scale),(int)(670*scale));MinimumSize=new((int)(840*scale),(int)(650*scale));AutoScaleDimensions=CurrentAutoScaleDimensions;ResumeLayout(true);
    }
    private void LoadCurrent()
    {
        if(loading)return;var game=Current;dirty=false;origin="file";
        source.SelectedIndex=(Cover?game.CoverPreference:game.IconPreference) switch{ArtworkSource.Automatic=>1,ArtworkSource.Online=>2,_=>0};
        ratio.SelectedIndex=Cover?0:3;preview.SetImage(store.ReadImage(game,Cover));
        status.Text=(Cover?game.CoverManual:game.IconManual)?"图片已锁定。应用默认来源后才会恢复自动更新。":"当前图片来自："+((Cover?game.CoverOrigin:game.IconOrigin) switch{"online"=>"网上匹配","file"=>"本地文件","screenshot"=>"游戏截图","preserved"=>"已保留的旧版图片",_=>"本地自动"});
    }
    private async Task Run(Func<Task> action)
    {
        if(busy)return;busy=true;foreach(var c in busyControls)c.Enabled=false;status.Text="正在处理…";
        try{await action();}catch(OperationCanceledException){}
        catch(ArtworkUnavailableException ex){if(!IsDisposed)status.Text=ex.Message;}
        catch(HttpRequestException ex){if(!IsDisposed)status.Text="网上图片暂时无法读取"+(ex.StatusCode is { } code?"（HTTP "+(int)code+"）":"")+"，原图已保留，请稍后重试。";}
        catch(Exception ex){if(!IsDisposed)status.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{busy=false;if(!IsDisposed){foreach(var c in busyControls)c.Enabled=true;RefreshCaptureAvailability();}}
    }
    private void RefreshCaptureAvailability()
    {
        bool available=GameWindowCover.Available;
        captureButton.Enabled=!busy&&available;captureAvailability.Visible=!available;
        captureAvailability.Text=available?"":GameWindowCover.MissingMessage;
    }
    private Task ChooseFileAsync()
    {
        using var dialog=new OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.bmp;*.ico",Title=Cover?"选择封面":"选择图标"};
        if(dialog.ShowDialog(this)!=DialogResult.OK){status.Text="已取消，原图片保留。";return Task.CompletedTask;}
        if(new FileInfo(dialog.FileName).Length>24_000_000)throw new IOException("请选择小于 24 MB 的图片。");
        using var image=Image.FromFile(dialog.FileName);
        if(image.Width>8192||image.Height>8192||(long)image.Width*image.Height>32_000_000)throw new IOException("图片尺寸过大。");
        preview.SetImage(GameWindowCover.Thumbnail(image,1280,1280));origin="file";dirty=true;status.Text="可调整裁剪，点击保存后替换。";return Task.CompletedTask;
    }
    private async Task SearchAsync()
    {
        var result=await new GameArtworkService().SearchAsync(query.Text,lifetime.Token);if(IsDisposed)return;
        matches.Items.Clear();matches.Items.AddRange(result);if(result.Length>0)matches.SelectedIndex=0;
        status.Text=result.Length==0?"未找到匹配，可使用本地图片或游戏截图。":"选择候选即可预览，保存后才会替换图片。";
        if(result.Length>0)await LoadMatchAsync();
    }
    private async Task LoadMatchAsync()
    {
        if(matches.SelectedItem is not ArtworkMatch match){status.Text="请先查找并选择一个游戏。";return;}
        var image=await new GameArtworkService().GetImageAsync(match,Cover,ratio.SelectedIndex==2,lifetime.Token);
        if(IsDisposed){image.Dispose();return;}preview.SetImage(image);origin="online";dirty=true;status.Text="网络候选："+match.Name+"。保存后固定保留。";
    }
    private async Task CaptureAsync()
    {
        var result=await GameWindowCover.CaptureAsync(gamePath,false,lifetime.Token,_=>{});
        if(IsDisposed){result.Image?.Dispose();return;}
        if(result.Image is null){status.Text=result.Message;return;}
        preview.SetImage(result.Image);origin="screenshot";dirty=true;status.Text="已取得游戏画面，可裁剪后保存。";
    }
    private Task SaveAsync()
    {
        using var image=preview.Export(Cover?1280:256);var game=Current;
        if(!store.SaveImage(game,image,Cover,null,lifetime.Token,manual:true,origin:origin))throw new IOException("图片记录已改变，请重新选择。");
        dirty=false;status.Text="图片已保存并锁定，自动任务不会覆盖。";return Task.CompletedTask;
    }
    private async Task ApplySourceAsync()
    {
        var preference=source.SelectedIndex switch{1=>(ArtworkSource?)ArtworkSource.Automatic,2=>ArtworkSource.Online,_=>null};
        var game=Current;store.SetImagePreference(game.Id,Cover,preference);dirty=false;
        var prefs=LibraryPreferencesStore.Load();var effective=preference??(Cover?prefs.CoverSource:prefs.IconSource);
        if(effective==ArtworkSource.Online){await SearchAsync();if(!IsDisposed)status.Text="已恢复网上匹配；请确认候选，原图片暂时保留。";return;}
        if(Cover)
        {
            var captured=await GameWindowCover.CaptureAsync(gamePath,true,lifetime.Token,_=>{});using var image=captured.Image;
            if(image is null){if(!IsDisposed)status.Text="已恢复自动截图；"+captured.Message+"。原图片保留。";return;}
            var current=Current;if(!store.SaveImage(current,image,true,null,lifetime.Token,()=>captured.Target is not null&&GameLibraryWindowIdentity.Matches(captured.Target)))throw new IOException("游戏窗口改变，原封面保留。");
        }
        else{var current=Current;using var image=await Task.Run(()=>store.IconFor(current,lifetime.Token),lifetime.Token);}
        if(!IsDisposed){LoadCurrent();status.Text="已应用本地自动来源。";}
    }
}

public sealed partial class MainForm
{
    private readonly HashSet<string> _artworkAttempted=new(StringComparer.OrdinalIgnoreCase);
    private async Task EditGameArtworkAsync()
    {
        if(_selectedGame is not { } game)return;
        try{await _rememberSelectedTask;var store=await RecentStore();if(IsDisposed||store.Find(game.ExePath)is not { } record)return;
            using var dialog=new GameArtworkDialog(store,record,_appliedDesktopSettings);dialog.ShowDialog(this);
            if(!IsDisposed){if(SameGame(game.ExePath,_selectedGame?.ExePath))ReplaceGameIcon(store.ReadImage(record,false));ScheduleLibraryRefresh();}}
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
    }
    private async Task FillDefaultArtworkAsync(IEnumerable<string> paths)
    {
        try
        {
            var store=await RecentStore();
            foreach(var path in paths)
            {
                if(_desktopLifetime.IsCancellationRequested)return;
                if(store.Find(path)is not { } game)continue;
                bool cover=!game.CoverManual&&(game.CoverPreference??_libraryPreferences.CoverSource)==ArtworkSource.Online&&game.CoverOrigin!="online";
                bool icon=!game.IconManual&&(game.IconPreference??_libraryPreferences.IconSource)==ArtworkSource.Online&&game.IconOrigin!="online";
                if(!cover&&!icon||!_artworkAttempted.Add(game.Id+":"+game.Name))continue;
                try
                {
                    var service=new GameArtworkService();var matches=await service.SearchAsync(game.Name,_desktopLifetime.Token);
                    var exact=matches.Where(x=>GameArtworkService.MatchKey(x.Name)==GameArtworkService.MatchKey(game.Name)).ToArray();
                    if(exact.Length!=1)continue;
                    foreach(bool isCover in new[]{true,false})
                    {
                        if(isCover?!cover:!icon)continue;var current=store.Find(path);if(current is null)continue;
                        try{using var image=await service.GetImageAsync(exact[0],isCover,isCover&&_libraryPreferences.Portrait,_desktopLifetime.Token);store.SaveImage(current,image,isCover,null,_desktopLifetime.Token,()=>((isCover?current.CoverPreference:current.IconPreference)??(isCover?_libraryPreferences.CoverSource:_libraryPreferences.IconSource))==ArtworkSource.Online,origin:"online");}
                        catch(HttpRequestException){} // Missing assets retain the previous local image.
                    }
                }
                catch(OperationCanceledException){return;}catch(Exception ex){AppLog.Write("artwork",SafeDiagnosticOutput.ExceptionSummary(ex));}
            }
        }
        catch(OperationCanceledException){}catch(Exception ex){if(!IsDisposed)AppLog.Write("artwork",SafeDiagnosticOutput.ExceptionSummary(ex));}
    }
}
