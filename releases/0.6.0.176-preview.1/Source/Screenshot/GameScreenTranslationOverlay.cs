namespace ScreenshotTranslationUiTester;

internal sealed class GameScreenTranslationOverlay : Form
{
    private readonly ScreenTranslationConnection connection;
    private readonly GameDataSession data;
    private readonly System.Windows.Forms.Timer refresh=new(){Interval=250};
    private ScreenTranslationFrame? current;
    private bool refreshDisposed;
    internal GameScreenTranslationOverlay(ScreenTranslationConnection connection,GameDataSession data)
    {
        this.connection=connection;this.data=data;
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
        BackColor=TransparencyKey=Color.Magenta;DoubleBuffered=true;StartPosition=FormStartPosition.Manual;
        refresh.Tick+=(_,_)=>RefreshFrame();refresh.Start();
    }
    protected override bool ShowWithoutActivation=>true;
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x00000020|0x00000080;return p;}}
    private void RefreshFrame()
    {
        if(IsDisposed||Disposing)return;
        if(!connection.Connected){Close();return;}
        var frame=connection.Frame;
        if(data.Translation?.Enabled!=true||frame is null||!ScreenTranslationConnection.CanDisplay(frame)){current=null;Hide();return;}
        current=frame;if(Bounds!=frame.WindowBounds)Bounds=frame.WindowBounds;
        if(!Visible)Show();Invalidate();
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing&&!refreshDisposed){refreshDisposed=true;refresh.Stop();refresh.Dispose();current=null;}
        base.Dispose(disposing);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var frame=current;
        if(frame is null||!ReferenceEquals(frame,connection.Frame)||data.Translation?.Enabled!=true||!ScreenTranslationConnection.CanDisplay(frame))
        {current=null;if(Visible)Hide();return;}
        base.OnPaint(e);
        float sx=(float)ClientSize.Width/frame.Pixels.Width,sy=(float)ClientSize.Height/frame.Pixels.Height;
        foreach(var block in frame.Blocks)
        {
            string translated=data.Translation?.Lookup(block.Source)??"";if(translated.Length==0||translated==block.Source)continue;
            var r=Rectangle.Round(new RectangleF(block.Bounds.X*sx,block.Bounds.Y*sy,block.Bounds.Width*sx,block.Bounds.Height*sy));
            r.Inflate(4,3);r=Rectangle.Intersect(r,ClientRectangle);if(r.Width<8||r.Height<8)continue;
            float size=Math.Clamp(r.Height*0.64f,10,48);
            using var font=new Font("Microsoft YaHei UI",size,FontStyle.Regular,GraphicsUnit.Pixel);
            var measured=TextRenderer.MeasureText(e.Graphics,translated,font,new Size(r.Width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding);
            r.Height=Math.Min(ClientSize.Height-r.Top,Math.Max(r.Height,measured.Height+6));
            using var background=new SolidBrush(Color.FromArgb(22,25,31));e.Graphics.FillRectangle(background,r);
            TextRenderer.DrawText(e.Graphics,translated,font,r,Color.White,TextFormatFlags.WordBreak|TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix);
        }
    }
}
