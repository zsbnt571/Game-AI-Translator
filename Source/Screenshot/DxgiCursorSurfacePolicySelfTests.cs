using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

internal static class DxgiCursorSurfacePolicySelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<string>();var failures=0;
        void Test(string name,Action body){try{body();rows.Add("PASS "+name);}catch(Exception ex){failures++;rows.Add("FAIL "+name+": "+ex.Message);}}
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static (Point Position,bool Visible,Point Hotspot,string Result) Cursor(Point point,bool visible,string result="flags=1 handle=0x1 hotspot=0,0")=>(point,visible,Point.Empty,result);
        var display=new Rectangle(0,0,1920,1080);var point=new Point(500,400);

        Test("hidden system cursor accepts DXGI without pointer metadata",()=>
        {
            var decision=DxgiCursorSurfacePolicy.Evaluate(Cursor(point,false),Cursor(point,false),[new(display,null,0)]);
            Require(decision.Accept,decision.Reason);
        });
        Test("separate hardware pointer accepts cursor-free DXGI surface",()=>
        {
            var decision=DxgiCursorSurfacePolicy.Evaluate(Cursor(point,true),Cursor(point,true),[new(display,true,100)]);
            Require(decision.Accept,decision.Reason);
        });
        Test("integrated visible pointer rejects DXGI surface",()=>
        {
            var decision=DxgiCursorSurfacePolicy.Evaluate(Cursor(point,true),Cursor(point,true),[new(display,false,100)]);
            Require(!decision.Accept,"integrated pointer surface was accepted");
        });
        Test("unknown visible pointer state rejects DXGI surface",()=>
        {
            var decision=DxgiCursorSurfacePolicy.Evaluate(Cursor(point,true),Cursor(point,true),[new(display,null,0)]);
            Require(!decision.Accept,"unknown pointer surface was accepted");
        });
        Test("cursor movement during sequential output acquisition rejects DXGI surface",()=>
        {
            var decision=DxgiCursorSurfacePolicy.Evaluate(Cursor(point,true),Cursor(new Point(700,450),true),[new(display,true,100)]);
            Require(!decision.Accept,"moving cursor acquisition was accepted");
        });
        Test("unreliable GetCursorInfo result rejects DXGI surface",()=>
        {
            var decision=DxgiCursorSurfacePolicy.Evaluate(Cursor(point,true,"GetCursorInfo error=6"),Cursor(point,true),[new(display,true,100)]);
            Require(!decision.Accept,"unreliable system cursor state was accepted");
        });
        Test("cursor must resolve to exactly one duplicated output",()=>
        {
            var decision=DxgiCursorSurfacePolicy.Evaluate(Cursor(point,true),Cursor(point,true),[new(new Rectangle(1000,0,920,1080),true,100)]);
            Require(!decision.Accept,"cursor outside duplicated output was accepted");
        });
        Test("cursor-capability fallback does not rebuild DXGI primary",()=>
        {
            var primary=new FakeBackend("DXGI test",()=>throw new DxgiCursorExclusionUnavailableException("integrated pointer"));
            var fallback=new FakeBackend("GDI test",()=>CreateFrame("GDI test"));
            using var coordinator=new ScreenCaptureCoordinator(primary,fallback);
            var frame=coordinator.CaptureAsync(new Rectangle(0,0,32,32),Stopwatch.GetTimestamp()).GetAwaiter().GetResult();
            using(frame.Bitmap)
            {
                Require(frame.Metrics.UsedFallback,"fallback flag missing");
                Require(frame.Metrics.CaptureBackend=="GDI test","wrong backend "+frame.Metrics.CaptureBackend);
                Require(primary.InitializeCount==1,"DXGI capability decision rebuilt primary");
            }
        });
        Test("ordinary DXGI failure still rebuilds primary before fallback",()=>
        {
            var primary=new FakeBackend("DXGI test",()=>throw new InvalidOperationException("device lost"));
            var fallback=new FakeBackend("GDI test",()=>CreateFrame("GDI test"));
            using var coordinator=new ScreenCaptureCoordinator(primary,fallback);
            var frame=coordinator.CaptureAsync(new Rectangle(0,0,32,32),Stopwatch.GetTimestamp()).GetAwaiter().GetResult();
            using(frame.Bitmap)Require(primary.InitializeCount==2,"ordinary failure did not rebuild primary");
        });

        File.WriteAllLines(Path.Combine(output,"RESULT.txt"),failures==0?["PASS",..rows]:["FAIL",..rows]);
        File.WriteAllText(Path.Combine(output,"SUMMARY.txt"),$"Tests={rows.Count}{Environment.NewLine}Failures={failures}{Environment.NewLine}DXGIRemainsPrimary=True{Environment.NewLine}PixelCleanup=False{Environment.NewLine}CursorHideShow=False");
        return failures==0?0:1;
    }

    private static ScreenCaptureFrame CreateFrame(string backend)
    {
        var timestamp=Stopwatch.GetTimestamp();return new(new Bitmap(32,32),new(backend,1,0,0,0,timestamp,timestamp,1,4096,Environment.CurrentManagedThreadId,false));
    }

    private sealed class FakeBackend(string name,Func<ScreenCaptureFrame> capture):IScreenCaptureBackend
    {
        public string BackendName=>name;public bool IsAvailable{get;private set;}public int InitializeCount{get;private set;}
        public void Initialize(){InitializeCount++;IsAvailable=true;}
        public ScreenCaptureFrame CaptureFrame(Rectangle virtualBounds,long hotkeyTimestamp,CancellationToken cancellationToken)=>capture();
        public void Dispose()=>IsAvailable=false;
    }
}
