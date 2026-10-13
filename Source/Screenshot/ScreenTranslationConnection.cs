using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal sealed record ScreenTranslationBlock(string Source,RectangleF Bounds);
internal sealed record ScreenTranslationFrame(CoverTarget Target,Size Pixels,Rectangle WindowBounds,
    ScreenTranslationBlock[] Blocks,DateTime CapturedUtc);

// This adapter has no native write permission. It can only publish screen text
// after an explicit manual request, independently of the native bridge.
internal sealed class ScreenTranslationConnection : IGameDataConnection
{
    private readonly string exe;
    private readonly ApiSettings settings;
    private readonly OcrRuntimeManager ocr;
    private readonly CancellationTokenSource lifetime=new();
    private readonly object gate=new();
    private CancellationTokenSource? capture;
    private Task? worker;
    private volatile bool enabled,connected=true;
    private ScreenTranslationFrame? frame;
    private string status="正在准备屏幕翻译…";
    private int epoch=1;
    private int disposed;
    internal ScreenTranslationFrame? Frame=>System.Threading.Volatile.Read(ref frame);
    internal string Status=>System.Threading.Volatile.Read(ref status);
    public bool Connected=>connected&&!lifetime.IsCancellationRequested;
    internal ScreenTranslationConnection(string exe,ApiSettings settings)
    {this.exe=exe;this.settings=ApiSettingsSnapshot.Copy(settings);ocr=new(AppContext.BaseDirectory,new OcrService());}
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out NativeRect rectangle,int size);
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    internal static bool IsForeground(CoverTarget target)=>GetForegroundWindow()==target.Window&&GameLibraryWindowIdentity.Matches(target);
    internal static bool IsFresh(DateTime capturedUtc,DateTime now)=>now>=capturedUtc&&now-capturedUtc<TimeSpan.FromSeconds(8);
    internal static bool CanDisplay(ScreenTranslationFrame frame)=>frame.Pixels.Width>0&&frame.Pixels.Height>0&&!frame.WindowBounds.IsEmpty&&IsFresh(frame.CapturedUtc,DateTime.UtcNow)
        &&IsForeground(frame.Target)&&frame.WindowBounds==BoundsOf(frame.Target);
    private static Rectangle BoundsOf(CoverTarget target)
    {
        if(DwmGetWindowAttribute(target.Window,9,out var r,Marshal.SizeOf<NativeRect>())!=0)return Rectangle.Empty;
        return Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);
    }
    public Task<JsonElement> RequestAsync(JsonObject request,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();if(!Connected)throw new IOException("屏幕翻译会话已结束。");
        object reply;
        switch(request["op"]?.GetValue<string>())
        {
            case "translationPrepare":
                lock(gate){if(!Connected)throw new IOException("屏幕翻译会话已结束。");enabled=false;epoch++;capture?.Cancel();System.Threading.Volatile.Write(ref frame,null);}
                reply=new{epoch};break;
            case "translationEnable":
                lock(gate){if(!Connected)throw new IOException("屏幕翻译会话已结束。");enabled=true;StartWorker();}reply=new{enabled=true};break;
            case "translationDisable":
                lock(gate){if(!Connected)throw new IOException("屏幕翻译会话已结束。");enabled=false;epoch++;capture?.Cancel();System.Threading.Volatile.Write(ref frame,null);}
                reply=new{restored=true,restorePending=0};break;
            case "translationPoll":
                if(enabled)StartWorker();
                var current=Frame;
                string[] texts=enabled&&current is not null&&CanDisplay(current)?current.Blocks.Select(b=>b.Source).Distinct().ToArray():[];
                reply=new{epoch,texts=Array.Empty<string>(),readOnlyTexts=texts,displayOnly=true,
                    runtimeWarning=System.Threading.Volatile.Read(ref status)};break;
            case "translationCatalog":reply=new{total=0,skipped=0,next=0,texts=Array.Empty<string>(),done=true};break;
            case "translationApply":throw new InvalidOperationException("屏幕译文没有游戏内部写回权限。");
            default:throw new NotSupportedException("屏幕翻译不支持此游戏操作。");
        }
        return Task.FromResult(JsonSerializer.SerializeToElement(reply));
    }
    private void StartWorker()
    {
        lock(gate)
        {
            if(!enabled||!Connected||worker is {IsCompleted:false})return;
            capture?.Dispose();var source=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);capture=source;
            var token=source.Token;var generation=epoch;
            worker=Task.Run(()=>CaptureLoopAsync(token,generation));
        }
    }
    private async Task WatchTargetAsync(CoverTarget target,Rectangle bounds,CancellationTokenSource operation)
    {
        try
        {
            while(true)
            {
                await Task.Delay(100,operation.Token);
                if(IsForeground(target)&&bounds==BoundsOf(target))continue;
                System.Threading.Volatile.Write(ref frame,null);operation.Cancel();break;
            }
        }
        catch(OperationCanceledException)when(operation.IsCancellationRequested){}
    }
    internal static ScreenTranslationBlock[] MatchStableBlocks(Bitmap original,Bitmap current,ScreenTranslationBlock[] blocks,CancellationToken token)
    {
        if(original.Size!=current.Size)return [];
        var area=new Rectangle(Point.Empty,original.Size);
        var before=original.LockBits(area,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try
        {
            var after=current.LockBits(area,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            try
            {
                var retained=new List<ScreenTranslationBlock>();
                foreach(var block in blocks.Take(64))
                {
                    token.ThrowIfCancellationRequested();
                    var box=Rectangle.Intersect(Rectangle.FromLTRB((int)Math.Floor(block.Bounds.Left),(int)Math.Floor(block.Bounds.Top),
                        (int)Math.Ceiling(block.Bounds.Right),(int)Math.Ceiling(block.Bounds.Bottom)),area);
                    if(box.Width<=0||box.Height<=0)continue;
                    var left=new byte[box.Width*4];var right=new byte[left.Length];var same=true;
                    for(var y=box.Top;y<box.Bottom;y++)
                    {
                        if((y&31)==0)token.ThrowIfCancellationRequested();
                        Marshal.Copy(before.Scan0+y*before.Stride+box.Left*4,left,0,left.Length);
                        Marshal.Copy(after.Scan0+y*after.Stride+box.Left*4,right,0,right.Length);
                        if(left.AsSpan().SequenceEqual(right))continue;same=false;break;
                    }
                    if(same)retained.Add(block);
                }
                return retained.ToArray();
            }
            finally{current.UnlockBits(after);}
        }
        finally{original.UnlockBits(before);}
    }
    private async Task CaptureLoopAsync(CancellationToken token,int generation)
    {
        string lastHash="";ScreenTranslationBlock[] lastBlocks=[];
        bool sawWindow=false;var started=Stopwatch.StartNew();
        try
        {
            while(enabled&&!token.IsCancellationRequested)
            {
                try
                {
                    var found=await Task.Run(()=>GameLibraryWindowIdentity.Find(exe),token);
                    if(found.Target is not {} target)
                    {
                        System.Threading.Volatile.Write(ref frame,null);
                        System.Threading.Volatile.Write(ref status,"屏幕翻译："+found.Message);
                        if(found.Code=="process-not-found"&&(sawWindow||started.Elapsed>TimeSpan.FromSeconds(30))){connected=false;break;}
                    }
                    else if(!IsForeground(target))
                    {
                        sawWindow=true;
                        System.Threading.Volatile.Write(ref frame,null);
                        System.Threading.Volatile.Write(ref status,"屏幕翻译已等待；回到该游戏窗口后自动继续。");
                    }
                    else
                    {
                        sawWindow=true;var bounds=BoundsOf(target);
                        // Cold OCR may need longer than a frame remains displayable.
                        // Let that bounded initialization finish, discard its old
                        // result, then sample again using the warm backend.
                        using var operation=CancellationTokenSource.CreateLinkedTokenSource(token);operation.CancelAfter(TimeSpan.FromSeconds(45));
                        var watch=WatchTargetAsync(target,bounds,operation);
                        try
                        {
                            var requestedUtc=DateTime.UtcNow;
                            var captured=await GameWindowCover.CaptureOcrFrameAsync(target,operation.Token);
                            using var image=captured.Image;
                            if(image is null)throw new IOException(captured.Message);
                            var sampledUtc=captured.CapturedUtc??requestedUtc;
                            if(!IsFresh(sampledUtc,DateTime.UtcNow)||!IsForeground(target)||bounds.IsEmpty||bounds!=BoundsOf(target))continue;
                            string hash=SessionServices.ComputeImageHash(image);
                            ScreenTranslationBlock[] displayBlocks;
                            if(hash!=lastHash)
                            {
                                System.Threading.Volatile.Write(ref frame,null);
                                System.Threading.Volatile.Write(ref status,"屏幕翻译：正在识别当前游戏文字…");
                                var result=await ocr.RecognizeWithFallbackAsync(settings.OcrEngine,image,settings.OcrLanguage,operation.Token,allowAutomaticWindowsFallback:true);
                                if(!string.IsNullOrWhiteSpace(result.Error))throw new IOException("屏幕文字识别未完成。");
                                operation.Token.ThrowIfCancellationRequested();
                                if(!IsFresh(sampledUtc,DateTime.UtcNow))continue;
                                lastBlocks=result.Blocks.OrderBy(b=>b.ReadingOrder).Where(b=>!string.IsNullOrWhiteSpace(b.NormalizedText)
                                    &&b.NormalizedText.Length<=6000&&float.IsFinite(b.BoundingBox.X)&&float.IsFinite(b.BoundingBox.Y)
                                    &&float.IsFinite(b.BoundingBox.Width)&&float.IsFinite(b.BoundingBox.Height)
                                    &&b.BoundingBox.Width>0&&b.BoundingBox.Height>0)
                                    .Take(64).Select(b=>new ScreenTranslationBlock(b.NormalizedText,RectangleF.Intersect(b.BoundingBox,new(Point.Empty,image.Size))))
                                    .Where(b=>b.Bounds.Width>0&&b.Bounds.Height>0).ToArray();
                                lastHash=hash;
                                // OCR can finish after a scene change in the same HWND. Recheck
                                // each text region, allowing animation elsewhere in the image.
                                var confirmed=await GameWindowCover.CaptureOcrFrameAsync(target,operation.Token);
                                using var currentImage=confirmed.Image;
                                if(currentImage is null||!IsFresh(sampledUtc,DateTime.UtcNow))continue;
                                displayBlocks=MatchStableBlocks(image,currentImage,lastBlocks,operation.Token);
                            }
                            else displayBlocks=lastBlocks;
                            operation.Token.ThrowIfCancellationRequested();
                            lock(gate)
                            {
                                if(enabled&&generation==epoch&&!token.IsCancellationRequested&&IsFresh(sampledUtc,DateTime.UtcNow)
                                    &&IsForeground(target)&&bounds==BoundsOf(target))
                                {
                                    System.Threading.Volatile.Write(ref frame,new(target,image.Size,bounds,displayBlocks,sampledUtc));
                                    System.Threading.Volatile.Write(ref status,displayBlocks.Length==0?"屏幕翻译：当前画面未识别到稳定文字，将继续检查。":"屏幕翻译：已识别文字，译文将在游戏画面上显示。");
                                }
                            }
                        }
                        finally{operation.Cancel();await watch;}
                    }
                }
                catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
                catch(Exception ex)
                {
                    System.Threading.Volatile.Write(ref frame,null);
                    System.Threading.Volatile.Write(ref status,"屏幕翻译暂未完成，将重试："+SafeDiagnosticOutput.ExceptionSummary(ex));
                }
                await Task.Delay(1800,token);
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        finally{System.Threading.Volatile.Write(ref frame,null);}
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        lock(gate){enabled=false;connected=false;epoch++;lifetime.Cancel();capture?.Cancel();System.Threading.Volatile.Write(ref frame,null);}
        _=DisposeWorkerAsync();
    }
    private async Task DisposeWorkerAsync()
    {
        var running=worker;try{if(running is not null)await running;}catch{}
        try{await ocr.DisposeAsync();}finally{capture?.Dispose();lifetime.Dispose();}
    }
}
