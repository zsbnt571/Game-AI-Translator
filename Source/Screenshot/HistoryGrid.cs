using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester;

internal sealed class HistoryGrid : ScrollableControl
{
    private sealed record Card(SessionHistoryItem Record,Bitmap Thumbnail);
    private readonly List<Card> cards=[];
    private readonly ToolTip tip=new(){InitialDelay=600,ReshowDelay=300};
    private Font? textFont;
    private int selected=-1,hover=-1,thumbnailSide=220;
    private Size cell;
    private int gap,columns=1;
    internal bool Compact {get;set;}
    private Point[] compactPositions=[];
    private (string Text,int Y)[] compactHeaders=[];
    internal event EventHandler? SelectionChanged;
    internal int DecodeCount {get;private set;}
    internal int Count=>cards.Count;
    internal int Columns=>columns;
    internal Size TileSize=>cell;
    internal IReadOnlyList<SessionHistoryItem> Records=>cards.Select(c=>c.Record).ToArray();
    internal int SelectedIndex {get=>selected;set { var old=selected;selected=value>=0&&value<Count?value:-1;InvalidateCard(old);InvalidateCard(selected);if(old!=selected)SelectionChanged?.Invoke(this,EventArgs.Empty); }}
    internal SessionHistoryItem? SelectedRecord=>selected>=0?cards[selected].Record:null;
    internal HistoryGrid()
    {
        AutoScroll=true;TabStop=true;AccessibleName="截图历史网格";
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
        FontManager.MarkPreviewTextRoot(this); // History has its own text-size preference.
    }
    private static string Key(SessionHistoryItem record)=>record.SourceImagePath??record.Timestamp.ToString("O");
    internal void SetRecords(IReadOnlyList<SessionHistoryItem> records,int side,float size)
    {
        var chosen=SelectedRecord;
        if(cards.Count!=records.Count||!cards.Select(c=>c.Record).SequenceEqual(records))
        {
            var old=cards.GroupBy(c=>Key(c.Record)).ToDictionary(g=>g.Key,g=>new Queue<Card>(g));
            var next=new List<Card>();
            foreach(var record in records)
            {
                Card? previous=null;
                if(old.TryGetValue(Key(record),out var queue)&&queue.Count>0)previous=queue.Dequeue();
                if(previous is not null&&previous.Record.ThumbnailJpeg.AsSpan().SequenceEqual(record.ThumbnailJpeg))next.Add(new(record,previous.Thumbnail));
                else
                {
                    previous?.Thumbnail.Dispose();Bitmap thumbnail;
                    try{using var stream=new MemoryStream(record.ThumbnailJpeg);using var image=Image.FromStream(stream);thumbnail=new Bitmap(image);}
                    catch{thumbnail=new Bitmap(320,180);using var g=Graphics.FromImage(thumbnail);g.Clear(Color.FromArgb(225,230,237));using var font=new Font("Microsoft YaHei UI",12);g.DrawString("图片暂不可用",font,Brushes.DimGray,80,75);}
                    next.Add(new(record,thumbnail));DecodeCount++;
                }
            }
            foreach(var queue in old.Values)foreach(var card in queue)card.Thumbnail.Dispose();
            cards.Clear();cards.AddRange(next);selected=chosen is null?-1:cards.FindIndex(c=>Key(c.Record)==Key(chosen));hover=-1;
        }
        if(textFont is null||Math.Abs(textFont.Size-size)>.01f)
        {var old=textFont;textFont=new Font("Microsoft YaHei UI",size,FontStyle.Regular);old?.Dispose();}
        thumbnailSide=side;Reflow();Invalidate();
    }
    internal void Reflow()
    {
        var scale=DeviceDpi/96f;gap=(int)Math.Round(12*scale);
        if(Compact)
        {
            int S(int n)=>(int)(n*scale);int compactAvailable=Math.Max(S(130),ClientSize.Width-SystemInformation.VerticalScrollBarWidth-S(4));columns=compactAvailable>=S(250)?2:1;
            int compactWidth=(compactAvailable-gap*(columns-1))/columns;int fh=textFont?.Height??Font.Height;cell=new(compactWidth,(int)(compactWidth*.56f)+fh*2+S(11));
            var positions=new List<Point>();var headers=new List<(string Text,int Y)>();DateTime? date=null;int y=S(4),col=0;
            foreach(var card in cards)
            {var day=card.Record.Timestamp.LocalDateTime.Date;if(day!=date){if(col>0){y+=cell.Height+gap;col=0;}date=day;headers.Add((day==DateTime.Today?"今天":day==DateTime.Today.AddDays(-1)?"昨天":day.ToString("MM月dd日"),y));y+=fh+S(9);}positions.Add(new(col*(cell.Width+gap)+S(2),y));if(++col==columns){y+=cell.Height+gap;col=0;}}
            // Scrollbar/layout updates may re-enter painting. Publish complete
            // geometry instead of clearing a collection a paint is enumerating.
            compactPositions=positions.ToArray();compactHeaders=headers.ToArray();
            if(col>0)y+=cell.Height+gap;AutoScrollMinSize=new(0,y);Invalidate();return;
        }
        var width=Compact?Math.Max(120,(ClientSize.Width-(int)(36*scale)-SystemInformation.VerticalScrollBarWidth)/Math.Max(1,(ClientSize.Width-(int)(12*scale))/(int)(170*scale))):(int)Math.Round((thumbnailSide+52)*scale);
        var imageHeight=Compact?(int)(width*.56):(int)Math.Round(thumbnailSide*.64*scale);
        var textHeight=Math.Max((int)(72*scale),(textFont?.Height??Font.Height)*3+(int)(10*scale));
        cell=new Size(width,imageHeight+textHeight+(int)(24*scale));
        // Reserve scrollbar width consistently so appearing scrollbars cannot oscillate columns.
        var available=Width-SystemInformation.VerticalScrollBarWidth;
        columns=Math.Max(1,(available-gap)/(cell.Width+gap));
        AutoScrollMinSize=new Size(0,((Count+columns-1)/columns)*(cell.Height+gap)+gap);
        Invalidate();
    }
    internal Rectangle CardBounds(int index)
        =>Compact&&index<compactPositions.Length?new Rectangle(compactPositions[index].X,compactPositions[index].Y+AutoScrollPosition.Y,cell.Width,cell.Height):new(gap+(index%columns)*(cell.Width+gap)+AutoScrollPosition.X,gap+(index/columns)*(cell.Height+gap)+AutoScrollPosition.Y,cell.Width,cell.Height);
    internal int HitTest(Point location)
    {
        for(var i=0;i<Count;i++)if(CardBounds(i).Contains(location))return i;return -1;
    }
    internal void ClearSelection()=>SelectedIndex=-1;
    private void InvalidateCard(int i){if(i>=0&&i<Count)Invalidate(Rectangle.Inflate(CardBounds(i),2,2));}
    protected override void OnResize(EventArgs e){base.OnResize(e);Reflow();}
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);Reflow();}
    protected override void OnScroll(ScrollEventArgs e){base.OnScroll(e);Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var p=UiTheme.Current;var g=e.Graphics;g.InterpolationMode=InterpolationMode.HighQualityBicubic;
        using var background=new SolidBrush(p.Main);g.FillRectangle(background,e.ClipRectangle);
        var scale=DeviceDpi/96f;var pad=(int)(12*scale);var font=textFont??Font;
        if(Count==0)WorkspaceDrawing.Text(g,"这里会保存你的截图和译图\n点击“开始截图”或“打开图片”",Font,ClientRectangle,p.SecondaryText,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak);
        if(Compact)
        {
            using var heading=new Font(font,FontStyle.Bold);
            foreach(var header in compactHeaders)WorkspaceDrawing.Text(g,header.Text,heading,new(0,header.Y+AutoScrollPosition.Y,Width,font.Height+4),p.Text);
            for(int i=0;i<Count;i++)
            {
                var bounds=CardBounds(i);if(!bounds.IntersectsWith(e.ClipRectangle))continue;var imageBox=new Rectangle(bounds.X,bounds.Y,bounds.Width,(int)(cell.Width*.56f));
                using(var path=WorkspaceDrawing.Round(imageBox,(int)(5*scale))){var state=g.Save();g.SetClip(path,CombineMode.Intersect);var image=cards[i].Thumbnail;double ratio=Math.Max((double)imageBox.Width/image.Width,(double)imageBox.Height/image.Height);int w=(int)(image.Width*ratio),h=(int)(image.Height*ratio);g.DrawImage(image,new Rectangle(imageBox.X+(imageBox.Width-w)/2,imageBox.Y+(imageBox.Height-h)/2,w,h));g.Restore(state);}
                if(i==selected||i==hover){using var border=new Pen(i==selected?p.Accent:p.Border,i==selected?2:1);using var path=WorkspaceDrawing.Round(Rectangle.Inflate(imageBox,1,1),(int)(5*scale));g.DrawPath(border,path);}
                WorkspaceDrawing.Text(g,MainForm.GalleryItemTitle(cards[i].Record),heading,new(bounds.X,imageBox.Bottom+3,bounds.Width,font.Height+4),p.Text);
                WorkspaceDrawing.Text(g,cards[i].Record.Timestamp.LocalDateTime.ToString("HH:mm"),font,new(bounds.X,imageBox.Bottom+font.Height+6,bounds.Width,font.Height+3),p.SecondaryText);
            }
            return;
        }
        for(var i=0;i<Count;i++)
        {
            var bounds=CardBounds(i);if(!bounds.IntersectsWith(e.ClipRectangle))continue;
            using var fill=new SolidBrush(i==selected||i==hover?p.Control:p.Secondary);g.FillRectangle(fill,bounds);
            using var border=new Pen(i==selected?p.Accent:p.Border,i==selected?2:1);g.DrawRectangle(border,Rectangle.Inflate(bounds,-1,-1));
            var imageBox=new Rectangle(bounds.Left+pad,bounds.Top+pad,bounds.Width-2*pad,Compact?(int)(cell.Width*.56):(int)(thumbnailSide*.64*scale));
            var image=cards[i].Thumbnail;var ratio=Math.Min(imageBox.Width/(float)image.Width,imageBox.Height/(float)image.Height);
            var w=Math.Max(1,(int)(image.Width*ratio));var h=Math.Max(1,(int)(image.Height*ratio));
            g.DrawImage(image,new Rectangle(imageBox.X+(imageBox.Width-w)/2,imageBox.Y+(imageBox.Height-h)/2,w,h));
            var record=cards[i].Record;var textBox=new Rectangle(imageBox.Left,imageBox.Bottom+pad,imageBox.Width,font.Height+4);
            TextRenderer.DrawText(g,record.Timestamp.ToString("MM-dd HH:mm"),font,textBox,p.SecondaryText,TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
            textBox.Y+=font.Height+4;textBox.Height=bounds.Bottom-pad-textBox.Y;
            var summary=string.IsNullOrWhiteSpace(record.TranslationSummary)?record.SourceSummary:record.TranslationSummary;
            TextRenderer.DrawText(g,summary,font,textBox,p.Text,TextFormatFlags.NoPrefix|TextFormatFlags.WordBreak|TextFormatFlags.EndEllipsis|TextFormatFlags.TextBoxControl);
            if(i==selected&&Focused)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(bounds,-4,-4),p.Text,p.Control);
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);var next=HitTest(e.Location);if(next==hover)return;
        var old=hover;hover=next;InvalidateCard(old);InvalidateCard(next);
        tip.SetToolTip(this,next<0?null:cards[next].Record.Timestamp.ToString("g")+"\n"+cards[next].Record.TranslationSummary);
    }
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);var old=hover;hover=-1;InvalidateCard(old);tip.SetToolTip(this,null);}
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left){Focus();SelectedIndex=HitTest(e.Location);}}
    protected override bool IsInputKey(Keys keyData)=>keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End ||base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);var next=e.KeyCode switch {Keys.Left=>selected-1,Keys.Right=>selected+1,Keys.Up=>selected-columns,Keys.Down=>selected+columns,Keys.Home=>0,Keys.End=>Count-1,_=>selected};
        if(next!=selected&&Count>0){SelectedIndex=Math.Clamp(next,0,Count-1);var r=CardBounds(selected);if(r.Top<0)AutoScrollPosition=new(0,-AutoScrollPosition.Y+r.Top-gap);else if(r.Bottom>ClientSize.Height)AutoScrollPosition=new(0,-AutoScrollPosition.Y+r.Bottom-ClientSize.Height+gap);e.Handled=true;}
        if(e.KeyCode==Keys.Enter&&SelectedRecord is not null){OnDoubleClick(EventArgs.Empty);e.Handled=true;}
    }
    protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);InvalidateCard(selected);}
    protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);InvalidateCard(selected);}
    protected override void Dispose(bool disposing)
    {if(disposing){foreach(var c in cards)c.Thumbnail.Dispose();cards.Clear();textFont?.Dispose();tip.Dispose();}base.Dispose(disposing);}
}
