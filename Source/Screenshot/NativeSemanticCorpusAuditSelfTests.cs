using System.Text.Json;
namespace ScreenshotTranslationUiTester;
internal static class NativeSemanticCorpusAuditSelfTests
{
    internal static int Run(string output,string unusedRoot)
    {
        Directory.CreateDirectory(output);var rows=new List<object>();var failed=0;
        void Check(string name,bool pass){rows.Add(new{Name=name,Pass=pass});if(!pass)failed++;}
        bool Stop(bool mode,int all,int semantic,params int[] statuses)=>ScopedProductionChainHarness.AuditStopReason(mode,all,semantic,statuses).Length>0;
        bool Semantic(string failure="System.IO.InvalidDataException",string message="Product rejected one or more blocks",int rejected=1,long pixels=0,int requests=1,int[]? statuses=null)=>
            ScopedProductionChainHarness.IsVerifiedSemanticRejection(failure,message,rejected,pixels,requests,statuses??[200]);
        Check("default_one_semantic_continue",!Stop(false,1,1,200));
        Check("default_two_semantic_stop",Stop(false,2,2,200));
        Check("explicit_37_semantic_continue",!Stop(true,37,37,200));
        Check("explicit_one_nonsemantic_continue",!Stop(true,20,19,200));
        Check("explicit_two_nonsemantic_stop",Stop(true,20,18,200));
        Check("auth_401_immediate",Stop(true,0,0,401));
        Check("auth_403_immediate",Stop(true,0,0,403));
        Check("rate_429_immediate",Stop(true,0,0,429));
        Check("service_first_bounded",!Stop(true,1,0,503));
        Check("service_second_stop",Stop(true,2,0,503));
        Check("invalid_counter_stop",Stop(true,1,2,200));
        Check("semantic_observed_product_rejection",Semantic());
        Check("pixel_mismatch_not_semantic",!Semantic(pixels:1));
        Check("no_bitmap_not_semantic",!Semantic(pixels:-1));
        Check("different_exception_not_semantic",!Semantic(failure:"System.InvalidOperationException"));
        Check("different_message_not_semantic",!Semantic(message:"Actual Preview versus diagnostic pixels differ"));
        Check("no_rejected_blocks_not_semantic",!Semantic(rejected:0));
        Check("no_request_not_semantic",!Semantic(requests:0,statuses:[]));
        Check("unobserved_request_not_semantic",!Semantic(requests:2));
        Check("server_failure_not_semantic",!Semantic(statuses:[503]));
        Check("transport_recovery_not_semantic",!Semantic(requests:2,statuses:[503,200]));
        Check("zero_inputs_rejected",!ScopedProductionChainHarness.AuditInputCountAllowed(true,0));
        Check("37_inputs_allowed",ScopedProductionChainHarness.AuditInputCountAllowed(true,37));
        Check("38_inputs_rejected",!ScopedProductionChainHarness.AuditInputCountAllowed(true,38));
        Check("existing_default_size_contract",ScopedProductionChainHarness.AuditInputCountAllowed(false,38));
        File.WriteAllText(Path.Combine(output,"SEMANTIC-CORPUS-AUDIT-RESULTS.json"),JsonSerializer.Serialize(new{Passed=rows.Count-failed,Failed=failed,RealApiCalls=0,ProductValidationUnchanged=true,Rows=rows},new JsonSerializerOptions{WriteIndented=true}));
        return failed==0?0:2;
    }
}
