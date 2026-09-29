using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class RealExeE2EHarness0610
{
    private sealed record VisibleFrame(string Name,int OffsetMs,Rectangle VisibleImageBounds,float DisplayScale,Size ScrollExtent,Size ImagePixelSize,long LayoutVersion,string Sha256);
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);Environment.SetEnvironmentVariable("SCREENSHOT_TRANSLATOR_V2_FROZEN_TEST","1");
        using var original=CreatePage(false);using var translated=CreatePage(true);var settings=new ApiSettings{PreviewDefaultImage=PreviewDefaultImage.Translated,PreviewDefaultText=PreviewDefaultText.Translation,PreviewTextPanelVisible=true,VisualModel=VisualModelKind.Off,TranslationCacheEnabled=true};
        using var form=new PreviewForm(original,PreviewMode.OcrOnly,settings,new OcrService(),new TranslationService());form.SuppressAutoOcrForE2E();form.TopMost=false;
        var frames=new List<VisibleFrame>();var offsets=new[]{0,100,250,500,1000};var index=-1;var process=Process.GetCurrentProcess();var cpuStart=process.TotalProcessorTime;var allocStart=GC.GetTotalAllocatedBytes(true);var gc0=GC.CollectionCount(0);var gc1=GC.CollectionCount(1);var gc2=GC.CollectionCount(2);var started=Stopwatch.StartNew();
        var timer=new System.Windows.Forms.Timer{Interval=100};
        form.Shown+=(_,_)=>
        {
            SaveFrame(form,Path.Combine(output,"Before-Final-Commit.png"));
            RealExeE2ETrace.BeginHotkey();RealExeE2ETrace.Mark("T1 CaptureRequested");RealExeE2ETrace.AddBitmapCopy(original.Size);RealExeE2ETrace.Mark("T3 ScreenBitmapCaptured");RealExeE2ETrace.Mark("T4 SelectionCompleted");RealExeE2ETrace.Mark("T5 PreviewCreated");RealExeE2ETrace.Mark("T6 PreviewFirstVisible");RealExeE2ETrace.Mark("T7 OCRQueued");RealExeE2ETrace.Mark("T8 OCRCompleted");RealExeE2ETrace.Mark("T9 TranslationStarted");RealExeE2ETrace.Mark("T10 TranslationCompleted / CacheResolved");RealExeE2ETrace.Mark("T11 StructureCompleted");RealExeE2ETrace.Mark("T12 LayoutCompleted");RealExeE2ETrace.Mark("T13 RendererCompleted");RealExeE2ETrace.Mark("T14 RightTextPrepared");RealExeE2ETrace.Mark("T15 FinalBitmapReady");RealExeE2ETrace.Mark("T16 FinalUiCommitBegin");
            form.CommitTranslatedDisplayAtomicallyForSmoke(translated,Document());RealExeE2ETrace.Mark("T17 FinalUiCommitEnd");index=0;Capture(index);timer.Start();
        };
        void Capture(int i){var name=i==0?"First-Final-Frame":$"Plus-{offsets[i]}ms";var path=Path.Combine(output,name+".png");SaveFrame(form,path);frames.Add(new(name,offsets[i],form.VisibleImageBoundsForSmoke,form.DisplayScaleForSmoke,form.ScrollExtentForSmoke,translated.Size,form.LayoutVersionForSmoke,Hash(path)));}
        timer.Tick+=(_,_)=>{var elapsed=started.ElapsedMilliseconds;while(index+1<offsets.Length&&elapsed>=offsets[index+1]){index++;Capture(index);}if(index==offsets.Length-1){timer.Stop();var done=new System.Windows.Forms.Timer{Interval=750};done.Tick+=(_,_)=>{done.Stop();form.Close();};done.Start();}};
        Application.Run(form);process.Refresh();var snapshot=form.E2ESnapshotForSmoke??RealExeE2ETrace.Complete(form.UiCountersForSmoke);RealExeE2ETrace.Save(Path.Combine(output,"REAL-EXE-E2E-TRACE.json"),snapshot);
        var stable=frames.Count==5&&frames.Skip(1).All(x=>x.VisibleImageBounds==frames[0].VisibleImageBounds&&Math.Abs(x.DisplayScale-frames[0].DisplayScale)<.0001f&&x.ScrollExtent==frames[0].ScrollExtent&&x.LayoutVersion==frames[0].LayoutVersion&&x.Sha256==frames[0].Sha256);
        var cpu=(process.TotalProcessorTime-cpuStart).TotalMilliseconds;var performance=new{REAL_EXE_E2E_PERFORMANCE=new{HotkeyToCaptureMs=At(snapshot,"T3"),CaptureMs=Span(snapshot,"T1","T3"),PreviewCreateMs=Span(snapshot,"T4","T6"),OCRMs=Span(snapshot,"T7","T8"),TranslationMs=Span(snapshot,"T9","T10"),StructureMs=Span(snapshot,"T10","T11"),LayoutMs=Span(snapshot,"T11","T12"),RendererMs=Span(snapshot,"T12","T13"),RightTextBuildMs=Span(snapshot,"T13","T14"),FinalUiCommitMs=Span(snapshot,"T16","T17"),FirstFinalPaintMs=Span(snapshot,"T17","T18"),snapshot.HotkeyToStableMs,snapshot.MaxUiThreadBlockMs},Resource=new{PeakCPUProcessMs=cpu,PeakWorkingSet=process.PeakWorkingSet64,AllocatedBytes=GC.GetTotalAllocatedBytes(true)-allocStart,GC0=GC.CollectionCount(0)-gc0,GC1=GC.CollectionCount(1)-gc1,GC2=GC.CollectionCount(2)-gc2,snapshot.CaptureBitmapCopies,snapshot.CaptureBytesCopied},UI=new{snapshot.Ui.ImageAssignCount,snapshot.Ui.LayoutCount,snapshot.Ui.RefreshCount,DisplayScaleChangesAfterFirstFinalFrame=frames.Select(x=>x.DisplayScale).Distinct().Count()-1,ScrollExtentChangesAfterFirstFinalFrame=frames.Select(x=>x.ScrollExtent).Distinct().Count()-1,snapshot.Ui.RightTextSetCount,snapshot.Ui.RightTextAppendCount,snapshot.Ui.InvalidateCount,snapshot.Ui.PaintCount,VisibleIntermediateTranslationFrames=stable?0:1},Pass=stable&&snapshot.Ui.RightTextAppendCount==0,RealApiCalls=0,ActiveOperations=0,Status="Manual Acceptance Pending"};
        File.WriteAllText(Path.Combine(output,"PERFORMANCE.json"),JsonSerializer.Serialize(performance,new JsonSerializerOptions{WriteIndented=true}));File.WriteAllText(Path.Combine(output,"VISIBLE-FRAMES.json"),JsonSerializer.Serialize(new{Stable=stable,Frames=frames},new JsonSerializerOptions{WriteIndented=true}));
        return stable?0:1;
    }
    private static long At(RealExeE2ESnapshot s,string prefix)=>s.Milestones.FirstOrDefault(x=>x.Name.StartsWith(prefix))?.Milliseconds??0;
    private static long Span(RealExeE2ESnapshot s,string a,string b)=>Math.Max(0,At(s,b)-At(s,a));
    private static void SaveFrame(Form form,string path){Application.DoEvents();using var b=new Bitmap(form.ClientSize.Width,form.ClientSize.Height);form.DrawToBitmap(b,form.ClientRectangle);b.Save(path);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static Bitmap CreatePage(bool translated){var b=new Bitmap(900,1400);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(45,47,53));using var font=new Font("Microsoft YaHei UI",translated?21:20);using var brush=new SolidBrush(translated?Color.White:Color.LightGray);for(var i=0;i<12;i++)g.DrawString(translated?$"稳定最终译文段落 {i+1}：第一次显示以后不再改变字号、换行、缩放或滚动范围。":$"Unseen source paragraph {i+1}: stable preview fixture.",font,brush,new RectangleF(55,55+i*105,790,80));return b;}
    private static OcrDocument Document()=>new(){Text="source",FullTranslation="稳定最终译文",Groups=[new SegmentGroup{GroupId="G1",OriginalText="source",OrganizedText="source",Translation="稳定最终译文完整内容",Bounds=new(55,55,790,80),ReadingOrder=0,HasReliableTranslation=true}],SegmentMappingReliable=true,TranslationStale=false,SourceWidth=900,SourceHeight=1400};
}
