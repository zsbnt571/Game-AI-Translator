using System.Drawing.Drawing2D;
using System.Text.Json;
namespace ScreenshotTranslationUiTester;

internal sealed class RpgMapGrid:Control
{
    private JsonElement? map;
    private Bitmap? terrainLayer;
    private string terrainSignature="";
    private int terrainRevision;
    private long lastResize;
    internal int TerrainBuilds {get;private set;}
    private int width,height;
    private Point player=new(-1,-1);
    internal Point PlayerPosition=>player;
    private int[] cells=[],kinds=[],terrain=[],regions=[];
    private JsonElement[] events=[];
    private float cell=18,panX,panY;
    private bool dragging,autoFit=true;
    private Point dragFrom;
    private readonly Dictionary<int,(float Cell,float X,float Y,Point? Selected,bool Auto)> views=[];
    internal int MapId=>map?.GetProperty("id").GetInt32()??0;
    internal Point? SelectedTile {get;private set;}
    internal event EventHandler? TileSelected;
    internal JsonElement[] SelectedEvents=>SelectedTile is { } p?events.Where(e=>e.GetProperty("x").GetInt32()==p.X&&e.GetProperty("y").GetInt32()==p.Y).ToArray():[];
    internal string SelectionText=>SelectedTile is { } p?$"格子 ({p.X}, {p.Y}) · {(cells[p.Y*width+p.X]>0?"可通行":"阻挡")} · 事件 {SelectedEvents.Length}":"点击格子查看地形与事件";
    internal string TileDetails=>SelectedTile is { } p?$"地形标记 {terrain.ElementAtOrDefault(p.Y*width+p.X)} · 区域编号 {regions.ElementAtOrDefault(p.Y*width+p.X)}":"";
    internal RpgMapGrid(){DoubleBuffered=true;ResizeRedraw=true;TabStop=true;SetStyle(ControlStyles.Selectable|ControlStyles.Opaque|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
    internal void ClearMap(){map=null;SelectedTile=null;Invalidate();}
    internal void SetMap(JsonElement value)
    {
        int old=MapId;if(old!=0)views[old]=(cell,panX,panY,SelectedTile,autoFit);
        terrainRevision++;map=value.Clone();width=value.GetProperty("width").GetInt32();height=value.GetProperty("height").GetInt32();
        int[] Read(string key)=>value.TryGetProperty(key,out var a)?a.EnumerateArray().Select(x=>x.GetInt32()).ToArray():[];
        cells=Read("cells");kinds=Read("kinds");terrain=Read("terrain");regions=Read("regions");events=value.GetProperty("events").EnumerateArray().Select(e=>e.Clone()).ToArray();
        player=new(value.GetProperty("x").GetInt32(),value.GetProperty("y").GetInt32());
        if(width<=0||height<=0||cells.Length!=width*height){ClearMap();return;}
        if(views.TryGetValue(MapId,out var view)){cell=view.Cell;panX=view.X;panY=view.Y;SelectedTile=view.Selected;autoFit=view.Auto;if(autoFit)Fit();}else{SelectedTile=null;Fit();}
        if(SelectedTile is { } p&&(p.X>=width||p.Y>=height))SelectedTile=null;
        Invalidate();
    }
    internal bool UpdateLive(JsonElement value)
    {
        if(value.GetProperty("id").GetInt32()!=MapId)return false;
        var next=new Point(value.GetProperty("x").GetInt32(),value.GetProperty("y").GetInt32());
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
    internal void Zoom(float factor)=>ZoomAt(factor,new(Width/2,Height/2));
    private void ZoomAt(float factor,Point anchor)
    {
        autoFit=false;float next=Math.Clamp(cell*factor,4*DeviceDpi/96f,72*DeviceDpi/96f),ratio=next/cell;
        panX=anchor.X-(anchor.X-panX)*ratio;panY=anchor.Y-(anchor.Y-panY)*ratio;cell=next;Invalidate();
    }
    protected override void OnSizeChanged(EventArgs e){lastResize=Environment.TickCount64;base.OnSizeChanged(e);if(autoFit&&map is not null)Fit();}
    protected override void OnMouseWheel(MouseEventArgs e){if(e is HandledMouseEventArgs h)h.Handled=true;ZoomAt(e.Delta>0?1.2f:1/1.2f,e.Location);}
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);Focus();
        if(e.Button==MouseButtons.Right){autoFit=false;dragging=true;dragFrom=e.Location;Capture=true;Cursor=Cursors.SizeAll;return;}
        if(e.Button!=MouseButtons.Left||map is null)return;
        int x=(int)Math.Floor((e.X-panX)/cell),y=(int)Math.Floor((e.Y-panY)/cell);
        if(x<0||y<0||x>=width||y>=height)return;SelectedTile=new(x,y);Invalidate();TileSelected?.Invoke(this,EventArgs.Empty);
    }
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging){panX+=e.X-dragFrom.X;panY+=e.Y-dragFrom.Y;dragFrom=e.Location;Invalidate();}}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(dragging){dragging=false;Capture=false;Cursor=Cursors.Default;}}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture){dragging=false;Cursor=Cursors.Default;}}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var palette=UiTheme.Current;var g=e.Graphics;g.Clear(palette.Secondary);
        if(map is not { } m){TextRenderer.DrawText(g,"开启修改后选择地图",Font,ClientRectangle,palette.SecondaryText,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);return;}
        int x0=Math.Max(0,(int)Math.Floor(-panX/cell)),y0=Math.Max(0,(int)Math.Floor(-panY/cell));
        int x1=Math.Min(width,(int)Math.Ceiling((Width-panX)/cell)),y1=Math.Min(height,(int)Math.Ceiling((Height-panY)/cell));
        string signature=$"{terrainRevision}|{Width}|{Height}|{cell}|{panX}|{panY}|{palette}";
        if(Environment.TickCount64-lastResize<120)
            DrawTerrain(g,palette,x0,y0,x1,y1); // Don't allocate a full bitmap for every resize step.
        else
        {
            if(terrainLayer is null||terrainSignature!=signature)
            {
                terrainLayer?.Dispose();terrainLayer=new Bitmap(Math.Max(1,Width),Math.Max(1,Height));terrainSignature=signature;TerrainBuilds++;
                using var layer=Graphics.FromImage(terrainLayer);DrawTerrain(layer,palette,x0,y0,x1,y1);
            }
            g.DrawImageUnscaled(terrainLayer,0,0);
        }
        g.SmoothingMode=SmoothingMode.AntiAlias;
        using var eventBrush=new SolidBrush(Color.FromArgb(234,142,39));
        using var playerBrush=new SolidBrush(Color.FromArgb(0,120,255));
        foreach(var ev in events)
        {
            int x=ev.GetProperty("x").GetInt32(),y=ev.GetProperty("y").GetInt32();if(x<x0||x>=x1||y<y0||y>=y1)continue;
            var r=new RectangleF(panX+x*cell+2,panY+y*cell+2,Math.Max(3,cell-4),Math.Max(3,cell-4));g.FillEllipse(eventBrush,r);
            if(cell>=24*DeviceDpi/96f)TextRenderer.DrawText(g,ev.GetProperty("id").ToString(),Font,Rectangle.Round(r),Color.FromArgb(44,28,8),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
        }
        int px=player.X,py=player.Y;
        if(px>=0&&py>=0)g.FillPolygon(playerBrush,new PointF[]{new(panX+(px+.5f)*cell,panY+py*cell+1),new(panX+(px+1)*cell-1,panY+(py+1)*cell-1),new(panX+px*cell+1,panY+(py+1)*cell-1)});
        if(SelectedTile is { } tile){using var pen=new Pen(palette.Accent,Math.Max(2,DeviceDpi/48f));g.DrawRectangle(pen,panX+tile.X*cell,panY+tile.Y*cell,cell,cell);}
    }
    protected override void OnPaintBackground(PaintEventArgs e){}
    protected override void Dispose(bool disposing){if(disposing)terrainLayer?.Dispose();base.Dispose(disposing);}
    private void DrawTerrain(Graphics g,ThemePalette palette,int x0,int y0,int x1,int y1)
    {
        g.Clear(palette.Secondary);
        bool dark=palette.Main.GetBrightness()<.4f;
        using var ground=new SolidBrush(dark?Color.FromArgb(54,93,84):Color.FromArgb(202,229,218));
        using var block=new SolidBrush(dark?Color.FromArgb(39,46,59):Color.FromArgb(207,214,225));
        using var water=new SolidBrush(dark?Color.FromArgb(39,87,128):Color.FromArgb(133,195,236));
        using var tagged=new SolidBrush(dark?Color.FromArgb(92,68,123):Color.FromArgb(210,186,229));
        using var line=new Pen(dark?Color.FromArgb(96,113,134):Color.FromArgb(145,168,191),Math.Max(1,DeviceDpi/96f));
        for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++)
        {
            int i=y*width+x,kind=kinds.ElementAtOrDefault(i);
            g.FillRectangle(kind==2?water:kind==3?tagged:cells[i]>0?ground:block,panX+x*cell,panY+y*cell,cell,cell);
            if(kind>=2&&cells[i]==0&&cell>=12){using var ink=new Pen(Color.FromArgb(100,palette.Text));g.DrawLine(ink,panX+x*cell+3,panY+y*cell+3,panX+(x+1)*cell-3,panY+(y+1)*cell-3);}
        }
        for(int x=x0;x<=x1;x++)g.DrawLine(line,panX+x*cell,panY+y0*cell,panX+x*cell,panY+y1*cell);
        for(int y=y0;y<=y1;y++)g.DrawLine(line,panX+x0*cell,panY+y*cell,panX+x1*cell,panY+y*cell);
    }

}
