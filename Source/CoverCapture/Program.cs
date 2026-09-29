using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using ScreenshotTranslationUiTester;
using static Vortice.Direct3D11.D3D11;

namespace FusionCoverCapture;

internal sealed record Reply(string Code,string Message,int Width=0,int Height=0,long ElapsedMs=0,string? Image=null,int Error=0,string Quality="",int RejectedFrames=0);
internal static class Program
{
    [ComImport,Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IItemInterop
    {
        void CreateForWindow(IntPtr hwnd,ref Guid iid,out IntPtr result);
        void CreateForMonitor(IntPtr monitor,ref Guid iid,out IntPtr result);
    }
    [ComImport,Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISurfaceAccess {void GetInterface(ref Guid iid,out IntPtr result);}
    [DllImport("combase.dll",CharSet=CharSet.Unicode)]private static extern int WindowsCreateString(string source,int length,out IntPtr value);
    [DllImport("combase.dll")]private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")]private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")]private static extern void RoUninitialize();
    [DllImport("combase.dll")]private static extern int RoGetActivationFactory(IntPtr name,ref Guid iid,out IntPtr factory);
    [DllImport("d3d11.dll")]private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi,out IntPtr device);
    private static int Main(string[] args)
    {
        var elapsed=Stopwatch.StartNew();
        // Parent assigns a kill-on-close Job before sending GO. No graphics work before this handshake.
        using var watchdog=new System.Threading.Timer(_=>Environment.Exit(3),null,9000,Timeout.Infinite);
        Reply reply;
        try
        {
            if(Console.ReadLine()!="GO"||args.Length!=4)return 2;
            var target=new CoverTarget(new IntPtr(long.Parse(args[0])),int.Parse(args[1]),Path.GetFullPath(args[2]),long.Parse(args[3]));
            Marshal.ThrowExceptionForHR(RoInitialize(1));
            try{reply=Capture(target);}finally{RoUninitialize();}
        }
        catch(Exception ex){reply=new("backend-error","窗口捕获调用失败",Error:ex.HResult);}
        Console.WriteLine(JsonSerializer.Serialize(reply with{ElapsedMs=elapsed.ElapsedMilliseconds}));
        return 0;
    }
    private static GraphicsCaptureItem Item(IntPtr hwnd)
    {
        const string name="Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name,name.Length,out var hstring));
        IntPtr factory=IntPtr.Zero,item=IntPtr.Zero;IItemInterop? interop=null;
        try
        {
            var iid=typeof(IItemInterop).GUID;Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring,ref iid,out factory));
            interop=(IItemInterop)Marshal.GetObjectForIUnknown(factory);
            var itemIid=new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            interop.CreateForWindow(hwnd,ref itemIid,out item);
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally{if(item!=IntPtr.Zero)Marshal.Release(item);if(interop is not null)Marshal.ReleaseComObject(interop);if(factory!=IntPtr.Zero)Marshal.Release(factory);WindowsDeleteString(hstring);}
    }
    private static Reply Capture(CoverTarget target)
    {
        if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,18362)||!GraphicsCaptureSession.IsSupported())
            return new("unsupported","系统或图形设备不支持窗口捕获");
        if(!CoverWindowIdentity.Matches(target))return new("window-changed","游戏窗口已退出或改变");
        D3D11CreateDevice(null!,DriverType.Hardware,DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0,FeatureLevel.Level_10_1,FeatureLevel.Level_10_0],out ID3D11Device device,out ID3D11DeviceContext context).CheckError();
        using(device)using(context)
        using(var dxgi=device!.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer,out var abi));
            IDirect3DDevice winrtDevice;
            try{winrtDevice=MarshalInterface<IDirect3DDevice>.FromAbi(abi);}finally{Marshal.Release(abi);}
            using(winrtDevice)
            {
                var item=Item(target.Window);var size=item.Size;
                if(size.Width<=0||size.Height<=0||size.Width>8192||size.Height>8192||(long)size.Width*size.Height>16_777_216)
                    return new("invalid-size","游戏窗口尺寸不可用或超过上限",size.Width,size.Height);
                using var pool=Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,size);
                using var session=pool.CreateCaptureSession(item);
                if(OperatingSystem.IsWindowsVersionAtLeast(10,0,19041)&&Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession","IsCursorCaptureEnabled"))session.IsCursorCaptureEnabled=false;
                session.StartCapture();
                var clock=Stopwatch.StartNew();
                CoverQualityResult? lastQuality=null;var rejected=0;var samples=0;
                while(clock.ElapsedMilliseconds<4000)
                {
                    if(!CoverWindowIdentity.Matches(target))return new("window-changed","游戏窗口已退出或改变");
                    using var frame=pool.TryGetNextFrame();
                    if(frame is null){Thread.Sleep(40);continue;}
                    var actual=frame.ContentSize;
                    if(actual.Width<=0||actual.Height<=0||actual.Width>size.Width||actual.Height>size.Height)return new("size-changed","窗口正在调整大小，请稍后更新");
                    var access=frame.Surface.As<ISurfaceAccess>();var textureId=new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
                    access.GetInterface(ref textureId,out var textureAbi);
                    using var source=new ID3D11Texture2D(textureAbi);
                    var desc=source.Description;
                    if(desc.Format!=Format.B8G8R8A8_UNorm||actual.Width>desc.Width||actual.Height>desc.Height)
                        return new("invalid-surface","窗口画面格式或尺寸不可用",actual.Width,actual.Height);
                    using var staging=device.CreateTexture2D(new Texture2DDescription(desc.Format,desc.Width,desc.Height,1,1,BindFlags.None,ResourceUsage.Staging,CpuAccessFlags.Read));
                    context!.CopyResource(staging,source);
                    // Native Map is contained by the process deadline + Job, never orphaned in the desktop.
                    var mapped=context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
                    // WGC BGRA surface is premultiplied; preserve alpha so transparent RGB
                    // and pixels outside ContentSize cannot become fake visible detail.
                    using var bitmap=new Bitmap(actual.Width,actual.Height,PixelFormat.Format32bppPArgb);
                    try
                    {
                        var locked=bitmap.LockBits(new Rectangle(Point.Empty,bitmap.Size),ImageLockMode.WriteOnly,PixelFormat.Format32bppPArgb);
                        try{
                            var row=new byte[actual.Width*4];
                            for(var y=0;y<actual.Height;y++){
                                Marshal.Copy(mapped.DataPointer+y*(int)mapped.RowPitch,row,0,row.Length);
                                // Clamp premultiplied components; fully transparent storage has no visible RGB.
                                for(var x=0;x<row.Length;x+=4){var alpha=row[x+3];for(var c=0;c<3;c++)row[x+c]=Math.Min(row[x+c],alpha);}
                                Marshal.Copy(row,0,locked.Scan0+y*locked.Stride,row.Length);
                            }
                        }finally{bitmap.UnlockBits(locked);}
                    }
                    finally{context.Unmap(staging,0);}
                    if(!CoverWindowIdentity.Matches(target))return new("window-changed","游戏窗口已退出或改变");
                    using var small=CoverQuality.VisibleThumbnail(bitmap);
                    using var bytes=new MemoryStream();small.Save(bytes,ImageFormat.Jpeg);
                    bytes.Position=0;
                    using var encoded=Image.FromStream(bytes);
                    lastQuality=CoverQuality.Analyze(encoded);samples++;
                    if(!lastQuality.Usable)
                    {
                        rejected++;
                        if(samples>=8)break;
                        // Keep one bounded session for subsequent frames, not one process per frame.
                        Thread.Sleep(350);continue;
                    }
                    return new("frame-ready","已取得窗口帧",actual.Width,actual.Height,Image:Convert.ToBase64String(bytes.ToArray()),Quality:lastQuality.Diagnostic,RejectedFrames:rejected);
                }
                if(lastQuality is not null)return new("invalid-frame","尚未取得有效画面，请在游戏画面可用时更新",size.Width,size.Height,Quality:lastQuality.Diagnostic,RejectedFrames:rejected);
                return new("first-frame-timeout","等待游戏窗口首帧超时，请在窗口可用时更新",size.Width,size.Height);
            }
        }
    }
}
