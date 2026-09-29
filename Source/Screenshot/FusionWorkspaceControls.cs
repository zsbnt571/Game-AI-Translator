using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester;

internal static class WorkspaceDrawing
{
    internal static GraphicsPath Round(Rectangle r,int radius)
    {
        var p=new GraphicsPath();int d=Math.Min(radius*2,Math.Min(r.Width,r.Height));
        if(d<2){p.AddRectangle(r);return p;}
        p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);
        p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
    internal static void Text(Graphics g,string text,Font font,Rectangle r,Color color,TextFormatFlags flags=TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis)
        =>TextRenderer.DrawText(g,text,font,r,color,flags|TextFormatFlags.NoPrefix);
    internal static Rectangle Fit(Size image,Rectangle box)
    {
        double k=Math.Min((double)box.Width/Math.Max(1,image.Width),(double)box.Height/Math.Max(1,image.Height));
        int w=(int)(image.Width*k),h=(int)(image.Height*k);return new(box.X+(box.Width-w)/2,box.Y+(box.Height-h)/2,w,h);
    }
}

internal sealed record LibraryTile(RecentGame Game,LibraryAnnotation Annotation,string Engine,Bitmap? Cover,Bitmap? Icon):IDisposable
{
    public void Dispose(){Cover?.Dispose();Icon?.Dispose();}
}

internal sealed partial class GameLibraryGrid:Control
{
    private LibraryTile[] tiles=[];
    private string? selected;
    private int hover=-1;
    private HashSet<string> runningPaths=new(StringComparer.OrdinalIgnoreCase);
    internal void UpdateRunning(IEnumerable<string> paths){var next=paths.ToHashSet(StringComparer.OrdinalIgnoreCase);if(!runningPaths.SetEquals(next)){runningPaths=next;Invalidate();}}
    internal bool ShelfMode {get;set;}
    internal bool Portrait {get;set;}=true;
    internal int CoverWidth {get;set;}=158;
    internal bool ListMode {get;private set;}
    internal void SetListMode(bool value){ListMode=value;scrollOffset=Point.Empty;Reflow();Invalidate();}
    internal event Action<RecentGame>? GameSelected;
    internal event Action<RecentGame>? GameActivated;
    internal event Action<RecentGame,Point>? ContextRequested;
    internal string EmptyText {get;set;}="把游戏拖到这里\n或点击右上角“导入游戏”";
    internal GameLibraryGrid(){TabStop=true;AccessibleName="游戏库";AccessibleRole=AccessibleRole.List;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable|ControlStyles.StandardClick|ControlStyles.StandardDoubleClick,true);}
    internal void SetTiles(LibraryTile[] next,string? selectedPath){var old=tiles;tiles=next;selected=selectedPath;selection.IntersectWith(next.Select(t=>t.Game.Id));if(!selecting&&old.Length==0&&selection.Count==0&&next.FirstOrDefault(t=>SamePath(t.Game.ExePath,selectedPath)) is {} chosen)selection.Add(chosen.Game.Id);foreach(var tile in old)tile.Dispose();Reflow();Invalidate();}
    internal void SelectPath(string? path){if(SamePath(selected,path))return;selected=path;if(tiles.FirstOrDefault(t=>SamePath(t.Game.ExePath,path)) is {} tile&&!selection.Contains(tile.Game.Id)){selection.Clear();selection.Add(tile.Game.Id);}Invalidate();}
    private int S(int n)=>(int)Math.Round(n*DeviceDpi/96f);
    private int Columns=>ListMode?1:ShelfMode?Math.Max(1,tiles.Length):Math.Max(1,(ClientSize.Width-S(12))/S((Portrait?CoverWidth:255)+14));
    private Size Cell {get{int width=ShelfMode?S(212):Math.Max(S(100),(ClientSize.Width-S(12)*(Columns-1)-S(4)-S(12))/Columns);return new(width,ListMode?Math.Max(S(94),Font.Height*3+S(32)):(int)(width*(Portrait&&!ShelfMode?1.24f:.52f))+Math.Max(S(52),Font.Height*2+S(12)));}}
    private int ShelfContentWidth=>S(4)+tiles.Length*Cell.Width+Math.Max(0,tiles.Length-1)*S(12);
    internal int ShelfContentHeight=>tiles.Length==0?S(64):Cell.Height+S(6)+(ShelfContentWidth>ClientSize.Width?S(12):0);
    private Rectangle BoundsFor(int i){var c=Cell;return new(S(2)+(i%Columns)*(c.Width+S(12))-scrollOffset.X,S(3)+(i/Columns)*(c.Height+S(12))-scrollOffset.Y,c.Width,c.Height);}
    private void Reflow(){UpdateScrollRange();Invalidate();}
    protected override void OnResize(EventArgs e){base.OnResize(e);Reflow();}
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);Reflow();}
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics;var p=UiTheme.Current;g.Clear(p.Main);g.SmoothingMode=SmoothingMode.AntiAlias;
        if(tiles.Length==0){WorkspaceDrawing.Text(g,EmptyText,Font,ClientRectangle,p.SecondaryText,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak);return;}
        var contentClip=g.Save();g.SetClip(ContentViewport,System.Drawing.Drawing2D.CombineMode.Intersect);
        using var bold=new Font(Font,FontStyle.Bold);
        for(int i=0;i<tiles.Length;i++)
        {
            var t=tiles[i];var r=BoundsFor(i);if(!r.IntersectsWith(e.ClipRectangle))continue;
            bool active=selection.Contains(t.Game.Id);
            using var shape=WorkspaceDrawing.Round(r,S(12));using var fill=new SolidBrush(p.Secondary);g.FillPath(fill,shape);
            using var pen=new Pen(active?p.Accent:i==hover?p.SecondaryText:p.Border,active?S(2):1);g.DrawPath(pen,shape);
            if(ListMode)
            {
                var imageBox=new Rectangle(r.X+S(6),r.Y+S(6),S(122),r.Height-S(12));
                if(t.Cover is not null)g.DrawImage(t.Cover,WorkspaceDrawing.Fit(t.Cover.Size,imageBox));else WorkspaceSkin.Icon(g,"game",new(imageBox.X+S(40),imageBox.Y+S(16),S(40),S(40)),p.Accent);
                int x=imageBox.Right+S(15),listY=r.Y+S(10),width=r.Right-x-S(20),height=Font.Height+S(6);
                WorkspaceDrawing.Text(g,t.Game.Name,bold,new(x,listY,width,height),p.Text);
                WorkspaceDrawing.Text(g,t.Annotation.ChineseName.Length>0?t.Annotation.ChineseName:t.Engine,Font,new(x,listY+height,width,height),p.SecondaryText);
                if(t.Annotation.Note.Length>0)WorkspaceDrawing.Text(g,t.Annotation.Note,Font,new(x,listY+height*2,width,Font.Height+S(4)),p.SecondaryText);
                if(runningPaths.Contains(t.Game.ExePath))WorkspaceDrawing.Text(g,"运行中",Font,new(r.Right-S(85),r.Top+S(7),S(75),height),Color.SeaGreen,TextFormatFlags.Right|TextFormatFlags.VerticalCenter);
                continue;
            }
            var cover=new Rectangle(r.X+S(5),r.Y+S(5),r.Width-S(10),(int)(r.Width*(Portrait&&!ShelfMode?1.24f:.52f))-S(5));
            using(var clip=WorkspaceDrawing.Round(cover,S(5)))
            {var state=g.Save();g.SetClip(clip,CombineMode.Intersect);using(var background=new SolidBrush(p.Control))g.FillRectangle(background,cover);
             if(t.Cover is not null){double ratio=Math.Max((double)cover.Width/t.Cover.Width,(double)cover.Height/t.Cover.Height);int w=(int)(t.Cover.Width*ratio),h=(int)(t.Cover.Height*ratio);g.DrawImage(t.Cover,new Rectangle(cover.X+(cover.Width-w)/2,cover.Y+(cover.Height-h)/2,w,h));}
             else WorkspaceDrawing.Text(g,"尚无封面",Font,cover,p.SecondaryText,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
             g.Restore(state);}
            int y=cover.Bottom+S(6),htext=Math.Max(S(20),Font.Height+S(1));
            var iconBox=new Rectangle(r.X+S(8),y,S(24),S(24));
            if(t.Icon is not null)g.DrawImage(t.Icon,WorkspaceDrawing.Fit(t.Icon.Size,iconBox));else WorkspaceSkin.Icon(g,"game",Rectangle.Inflate(iconBox,-S(5),-S(5)),p.Accent);
            int tx=iconBox.Right+S(9),tw=r.Right-tx-S(14);
            WorkspaceDrawing.Text(g,t.Game.Name,bold,new(tx,y-1,tw-S(2),htext),p.Text);
            WorkspaceDrawing.Text(g,t.Annotation.ChineseName.Length>0?t.Annotation.ChineseName:t.Engine,Font,new(tx,y+htext,tw-S(8),htext),p.SecondaryText);
            bool running=runningPaths.Contains(t.Game.ExePath);
            if(running){using var dot=new SolidBrush(Color.FromArgb(55,197,104));g.FillEllipse(dot,cover.Left+S(8),cover.Top+S(8),S(8),S(8));}
            if(t.Annotation.Note.Length>0)
            {
                var color=t.Annotation.Color=="lavender"?Color.FromArgb(231,226,252):t.Annotation.Color=="mint"?Color.FromArgb(220,244,229):Color.FromArgb(255,243,178);
                using var noteFont=new Font(Font.FontFamily,Math.Max(8,Font.Size*.9f));
                int width=Math.Min(cover.Width*2/3,Math.Max(S(76),TextRenderer.MeasureText(t.Annotation.Note,noteFont).Width+S(18)));
                var state=g.Save();g.TranslateTransform(cover.Right-width-S(5),cover.Bottom-S(33));g.RotateTransform(-5);
                using(var shadow=new SolidBrush(Color.FromArgb(30,0,0,0)))g.FillRectangle(shadow,S(2),S(2),width,S(32));
                using(var paper=new SolidBrush(color))g.FillRectangle(paper,0,0,width,S(32));
                using(var ink=new SolidBrush(Color.FromArgb(68,61,46)))using(var format=new StringFormat{LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})g.DrawString(t.Annotation.Note,noteFont,ink,new RectangleF(S(6),0,width-S(12),S(32)),format);g.Restore(state);
            }
            if(t.Annotation.Bookmark)
            {
                int x=cover.Right-S(22),y0=cover.Top;Point[] points=[new(x,y0),new(x+S(10),y0),new(x+S(10),y0+S(16)),new(x+S(5),y0+S(12)),new(x,y0+S(16)),new(x,y0)];
                using var bookmark=new SolidBrush(p.Accent);g.FillPolygon(bookmark,points);
            }
        }
        PaintSelectionRectangle(g);g.Restore(contentClip);PaintScrollBar(g);
    }
    private int Hit(Point point){if(!ContentViewport.Contains(point))return -1;for(int i=0;i<tiles.Length;i++)if(BoundsFor(i).Contains(point))return i;return -1;}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(selecting){UpdateBoxSelection(e.Location);return;}if(MoveScroll(e.Location))return;var i=Hit(e.Location);if(i!=hover){hover=i;Cursor=i>=0?Cursors.Hand:Cursors.Default;Invalidate();}}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=-1;scrollHover=false;Invalidate();}
    protected override void OnMouseDown(MouseEventArgs e)
    {base.OnMouseDown(e);if(e.Button is not (MouseButtons.Left or MouseButtons.Right))return;Focus();if(BeginScroll(e))return;int i=Hit(e.Location);if(e.Button==MouseButtons.Right){if(i>=0&&!selection.Contains(tiles[i].Game.Id))SelectIndex(i,Keys.None);return;}if(i<0){BeginBoxSelection(e.Location,ModifierKeys);return;}SelectIndex(i,ModifierKeys);}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(selecting&&e.Button==MouseButtons.Left){UpdateBoxSelection(e.Location);selecting=false;Capture=false;Invalidate();return;}if(EndScroll())return;int i=Hit(e.Location);if(e.Button==MouseButtons.Right&&i>=0)ContextRequested?.Invoke(tiles[i].Game,e.Location);}
    protected override void OnMouseDoubleClick(MouseEventArgs e){base.OnMouseDoubleClick(e);int i=Hit(e.Location);if(e.Button==MouseButtons.Left&&i>=0)GameActivated?.Invoke(tiles[i].Game);}
    private void Choose(int index)=>SelectIndex(index,Keys.None);
    protected override bool IsInputKey(Keys keyData)=>keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown||base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);if(tiles.Length==0)return;if(e.Control&&e.KeyCode==Keys.A){selection=tiles.Select(t=>t.Game.Id).ToHashSet();Invalidate();e.Handled=e.SuppressKeyPress=true;return;}if(e.KeyCode==Keys.Escape){selection.Clear();Invalidate();e.Handled=true;return;}
        int i=Array.FindIndex(tiles,x=>StringComparer.OrdinalIgnoreCase.Equals(x.Game.ExePath,selected));
        if(i>=0&&e.KeyCode==Keys.Enter){GameActivated?.Invoke(tiles[i].Game);e.Handled=e.SuppressKeyPress=true;return;}
        if(i>=0&&(e.KeyCode==Keys.Apps||e.Shift&&e.KeyCode==Keys.F10)){var r=BoundsFor(i);ContextRequested?.Invoke(tiles[i].Game,new(r.Left+S(20),r.Top+S(20)));e.Handled=e.SuppressKeyPress=true;return;}
        int page=Math.Max(1,(ShelfMode?ClientSize.Width:ClientSize.Height)/(ShelfMode?Cell.Width:Cell.Height))*(ShelfMode?1:Columns);
        int n=e.KeyCode switch{Keys.Left=>i-1,Keys.Right=>i+1,Keys.Up=>i-Columns,Keys.Down=>i+Columns,Keys.Home=>0,Keys.End=>tiles.Length-1,Keys.PageUp=>i-page,Keys.PageDown=>i+page,_=>i};
        if(e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)
        {n=Math.Clamp(n,0,tiles.Length-1);if(n!=i)SelectIndex(n,e.Modifiers);EnsureTileVisible(n);e.Handled=e.SuppressKeyPress=true;}
    }
    protected override void Dispose(bool disposing){if(disposing){boxScrollTimer?.Dispose();foreach(var tile in tiles)tile.Dispose();tiles=[];}base.Dispose(disposing);}
}

internal sealed class GalleryCanvas:Control
{
    private Bitmap? original,translated;
    internal int Mode {get;set;}=1;
    internal float Zoom {get;set;}=1;
    internal GalleryCanvas(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);AccessibleName="截图与译图预览";}
    internal void SetImages(Bitmap? source,Bitmap? result){var a=original;var b=translated;original=source;translated=result;a?.Dispose();b?.Dispose();Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics;var p=UiTheme.Current;g.Clear(p.Main);var area=Rectangle.Inflate(ClientRectangle,-2*DeviceDpi/96,-2*DeviceDpi/96);
        if(original is null&&translated is null){WorkspaceDrawing.Text(g,"选择一条记录，查看图片\n新的截图结果也会保存在这里",Font,area,p.SecondaryText,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak);return;}
        void Draw(Bitmap? image,Rectangle box,string empty){if(image is not null){var fit=WorkspaceDrawing.Fit(image.Size,box);int w=(int)(fit.Width*Zoom),h=(int)(fit.Height*Zoom);var state=g.Save();g.SetClip(box,CombineMode.Intersect);g.DrawImage(image,new Rectangle(box.X+(box.Width-w)/2,box.Y+(box.Height-h)/2,w,h));g.Restore(state);}else WorkspaceDrawing.Text(g,empty,Font,box,p.SecondaryText,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);}
        if(Mode==2){int width=(area.Width-12)/2;Draw(original,new(area.X,area.Y,width,area.Height),"无原图");Draw(translated,new(area.X+width+12,area.Y,width,area.Height),"这条记录没有译图");}
        else Draw(Mode==0?original:translated,area,Mode==0?"这条记录没有原图":"这条记录没有译图，可打开详细编辑生成");
    }
    protected override void Dispose(bool disposing){if(disposing)SetImages(null,null);base.Dispose(disposing);}
}
