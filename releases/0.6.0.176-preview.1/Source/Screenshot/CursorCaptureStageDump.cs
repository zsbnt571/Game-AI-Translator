using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class CursorCaptureStageDump
{
    private const string FlagFile="CURSOR-STAGE-DUMP-R2.flag";
    private const string ConfigFile="cursor-stage-dump-config.json";
    private static readonly object Gate=new();
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    private static readonly string[] Labels=["OUTSIDE","A","B"];
    private static DumpConfig? _config;
    private static DumpRun? _current;

    internal static bool Enabled=>Environment.GetEnvironmentVariable("ST_CURSOR_STAGE_DUMP")=="1"||File.Exists(Path.Combine(AppContext.BaseDirectory,FlagFile));

    internal static bool TryBeginRun(out string error)
    {
        lock(Gate)
        {
            error="";
            if(_current is not null)return true;
            try
            {
                var config=LoadConfig();Directory.CreateDirectory(config.OutputRoot);
                var label=Labels.FirstOrDefault(x=>!File.Exists(Path.Combine(config.OutputRoot,x,"capture-complete.json")));
                if(label is null){error="OUTSIDE / A / B 三次截图已经完成。请关闭诊断工具并告诉 Codex。";return false;}
                var directory=Path.Combine(config.OutputRoot,label);Directory.CreateDirectory(directory);
                foreach(var stale in Directory.EnumerateFiles(directory,"*.tmp",SearchOption.TopDirectoryOnly))File.Delete(stale);
                var reference=config.SurfaceReferencePath;
                if(!File.Exists(reference)){error=$"测试画面尚未启动或参考图不存在：{reference}";return false;}
                File.Copy(reference,Path.Combine(directory,"00-TEST-SURFACE-REFERENCE.png"),true);
                var cursor=SystemCursorCaptureAudit.Snapshot();
                using var desktopGraphics=Graphics.FromHwnd(IntPtr.Zero);
                var bounds=SystemInformation.VirtualScreen;
                var monitors=Screen.AllScreens.Select((screen,index)=>new MonitorMetadata(index+1,screen.DeviceName,screen.Bounds,screen.WorkingArea,screen.Primary)).ToArray();
                _current=new DumpRun(label,directory,new CaptureMetadata
                {
                    Timestamp=DateTimeOffset.Now,ProcessId=Environment.ProcessId,InteractiveSessionId=Process.GetCurrentProcess().SessionId,
                    RunLabel=label,VirtualScreenBounds=bounds,MonitorId="NOT YET RESOLVED",Monitors=monitors,
                    DpiX=desktopGraphics.DpiX,DpiY=desktopGraphics.DpiY,
                    WindowsScalingPercentX=Math.Round(desktopGraphics.DpiX/96d*100,2),WindowsScalingPercentY=Math.Round(desktopGraphics.DpiY/96d*100,2),
                    CursorScreenX=cursor.Position.X,CursorScreenY=cursor.Position.Y,CursorHotspotX=cursor.Hotspot.X,CursorHotspotY=cursor.Hotspot.Y,
                    CursorVisibleFlag=cursor.Visible,CursorDiagnosticResult=cursor.Result,
                    StageStatus=new Dictionary<string,string>(StringComparer.Ordinal)
                    {
                        ["00-TEST-SURFACE-REFERENCE"]="SAVED",
                        ["01-AUTO-RAW-CAPTURE"]="PENDING",["02-FORCED-DXGI-RAW"]="PENDING",["03-FORCED-GDI-RAW"]="PENDING",
                        ["04-NORMALIZED-DESKTOP"]="PENDING",["05-FROZEN-BITMAP"]="PENDING",["06-SELECTION-CROP"]="PENDING",
                        ["07-PREVIEW-SOURCE"]="PENDING",["08-OCR-SNAPSHOT"]="PENDING",["09-FINAL-TRANSLATED"]="NOT APPLICABLE - OCR/TRANSLATION NOT RUN IN CURSOR-ONLY DIAGNOSTIC"
                    }
                });
                File.WriteAllText(Path.Combine(directory,"09-FINAL-TRANSLATED-NOT-APPLICABLE.txt"),_current.Metadata.StageStatus["09-FINAL-TRANSLATED"]);
                WriteMetadata();
                File.WriteAllText(Path.Combine(directory,"capture-in-progress.json"),JsonSerializer.Serialize(new{label,started=DateTimeOffset.Now},JsonOptions));
                return true;
            }
            catch(Exception ex){error="无法开始 Cursor Stage Dump："+ex.Message;_current=null;return false;}
        }
    }

    internal static void RecordAutoCapture(ScreenCaptureFrame frame,Rectangle bounds,(Point Position,bool Visible,Point Hotspot,string Result) cursor)
    {
        lock(Gate)
        {
            if(_current is null)return;
            SaveBitmap("01-AUTO-RAW-CAPTURE.png",frame.Bitmap);
            // The current backend returns the virtual-desktop-normalized bitmap directly;
            // this same immutable boundary becomes the overlay's desktop input.
            SaveBitmap("04-NORMALIZED-DESKTOP.png",frame.Bitmap);
            _current.Metadata.ActualBackendSelected=BackendClass(frame.Metrics.CaptureBackend);
            _current.Metadata.ActualBackendName=frame.Metrics.CaptureBackend;
            _current.Metadata.BackendSelectionReason=frame.Metrics.UsedFallback
                ?$"Primary capture failed or was unavailable; coordinator selected fallback {frame.Metrics.CaptureBackend}. {frame.Metrics.FailureReason}"
                :$"Default primary {frame.Metrics.CaptureBackend} initialized and returned the product frame.";
            _current.Metadata.AutoUsedFallback=frame.Metrics.UsedFallback;
            _current.Metadata.AutoFailureReason=frame.Metrics.FailureReason;
            _current.Metadata.CursorScreenX=cursor.Position.X;_current.Metadata.CursorScreenY=cursor.Position.Y;
            _current.Metadata.CursorVisibleFlag=cursor.Visible;_current.Metadata.CursorHotspotX=cursor.Hotspot.X;_current.Metadata.CursorHotspotY=cursor.Hotspot.Y;
            SetBitmapMetadata(frame.Bitmap);
            _current.Metadata.StageStatus["01-AUTO-RAW-CAPTURE"]="SAVED - actual selected backend output";
            _current.Metadata.StageStatus["04-NORMALIZED-DESKTOP"]="SAVED - same backend-returned virtual desktop bitmap; no separate post-backend normalization stage";
            WriteMetadata();
        }
    }

    internal static Task CaptureForcedBackendsAsync(Rectangle bounds,long hotkeyTimestamp)=>Task.Run(()=>
    {
        CaptureForced(new DxgiDesktopDuplicationBackend(),"02-FORCED-DXGI-RAW.png","DXGI",bounds,hotkeyTimestamp);
        CaptureForced(new GdiFallbackCaptureBackend(),"03-FORCED-GDI-RAW.png","GDI",bounds,hotkeyTimestamp);
    });

    private static void CaptureForced(IScreenCaptureBackend backend,string file,string kind,Rectangle bounds,long timestamp)
    {
        try
        {
            backend.Initialize();
            if(!backend.IsAvailable)throw new InvalidOperationException(kind+" backend unavailable after Initialize.");
            var frame=backend.CaptureFrame(bounds,timestamp,CancellationToken.None);
            try
            {
                lock(Gate)
                {
                    if(_current is null)return;SaveBitmap(file,frame.Bitmap);
                    if(kind=="DXGI")_current.Metadata.DxgiAttemptResult="SUCCESS: "+frame.Metrics.CaptureBackend;
                    else _current.Metadata.GdiAttemptResult="SUCCESS: "+frame.Metrics.CaptureBackend;
                    _current.Metadata.StageStatus[kind=="DXGI"?"02-FORCED-DXGI-RAW":"03-FORCED-GDI-RAW"]="SAVED";WriteMetadata();
                }
            }
            finally{frame.Bitmap.Dispose();}
        }
        catch(Exception ex)
        {
            lock(Gate)
            {
                if(_current is null)return;
                var value=$"FAIL: {ex.GetType().Name}: {ex.Message}";
                if(kind=="DXGI"){_current.Metadata.DxgiAttemptResult=value;_current.Metadata.DxgiFailureCode=HResult(ex);}
                else _current.Metadata.GdiAttemptResult=value;
                _current.Metadata.StageStatus[kind=="DXGI"?"02-FORCED-DXGI-RAW":"03-FORCED-GDI-RAW"]=value;
                File.WriteAllText(Path.Combine(_current.Directory,Path.GetFileNameWithoutExtension(file)+"-ERROR.txt"),ex.ToString());WriteMetadata();
            }
        }
        finally{backend.Dispose();}
    }

    internal static void RecordFrozenBitmap(Bitmap bitmap){lock(Gate){if(_current is null)return;SaveBitmap("05-FROZEN-BITMAP.png",bitmap);_current.Metadata.StageStatus["05-FROZEN-BITMAP"]="SAVED";WriteMetadata();}}

    internal static void RecordSelectionCrop(Bitmap bitmap,Rectangle screenRect,Rectangle bitmapRect)
    {
        lock(Gate)
        {
            if(_current is null)return;SaveBitmap("06-SELECTION-CROP.png",bitmap);
            _current.Metadata.SelectionRectScreenCoordinates=screenRect;_current.Metadata.SelectionRectBitmapCoordinates=bitmapRect;
            _current.Metadata.MonitorId=Screen.FromRectangle(screenRect).DeviceName;_current.Metadata.StageStatus["06-SELECTION-CROP"]="SAVED";WriteMetadata();
        }
    }

    internal static void RecordPreviewSource(Bitmap bitmap){lock(Gate){if(_current is null)return;SaveBitmap("07-PREVIEW-SOURCE.png",bitmap);_current.Metadata.StageStatus["07-PREVIEW-SOURCE"]="SAVED";WriteMetadata();}}

    internal static void RecordOcrSnapshot(Bitmap bitmap)
    {
        string? completedLabel=null;
        lock(Gate)
        {
            if(_current is null)return;SaveBitmap("08-OCR-SNAPSHOT.png",bitmap);_current.Metadata.StageStatus["08-OCR-SNAPSHOT"]="SAVED";WriteMetadata();
            var inProgress=Path.Combine(_current.Directory,"capture-in-progress.json");if(File.Exists(inProgress))File.Delete(inProgress);
            File.WriteAllText(Path.Combine(_current.Directory,"capture-complete.json"),JsonSerializer.Serialize(new{_current.Label,completed=DateTimeOffset.Now},JsonOptions));
            completedLabel=_current.Label;_current=null;
        }
        if(completedLabel=="B")GenerateDiffs();
    }

    internal static bool TryGetSurfaceTarget(out Rectangle target)
    {
        target=Rectangle.Empty;
        try
        {
            var statePath=LoadConfig().SurfaceStatePath;if(!File.Exists(statePath))return false;
            var state=JsonSerializer.Deserialize<SurfaceState>(File.ReadAllText(statePath));
            if(state is null||state.ProcessId<=0)return false;
            try{if(Process.GetProcessById(state.ProcessId).HasExited)return false;}catch{return false;}
            target=state.TargetScreenRect;return target.Width>0&&target.Height>0;
        }
        catch{return false;}
    }

    private static void GenerateDiffs()
    {
        var config=LoadConfig();var diffRoot=Path.Combine(config.OutputRoot,"DIFFS");Directory.CreateDirectory(diffRoot);
        var stages=new[]{"00-TEST-SURFACE-REFERENCE.png","01-AUTO-RAW-CAPTURE.png","02-FORCED-DXGI-RAW.png","03-FORCED-GDI-RAW.png","04-NORMALIZED-DESKTOP.png","05-FROZEN-BITMAP.png","06-SELECTION-CROP.png","07-PREVIEW-SOURCE.png","08-OCR-SNAPSHOT.png"};
        foreach(var stage in stages)
        {
            try
            {
                using var outside=LoadComparable(config.OutputRoot,"OUTSIDE",stage);using var a=LoadComparable(config.OutputRoot,"A",stage);using var b=LoadComparable(config.OutputRoot,"B",stage);
                if(outside.Size!=a.Size||outside.Size!=b.Size)throw new InvalidDataException($"size mismatch outside={outside.Size} A={a.Size} B={b.Size}");
                var stageRoot=Path.Combine(diffRoot,Path.GetFileNameWithoutExtension(stage));Directory.CreateDirectory(stageRoot);
                using(var diff=Difference(outside,a))diff.Save(Path.Combine(stageRoot,"DIFF-OUTSIDE-vs-A.png"),ImageFormat.Png);
                using(var diff=Difference(outside,b))diff.Save(Path.Combine(stageRoot,"DIFF-OUTSIDE-vs-B.png"),ImageFormat.Png);
                using(var diff=Difference(a,b))diff.Save(Path.Combine(stageRoot,"DIFF-A-vs-B.png"),ImageFormat.Png);
            }
            catch(Exception ex)
            {
                var stageRoot=Path.Combine(diffRoot,Path.GetFileNameWithoutExtension(stage));Directory.CreateDirectory(stageRoot);File.WriteAllText(Path.Combine(stageRoot,"DIFF-ERROR.txt"),ex.ToString());
            }
        }
        File.WriteAllText(Path.Combine(diffRoot,"diff-generation.json"),JsonSerializer.Serialize(new{generated=DateTimeOffset.Now,classification="NOT PERFORMED - Codex analysis required",stages},JsonOptions));
    }

    private static Bitmap LoadComparable(string root,string label,string stage)
    {
        var path=Path.Combine(root,label,stage);if(!File.Exists(path))throw new FileNotFoundException(path);
        using var source=new Bitmap(path);var metadata=JsonSerializer.Deserialize<CaptureMetadata>(File.ReadAllText(Path.Combine(root,label,"capture-metadata.json")))!;
        var rect=metadata.SelectionRectBitmapCoordinates;
        if(source.Width==rect.Width&&source.Height==rect.Height)return new Bitmap(source);
        if(rect.Width<=0||rect.Height<=0||rect.Left<0||rect.Top<0||rect.Right>source.Width||rect.Bottom>source.Height)return new Bitmap(source);
        return source.Clone(rect,PixelFormat.Format32bppArgb);
    }

    private static Bitmap Difference(Bitmap first,Bitmap second)
    {
        var a=To32(first);var b=To32(second);var output=new Bitmap(a.Width,a.Height,PixelFormat.Format32bppArgb);
        try
        {
            var rect=new Rectangle(0,0,a.Width,a.Height);var ad=a.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);var bd=b.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);var od=output.LockBits(rect,ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            try
            {
                var aa=new byte[Math.Abs(ad.Stride)*a.Height];var bb=new byte[Math.Abs(bd.Stride)*b.Height];var oo=new byte[Math.Abs(od.Stride)*output.Height];Marshal.Copy(ad.Scan0,aa,0,aa.Length);Marshal.Copy(bd.Scan0,bb,0,bb.Length);
                for(var y=0;y<a.Height;y++)for(var x=0;x<a.Width;x++)
                {
                    var ai=y*ad.Stride+x*4;var bi=y*bd.Stride+x*4;var oi=y*od.Stride+x*4;
                    oo[oi]=(byte)Math.Min(255,Math.Abs(aa[ai]-bb[bi])*4);oo[oi+1]=(byte)Math.Min(255,Math.Abs(aa[ai+1]-bb[bi+1])*4);oo[oi+2]=(byte)Math.Min(255,Math.Abs(aa[ai+2]-bb[bi+2])*4);oo[oi+3]=255;
                }
                Marshal.Copy(oo,0,od.Scan0,oo.Length);
            }
            finally{a.UnlockBits(ad);b.UnlockBits(bd);output.UnlockBits(od);}
            return output;
        }
        finally{a.Dispose();b.Dispose();}
    }

    private static Bitmap To32(Bitmap value){var result=new Bitmap(value.Width,value.Height,PixelFormat.Format32bppArgb);using var g=Graphics.FromImage(result);g.DrawImageUnscaled(value,0,0);return result;}
    private static void SaveBitmap(string file,Bitmap bitmap){if(_current is null)return;using var clone=new Bitmap(bitmap);clone.Save(Path.Combine(_current.Directory,file),ImageFormat.Png);}
    private static void SetBitmapMetadata(Bitmap bitmap)
    {
        if(_current is null)return;_current.Metadata.BitmapWidth=bitmap.Width;_current.Metadata.BitmapHeight=bitmap.Height;_current.Metadata.PixelFormat=bitmap.PixelFormat.ToString();
        using var clone=To32(bitmap);var data=clone.LockBits(new Rectangle(0,0,clone.Width,clone.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);try{_current.Metadata.Stride=data.Stride;}finally{clone.UnlockBits(data);}
    }
    private static void WriteMetadata(){if(_current is not null)File.WriteAllText(Path.Combine(_current.Directory,"capture-metadata.json"),JsonSerializer.Serialize(_current.Metadata,JsonOptions));}
    private static string BackendClass(string name)=>name.Contains("DXGI",StringComparison.OrdinalIgnoreCase)?"DXGI":name.Contains("GDI",StringComparison.OrdinalIgnoreCase)?"GDI":"OTHER";
    private static string HResult(Exception ex)=>$"0x{ex.HResult:X8}";
    private static DumpConfig LoadConfig()=>_config??=JsonSerializer.Deserialize<DumpConfig>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,ConfigFile)))??throw new InvalidDataException(ConfigFile+" is invalid.");

    private sealed record DumpRun(string Label,string Directory,CaptureMetadata Metadata);
    private sealed record DumpConfig(string OutputRoot,string SurfaceStatePath,string SurfaceReferencePath);
    private sealed record SurfaceState(int ProcessId,Rectangle TargetScreenRect);
    private sealed record MonitorMetadata(int Index,string DeviceName,Rectangle Bounds,Rectangle WorkingArea,bool Primary);
    private sealed class CaptureMetadata
    {
        public DateTimeOffset Timestamp{get;set;}public int ProcessId{get;set;}public int InteractiveSessionId{get;set;}public string RunLabel{get;set;}="";
        public string ActualBackendSelected{get;set;}="PENDING";public string ActualBackendName{get;set;}="PENDING";public string BackendSelectionReason{get;set;}="PENDING";public bool AutoUsedFallback{get;set;}public string AutoFailureReason{get;set;}="";
        public string DxgiAttemptResult{get;set;}="PENDING";public string DxgiFailureCode{get;set;}="NOT APPLICABLE";public string GdiAttemptResult{get;set;}="PENDING";
        public string MonitorId{get;set;}="";public MonitorMetadata[] Monitors{get;set;}=[];public Rectangle VirtualScreenBounds{get;set;}public Rectangle SelectionRectScreenCoordinates{get;set;}public Rectangle SelectionRectBitmapCoordinates{get;set;}
        public float DpiX{get;set;}public float DpiY{get;set;}public double WindowsScalingPercentX{get;set;}public double WindowsScalingPercentY{get;set;}
        public int CursorScreenX{get;set;}public int CursorScreenY{get;set;}public int CursorHotspotX{get;set;}public int CursorHotspotY{get;set;}public bool CursorVisibleFlag{get;set;}public string CursorDiagnosticResult{get;set;}="";
        public int BitmapWidth{get;set;}public int BitmapHeight{get;set;}public string PixelFormat{get;set;}="";public int Stride{get;set;}public Dictionary<string,string> StageStatus{get;set;}=[];
    }
}
