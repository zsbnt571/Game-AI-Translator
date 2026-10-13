using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class TranslationAllocationContractV1SelfTests
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<string>();var failed=0;
        void Test(string name,Action action){try{action();rows.Add($"PASS | {name}");}catch(Exception ex){failed++;rows.Add($"FAIL | {name} | {ex.Message}");}}
        static void A(bool value,string message){if(!value)throw new Exception(message);}
        var one=Items(("TU1",new[]{"S1"}));
        Test("A 1 SourceId to 1 segment",()=>A(Valid(one,Full(("TU1","甲")),Alloc("TU1",Seg("TU1",0,"甲","S1"))).IsValid,"invalid"));
        var three=Items(("TU1",new[]{"S1","S2","S3"}));
        Test("B 3 SourceIds to 3 segments",()=>A(Valid(three,Full(("TU1","甲乙丙")),Alloc("TU1",Seg("TU1",0,"甲","S1"),Seg("TU1",1,"乙","S2"),Seg("TU1",2,"丙","S3"))).IsValid,"invalid"));
        Test("C 3 SourceIds to 2 segments",()=>A(Valid(three,Full(("TU1","甲乙")),Alloc("TU1",Seg("TU1",0,"甲","S1","S2"),Seg("TU1",1,"乙","S3"))).IsValid,"invalid"));
        var two=Items(("TU1",new[]{"S1","S2"}));
        Test("D 2 SourceIds to 1 merged segment",()=>A(Valid(two,Full(("TU1","合并")),Alloc("TU1",Seg("TU1",0,"合并","S1","S2"))).IsValid,"invalid"));
        Test("E target order differs",()=>A(Valid(two,Full(("TU1","后前")),Alloc("TU1",Seg("TU1",0,"后","S2"),Seg("TU1",1,"前","S1"))).IsValid,"invalid"));
        Test("F unknown SourceId rejected",()=>A(!Valid(one,Full(("TU1","甲")),Alloc("TU1",Seg("TU1",0,"甲","BAD"))).IsValid,"accepted"));
        Test("G missing SourceId rejected",()=>A(!Valid(two,Full(("TU1","甲")),Alloc("TU1",Seg("TU1",0,"甲","S1"))).IsValid,"accepted"));
        Test("H duplicate target content rejected",()=>A(!Valid(two,Full(("TU1","甲")),Alloc("TU1",Seg("TU1",0,"甲","S1"),Seg("TU1",1,"甲","S2"))).IsValid,"accepted"));
        Test("I missing translated span rejected",()=>A(!Valid(one,Full(("TU1","甲")),Alloc("TU1",Seg("TU1",0,"","S1"))).IsValid,"accepted"));
        Test("J malformed JSON recoverable",()=>{var r=TranslationService.ParseAllocationResponseForTest(Envelope("```json\n"+Json(one,"甲",("甲",new[]{"S1"}))+"\n```"),one);A(r.HasSourceAlignedAllocations,"not recovered");});
        Test("K mapping missing unrecoverable",()=>{try{TranslationService.ParseAllocationResponseForTest(Envelope("{\"translations\":[{\"id\":\"TU1\",\"translation\":\"甲\"}]}"),one);throw new Exception("accepted");}catch(BatchJsonException){}});
        Test("L legacy cache compatible",()=>{var v=Valid(one,Full(("TU1","甲")),null);A(v.Status==TranslationAllocationStatus.LegacyWithoutAllocation,"legacy rejected");});
        Test("prompt semantics preserved",()=>{var p=TranslationPromptBuilder.BuildSystemPrompt(new ApiSettings{TargetLanguage="zh-CN",TranslationStyle=TranslationStyle.GameLocalization});A(p.Contains("Do not censor")&&p.Contains("semantic fidelity")&&p.Contains("sourceIds"),"prompt regression");});
        Test("custom plain prompt preserved",()=>{var p=TranslationPromptBuilder.BuildPlainSystemPrompt(new ApiSettings{TargetLanguage="zh-CN",TranslationStyle=TranslationStyle.Custom,CustomTranslationPrompt="STYLE-MARKER"});A(p.Contains("STYLE-MARKER")&&p.Contains("Return only the translated text"),"custom lost");});
        // SourceIds, role and identity contract are request context: cached source-aligned
        // spans and validated UI structure must not be reused under a different request.
        // Allocation display metadata is not an input to TranslationCacheKeyBuilder.
        var cacheSettings=new ApiSettings{TargetLanguage="zh-CN",ApiUrl="https://example.test",Model="m"};
        var cacheItem=new TranslationItem("TU1","x",StructuredTextRole.Caption,["S1"],TranslationIdentityContract.CoreV2Block);
        string RequestKey(TranslationItem item)=>TranslationCacheKeyBuilder.Build([item],cacheSettings);
        Test("cache identity stable for equivalent deep copied request",()=>
            A(RequestKey(cacheItem)==RequestKey(cacheItem with{SourceIds=cacheItem.StableSourceIds.ToArray()}),"equivalent request key changed"));
        Test("cache identity isolates source membership",()=>
            A(RequestKey(cacheItem)!=RequestKey(cacheItem with{SourceIds=["OTHER"]}),"different source ids shared cache"));
        Test("cache identity isolates source role",()=>
            A(RequestKey(cacheItem)!=RequestKey(cacheItem with{RoleType=StructuredTextRole.Unknown}),"different role shared cache"));
        Test("cache identity isolates Core and nonCore request contracts",()=>
            A(RequestKey(cacheItem)!=RequestKey(cacheItem with{IdentityContract=TranslationIdentityContract.LegacyAllocationV1}),"different request contracts shared cache"));
        Test("source aligned mapping does not replicate full translation",()=>
        {
            var d=new RecognitionDocumentV2{TranslationGeneration=1};
            d.Regions.Add(new RecognitionRegion{RegionId="R1",SourceBlockIds=["S1"],TranslationUnitId="TU1"});
            d.Regions.Add(new RecognitionRegion{RegionId="R2",SourceBlockIds=["S2"],TranslationUnitId="TU1"});
            d.TranslationUnits.Add(new("TU1",["R1","R2"],RegionRoleType.Caption,"source",0,false,["S1","S2"]));
            var a=Alloc("TU1",Seg("TU1",0,"甲","S1"),Seg("TU1",1,"乙","S2"));
            TranslationMappingV2.Apply(d,new TranslationBatchResult(Full(("TU1","甲乙")),1,0,[],false,"甲乙",true,null,a),1);
            A(d.Regions[0].TranslationText=="甲"&&d.Regions[1].TranslationText=="乙"&&d.TranslationMappingMode=="SOURCE_ALIGNED_MAPPING_V1","replicated");
        });
        File.WriteAllLines(Path.Combine(output,"TRANSLATION-ALLOCATION-CONTRACT-V1-TESTS.txt"),rows);
        return failed==0?0:1;
    }
    private static TranslationAllocationValidation Valid(IReadOnlyList<TranslationItem> i,Dictionary<string,string> f,Dictionary<string,TranslationAllocationSegment[]>? a)=>TranslationAllocationContractV1.Validate(i,f,a);
    private static TranslationItem[] Items(params (string Id,string[] Sources)[] x)=>x.Select(v=>new TranslationItem(v.Id,"source",StructuredTextRole.Caption,v.Sources)).ToArray();
    private static Dictionary<string,string> Full(params (string Id,string Text)[] x)=>x.ToDictionary(v=>v.Id,v=>v.Text,StringComparer.Ordinal);
    private static TranslationAllocationSegment Seg(string id,int seq,string text,params string[] sources)=>new(id,sources,text,seq,.12);
    private static Dictionary<string,TranslationAllocationSegment[]> Alloc(string id,params TranslationAllocationSegment[] segments)=>new(StringComparer.Ordinal){{id,segments}};
    private static string Envelope(string content)=>JsonSerializer.Serialize(new{choices=new[]{new{message=new{content}}}});
    private static string Json(IReadOnlyList<TranslationItem> _,string full,params (string Text,string[] Sources)[] segments)=>JsonSerializer.Serialize(new{translations=new[]{new{id="TU1",translation=full,allocations=segments.Select((x,i)=>new{segmentId=$"A{i}",sourceIds=x.Sources,translatedText=x.Text,sequence=i})}}});
}
