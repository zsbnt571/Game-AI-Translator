namespace ScreenshotTranslationUiTester;

// Retained content with an unobtrusive theme-colored scroll thumb, in both themes.
internal sealed class WorkspaceScrollView : BufferedPage
{
    private int offset,dragAnchor;
    private bool laying,dragging,nearEdge,scrollVisible;
    private readonly System.Windows.Forms.Timer scrollIdle=new(){Interval=900};
    internal int ScrollOffset {get=>offset;set{offset=value;Reflow();}}
    internal WorkspaceScrollView(){AutoScroll=false;TabStop=false;scrollIdle.Tick+=(_,_)=>{scrollIdle.Stop();if(!dragging&&!nearEdge){scrollVisible=false;InvalidateRail();}};}
    private void InvalidateRail()=>Invalidate(new Rectangle(Math.Max(0,Width-14*DeviceDpi/96),0,14*DeviceDpi/96,Height));
    private void RevealScroll(){scrollVisible=true;scrollIdle.Stop();scrollIdle.Start();InvalidateRail();}
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);if(e.Control is not { } child)return;
        child.Dock=DockStyle.None;child.SizeChanged+=(_,_)=>Reflow();Reflow();
    }
    protected override void OnLayout(LayoutEventArgs e){base.OnLayout(e);Reflow();}
    private void Reflow()
    {
        if(laying||Controls.Count==0)return;laying=true;
        try
        {
            var child=Controls[0];int width=Math.Max(1,ClientSize.Width-Padding.Horizontal-12*DeviceDpi/96);
            // Auto-sized tables must retain the viewport width when scrolling changes Top.
            var constraint=new Size(width,0);
            if(child.Width!=width||child.MinimumSize!=constraint||child.MaximumSize!=constraint)
            {
                child.SuspendLayout();
                try{child.MaximumSize=Size.Empty;child.MinimumSize=constraint;child.MaximumSize=constraint;child.Width=width;}
                finally{child.ResumeLayout(true);}
            }
            int height=child.AutoSize?child.GetPreferredSize(new(width,0)).Height:child.Height;
            if(child.AutoSize&&child.Height!=height)child.Height=height;
            offset=Math.Clamp(offset,0,Math.Max(0,child.Height-ClientSize.Height+Padding.Vertical));
            var location=new Point(Padding.Left,Padding.Top-offset);if(child.Location!=location)child.Location=location;
            Invalidate(new Rectangle(Math.Max(0,Width-12*DeviceDpi/96),0,12*DeviceDpi/96,Height));
        }
        finally{laying=false;}
    }
    internal void ScrollWheel(int delta)
    {
        if(delta==0||Controls.Count==0)return;
        int maximum=Math.Max(0,Controls[0].Height+Padding.Vertical-ClientSize.Height);
        int next=Math.Clamp(offset-Math.Sign(delta)*Math.Max(1,Math.Abs(delta)/120)*60*DeviceDpi/96,0,maximum);
        RevealScroll();if(next==offset)return;offset=next;Reflow();
    }
    protected override void OnMouseWheel(MouseEventArgs e){if(e is HandledMouseEventArgs h)h.Handled=true;ScrollWheel(e.Delta);}
    protected override void WndProc(ref Message m)
    {
        // Consume at the nearest viewport, including its edges. DefWindowProc
        // must not forward a second scroll into the containing library page.
        if(m.Msg==0x20A){ScrollWheel((short)(m.WParam.ToInt64()>>16));m.Result=IntPtr.Zero;return;}
        base.WndProc(ref m);
    }
    private int ScrollMaximum=>Controls.Count==0?0:Math.Max(0,Controls[0].Height+Padding.Vertical-ClientSize.Height);
    private Rectangle ScrollThumb
    {
        get{int total=Height+ScrollMaximum,h=Math.Min(Height,Math.Max(28*DeviceDpi/96,(int)((long)Height*Height/Math.Max(1,total))));
            return new(Width-7*DeviceDpi/96,(int)((long)offset*(Height-h)/Math.Max(1,ScrollMaximum)),4*DeviceDpi/96,h);}
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left||e.X<Width-14*DeviceDpi/96||ScrollMaximum==0)return;
        RevealScroll();var thumb=ScrollThumb;
        if(e.Y>=thumb.Top&&e.Y<thumb.Bottom){dragging=true;dragAnchor=e.Y-thumb.Y;Capture=true;}
        else{offset=Math.Clamp(offset+(e.Y<thumb.Top?-Height:Height),0,ScrollMaximum);Reflow();}
    }
    private void Drag(int y){int travel=Math.Max(1,Height-ScrollThumb.Height);offset=(int)((long)Math.Clamp(y-dragAnchor,0,travel)*ScrollMaximum/travel);Reflow();}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);bool wasNear=nearEdge;nearEdge=e.X>=Width-18*DeviceDpi/96;if(nearEdge||dragging)RevealScroll();else if(wasNear){scrollIdle.Stop();scrollIdle.Start();}if(dragging)Drag(e.Y);}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);nearEdge=false;scrollIdle.Stop();scrollIdle.Start();}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragging=false;Capture=false;RevealScroll();}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture){dragging=false;scrollIdle.Stop();scrollIdle.Start();}}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);if(!scrollVisible||ScrollMaximum==0)return;
        using var shape=WorkspaceDrawing.Round(ScrollThumb,2*DeviceDpi/96);using var brush=new SolidBrush(UiTheme.Current.SecondaryText);e.Graphics.FillPath(brush,shape);
    }
    protected override void Dispose(bool disposing){if(disposing)scrollIdle.Dispose();base.Dispose(disposing);}
}
