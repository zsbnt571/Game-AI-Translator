using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace ScreenshotTranslationUiTester;

public sealed record ScreenCaptureMetrics(string CaptureBackend,long ActualFrameAcquireMs,long GpuToCpuCopyMs,
    long VirtualDesktopComposeMs,long FrameAgeAtHotkeyMs,long CapturedFrameTimestamp,long HotkeyTimestamp,
    int BitmapCopyCount,long BytesCopied,int CaptureThreadId,bool UsedFallback,string FailureReason="");

public sealed record ScreenCaptureFrame(Bitmap Bitmap,ScreenCaptureMetrics Metrics);

internal sealed class DxgiCursorExclusionUnavailableException(string message):InvalidOperationException(message);

internal static class SystemCursorCaptureAudit
{
    [StructLayout(LayoutKind.Sequential)]private struct CursorInfo{public int Size;public int Flags;public IntPtr Cursor;public Point Position;}
    [StructLayout(LayoutKind.Sequential)]private struct IconInfo{[MarshalAs(UnmanagedType.Bool)]public bool IsIcon;public int HotspotX;public int HotspotY;public IntPtr MaskBitmap;public IntPtr ColorBitmap;}
    [DllImport("user32.dll")]private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")]private static extern bool GetIconInfo(IntPtr icon,out IconInfo info);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr value);
    internal static (Point Position,bool Visible,Point Hotspot,string Result) Snapshot()
    {
        var info=new CursorInfo{Size=Marshal.SizeOf<CursorInfo>()};var ok=GetCursorInfo(ref info);var hotspot=Point.Empty;var hotspotResult="hotspot=unavailable";
        if(ok&&info.Cursor!=IntPtr.Zero&&GetIconInfo(info.Cursor,out var icon))
        {
            hotspot=new(icon.HotspotX,icon.HotspotY);hotspotResult=$"hotspot={hotspot.X},{hotspot.Y}";
            if(icon.MaskBitmap!=IntPtr.Zero)DeleteObject(icon.MaskBitmap);if(icon.ColorBitmap!=IntPtr.Zero)DeleteObject(icon.ColorBitmap);
        }
        return(info.Position,ok&&(info.Flags&1)!=0,hotspot,ok?$"flags={info.Flags} handle=0x{info.Cursor.ToInt64():X} {hotspotResult}":$"GetCursorInfo error={Marshal.GetLastWin32Error()}");
    }
}

public interface IScreenCaptureBackend : IDisposable
{
    string BackendName { get; }
    bool IsAvailable { get; }
    void Initialize();
    ScreenCaptureFrame CaptureFrame(Rectangle virtualBounds,long hotkeyTimestamp,CancellationToken cancellationToken);
}

public sealed class ScreenCaptureCoordinator : IDisposable
{
    private readonly object _gate=new();
    private readonly IScreenCaptureBackend _primary;
    private readonly IScreenCaptureBackend _fallback;
    private bool _disposed;
    public ScreenCaptureCoordinator(IScreenCaptureBackend? primary=null,IScreenCaptureBackend? fallback=null)
    {
        _primary=primary??new DxgiDesktopDuplicationBackend();_fallback=fallback??new GdiFallbackCaptureBackend();
        TryInitialize(_primary);TryInitialize(_fallback);
        SystemEvents.DisplaySettingsChanged+=DisplaySettingsChanged;
    }
    public Task<ScreenCaptureFrame> CaptureAsync(Rectangle bounds,long hotkeyTimestamp,CancellationToken cancellationToken=default)=>
        Task.Factory.StartNew(()=>CaptureOnWorker(bounds,hotkeyTimestamp,cancellationToken),cancellationToken,
            TaskCreationOptions.DenyChildAttach|TaskCreationOptions.LongRunning,TaskScheduler.Default);
    private ScreenCaptureFrame CaptureOnWorker(Rectangle bounds,long hotkeyTimestamp,CancellationToken token)
    {
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(_primary.IsAvailable)
            {
                try{return _primary.CaptureFrame(bounds,hotkeyTimestamp,token);}
                catch(DxgiCursorExclusionUnavailableException ex)
                {
                    // This is a per-frame capability decision, not a broken duplication session.
                    // Keep DXGI initialized so a later frame can use it when the adapter reports
                    // a cursor overlay that is separate from the acquired desktop surface.
                    AppLog.Write("capture-backend",$"Primary {_primary.BackendName} cannot guarantee a cursor-excluded frame; using GDI fallback",ex);
                    var fallback=_fallback.CaptureFrame(bounds,hotkeyTimestamp,token);
                    return fallback with{Metrics=fallback.Metrics with{UsedFallback=true,FailureReason=ex.GetType().Name+": "+ex.Message}};
                }
                catch(Exception ex){AppLog.Write("capture-backend",$"Primary {_primary.BackendName} failed; using GDI fallback",ex);TryInitialize(_primary);var fallback=_fallback.CaptureFrame(bounds,hotkeyTimestamp,token);return fallback with{Metrics=fallback.Metrics with{UsedFallback=true,FailureReason=ex.GetType().Name+": "+ex.Message}};}
            }
            var value=_fallback.CaptureFrame(bounds,hotkeyTimestamp,token);return value with{Metrics=value.Metrics with{UsedFallback=true,FailureReason="Primary backend unavailable"}};
        }
    }
    private static void TryInitialize(IScreenCaptureBackend backend){try{backend.Initialize();}catch(Exception ex){AppLog.Write("capture-backend",$"Initialize {backend.BackendName} failed",ex);}}
    private void DisplaySettingsChanged(object? sender,EventArgs e){lock(_gate){if(_disposed)return;TryInitialize(_primary);}}
    public void Dispose(){lock(_gate){if(_disposed)return;_disposed=true;SystemEvents.DisplaySettingsChanged-=DisplaySettingsChanged;_primary.Dispose();_fallback.Dispose();}}
}

public sealed class GdiFallbackCaptureBackend : IScreenCaptureBackend
{
    public string BackendName=>"GDI Fallback / Graphics.CopyFromScreen";public bool IsAvailable{get;private set;}
    public void Initialize()=>IsAvailable=true;
    public ScreenCaptureFrame CaptureFrame(Rectangle bounds,long hotkeyTimestamp,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();var sw=Stopwatch.StartNew();var bitmap=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(bitmap))graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);
        sw.Stop();return new(bitmap,new(BackendName,sw.ElapsedMilliseconds,0,0,AgeMs(hotkeyTimestamp),Stopwatch.GetTimestamp(),hotkeyTimestamp,1,(long)bounds.Width*bounds.Height*4,Environment.CurrentManagedThreadId,false));
    }
    public void Dispose()=>IsAvailable=false;
    private static long AgeMs(long timestamp)=>(long)((Stopwatch.GetTimestamp()-timestamp)*1000d/Stopwatch.Frequency);
}

internal readonly record struct DxgiOutputPointerSurfaceState(Rectangle DesktopBounds,bool? SeparatePointerVisible,long LastMouseUpdateTime);

internal static class DxgiCursorSurfacePolicy
{
    internal static (bool Accept,string Reason) Evaluate(
        (Point Position,bool Visible,Point Hotspot,string Result) before,
        (Point Position,bool Visible,Point Hotspot,string Result) after,
        IReadOnlyList<DxgiOutputPointerSurfaceState> outputs)
    {
        if(!Reliable(before)||!Reliable(after))
            return(false,"Windows cursor state could not be read reliably around DXGI acquisition.");
        if(before.Visible!=after.Visible||(before.Visible&&before.Position!=after.Position))
            return(false,$"Windows cursor changed during DXGI acquisition ({before.Position}/{before.Visible} -> {after.Position}/{after.Visible}).");
        if(!after.Visible)return(true,"Windows cursor is not visible; no system-pointer pixels require exclusion.");
        var active=outputs.Where(x=>x.DesktopBounds.Contains(after.Position)).ToArray();
        if(active.Length!=1)
            return(false,$"Visible Windows cursor at {after.Position} could not be assigned to exactly one duplicated output.");
        if(active[0].SeparatePointerVisible is true)
            return(true,$"DXGI reports a separate hardware pointer for output {active[0].DesktopBounds}; acquired surface is cursor-free.");
        if(active[0].SeparatePointerVisible is false)
            return(false,$"DXGI reports no separate pointer for output {active[0].DesktopBounds}; Desktop Duplication may have drawn the visible Windows cursor into the acquired surface.");
        return(false,$"DXGI has no valid pointer update for output {active[0].DesktopBounds}; cursor exclusion cannot be proven for this frame.");
    }

    private static bool Reliable((Point Position,bool Visible,Point Hotspot,string Result) state)=>
        state.Result.StartsWith("flags=",StringComparison.Ordinal);
}

public sealed class DxgiDesktopDuplicationBackend : IScreenCaptureBackend
{
    private sealed class OutputCapture : IDisposable
    {
        internal readonly IDXGIOutput Output;internal readonly IDXGIOutputDuplication Duplication;internal readonly Rectangle DesktopBounds;
        internal ID3D11Texture2D? Staging;internal Bitmap? LastFrame;internal long LastFrameTimestamp;
        internal bool? SeparatePointerVisible;internal long LastMouseUpdateTime;
        internal OutputCapture(IDXGIOutput output,IDXGIOutputDuplication duplication,Rectangle bounds){Output=output;Duplication=duplication;DesktopBounds=bounds;}
        public void Dispose(){LastFrame?.Dispose();Staging?.Dispose();Duplication.Dispose();Output.Dispose();}
    }
    private ID3D11Device? _device;private ID3D11DeviceContext? _context;private readonly List<OutputCapture> _outputs=[];
    public string BackendName=>"DXGI Desktop Duplication";public bool IsAvailable{get;private set;}
    public void Initialize()
    {
        DisposeResources();var result=D3D11CreateDevice(null!,DriverType.Hardware,DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1,FeatureLevel.Level_11_0,FeatureLevel.Level_10_1,FeatureLevel.Level_10_0],out _device,out _context);
        result.CheckError();using var dxgiDevice=_device!.QueryInterface<IDXGIDevice>();using var adapter=dxgiDevice.GetAdapter();
        for(uint index=0;;index++)
        {
            var enumResult=adapter.EnumOutputs(index,out var output);if(enumResult.Failure)break;
            var description=output.Description;if(!description.AttachedToDesktop){output.Dispose();continue;}
            if(description.Rotation!=ModeRotation.Identity){output.Dispose();throw new NotSupportedException($"Rotated output {description.DeviceName} requires fallback capture.");}
            using var output1=output.QueryInterface<IDXGIOutput1>();var duplication=output1.DuplicateOutput(_device);
            var r=description.DesktopCoordinates;_outputs.Add(new(output,duplication,Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom)));
        }
        var union=_outputs.Select(x=>x.DesktopBounds).Aggregate(Rectangle.Union);
        var expected=SystemInformation.VirtualScreen;
        if(union!=expected||_outputs.Sum(x=>(long)x.DesktopBounds.Width*x.DesktopBounds.Height)<(long)expected.Width*expected.Height)
            throw new NotSupportedException($"DXGI adapter outputs do not cover virtual desktop. outputs={union}; virtual={expected}");
        IsAvailable=_outputs.Count>0;
    }
    public ScreenCaptureFrame CaptureFrame(Rectangle virtualBounds,long hotkeyTimestamp,CancellationToken cancellationToken)
    {
        if(!IsAvailable||_device is null||_context is null)throw new InvalidOperationException("DXGI Desktop Duplication is unavailable.");
        var cursorBefore=SystemCursorCaptureAudit.Snapshot();
        var acquire=Stopwatch.StartNew();var compose=Stopwatch.StartNew();var copies=0;long bytes=0,gpuCopyMs=0;var result=new Bitmap(virtualBounds.Width,virtualBounds.Height,PixelFormat.Format32bppArgb);
        try
        {
            using var destination=Graphics.FromImage(result);destination.Clear(Color.Black);
            foreach(var output in _outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();IDXGIResource? resource=null;var acquired=false;
                try
                {
                    var status=output.Duplication.AcquireNextFrame(120,out var frameInfo,out resource);
                    if(status.Failure)
                    {
                        // DXGI_ERROR_WAIT_TIMEOUT means the desktop has not changed. Reusing the last
                        // identical frame is correct; all other failures rebuild/fall back in the coordinator.
                        if(status.Code==unchecked((int)0x887A0027)&&output.LastFrame is not null)
                        {destination.DrawImageUnscaled(output.LastFrame,output.DesktopBounds.X-virtualBounds.X,output.DesktopBounds.Y-virtualBounds.Y);copies++;bytes+=(long)output.LastFrame.Width*output.LastFrame.Height*4;continue;}
                        status.CheckError();
                    }
                    acquired=true;
                    if(frameInfo.LastMouseUpdateTime!=0&&frameInfo.LastMouseUpdateTime>=output.LastMouseUpdateTime)
                    {
                        output.LastMouseUpdateTime=frameInfo.LastMouseUpdateTime;
                        output.SeparatePointerVisible=(bool)frameInfo.PointerPosition.Visible;
                    }
                    using var sourceTexture=resource!.QueryInterface<ID3D11Texture2D>();var desc=sourceTexture.Description;
                    if(output.Staging is null||output.Staging.Description.Width!=desc.Width||output.Staging.Description.Height!=desc.Height)
                    {output.Staging?.Dispose();output.Staging=_device.CreateTexture2D(new Texture2DDescription(desc.Format,desc.Width,desc.Height,1,1,BindFlags.None,ResourceUsage.Staging,CpuAccessFlags.Read));}
                    var copy=Stopwatch.StartNew();_context.CopyResource(output.Staging,sourceTexture);var mapped=_context.Map(output.Staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        var width=(int)desc.Width;var height=(int)desc.Height;using var monitorBitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb);var locked=monitorBitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
                        try{var row=new byte[width*4];for(var y=0;y<height;y++){Marshal.Copy(mapped.DataPointer+y*(int)mapped.RowPitch,row,0,row.Length);Marshal.Copy(row,0,locked.Scan0+y*locked.Stride,row.Length);}}
                        finally{monitorBitmap.UnlockBits(locked);}destination.DrawImageUnscaled(monitorBitmap,output.DesktopBounds.X-virtualBounds.X,output.DesktopBounds.Y-virtualBounds.Y);
                        output.LastFrame?.Dispose();output.LastFrame=new Bitmap(monitorBitmap);output.LastFrameTimestamp=Stopwatch.GetTimestamp();
                    }
                    finally{_context.Unmap(output.Staging,0);}
                    copy.Stop();gpuCopyMs+=copy.ElapsedMilliseconds;copies+=2;bytes+=(long)desc.Width*desc.Height*8;
                }
                finally{resource?.Dispose();if(acquired)output.Duplication.ReleaseFrame();}
            }
            var cursorAfter=SystemCursorCaptureAudit.Snapshot();
            var pointerDecision=DxgiCursorSurfacePolicy.Evaluate(cursorBefore,cursorAfter,_outputs.Select(x=>new DxgiOutputPointerSurfaceState(x.DesktopBounds,x.SeparatePointerVisible,x.LastMouseUpdateTime)).ToArray());
            if(!pointerDecision.Accept)
            {
                // The per-output cache was populated from this same acquisition. Never retain
                // a frame whose cursor-exclusion state was rejected, otherwise a later timeout
                // could resurrect pointer pixels from the rejected surface.
                foreach(var output in _outputs){output.LastFrame?.Dispose();output.LastFrame=null;output.LastFrameTimestamp=0;}
                throw new DxgiCursorExclusionUnavailableException(pointerDecision.Reason);
            }
            compose.Stop();acquire.Stop();var capturedAt=_outputs.Max(x=>x.LastFrameTimestamp);
            return new(result,new(BackendName,acquire.ElapsedMilliseconds,gpuCopyMs,compose.ElapsedMilliseconds,
                capturedAt<=0?0:Math.Max(0,ElapsedMs(capturedAt,hotkeyTimestamp)),capturedAt,hotkeyTimestamp,copies,bytes,Environment.CurrentManagedThreadId,false));
        }
        catch{result.Dispose();throw;}
    }
    public void Dispose(){DisposeResources();GC.SuppressFinalize(this);}
    private void DisposeResources(){IsAvailable=false;foreach(var output in _outputs)output.Dispose();_outputs.Clear();_context?.Dispose();_device?.Dispose();_context=null;_device=null;}
    private static long ElapsedMs(long start,long end)=>(long)((end-start)*1000d/Stopwatch.Frequency);
}
