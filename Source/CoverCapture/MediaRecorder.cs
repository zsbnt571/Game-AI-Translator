using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScreenshotTranslationUiTester;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using WinRT;
using static Vortice.Direct3D11.D3D11;

namespace FusionCoverCapture;

// A persistent WGC session feeds the Windows H.264 encoder. The game is never
// injected into, and no desktop/other-window fallback is allowed.
internal static partial class Program
{
    [DllImport("d3d11.dll")]private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr surface,out IntPtr result);
    private sealed record RecordReply(string Code,string Message,int Width=0,int Height=0,long Frames=0,double Seconds=0,string Reason="",int Error=0);
    private static void Send(RecordReply value){Console.WriteLine(JsonSerializer.Serialize(value));Console.Out.Flush();}
    private static int Record(string[] args)
    {
        using var stop=new CancellationTokenSource();
        using var safety=new System.Threading.Timer(_=>Environment.Exit(3),null,15000,Timeout.Infinite);
        string reason="manual";
        try
        {
            if(Console.ReadLine()!="GO")return 2;
            var target=new CoverTarget(new IntPtr(long.Parse(args[0])),int.Parse(args[1]),Path.GetFullPath(args[2]),long.Parse(args[3]));
            var path=Path.GetFullPath(args[5]);
            if(!path.EndsWith(".recording.mp4",StringComparison.OrdinalIgnoreCase)||File.Exists(path))throw new IOException("Invalid recording destination");
            if(!CoverWindowIdentity.Matches(target))throw new IOException("Game window is unavailable");
            _=Task.Run(()=>{try{while(Console.ReadLine() is {} line){if(line=="STOP")break;}}catch{}finally{stop.Cancel();safety.Change(30000,Timeout.Infinite);}});
            Marshal.ThrowExceptionForHR(RoInitialize(1));
            try
            {
                var result=RecordAsync(target,path,stop.Token,r=>reason=r,()=>safety.Change(Timeout.Infinite,Timeout.Infinite)).GetAwaiter().GetResult();
                Send(result with{Reason=reason});return result.Code=="recorded"?0:1;
            }
            finally{RoUninitialize();}
        }
        catch(Exception ex){Send(new("record-failed","录像未完成，已写入的临时文件保留。",Reason:reason,Error:ex.HResult));return 1;}
    }
    private static async Task<RecordReply> RecordAsync(CoverTarget target,string path,CancellationToken stop,Action<string> stopped,Action ready)
    {
        if(!GraphicsCaptureSession.IsSupported())throw new NotSupportedException("Window capture unavailable");
        D3D11CreateDevice(null!,DriverType.Hardware,DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0,FeatureLevel.Level_10_1,FeatureLevel.Level_10_0],out ID3D11Device device,out ID3D11DeviceContext context).CheckError();
        using(device)using(context)using(var dxgi=device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer,out var abi));
            IDirect3DDevice winrt;try{winrt=MarshalInterface<IDirect3DDevice>.FromAbi(abi);}finally{Marshal.Release(abi);}
            using(winrt)
            {
                var item=Item(target.Window);var size=item.Size;
                if(!IsCaptureSizeAllowed(size.Width,size.Height,true))throw new IOException("Window size unsupported");
                // Even output dimensions are required by H.264; trim at most one
                // edge pixel. WGC window content includes its native border.
                int width=size.Width&~1,height=size.Height&~1;
                if(width<32||height<18)throw new IOException("Window too small");
                using var pool=Direct3D11CaptureFramePool.CreateFreeThreaded(winrt,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,size);
                using var capture=pool.CreateCaptureSession(item);
                if(OperatingSystem.IsWindowsVersionAtLeast(10,0,19041))capture.IsCursorCaptureEnabled=false;
                capture.StartCapture();
                using var latest=device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,(uint)width,(uint)height,1,1,BindFlags.ShaderResource));
                bool hasFrame=false,finished=false;Exception? failure=null;long frames=0;double recordedSeconds=0;
                var clock=Stopwatch.StartNew();var gate=new object();
                var raw=VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8,(uint)width,(uint)height);
                raw.FrameRate.Numerator=30;raw.FrameRate.Denominator=1;
                var source=new MediaStreamSource(new VideoStreamDescriptor(raw)){BufferTime=TimeSpan.Zero,CanSeek=false};
                source.Starting+=(_,a)=>a.Request.SetActualStartPosition(TimeSpan.Zero);
                source.SampleRequested+=(_,a)=>
                {
                    lock(gate)
                    {
                        if(finished){a.Request.Sample=null;return;}
                        try
                        {
                            if(stop.IsCancellationRequested){finished=true;a.Request.Sample=null;return;}
                            if(!CoverWindowIdentity.Matches(target)){stopped("window-unavailable");finished=true;a.Request.Sample=null;return;}
                            if(clock.Elapsed.TotalHours>=2){stopped("duration-limit");finished=true;a.Request.Sample=null;return;}
                            if(frames%90==0&&new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace<256L*1024*1024)
                            {stopped("disk-space");finished=true;a.Request.Sample=null;return;}
                            var due=frames*1000d/30;
                            var delay=due-clock.Elapsed.TotalMilliseconds;
                            if(delay>1&&stop.WaitHandle.WaitOne((int)Math.Min(delay,1000))){finished=true;a.Request.Sample=null;return;}
                            Direct3D11CaptureFrame? frame=null;
                            var deadline=Stopwatch.StartNew();
                            do
                            {
                                frame=pool.TryGetNextFrame();
                                if(frame is not null||hasFrame)break;
                                if(stop.WaitHandle.WaitOne(10)){finished=true;a.Request.Sample=null;return;}
                            }while(deadline.ElapsedMilliseconds<5000);
                            using(frame)
                            {
                                if(frame is not null)
                                {
                                    if(frame.ContentSize.Width!=size.Width||frame.ContentSize.Height!=size.Height)
                                    {stopped("window-resized");finished=true;a.Request.Sample=null;return;}
                                    var access=frame.Surface.As<ISurfaceAccess>();var textureId=typeof(ID3D11Texture2D).GUID;
                                    // The interface wrapper's managed GUID isn't guaranteed to
                                    // carry its native IID, so use the published D3D11 IID.
                                    textureId=new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
                                    access.GetInterface(ref textureId,out var ptr);
                                    using var texture=new ID3D11Texture2D(ptr);
                                    context.CopySubresourceRegion(latest,0,0,0,0,texture,0,new Vortice.Mathematics.Box(0,0,0,width,height,1));
                                    hasFrame=true;
                                }
                            }
                            if(!hasFrame)throw new IOException("No game frame received");
                            // Each submitted sample owns a separate texture. Never return a
                            // pooled capture surface while the encoder still references it.
                            var owned=device.CreateTexture2D(latest.Description);context.CopyResource(owned,latest);
                            IDirect3DSurface? surface=null;
                            try
                            {
                                using var nativeSurface=owned.QueryInterface<IDXGISurface>();
                                Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(nativeSurface.NativePointer,out var surfaceAbi));
                                try{surface=MarshalInterface<IDirect3DSurface>.FromAbi(surfaceAbi);}finally{Marshal.Release(surfaceAbi);}
                                var timestamp=frames==0?TimeSpan.Zero:TimeSpan.FromMilliseconds(clock.Elapsed.TotalMilliseconds);
                                var sample=MediaStreamSample.CreateFromDirect3D11Surface(surface,timestamp);
                                sample.Duration=TimeSpan.FromSeconds(1d/30);
                                recordedSeconds=timestamp.TotalSeconds+sample.Duration.TotalSeconds;
                                var ownedSurface=surface;sample.Processed+=(_,_)=>{ownedSurface.Dispose();owned.Dispose();};
                                a.Request.Sample=sample;frames++;
                                if(frames==1){clock.Restart();ready();Send(new("recording","正在录制游戏画面（无声音）",width,height));}
                            }
                            catch{surface?.Dispose();owned.Dispose();throw;}
                        }
                        catch(Exception ex){failure=ex;finished=true;a.Request.Sample=null;}
                    }
                };
                using(var create=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){}
                var file=await StorageFile.GetFileFromPathAsync(path);
                using(var output=await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var profile=MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);profile.Audio=null;
                    profile.Video.Width=(uint)width;profile.Video.Height=(uint)height;
                    profile.Video.FrameRate.Numerator=30;profile.Video.FrameRate.Denominator=1;
                    profile.Video.Bitrate=(uint)Math.Clamp((long)width*height*3,2_000_000,16_000_000);
                    var encoder=new MediaTranscoder{HardwareAccelerationEnabled=true};
                    var prepared=await encoder.PrepareMediaStreamSourceTranscodeAsync(source,output,profile);
                    if(!prepared.CanTranscode)throw new IOException("Encoder: "+prepared.FailureReason);
                    await prepared.TranscodeAsync();await output.FlushAsync();
                }
                if(failure is not null)throw failure;
                return new(frames>0?"recorded":"record-empty",frames>0?"录像已保存":"录像没有取得画面",width,height,frames,recordedSeconds);
            }
        }
    }
}
