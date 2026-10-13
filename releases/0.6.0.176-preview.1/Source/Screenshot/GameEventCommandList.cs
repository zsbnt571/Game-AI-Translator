namespace ScreenshotTranslationUiTester;

// Retain selection and scroll within the event list, with the same palette as data rows.
internal sealed class GameEventCommandList:Control
{
    private string[] items=[];
    private int selected=-1,offset;
    private int[] rowTops=[0];
    private int measuredWidth=-1;
    private bool dragging;
    private int S(int value)=>Math.Max(1,value*DeviceDpi/96);
    private const TextFormatFlags TextFlags=TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.TextBoxControl;
    private int TotalHeight=>rowTops[^1];
    private int TextWidth(int width)=>Math.Max(1,width-S(28));
    private void MeasureRows(int width)
    {
        if(measuredWidth==width)return;measuredWidth=width;rowTops=new int[items.Length+1];
        for(int i=0;i<items.Length;i++)rowTops[i+1]=rowTops[i]+Math.Max(Font.Height+S(14),TextRenderer.MeasureText(items[i],Font,new Size(TextWidth(width),int.MaxValue),TextFlags).Height+S(14));
        offset=Math.Clamp(offset,0,Math.Max(0,TotalHeight-Height));
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        int width=proposedSize.Width>0?proposedSize.Width:Math.Max(S(160),Width);MeasureRows(width);
        return new(width,Math.Clamp(TotalHeight,S(78),S(340)));
    }
    internal event EventHandler? SelectedIndexChanged;
    internal int SelectedIndex
    {
        get=>selected;
        set
        {
            value=Math.Clamp(value,-1,items.Length-1);if(selected==value)return;selected=value;
            MeasureRows(Width);
            if(value>=0){if(rowTops[value]<offset||rowTops[value+1]-rowTops[value]>=Height)offset=rowTops[value];else if(rowTops[value+1]>offset+Height)offset=rowTops[value+1]-Height;}
            Invalidate();SelectedIndexChanged?.Invoke(this,EventArgs.Empty);
        }
    }
    internal GameEventCommandList(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);AutoSize=true;TabStop=true;AccessibleName="事件指令列表";}
    internal void SetItems(string[] values)
    {
        items=values;selected=-1;offset=0;measuredWidth=-1;AccessibleDescription=string.Join("\n\n",values);
        Height=GetPreferredSize(new Size(Width,0)).Height;Parent?.PerformLayout(this,nameof(PreferredSize));Invalidate();
    }
    protected override void OnFontChanged(EventArgs e){measuredWidth=-1;base.OnFontChanged(e);Parent?.PerformLayout();Invalidate();}
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);MeasureRows(Width);}
    protected override void OnPaint(PaintEventArgs e)
    {
        MeasureRows(Width);var p=UiTheme.Current;e.Graphics.Clear(p.Secondary);
        for(int i=0;i<items.Length;i++)
        {
            var r=new Rectangle(0,rowTops[i]-offset,Width-S(10),rowTops[i+1]-rowTops[i]);
            if(r.Bottom<=0)continue;if(r.Top>=Height)break;
            if(i==selected){using var b=new SolidBrush(p.Control);e.Graphics.FillRectangle(b,r);}
            TextRenderer.DrawText(e.Graphics,items[i],Font,new Rectangle(S(8),r.Top+S(7),TextWidth(Width),r.Height-S(14)),i==selected?p.Accent:p.Text,TextFlags);
            using var line=new Pen(p.Border);e.Graphics.DrawLine(line,S(5),r.Bottom-1,r.Right-S(5),r.Bottom-1);
        }
        if(TotalHeight>Height)
        {
            int h=Math.Max(S(20),Height*Height/TotalHeight),y=offset*(Height-h)/Math.Max(1,TotalHeight-Height);
            using var b=new SolidBrush(p.SecondaryText);e.Graphics.FillRectangle(b,Width-5*DeviceDpi/96,y,3*DeviceDpi/96,h);
        }
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-1,-1));
    }
    protected override void OnMouseWheel(MouseEventArgs e){if(e is HandledMouseEventArgs h)h.Handled=true;offset=Math.Clamp(offset-Math.Sign(e.Delta)*(Font.Height+S(10))*3,0,Math.Max(0,TotalHeight-Height));Invalidate();}
    private void Drag(int y){offset=Math.Clamp(y*Math.Max(0,TotalHeight-Height)/Math.Max(1,Height),0,Math.Max(0,TotalHeight-Height));Invalidate();}
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;Focus();
        if(e.X>=Width-S(10)&&TotalHeight>Height){dragging=true;Capture=true;Drag(e.Y);return;}
        int row=Array.FindIndex(rowTops,y=>y>offset+e.Y)-1;if(row>=0&&row<items.Length)SelectedIndex=row;
    }
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging)Drag(e.Y);}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragging=false;Capture=false;}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture)dragging=false;}
    protected override bool IsInputKey(Keys keyData)=>keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown||base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int page=Math.Max(1,Height/Math.Max(1,Font.Height*2+S(14)));
        int next=e.KeyCode switch{Keys.Up=>selected-1,Keys.Down=>selected+1,Keys.Home=>0,Keys.End=>items.Length-1,Keys.PageUp=>selected-page,Keys.PageDown=>selected+page,_=>selected};
        if(next!=selected&&items.Length>0){SelectedIndex=Math.Clamp(next,0,items.Length-1);e.Handled=true;}base.OnKeyDown(e);
    }
}
