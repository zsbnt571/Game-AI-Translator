using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class Repair0612TargetedTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var failures=new List<string>();var report=new List<object>();
        var environmentNotes=new List<string>();
        try
        {
            try
            {
                using var probe=new DxgiDesktopDuplicationBackend();probe.Initialize();
                using var coordinator=new ScreenCaptureCoordinator();
                for(var i=0;i<10;i++)
                {
                    var requested=Stopwatch.GetTimestamp();var dispatch=Stopwatch.StartNew();var task=coordinator.CaptureAsync(SystemInformation.VirtualScreen,requested);var callerBlock=dispatch.Elapsed.TotalMilliseconds;
                    var frame=task.GetAwaiter().GetResult();using(frame.Bitmap)
                    {if(i==0)frame.Bitmap.Save(Path.Combine(output,"capture-first.png"),ImageFormat.Png);if(frame.Bitmap.Size!=SystemInformation.VirtualScreen.Size)failures.Add($"capture {i} size mismatch");if(callerBlock>40)failures.Add($"capture {i} caller blocked {callerBlock:F2}ms");report.Add(new{i,callerBlockMs=callerBlock,frame.Metrics,frame.Bitmap.Width,frame.Bitmap.Height});}
                }
            }
            catch(Exception ex) when(ex is SharpGen.Runtime.SharpGenException or System.ComponentModel.Win32Exception)
            {environmentNotes.Add("Real desktop capture skipped: test runner has no interactive desktop capture permission. "+ex.GetType().Name+": "+ex.Message);}

            var primary=new FakeBackend("Fake GPU",failCapture:false);var fallback=new FakeBackend("Fake GDI",failCapture:false);
            using(var controlled=new ScreenCaptureCoordinator(primary,fallback))
            {var dispatch=Stopwatch.StartNew();var task=controlled.CaptureAsync(new Rectangle(0,0,3840,2160),Stopwatch.GetTimestamp());var blocked=dispatch.Elapsed.TotalMilliseconds;var frame=task.GetAwaiter().GetResult();using(frame.Bitmap)report.Add(new{controlled=true,blocked,frame.Metrics});if(blocked>40)failures.Add($"controlled caller blocked {blocked:F2}ms");if(frame.Metrics.CaptureThreadId==Environment.CurrentManagedThreadId)failures.Add("capture remained on caller thread");}
            var broken=new FakeBackend("Broken GPU",failCapture:true);var compatible=new FakeBackend("Fake GDI",failCapture:false);
            using(var fallbackTest=new ScreenCaptureCoordinator(broken,compatible))
            {var frame=fallbackTest.CaptureAsync(new Rectangle(0,0,800,450),Stopwatch.GetTimestamp()).GetAwaiter().GetResult();using(frame.Bitmap){if(!frame.Metrics.UsedFallback||frame.Metrics.CaptureBackend!="Fake GDI")failures.Add("automatic fallback did not report GDI backend");}}

            RunPreviewIdentityIntegration(output,failures);
        }
        catch(Exception ex){failures.Add(ex.ToString());}
        File.WriteAllText(Path.Combine(output,"capture-metrics.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllLines(Path.Combine(output,"ENVIRONMENT.txt"),environmentNotes);
        File.WriteAllLines(Path.Combine(output,"RESULT.txt"),failures.Count==0?["PASS","Status=Manual Acceptance Pending"]:["FAIL",..failures]);
        return failures.Count==0?0:2;
    }

    private static void RunPreviewIdentityIntegration(string output,List<string> failures)
    {
        using var image=new Bitmap(900,650);using(var g=Graphics.FromImage(image)){g.Clear(Color.FromArgb(48,50,56));g.DrawString("Emma & Sophia",SystemFonts.CaptionFont??SystemFonts.DefaultFont,Brushes.White,40,35);g.DrawString("The vault was closed for six hundred years.",SystemFonts.DefaultFont,Brushes.Gainsboro,40,110);}
        var title=new SegmentGroup{GroupId="SRC-T",GroupType=SegmentType.Title,OriginalText="Emma & Sophia",OrganizedText="Emma & Sophia",Translation="艾玛与索菲娅",Bounds=new(40,35,240,35),ReadingOrder=1};
        var body1=new SegmentGroup{GroupId="SRC-B1",GroupType=SegmentType.Body,OriginalText="The vault was closed",OrganizedText="The vault was closed",Translation="地窖已经封闭",Bounds=new(40,110,430,28),ReadingOrder=2};
        var body2=new SegmentGroup{GroupId="SRC-B2",GroupType=SegmentType.Body,OriginalText="for six hundred years.",OrganizedText="for six hundred years.",Translation="六百年。",Bounds=new(40,142,430,28),ReadingOrder=3};
        var document=new OcrDocument{Groups=[title,body1,body2]};var recognition=new RecognitionDocumentV2();
        recognition.TranslationUnits.Add(new("TU-T",["RR-T"],RegionRoleType.Title,"Emma & Sophia",1,false,["SRC-T"]));
        recognition.TranslationUnits.Add(new("TU-B",["RR-B1","RR-B2"],RegionRoleType.BodyParagraph,"The vault was closed for six hundred years.",2,false,["SRC-B1","SRC-B2"]));
        var semantic=new[]{new TranslationSemanticGroup("TU-T","Emma & Sophia",["SRC-T"],SegmentType.Title,title.Bounds,1),new TranslationSemanticGroup("TU-B","The vault was closed for six hundred years.",["SRC-B1","SRC-B2"],SegmentType.Body,RectangleF.Union(body1.Bounds,body2.Bounds),2)};
        var translations=new Dictionary<string,string>{{"TU-T","艾玛与索菲娅"},{"TU-B","地窖已经封闭六百年。"}};
        using var form=new PreviewForm(image,PreviewMode.OcrOnly,new ApiSettings{VisualModel=VisualModelKind.Off,PreviewTextPanelVisible=true},new OcrService(),new TranslationService());form.SuppressAutoOcrForE2E();form.Show();Application.DoEvents();form.SetRecognitionMappingForSmoke(recognition,semantic,translations);form.CommitTranslatedDisplayAtomicallyForSmoke(image,document);Application.DoEvents();
        var ranges=form.RightTextRangeIdentitiesForSmoke.Values.OrderBy(x=>x.Start).ToArray();if(ranges.Length!=2)failures.Add($"right identity count={ranges.Length}");
        if(ranges.Length==2){form.ClickRightTextAtForSmoke(ranges[0].Start+1);for(var i=0;i<20;i++)form.ClickRightTextAtForSmoke(ranges[0].Start+1);if(!form.ActiveHighlightSourceBlockIdsForSmoke.SequenceEqual(["SRC-T"]))failures.Add("title right-to-left mapping mismatch");form.ClickRightTextAtForSmoke(ranges[1].Start+1);if(!form.ActiveHighlightSourceBlockIdsForSmoke.SequenceEqual(["SRC-B1","SRC-B2"]))failures.Add("multi-source body mapping mismatch");form.SelectSourceForSmoke("SRC-B2");if(!form.ActiveHighlightSourceBlockIdsForSmoke.SequenceEqual(["SRC-B1","SRC-B2"]))failures.Add("left-to-right identity table mismatch");}
        var highlight=form.HighlightMetricsForSmoke;if(highlight.WholeDocumentHighlightResetCount!=0)failures.Add("whole document highlight reset");if(highlight.RedundantHighlightUpdateCount!=0)failures.Add("redundant highlight update");if(highlight.RightTextFullAssignmentDuringInteraction!=0)failures.Add("right text assigned during interaction");
        var counters=form.UiCountersForSmoke;if(counters.RightTranslationFinalSetCount!=1)failures.Add($"final translation set count={counters.RightTranslationFinalSetCount}");if(counters.RightPanelWidthChangeAfterFinalText!=0)failures.Add($"automatic right panel width changes={counters.RightPanelWidthChangeAfterFinalText}");
        using var shot=new Bitmap(form.ClientSize.Width,form.ClientSize.Height);form.DrawToBitmap(shot,form.ClientRectangle);shot.Save(Path.Combine(output,"right-text-mapping-integration.png"),ImageFormat.Png);File.WriteAllText(Path.Combine(output,"right-text-metrics.json"),JsonSerializer.Serialize(counters,new JsonSerializerOptions{WriteIndented=true}));form.Close();
    }

    private sealed class FakeBackend(string name,bool failCapture):IScreenCaptureBackend
    {
        public string BackendName=>name;public bool IsAvailable{get;private set;}public int InitializeCount{get;private set;}
        public void Initialize(){InitializeCount++;IsAvailable=true;}
        public ScreenCaptureFrame CaptureFrame(Rectangle bounds,long hotkeyTimestamp,CancellationToken cancellationToken)
        {if(failCapture)throw new InvalidOperationException("forced backend failure");Thread.Sleep(15);var bitmap=new Bitmap(bounds.Width,bounds.Height);return new(bitmap,new(name,15,5,3,0,Stopwatch.GetTimestamp(),hotkeyTimestamp,1,(long)bounds.Width*bounds.Height*4,Environment.CurrentManagedThreadId,false));}
        public void Dispose()=>IsAvailable=false;
    }
}
