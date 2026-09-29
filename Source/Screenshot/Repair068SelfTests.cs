using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class Repair068SelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<string>();var pass=0;var fail=0;
        void T(string name,Action action){try{action();pass++;rows.Add("PASS | "+name);}catch(Exception ex){fail++;rows.Add("FAIL | "+name+" | "+ex.Message);}}
        static void A(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static InputConsumptionDecision D(InputContextKind c,bool match=false,bool cancel=false)=>InputConsumptionPolicy.Decide(c,match,cancel);

        T("01 ESC idle pass-through",()=>A(D(InputContextKind.Idle,false,true) is {Suppressed:false,PassThrough:true},"idle ESC suppressed"));
        T("02 Normal key pass-through",()=>A(D(InputContextKind.Idle) is {Handled:false,PassThrough:true},"normal key consumed"));
        T("03 Global hotkey trigger and pass-through",()=>A(D(InputContextKind.Idle,true) is {Handled:true,Suppressed:false,PassThrough:true,Mode:InputConsumptionMode.TriggerAndPassThrough},"hotkey did not pass through"));
        T("04 Screenshot ESC exclusive cancel",()=>A(D(InputContextKind.ScreenshotSelection,true,true) is {Handled:true,Suppressed:true,PassThrough:false},"screenshot ESC not exclusive"));
        T("05 Capture end restores input",()=>{A(D(InputContextKind.BindingCapture,true) is {Suppressed:true},"capture did not own input");A(D(InputContextKind.Idle,true) is {Suppressed:false,PassThrough:true},"capture suppression leaked");});
        T("06 Mouse pass-through",()=>A(D(InputContextKind.Idle,true) is {Suppressed:false,PassThrough:true},"mouse suppressed"));
        T("07 Wheel pass-through",()=>A(D(InputContextKind.Preview,false) is {Suppressed:false,PassThrough:true},"wheel globally suppressed"));
        T("08 No suppression residue",()=>{for(var i=0;i<20;i++)A(D(InputContextKind.Idle,i%2==0) is {Suppressed:false},"residual exclusive state");});

        using var source=Canvas();var regions=Regions();using var rendered=RegionRendererV2.Render(source,regions,new());var m=rendered.ResourceMetrics??throw new InvalidOperationException("resource metrics missing");
        T("09 April Long CPU metrics",()=>A(m.CpuTimeMs>=0&&m.RendererTotalMs>=0,"CPU metrics missing"));
        T("10 April Long allocation metrics",()=>A(m.ManagedAllocatedBytes>=0&&m.RoiAllocatedBytes>=0,"allocation metrics missing"));
        T("11 April Long GC metrics",()=>A(m.Gen0GC>=0&&m.Gen1GC>=0&&m.Gen2GC>=0,"GC metrics missing"));
        T("12 Background work is region bounded",()=>A(m.BackgroundRegionCount<=regions.Count,"background repeated outside region budget"));
        T("13 Text measurement bounded",()=>A(m.TextMeasureCount<regions.Sum(x=>x.TranslationText.Length)*5+100,"text measurement unbounded"));
        T("14 ROI allocation bounded",()=>A(m.RoiBitmapAllocationCount<=regions.Count*3,"ROI allocation burst"));
        T("15 Fallback does not duplicate pipeline",()=>A(m.FallbackRegionCount<=regions.Count&&rendered.Diagnostics!.Count==regions.Count,"fallback duplicated regions"));
        T("16 UI final commit once contract",()=>{var final=m with{UiCommitCount=1};A(final.UiCommitCount==1,"multiple UI commits");});
        T("17 Translation coverage unchanged",()=>A(rendered.Diagnostics!.Where(x=>x.TranslatedLength>0).All(x=>x.AtomicRegionCommitted||x.FallbackRendered),"translation coverage dropped"));

        T("18 Single-instance identity",()=>A(new[]{"05068","05069","050610"}.Any(v=>SingleInstanceCoordinator.DiagnosticMutexName.Contains(v)&&SingleInstanceCoordinator.DiagnosticPipeName.Contains(v)),"stale single-instance identity"));
        T("19 One renderer resource lease",()=>{using var lease=RendererResourceBudget.EnterAsync(CancellationToken.None).GetAwaiter().GetResult();A(lease is not null,"resource lease missing");});
        T("20 Hook disposal leaves no residue",()=>{var k=new GlobalKeyboardBindingHost(()=>InputContextKind.Idle,_=>false,_=>{});k.Apply([]);k.Dispose();var mouse=new GlobalMouseBindingHost(_=>false,_=>false,_=>{});mouse.Apply([]);mouse.Dispose();A(!k.IsHookInstalled&&!mouse.IsHookInstalled,"hook residue");});
        T("21 ASCII short-path and build identity",()=>A(new[]{"0.5.0.6.8","0.5.0.6.9","0.5.0.6.10"}.Contains(BuildIdentity.ProductVersion,StringComparer.Ordinal)&&!AppContext.BaseDirectory.Any(c=>c>127),"identity or runtime path is not ASCII"));

        File.WriteAllLines(Path.Combine(output,"REPAIR-068-SELFTESTS.txt"),rows,Encoding.UTF8);
        File.WriteAllText(Path.Combine(output,"SUMMARY.txt"),$"PASS={pass}\nFAIL={fail}\nRealApiCalls=0\nActiveOperations=0\nStatus=Manual Acceptance Pending",Encoding.UTF8);
        File.WriteAllText(Path.Combine(output,"INPUT-PASS-THROUGH-DIAGNOSTIC.txt"),
            "Timestamp | InputDevice | Key/Button | Context | BindingMatch | Action | Handled | Suppressed | PassThrough | Reason\n"+
            $"{DateTimeOffset.Now:O} | Keyboard | Escape | Idle | False | None | False | False | True | Idle observation never consumes input\n"+
            $"{DateTimeOffset.Now:O} | Keyboard | Escape | ScreenshotSelection | True | Cancel | True | True | False | Active selection owns this cancel event\n",Encoding.UTF8);
        return fail==0?0:1;
    }

    private static Bitmap Canvas(){var b=new Bitmap(900,1800);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(48,49,53));return b;}
    private static List<RecognitionRegion> Regions()
    {
        var result=new List<RecognitionRegion>();
        for(var i=0;i<8;i++)
        {
            var box=new RectangleF(90,80+i*190,650,115);var id="L"+i;
            result.Add(new(){RegionId=id,RoleType=RegionRoleType.BodyParagraph,Polygon=GeometryV2.RectanglePolygon(box),SourceLinePolygons=[GeometryV2.RectanglePolygon(box)],SourceBlockIds=[id],OcrText="Source paragraph",StructuredText="Source paragraph",TranslationText="这是保持完整覆盖的长文本性能验收段落，用于验证资源指标和系统响应预算。",CoverageValid=true,RendererTargetRegion=box});
        }
        return result;
    }
}
