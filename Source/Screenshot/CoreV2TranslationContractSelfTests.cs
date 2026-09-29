using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class CoreV2TranslationContractSelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var rows=new List<object>();var failed=0;
        void Test(string id,Action test)
        {
            try { test(); rows.Add(new{id,Pass=true,Error=""}); }
            catch(Exception ex) { failed++;rows.Add(new{id,Pass=false,Error=$"{ex.GetType().Name}: {ex.Message}"}); }
        }
        TranslationItem Item(string id,string text="source",params string[] sources) =>
            new(id,text,StructuredTextRole.BodyParagraph,sources,TranslationIdentityContract.CoreV2Block);
        RecoveryExecution RunItems(TranslationItem[] items,Func<RecoveryRequest,string> response) =>
            TranslationRecoveryCoordinator.RunAsync(items,null,(q,_)=>Task.FromResult(response(q)),null,default)
                .GetAwaiter().GetResult();
        string Json(params (string Id,string Text)[] values)
        {
            var content=JsonSerializer.Serialize(values.ToDictionary(x=>x.Id,x=>x.Text,StringComparer.Ordinal));
            return JsonSerializer.Serialize(new{choices=new[]{new{message=new{content}}}});
        }
        string Raw(string content) => JsonSerializer.Serialize(new{choices=new[]{new{message=new{content}}}});
        void Require(bool value,string message) { if(!value)throw new InvalidOperationException(message); }

        Test("A_EXACT_7_ACCEPT",()=>
        {
            var items=Enumerable.Range(1,7).Select(i=>Item($"B{i}")).ToArray();
            var result=RunItems(items,q=>Json(q.Items.Select(x=>(x.Id,"译文" )).ToArray()));
            Require(result.Translations.Count==7&&result.Stats.FinalMissingGroupCount==0,"exact seven not accepted");
        });
        Test("B_DUPLICATE_AND_MISSING_REJECT",()=>
        {
            var items=new[]{Item("B1"),Item("B2")};
            try { RunItems(items,_=>Raw("{\"B1\":\"一\",\"B1\":\"二\"}"));throw new InvalidOperationException("duplicate accepted"); }
            catch(TranslationRecoveryFailedException ex) { Require(ex.RemainingMissingIds.Count>0,"missing ids not explicit"); }
        });
        Test("C_EMPTY_REJECT",()=>
        {
            try { RunItems(new[]{Item("B1")},_=>Json(("B1","")));throw new InvalidOperationException("empty accepted"); }
            catch(TranslationRecoveryFailedException) { }
        });
        Test("D_UNKNOWN_ID_REJECT",()=>
        {
            try { RunItems(new[]{Item("B1")},_=>Json(("B9","未知")));throw new InvalidOperationException("unknown accepted"); }
            catch(TranslationRecoveryFailedException ex) { Require(ex.RecoveryStats.LastFailureType==TranslationFailureKind.UnexpectedGroupId,"unknown reason lost"); }
        });
        Test("D2_TRANSIENT_UNKNOWN_ID_SAFE_RETRY",()=>
        {
            var calls=0;
            var result=RunItems(new[]{Item("B1"),Item("B2")},q=>
                calls++==0?Json(("B2","错误映射"),("B3","未知")):Json(q.Items.Select(x=>(x.Id,"译文")).ToArray()));
            Require(calls==2&&result.Translations.Count==2&&result.Stats.RetryRequestCount==1,
                "transient unknown ID did not use one bounded exact-ID retry");
        });
        Test("E_MULTI_SOURCE_BLOCK_NO_PROVIDER_ALLOCATION",()=>
        {
            var result=RunItems(new[]{Item("B1","joined","R1","R2","R3")},_=>Json(("B1","合并译文")));
            Require(result.Translations["B1"]=="合并译文"&&result.Allocations.Count==0,"provider allocation still owns Core V2");
        });
        Test("F_MISSING_ZERO_NO_RECOVERY",()=>
        {
            var calls=0;var items=new[]{Item("B1"),Item("B2")};
            var result=RunItems(items,q=>{calls++;return Json(q.Items.Select(x=>(x.Id,"译文")).ToArray());});
            Require(result.Stats.FinalMissingGroupCount==0&&calls==1&&result.Stats.RetryRequestCount==0,"recovery ran with missing zero");
        });
        File.WriteAllText(Path.Combine(output,"CORE-V2-TRANSLATION-CONTRACT-TESTS.json"),
            JsonSerializer.Serialize(new{Passed=rows.Count-failed,Failed=failed,Rows=rows},new JsonSerializerOptions{WriteIndented=true}));
        return failed==0?0:2;
    }
}
