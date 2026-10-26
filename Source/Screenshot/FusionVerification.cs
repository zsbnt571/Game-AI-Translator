using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class FusionVerification
{
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<object>();var failures=0;
        async Task Check(string name,Func<Task<object>> run)
        {var sw=Stopwatch.StartNew();try{var details=await run();rows.Add(new{Name=name,Pass=true,Milliseconds=sw.ElapsedMilliseconds,Details=details});}
         catch(Exception ex){failures++;rows.Add(new{Name=name,Pass=false,Milliseconds=sw.ElapsedMilliseconds,Details=SafeDiagnosticOutput.ExceptionSummary(ex)});}}
        void Assert(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        var config=new FusionConfiguration();var original=ApiSettingsSnapshot.Copy(config.State.Profiles[config.State.CommonProfile]);
        await Check("DPAPI and imported credentials",()=>{
            var json=FusionSecrets.Serialize(original);var roundtrip=FusionSecrets.Deserialize<ApiSettings>(json);
            Assert(!string.IsNullOrEmpty(original.ApiKey),"No imported credential");Assert(!json.Contains(original.ApiKey),"Credential is not encrypted");Assert(roundtrip.ApiKey==original.ApiKey,"DPAPI roundtrip mismatch");
            return Task.FromResult<object>(new{Profiles=config.State.Profiles.Count,Encrypted=true});});
        await Check("API routing, shared content and immutable request settings",()=>{
            var priorContent=config.State.TranslationContent!;
            config.State.TranslationContent=TranslationContentSettings.From(original);
            config.State.TranslationContent.TargetLanguage="Japanese";
            config.State.Profiles["verification-private"]=ApiSettingsSnapshot.Copy(original);config.State.Profiles["verification-private"].TargetLanguage="Japanese";
            config.State.EmbeddedProfile="verification-private";var shot=config.Effective(original,false);var embed=config.Effective(original,true);
            Assert(embed.TargetLanguage=="ja"&&shot.TargetLanguage=="ja","Shared content mapping failed");
            config.State.Profiles["verification-private"].TargetLanguage="English";Assert(embed.TargetLanguage=="ja","Settings snapshot changed after profile edit");
            config.State.TranslationContent=priorContent;
            return Task.FromResult<object>(new{ScreenshotTarget=shot.TargetLanguage,EmbeddedSnapshot=embed.TargetLanguage});});
        var service=new TranslationService();
        await Check("Real screenshot request: Chinese and custom terminology",async()=>{
            var p=ApiSettingsSnapshot.Copy(original);p.TargetLanguage="zh-CN";p.CustomTranslationPrompt="Translate lantern as 星灯. Only return translated text.";
            var result=await service.TranslatePlainAsync("Bring the lantern to the village in 2 weeks.",p);
            Assert(result.Text.Contains("星灯")&&result.Text.Contains('2'),"Custom terminology or digit did not reach response");return result;});
        await Check("Real screenshot request: independent Japanese target",async()=>{
            var p=ApiSettingsSnapshot.Copy(original);p.TargetLanguage="ja";p.CustomTranslationPrompt="Translate into natural Japanese.";
            var result=await service.TranslatePlainAsync("Welcome back. The village is waiting for you.",p);
            Assert(result.Text.Any(c=>c>='\u3040'&&c<='\u30ff'),"Japanese target not reflected");return result;});
        await Check("Invalid API configuration is rejected",async()=>{
            var p=ApiSettingsSnapshot.Copy(original);p.ApiUrl="http://127.0.0.1:1";p.ApiKey="invalid-test";using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            bool rejected=false;try{await service.TranslatePlainAsync("Hello",p,timeout.Token);}catch{rejected=true;}Assert(rejected,"Invalid API reported success");return new{Rejected=true,BoundSeconds=5};});
        var report=new{Version=BuildIdentity.BuildVersion,Timestamp=DateTimeOffset.Now,Failures=failures,Checks=rows};
        File.WriteAllText(Path.Combine(output,"fusion-verification.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        return failures==0?0:1;
    }
}
