using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed class CursorCaptureTestSurfaceForm : Form
{
    private const string ConfigFile="cursor-test-surface-config.json";
    private readonly SurfacePanel _surface=new(){Location=new Point(20,20),Size=new Size(1080,700)};
    private readonly Panel _outside=new(){Location=new Point(1120,20),Size=new Size(260,700),BackColor=Color.FromArgb(31,36,48)};
    private readonly SurfaceConfig _config;

    internal CursorCaptureTestSurfaceForm()
    {
        _config=JsonSerializer.Deserialize<SurfaceConfig>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,ConfigFile)))
            ??throw new InvalidDataException(ConfigFile+" is invalid.");
        Text="CursorCaptureTestSurface — USER MANUAL WINDOWS POINTER";
        StartPosition=FormStartPosition.Manual;Location=new Point(80,80);ClientSize=new Size(1400,740);
        FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;MinimizeBox=true;AutoScaleMode=AutoScaleMode.Dpi;
        Controls.Add(_surface);Controls.Add(_outside);
        _outside.Controls.Add(new Label
        {
            Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=Color.White,
            Font=new Font("Microsoft YaHei UI",18,FontStyle.Bold),
            Text="OUTSIDE\r\n\r\n第一次截图时\r\n把普通 Windows\r\n白色箭头放在这里\r\n\r\n然后按产品快捷键"
        });
        Shown+=(_,_)=>{WriteReference();WriteState();};
        LocationChanged+=(_,_)=>WriteState();SizeChanged+=(_,_)=>WriteState();FormClosed+=(_,_)=>DeleteState();
    }

    private void WriteReference()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_config.ReferencePath)!);
        using var bitmap=_surface.CreateReference();bitmap.Save(_config.ReferencePath,ImageFormat.Png);
    }

    private void WriteState()
    {
        if(!IsHandleCreated||WindowState==FormWindowState.Minimized)return;
        Directory.CreateDirectory(Path.GetDirectoryName(_config.StatePath)!);
        var rect=_surface.RectangleToScreen(_surface.ClientRectangle);
        var payload=new{ProcessId=Environment.ProcessId,TargetScreenRect=rect,Timestamp=DateTimeOffset.Now,Dpi=DeviceDpi};
        File.WriteAllText(_config.StatePath,JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true}));
    }

    private void DeleteState(){try{if(File.Exists(_config.StatePath))File.Delete(_config.StatePath);}catch{}}
    private sealed record SurfaceConfig(string StatePath,string ReferencePath);

    private sealed class SurfacePanel : Panel
    {
        internal SurfacePanel(){DoubleBuffered=true;}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using var bitmap=CreateReference();e.Graphics.DrawImageUnscaled(bitmap,Point.Empty);}

        internal Bitmap CreateReference()
        {
            var bitmap=new Bitmap(Math.Max(1,ClientSize.Width),Math.Max(1,ClientSize.Height),PixelFormat.Format32bppArgb);
            using var g=Graphics.FromImage(bitmap);g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.FromArgb(238,241,246));
            const int tile=24;
            using var light=new SolidBrush(Color.FromArgb(238,241,246));using var dark=new SolidBrush(Color.FromArgb(217,223,232));
            for(var y=0;y<bitmap.Height;y+=tile)for(var x=0;x<bitmap.Width;x+=tile)g.FillRectangle(((x/tile+y/tile)&1)==0?light:dark,x,y,tile,tile);
            using var header=new SolidBrush(Color.FromArgb(20,27,40));g.FillRectangle(header,0,0,bitmap.Width,88);
            using var titleFont=new Font("Microsoft YaHei UI",26,FontStyle.Bold,GraphicsUnit.Pixel);using var bodyFont=new Font("Microsoft YaHei UI",18,FontStyle.Regular,GraphicsUnit.Pixel);
            using var markerFont=new Font("Microsoft YaHei UI",34,FontStyle.Bold,GraphicsUnit.Pixel);using var white=new SolidBrush(Color.White);using var ink=new SolidBrush(Color.FromArgb(28,38,54));
            g.DrawString("WINDOWS POINTER TEST",titleFont,white,new PointF(28,23));
            g.DrawString("保持画面静止。第二次把普通白色箭头放在 MARKER A；第三次放在 MARKER B。",bodyFont,ink,new RectangleF(35,112,bitmap.Width-70,60));
            DrawMarker(g,new Rectangle(180,260,210,210),"MARKER A",Color.FromArgb(28,126,214),markerFont,white);
            DrawMarker(g,new Rectangle(bitmap.Width-390,260,210,210),"MARKER B",Color.FromArgb(196,71,64),markerFont,white);
            using var border=new Pen(Color.FromArgb(37,48,67),4);g.DrawRectangle(border,2,2,bitmap.Width-5,bitmap.Height-5);
            using var footer=new SolidBrush(Color.FromArgb(225,229,236));g.FillRectangle(footer,35,575,bitmap.Width-70,80);
            g.DrawString("这是静态像素测试面；无动画、无滚动、无鼠标图案。",bodyFont,ink,new PointF(58,601));
            return bitmap;
        }

        private static void DrawMarker(Graphics g,Rectangle rect,string text,Color color,Font font,Brush brush)
        {
            using var fill=new SolidBrush(color);using var outline=new Pen(Color.FromArgb(24,31,44),5);g.FillRectangle(fill,rect);g.DrawRectangle(outline,rect);
            var size=g.MeasureString(text,font);g.DrawString(text,font,brush,rect.Left+(rect.Width-size.Width)/2,rect.Top+(rect.Height-size.Height)/2);
        }
    }
}
