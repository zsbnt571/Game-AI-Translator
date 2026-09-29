using System.Diagnostics;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed record E2EMilestone(string Name,long Milliseconds,int ThreadId);
public sealed record PreviewUiCounters(int RightTextSetCount,int RightTextAppendCount,int ImageAssignCount,
    int LayoutCount,int InvalidateCount,int RefreshCount,int PaintCount,int ScrollExtentChangeCount,
    int DisplayScaleChangeCount,long LayoutVersion,int VisibleIntermediateTranslationFrames,
    int RightTranslationFinalSetCount=0,int RightPanelWidthChangeAfterFinalText=0,
    int RightFontChangeAfterFinalText=0,int RightLayoutEventAfterFinalText=0,
    int RightScrollExtentChangeAfterFinalText=0);

public sealed class RealExeE2ESnapshot
{
    public string OperationId { get; init; }="";
    public IReadOnlyList<E2EMilestone> Milestones { get; init; }=[];
    public long HotkeyToStableMs { get; init; }
    public long MaxUiThreadBlockMs { get; init; }
    public int UiThreadBlockEvents { get; init; }
    public int CaptureBitmapCopies { get; init; }
    public long CaptureBytesCopied { get; init; }
    public IReadOnlyDictionary<string,long> ConfirmPhaseMs { get; init; }=new Dictionary<string,long>();
    public ScreenCaptureMetrics? Capture { get; init; }
    public PreviewUiCounters Ui { get; init; }=new(0,0,0,0,0,0,0,0,0,0,0);
}

public static class RealExeE2ETrace
{
    private static readonly object Gate=new();
    private static Stopwatch? _watch;private static string _operationId="";private static readonly List<E2EMilestone> Marks=[];
    private static System.Windows.Forms.Timer? _uiPulse;private static long _lastPulse;private static long _maxBlock;private static int _blockEvents,_copies;private static long _bytes;private static ScreenCaptureMetrics? _capture;
    public static bool Active{get{lock(Gate)return _watch is not null;}}
    public static ScreenCaptureMetrics? CurrentCaptureMetrics{get{lock(Gate)return _capture;}}
    public static void BeginHotkey()
    {
        lock(Gate){_uiPulse?.Dispose();_operationId=Guid.NewGuid().ToString("N");_watch=Stopwatch.StartNew();Marks.Clear();_copies=0;_bytes=0;_capture=null;_maxBlock=0;_blockEvents=0;MarkUnsafe("T0 HotkeyReceived");_lastPulse=Stopwatch.GetTimestamp();_uiPulse=new(){Interval=16};_uiPulse.Tick+=(_,_)=>Pulse();_uiPulse.Start();}
    }
    public static void Mark(string name){lock(Gate){if(_watch is null)return;MarkUnsafe(name);if(name.StartsWith("T_CONFIRM",StringComparison.Ordinal)){_maxBlock=0;_blockEvents=0;_lastPulse=Stopwatch.GetTimestamp();}}}
    public static void AddBitmapCopy(Size size){lock(Gate){if(_watch is null)return;_copies++;_bytes+=(long)Math.Max(0,size.Width)*Math.Max(0,size.Height)*4;}}
    public static void SetCaptureMetrics(ScreenCaptureMetrics value){lock(Gate){_capture=value;_copies+=value.BitmapCopyCount;_bytes+=value.BytesCopied;MarkUnsafe("T_CAPTURE_BACKEND_COMPLETE "+value.CaptureBackend);}}
    public static RealExeE2ESnapshot Complete(PreviewUiCounters ui)
    {
        lock(Gate){if(_watch is null)return new();MarkUnsafe("T19 PreviewStable");MarkUnsafe("T20 PreviewResponsive");_watch.Stop();_uiPulse?.Stop();_uiPulse?.Dispose();_uiPulse=null;long At(string n)=>Marks.FirstOrDefault(x=>x.Name==n)?.Milliseconds??-1;var confirm=At("T_CONFIRM SelectionConfirmPressed");long Since(string n){var v=At(n);return confirm>=0&&v>=0?v-confirm:-1;}var phase=new Dictionary<string,long>{["ConfirmToOverlayDisposedMs"]=Since("T_OVERLAY_DISPOSE_END"),["ConfirmToCaptureFinalizedMs"]=Since("T_CAPTURE_FINALIZE_END"),["ConfirmToPreviewCreatedMs"]=Since("T_PREVIEW_CREATE_END"),["ConfirmToPreviewVisibleMs"]=Since("T6 PreviewFirstVisible"),["ConfirmToPreviewResponsiveMs"]=Since("T_PREVIEW_RESPONSIVE"),["ConfirmToOcrQueuedMs"]=Since("T7 OCRQueued"),["HotkeyToFrozenFrameVisibleMs"]=At("T_OVERLAY_VISIBLE")};var value=new RealExeE2ESnapshot{OperationId=_operationId,Milestones=Marks.ToArray(),HotkeyToStableMs=_watch.ElapsedMilliseconds,MaxUiThreadBlockMs=_maxBlock,UiThreadBlockEvents=_blockEvents,CaptureBitmapCopies=_copies,CaptureBytesCopied=_bytes,ConfirmPhaseMs=phase,Capture=_capture,Ui=ui};_watch=null;return value;}
    }
    public static void Save(string path,RealExeE2ESnapshot value){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));}
    private static void Pulse(){lock(Gate){if(_watch is null)return;var now=Stopwatch.GetTimestamp();var elapsed=(long)((now-_lastPulse)*1000d/Stopwatch.Frequency);var block=Math.Max(0,elapsed-16);if(block>_maxBlock)_maxBlock=block;if(block>50)_blockEvents++;_lastPulse=now;}}
    private static void MarkUnsafe(string name)=>Marks.Add(new(name,_watch!.ElapsedMilliseconds,Environment.CurrentManagedThreadId));
}
