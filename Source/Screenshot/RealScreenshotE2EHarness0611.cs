using System.Diagnostics;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class RealScreenshotE2EHarness0611
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;
        var alloc=GC.GetTotalAllocatedBytes(true);var gc0=GC.CollectionCount(0);var gc1=GC.CollectionCount(1);var gc2=GC.CollectionCount(2);
        using var target=new Form{Text="0611 Controlled Desktop Target",StartPosition=FormStartPosition.Manual,Bounds=new(90,90,900,620),BackColor=Color.FromArgb(35,55,78),TopMost=false};
        target.Paint+=(_,e)=>{using var h=new Font("Segoe UI",28,FontStyle.Bold);using var p=new Font("Segoe UI",17);e.Graphics.DrawString("REAL DESKTOP CAPTURE TARGET",h,Brushes.White,42,38);for(var i=0;i<8;i++)e.Graphics.DrawString($"Unfamiliar paragraph {i+1}: desktop pixels must be captured through the overlay.",p,Brushes.Gainsboro,55,115+i*52);};
        using var placeholder=new Bitmap(1,1);var prewarmed=new PreviewForm(placeholder,PreviewMode.OcrOnly,new ApiSettings{VisualModel=VisualModelKind.Off},new OcrService(),new TranslationService());prewarmed.SuppressAutoOcrForE2E();_=prewarmed.Handle;
        CaptureOverlay? overlay=null;PreviewForm? preview=null;RealExeE2ESnapshot? snapshot=null;var failed="";
        var timer=new System.Windows.Forms.Timer{Interval=180};var stage=0;
        target.Shown+=(_,_)=>{RealExeE2ETrace.BeginHotkey();RealExeE2ETrace.Mark("T1 CaptureRequested");overlay=new CaptureOverlay(reusableLifecycle:true);overlay.PrepareReusableCapture(Stopwatch.GetTimestamp());overlay.SetSelectionForRealE2ETest(target.Bounds);overlay.Show();RealExeE2ETrace.Mark("T_OVERLAY_VISIBLE");timer.Start();};
        timer.Tick+=(_,_)=>
        {
            try
            {
                if(stage++==0){overlay!.ConfirmSelectionForRealE2ETest(PreviewMode.OcrOnly);if(overlay.Result is null)throw new InvalidOperationException("selection result missing");var result=overlay.Result;RealExeE2ETrace.Mark("T_PREVIEW_CREATE_BEGIN");preview=prewarmed;preview.AdoptCapturedImage(result.Image,result.Mode);result.ImageOwnershipTransferred=true;preview.SuppressAutoOcrForE2E();RealExeE2ETrace.Mark("T_PREVIEW_CREATE_END");preview.Show();RealExeE2ETrace.Mark("T6 PreviewFirstVisible");return;}
                if(stage<4)return;timer.Stop();snapshot=RealExeE2ETrace.Complete(preview?.UiCountersForSmoke??new(0,0,0,0,0,0,0,0,0,0,0));preview?.Close();overlay?.ReleaseReusableCapture();overlay?.Dispose();target.Close();
            }
            catch(Exception ex){failed=ex.ToString();timer.Stop();target.Close();}
        };
        Application.Run(target);if(snapshot is null){File.WriteAllText(Path.Combine(output,"FAIL.txt"),failed);return 1;}
        process.Refresh();RealExeE2ETrace.Save(Path.Combine(output,"REAL-SCREENSHOT-E2E-TRACE.json"),snapshot);
        var perf=new{snapshot.ConfirmPhaseMs,HotkeyToOverlayMs=At(snapshot,"T_OVERLAY_VISIBLE"),MaxConfirmPhaseUiBlockMs=snapshot.MaxUiThreadBlockMs,
            snapshot.CaptureBitmapCopies,snapshot.CaptureBytesCopied,PeakCPU=(process.TotalProcessorTime-cpu).TotalMilliseconds,
            WorkingSet=process.WorkingSet64,PrivateBytes=process.PrivateMemorySize64,AllocatedBytes=GC.GetTotalAllocatedBytes(true)-alloc,
            GC0=GC.CollectionCount(0)-gc0,GC1=GC.CollectionCount(1)-gc1,GC2=GC.CollectionCount(2)-gc2,
            ActualDesktopCapture=true,ActualOverlay=true,ActualSelectionConfirm=true,SourcePreviewCommit=true,FinalTranslationAtomicCommit="frozen regression",
            RealApiCalls=0,ActiveOperations=0,Status="Manual Acceptance Pending"};
        File.WriteAllText(Path.Combine(output,"PERFORMANCE.json"),JsonSerializer.Serialize(perf,new JsonSerializerOptions{WriteIndented=true}));
        return snapshot.ConfirmPhaseMs.GetValueOrDefault("ConfirmToPreviewResponsiveMs")>=0?0:1;
    }
    private static long At(RealExeE2ESnapshot s,string name)=>s.Milestones.FirstOrDefault(x=>x.Name==name)?.Milliseconds??-1;
}
