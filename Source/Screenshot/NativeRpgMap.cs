using System.Text.Json;
using W = System.Windows;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
namespace ScreenshotTranslationUiTester;
internal sealed class NativeRpgMap : NativeRpgElement
{
    private sealed class MapCanvas : W.FrameworkElement
    {
        internal Action<WM.DrawingContext,W.Size>? Draw;
        protected override void OnRender(WM.DrawingContext dc)=>Draw?.Invoke(dc,RenderSize);
    }
    internal Font Font=SystemFonts.DefaultFont;
    private int DeviceDpi=>96;
    private Size ClientSize=>new((int)View.ActualWidth,(int)View.ActualHeight);
    internal bool Capture=>View.IsMouseCaptured;
    internal NativeRpgMap():base(new MapCanvas{MinHeight=260,ClipToBounds=true,Focusable=true})
    {
        var canvas=(MapCanvas)View;canvas.Draw=DrawNative;
        View.SizeChanged+=(_,_)=>{if(autoFit&&map is not null)Fit();};
        View.MouseWheel+=(_,e)=>{var p=e.GetPosition(View);ZoomAt(e.Delta>0?1.2f:1/1.2f,new((int)p.X,(int)p.Y));e.Handled=true;};
        View.MouseDown+=(_,e)=>
        {
            if(e.ChangedButton is WI.MouseButton.XButton1 or WI.MouseButton.XButton2)return;
            View.Focus();var p=e.GetPosition(View);
            if(e.ChangedButton==WI.MouseButton.Right){autoFit=false;dragging=true;dragFrom=new((int)p.X,(int)p.Y);View.CaptureMouse();View.Cursor=WI.Cursors.SizeAll;e.Handled=true;return;}
            if(e.ChangedButton!=WI.MouseButton.Left||map is null)return;
            int x=(int)Math.Floor((p.X-panX)/cell),y=(int)Math.Floor((p.Y-panY)/cell);
            if(x<0||y<0||x>=width||y>=height)return;SelectedTile=new(x,y);Invalidate();TileSelected?.Invoke(this,EventArgs.Empty);e.Handled=true;
        };
        View.MouseMove+=(_,e)=>{if(!dragging)return;var p=e.GetPosition(View);panX+=(float)p.X-dragFrom.X;panY+=(float)p.Y-dragFrom.Y;dragFrom=new((int)p.X,(int)p.Y);Invalidate();};
        View.MouseUp+=(_,e)=>{if(!dragging)return;dragging=false;View.ReleaseMouseCapture();View.Cursor=WI.Cursors.Arrow;e.Handled=true;};
        View.LostMouseCapture+=(_,_)=>{dragging=false;View.Cursor=WI.Cursors.Arrow;};
    }
    private JsonElement? map;


    private int terrainRevision;

    internal int TerrainBuilds {get;private set;}
    private int width,height;
    private PointF player=new(-1,-1);
    internal PointF PlayerPosition=>player;
    private int[] cells=[],kinds=[],terrain=[],regions=[];
    private JsonElement[] events=[];
    private float cell=18,panX,panY;
    private bool dragging,autoFit=true;
    private Point dragFrom;
    private readonly Dictionary<int,(float Cell,float X,float Y,Point? Selected,bool Auto)> views=[];
    internal int MapId=>map?.GetProperty("id").GetInt32()??0;
    internal Point? SelectedTile {get;private set;}
    internal event EventHandler? TileSelected;
    internal JsonElement[] SelectedEvents=>SelectedTile is { } p?events.Where(e=>TryPosition(e,out var position)&&position.X>=p.X&&position.X<p.X+1&&position.Y>=p.Y&&position.Y<p.Y+1).ToArray():[];
    internal string SelectionText=>SelectedTile is { } p?$"格子 ({p.X}, {p.Y}) · {(cells[p.Y*width+p.X]>0?"可通行":"阻挡")} · 事件 {SelectedEvents.Length}":"点击格子查看地形与事件";
    internal string TileDetails=>SelectedTile is { } p?$"地形标记 {terrain.ElementAtOrDefault(p.Y*width+p.X)} · 区域编号 {regions.ElementAtOrDefault(p.Y*width+p.X)}":"";
    
    internal void ClearMap(){map=null;SelectedTile=null;Invalidate();}
    internal void SetMap(JsonElement value)
    {
        int old=MapId;if(old!=0)views[old]=(cell,panX,panY,SelectedTile,autoFit);
        terrainRevision++;map=value.Clone();width=value.GetProperty("width").GetInt32();height=value.GetProperty("height").GetInt32();
        int[] Read(string key)=>value.TryGetProperty(key,out var a)?a.EnumerateArray().Select(x=>x.GetInt32()).ToArray():[];
        cells=Read("cells");kinds=Read("kinds");terrain=Read("terrain");regions=Read("regions");events=value.GetProperty("events").EnumerateArray().Select(e=>e.Clone()).ToArray();
        player=TryPosition(value,out var position)?position:new(-1,-1);
        if(width<=0||height<=0||cells.Length!=width*height){ClearMap();return;}
        if(views.TryGetValue(MapId,out var view)){cell=view.Cell;panX=view.X;panY=view.Y;SelectedTile=view.Selected;autoFit=view.Auto;if(autoFit)Fit();}else{SelectedTile=null;Fit();}
        if(SelectedTile is { } p&&(p.X>=width||p.Y>=height))SelectedTile=null;
        Invalidate();
    }
    internal bool UpdateLive(JsonElement value)
    {
        if(value.GetProperty("id").GetInt32()!=MapId)return false;
        var next=TryPosition(value,out var position)?position:new PointF(-1,-1);
        var incoming=value.GetProperty("events").EnumerateArray().Select(e=>e.Clone()).ToArray();
        bool changed=next!=player||!events.Select(e=>e.GetRawText()).SequenceEqual(incoming.Select(e=>e.GetRawText()));
        if(changed){player=next;events=incoming;Invalidate();}return changed;
    }
    internal void Fit()
    {
        autoFit=true;if(width<1||height<1)return;
        cell=Math.Clamp(Math.Min((ClientSize.Width-16f)/width,(ClientSize.Height-16f)/height),4*DeviceDpi/96f,36*DeviceDpi/96f);
        panX=(ClientSize.Width-width*cell)/2;panY=(ClientSize.Height-height*cell)/2;Invalidate();
    }
    private static bool TryPosition(JsonElement value,out PointF position)
    {
        position=default;
        if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty("x",out var x)||!value.TryGetProperty("y",out var y)||
            x.ValueKind!=JsonValueKind.Number||y.ValueKind!=JsonValueKind.Number||!x.TryGetSingle(out var px)||!y.TryGetSingle(out var py)||
            !float.IsFinite(px)||!float.IsFinite(py))return false;
        // Half-step/pixel movement plugins legitimately use fractional tile
        // coordinates. Keep them for display; never round the game's data.
        position=new(px,py);return true;
    }
    internal void Zoom(float factor)=>ZoomAt(factor,new((int)(Width/2),(int)(Height/2)));
    private void ZoomAt(float factor,Point anchor)
    {
        autoFit=false;float next=Math.Clamp(cell*factor,4*DeviceDpi/96f,72*DeviceDpi/96f),ratio=next/cell;
        panX=anchor.X-(anchor.X-panX)*ratio;panY=anchor.Y-(anchor.Y-panY)*ratio;cell=next;Invalidate();
    }
    private void DrawNative(WM.DrawingContext dc, W.Size size)
    {
        var p = UiTheme.Current; dc.DrawRectangle(WpfDrawing.Brush(p.Secondary), null, new(size));
        if (map is null) { WpfCanvasDrawing.Text(dc, UiLanguage.Text("开启修改后选择地图"), Font, p.SecondaryText, new(20, 20, Math.Max(1, size.Width - 40), 40)); return; }
        int x0 = Math.Max(0, (int)Math.Floor(-panX / cell)), y0 = Math.Max(0, (int)Math.Floor(-panY / cell));
        int x1 = Math.Min(width, (int)Math.Ceiling((size.Width - panX) / cell)), y1 = Math.Min(height, (int)Math.Ceiling((size.Height - panY) / cell)); bool dark = p.Main.GetBrightness() < .4f;
        var ground = WpfDrawing.Brush(dark ? Color.FromArgb(54, 93, 84) : Color.FromArgb(202, 229, 218));
        var block = WpfDrawing.Brush(dark ? Color.FromArgb(39, 46, 59) : Color.FromArgb(207, 214, 225));
        var water = WpfDrawing.Brush(dark ? Color.FromArgb(39, 87, 128) : Color.FromArgb(133, 195, 236));
        var tagged = WpfDrawing.Brush(dark ? Color.FromArgb(92, 68, 123) : Color.FromArgb(210, 186, 229));
        var line = WpfCanvasDrawing.Pen(dark ? Color.FromArgb(96, 113, 134) : Color.FromArgb(145, 168, 191));
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = y * width + x, kind = kinds.ElementAtOrDefault(i); dc.DrawRectangle(kind == 2 ? water : kind == 3 ? tagged : cells[i] > 0 ? ground : block, null, new(panX + x * cell, panY + y * cell, cell, cell)); }
        for (int x = x0; x <= x1; x++) dc.DrawLine(line, new(panX + x * cell, panY + y0 * cell), new(panX + x * cell, panY + y1 * cell));
        for (int y = y0; y <= y1; y++) dc.DrawLine(line, new(panX + x0 * cell, panY + y * cell), new(panX + x1 * cell, panY + y * cell));
        foreach (var ev in events)
        {
            if (!TryPosition(ev, out var position) || position.X < x0 || position.X >= x1 || position.Y < y0 || position.Y >= y1) continue;
            var r = new W.Rect(panX + position.X * cell + 2, panY + position.Y * cell + 2, Math.Max(3, cell - 4), Math.Max(3, cell - 4)); dc.DrawEllipse(WpfDrawing.Brush(Color.FromArgb(234, 142, 39)), null, new(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
            if (cell >= 24 * DeviceDpi / 96f && ev.TryGetProperty("id", out var id)) WpfCanvasDrawing.Text(dc, id.ToString(), Font, Color.FromArgb(44, 28, 8), r);
        }
        if (player.X >= 0 && player.Y >= 0)
        {
            var shape = new WM.StreamGeometry(); using (var path = shape.Open()) { path.BeginFigure(new(panX + (player.X + .5f) * cell, panY + player.Y * cell + 1), true, true); path.LineTo(new(panX + (player.X + 1) * cell - 1, panY + (player.Y + 1) * cell - 1), true, false); path.LineTo(new(panX + player.X * cell + 1, panY + (player.Y + 1) * cell - 1), true, false); } shape.Freeze(); dc.DrawGeometry(WpfDrawing.Brush(Color.FromArgb(0, 120, 255)), null, shape);
        }
        if (SelectedTile is { } selected) dc.DrawRectangle(null, WpfCanvasDrawing.Pen(p.Accent, 2), new(panX + selected.X * cell, panY + selected.Y * cell, cell, cell));
    }
}
