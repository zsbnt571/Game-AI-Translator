using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using ScreenshotTranslationUiTester;
using W=System.Windows;

static class Program
{
    sealed class TimerResolution:IDisposable
    {
        [System.Runtime.InteropServices.DllImport("winmm.dll")]static extern uint timeBeginPeriod(uint period);
        [System.Runtime.InteropServices.DllImport("winmm.dll")]static extern uint timeEndPeriod(uint period);
        readonly bool active;
        internal TimerResolution(bool enabled){active=enabled&&timeBeginPeriod(1)==0;}
        public void Dispose(){if(active)timeEndPeriod(1);}
    }
    static readonly List<double> draws=[],latencies=[],intervals=[];
    static long request,lastDraw;
    static bool measuring;
    static void Draw(double milliseconds)
    {
        if(!measuring)return;long now=Stopwatch.GetTimestamp();draws.Add(milliseconds);
        if(request!=0){latencies.Add(Stopwatch.GetElapsedTime(request,now).TotalMilliseconds);request=0;}
        if(lastDraw!=0)intervals.Add(Stopwatch.GetElapsedTime(lastDraw,now).TotalMilliseconds);lastDraw=now;
    }
    sealed class PaintObserver:NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            if(m.Msg!=0x000f){base.WndProc(ref m);return;}
            long started=Stopwatch.GetTimestamp();base.WndProc(ref m);Draw(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
    [STAThread] static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        int count=int.Parse(args[1]);bool software=args.Contains("--software");
        bool preciseTimer=args.Contains("--precise-timer");using var timerResolution=new TimerResolution(preciseTimer);
        AppDataPaths.Initialize(["--data-root="+Path.Combine(output,"data"),"--disable-global-input"]);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#if NATIVE
        WpfWorkspace.Enabled=true;
        if(software)System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        DesktopRenderMetrics.Enabled=true;DesktopRenderMetrics.DrawCompleted+=(_,ms)=>Draw(ms);
#endif
        using var grid=new GameLibraryGrid{Font=new System.Drawing.Font("Microsoft YaHei UI",10),Dock=DockStyle.Fill};
        UiTheme.Apply(grid,new ApiSettings{ThemeMode=ApplicationThemeMode.Night});
        var tiles=new LibraryTile[count];
        for(int i=0;i<count;i++)
        {
            var bitmap=new System.Drawing.Bitmap(80,110);
            using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.FromArgb(45+i%80,55+(i*3)%90,70+(i*7)%80));
                using var brush=new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(130,190,210));graphics.FillEllipse(brush,15,20,50,50);
            }
            tiles[i]=new(new(i.ToString(),"测试游戏 "+i+" Example title",Path.Combine(output,"Synthetic-"+i+".exe"),DateTimeOffset.UnixEpoch),new("示例标题 "+i),"Engine",bitmap,null);
        }
        grid.SetTiles(tiles,null);var handle=grid.Handle;
        var wheel=typeof(GameLibraryGrid).GetMethod("OnMouseWheel",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var scrollValue=typeof(GameLibraryGrid).GetProperty("ScrollValue",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var clock=new Stopwatch();TimeSpan cpuStart=TimeSpan.Zero;double startedAt=0;int updates=0;
        var process=Process.GetCurrentProcess();
        object Distribution(List<double> samples)
        {var a=samples.Order().ToArray();return new{count=a.Length,mean=a.DefaultIfEmpty().Average(),p50=a.Length>0?a[(a.Length-1)/2]:0,p95=a.Length>0?a[(int)((a.Length-1)*.95)]:0,max=a.LastOrDefault()};}
        bool Tick()
        {
            double t=clock.Elapsed.TotalSeconds;
            if(!measuring&&t>=2){measuring=true;startedAt=t;cpuStart=process.TotalProcessorTime;}
            if(t>=12)
            {
                double seconds=t-startedAt;process.Refresh();
                var result=new{version=typeof(MainForm).Assembly.GetName().Version?.ToString(),renderer=
#if NATIVE
                    "WPF",
#else
                    "WinForms",
#endif
                    software,preciseTimer,count,viewport=new{width=grid.ClientSize.Width,height=grid.ClientSize.Height},seconds,updates,
                    drawCallbacksPerSecond=draws.Count/seconds,cpuCorePercent=(process.TotalProcessorTime-cpuStart).TotalSeconds/seconds*100,privateBytes=process.PrivateMemorySize64,workingSet=process.WorkingSet64,
                    cpuDrawMs=Distribution(draws),requestToDrawMs=Distribution(latencies),drawIntervalMs=Distribution(intervals),
                    scope="Synthetic scrolling, same source model and viewport. CPU draw completion is not pixel-presentation FPS; GPU completion is not measured."};
                File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine(JsonSerializer.Serialize(result));measuring=false;return false;
            }
            int before=(int)scrollValue.GetValue(grid)!;long requestedAt=Stopwatch.GetTimestamp();
            int direction=(int)(t/2)%2==0?-40:40;
            wheel.Invoke(grid,[new MouseEventArgs(MouseButtons.None,0,400,300,direction)]);
            if(measuring&&(int)scrollValue.GetValue(grid)!=before){updates++;if(request==0)request=requestedAt;}
            return true;
        }
#if NATIVE
        var app=new W.Application();var window=new W.Window{Title="Fusion renderer benchmark",Width=966,Height=669,WindowStartupLocation=W.WindowStartupLocation.CenterScreen,Content=grid.NativeView};
        var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(16)};
        timer.Tick+=(_,_)=>{if(!Tick()){timer.Stop();window.Close();}};
        window.Loaded+=(_,_)=>{clock.Start();timer.Start();};app.Run(window);
#else
        using var form=new Form{Text="Fusion renderer benchmark",ClientSize=new(950,630),StartPosition=FormStartPosition.CenterScreen};
        form.Controls.Add(grid);var observer=new PaintObserver();observer.AssignHandle(grid.Handle);
        using var timer=new System.Windows.Forms.Timer{Interval=16};
        timer.Tick+=(_,_)=>{if(!Tick()){timer.Stop();observer.ReleaseHandle();form.Close();}};
        form.Shown+=(_,_)=>{clock.Start();timer.Start();};Application.Run(form);
#endif
        return 0;
    }
}
