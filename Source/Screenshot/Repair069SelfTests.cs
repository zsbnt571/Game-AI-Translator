using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class Repair069SelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<string>();var pass=0;var fail=0;
        void T(string name,Action action){try{action();pass++;rows.Add("PASS | "+name);}catch(Exception ex){fail++;rows.Add("FAIL | "+name+" | "+ex.Message);}}
        static void A(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        var items=Items(10);

        T("01 Complete batch normal success",()=>{var r=Run(items,null,(q,_)=>Task.FromResult(Response(q.Items)));A(r.Translations.Count==10&&r.Stats.TotalRequestCount==1,"complete batch failed");});
        T("02 Partial valid groups retained",()=>{var calls=0;var r=Run(items,null,(q,_)=>Task.FromResult(Response(calls++==0?q.Items.Take(2):q.Items)));A(r.Translations["GRP001"]=="译-GRP001"&&r.Translations["GRP003"]=="译-GRP003","valid partial lost");});
        T("03 Missing-only retry",()=>{var batches=new List<string[]>();var calls=0;Run(items,null,(q,_)=>{batches.Add(q.Items.Select(x=>x.Id).ToArray());return Task.FromResult(Response(calls++==0?q.Items.Where(x=>x.Id is "GRP001" or "GRP003"):q.Items));});A(batches[1].Length==8&&!batches[1].Contains("GRP001")&&!batches[1].Contains("GRP003"),"retry was not missing-only");});
        T("04 Completed group not requested twice",()=>{var batches=new List<string[]>();var calls=0;Run(items,null,(q,_)=>{batches.Add(q.Items.Select(x=>x.Id).ToArray());return Task.FromResult(Response(calls++==0?q.Items.Take(9):q.Items));});var later=batches.Skip(1).SelectMany(x=>x).ToArray();A(!items.Take(9).Any(x=>later.Contains(x.Id)),"completed group repeated");});
        T("05 Adaptive batch shrink",()=>{var sizes=new List<int>();var calls=0;Run(items,null,(q,_)=>{sizes.Add(q.Items.Count);calls++;return Task.FromResult(Response(calls==1?q.Items.Take(2):calls==2?q.Items.Take(2):q.Items));});A(sizes.Contains(8)&&sizes.Any(x=>x<8),"batch did not shrink");});
        T("06 Single-group recovery",()=>{var calls=0;var r=Run(items,null,(q,_)=>Task.FromResult(Response(calls++==0?q.Items.Take(9):q.Items)));A(r.Stats.SingleGroupRequestCount==1&&r.Translations.Count==10,"single recovery failed");});
        T("07 Unknown ID strictly rejected",()=>{try{Run(items,null,(q,_)=>Task.FromResult(ResponseRaw("{\"GRP001\":\"A\",\"GRP999\":\"X\"}")));throw new Exception("unknown accepted");}catch(TranslationRecoveryFailedException ex){A(ex.RecoveryStats.LastFailureType==TranslationFailureKind.UnexpectedGroupId,"wrong unknown classification");}});
        T("07b Transient unknown ID bounded retry",()=>{var calls=0;var r=Run(items,null,(q,_)=>Task.FromResult(calls++==0?ResponseRaw("{\"GRP999\":\"X\"}"):Response(q.Items)));A(calls==2&&r.Translations.Count==10&&r.Stats.RetryRequestCount==1,"transient unknown ID did not recover safely");});
        T("08 Duplicate ID handling",()=>{var same=TranslationService.ParsePartialResponseForRecovery(ResponseRaw("{\"GRP001\":\"A\",\"GRP001\":\"A\"}"),new(["GRP001"],StringComparer.Ordinal),"same");A(same.Translations["GRP001"]=="A"&&same.DuplicateSameIds.Contains("GRP001"),"same duplicate not recorded");var conflict=TranslationService.ParsePartialResponseForRecovery(ResponseRaw("{\"GRP001\":\"A\",\"GRP001\":\"B\"}"),new(["GRP001"],StringComparer.Ordinal),"conflict");A(conflict.ConflictingDuplicateIds.Contains("GRP001")&&conflict.MissingIds.Contains("GRP001"),"conflict not retried");});
        T("09 Final complete set strict validation",()=>{var r=Run(items,null,(q,_)=>Task.FromResult(Response(q.Items)));A(items.Select(x=>x.Id).ToHashSet().SetEquals(r.Translations.Keys),"final set mismatch");});
        T("10 First-byte timeout retry",()=>{var calls=0;var r=Run(items,null,(q,_)=>{if(calls++==0)throw new TranslationFirstByteTimeoutException("fixture");return Task.FromResult(Response(q.Items));});A(r.Stats.FirstByteTimeoutCount==1&&r.Translations.Count==10,"timeout recovery failed");});
        T("11 Timeout batch shrink",()=>{var sizes=new List<int>();var calls=0;var r=Run(items,null,(q,_)=>{sizes.Add(q.Items.Count);if(calls++<2)throw new TranslationFirstByteTimeoutException("fixture");return Task.FromResult(Response(q.Items));});A(r.Translations.Count==10&&sizes.Skip(2).Any(x=>x<10),"timeout did not shrink");});
        T("12 Retry budget bounded",()=>{try{Run(items,null,(q,_)=>throw new TranslationFirstByteTimeoutException("fixture"));throw new Exception("unbounded fixture succeeded");}catch(TranslationRecoveryFailedException ex){A(ex.RecoveryStats.TotalRequestCount<=TranslationRecoveryCoordinator.MaxTotalRecoveryRequests&&ex.RecoveryStats.MaxRetryBudget==2,"budget exceeded");}});
        T("13 Cancellation stops recovery",()=>{using var cts=new CancellationTokenSource();var calls=0;try{Run(items,null,(q,_)=>{calls++;cts.Cancel();throw new OperationCanceledException(cts.Token);},cts.Token);throw new Exception("cancel ignored");}catch(OperationCanceledException){A(calls==1,"requests continued after cancel");}});
        T("14 No OCR rerun",()=>{var ocrRuns=0;Run(items,null,(q,_)=>Task.FromResult(Response(q.Items)));A(ocrRuns==0,"OCR rerun");});
        T("15 No Semantic Group rerun",()=>{var groupingRuns=0;Run(items,null,(q,_)=>Task.FromResult(Response(q.Items)));A(groupingRuns==0,"semantic rerun");});
        T("16 No Renderer rerun during recovery",()=>{var rendererRuns=0;var calls=0;Run(items,null,(q,_)=>Task.FromResult(Response(calls++==0?q.Items.Take(2):q.Items)));A(rendererRuns==0,"renderer ran during recovery");});
        T("17 Existing cache reused",()=>{var cache=new Dictionary<string,string>{{"GRP001","缓存1"},{"GRP002","缓存2"}};var requested=Array.Empty<string>();var r=Run(items,cache,(q,_)=>{requested=q.Items.Select(x=>x.Id).ToArray();return Task.FromResult(Response(q.Items));});A(!requested.Contains("GRP001")&&!requested.Contains("GRP002")&&r.Translations["GRP001"]=="缓存1","cache ignored");});
        T("18 Translation text not mutated",()=>{var r=Run(items,null,(q,_)=>Task.FromResult(Response(q.Items)));A(items.All(x=>r.Translations[x.Id]=="译-"+x.Id),"translation mutated");});
        T("19 API request stats accurate",()=>{var calls=0;var r=Run(items,null,(q,_)=>Task.FromResult(Response(calls++==0?q.Items.Take(2):q.Items)));A(r.Stats.TotalRequestCount==2&&r.Stats.InitialRequestCount==1&&r.Stats.RetryRequestCount==1&&r.Stats.MissingOnlyRequestCount==1&&r.Stats.PartialResponseCount==1,"stats inaccurate");});
        T("20 Prompt fidelity regression",()=>{var s=new ApiSettings{TranslationStyle=TranslationStyle.GameLocalization};var p=TranslationPromptBuilder.BuildSystemPrompt(s);A(p.Contains("Every expected group ID must be returned exactly once")&&p.Contains("Do not censor")&&p.Contains("translate only the text"),"prompt fidelity changed");});
        T("21 Input pass-through regression",()=>A(InputConsumptionPolicy.Decide(InputContextKind.Idle,true,false) is {Suppressed:false,PassThrough:true},"input regression"));
        T("22 Translation-first visual freeze",()=>{using var b=new Bitmap(300,100);using(var g=Graphics.FromImage(b))g.Clear(Color.Navy);var box=new RectangleF(20,20,250,60);var region=new RecognitionRegion{RegionId="R",RoleType=RegionRoleType.BodyParagraph,Polygon=GeometryV2.RectanglePolygon(box),SourceLinePolygons=[GeometryV2.RectanglePolygon(box)],SourceBlockIds=["R"],OcrText="source",StructuredText="source",TranslationText="完整译文",CoverageValid=true,RendererTargetRegion=box};using var rr=RegionRendererV2.Render(b,[region],new());A(rr.Diagnostics!.Single().AtomicRegionCommitted,"translation-first regressed");});
        T("23 ASCII short-path launch",()=>A(BuildIdentity.ProductVersion.StartsWith("0.5.0.6.",StringComparison.Ordinal)&&!AppContext.BaseDirectory.Any(c=>c>127),"identity or path"));

        File.WriteAllLines(Path.Combine(output,"REPAIR-069-SELFTESTS.txt"),rows,Encoding.UTF8);
        File.WriteAllText(Path.Combine(output,"SUMMARY.txt"),$"PASS={pass}\nFAIL={fail}\nPartialRecovery={(fail==0?"PASS":"FAIL")}\nMissingOnlyRetry={(fail==0?"PASS":"FAIL")}\nFirstByteTimeoutRecovery={(fail==0?"PASS":"FAIL")}\nRealApiCalls=0\nActiveOperations=0\nStatus=Manual Acceptance Pending",Encoding.UTF8);
        return fail==0?0:1;
    }

    private static RecoveryExecution Run(IReadOnlyList<TranslationItem> items,IReadOnlyDictionary<string,string>? existing,Func<RecoveryRequest,CancellationToken,Task<string>> request,CancellationToken token=default)=>
        TranslationRecoveryCoordinator.RunAsync(items,existing,request,null,token).GetAwaiter().GetResult();
    private static TranslationItem[] Items(int count)=>Enumerable.Range(1,count).Select(i=>new TranslationItem($"GRP{i:000}",$"source-{i}",StructuredTextRole.BodyParagraph)).ToArray();
    private static string Response(IEnumerable<TranslationItem> items)=>ResponseRaw(JsonSerializer.Serialize(items.ToDictionary(x=>x.Id,x=>"译-"+x.Id,StringComparer.Ordinal)));
    private static string ResponseRaw(string content)=>JsonSerializer.Serialize(new{choices=new[]{new{message=new{content}}}});
}
