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
        await Check("Mode isolation, language mapping and immutable request settings",()=>{
            config.State.Profiles["verification-private"]=ApiSettingsSnapshot.Copy(original);config.State.Profiles["verification-private"].TargetLanguage="Japanese";
            config.State.EmbeddedProfile="verification-private";var shot=config.Effective(original,false);var embed=config.Effective(original,true);
            Assert(embed.TargetLanguage=="ja","Japanese mapping failed");Assert(shot.TargetLanguage==FusionConfiguration.NormalizeLanguage(original.TargetLanguage),"Private profile affected common");
            config.State.Profiles["verification-private"].TargetLanguage="English";Assert(embed.TargetLanguage=="ja","Settings snapshot changed after profile edit");
            return Task.FromResult<object>(new{ScreenshotTarget=shot.TargetLanguage,EmbeddedSnapshot=embed.TargetLanguage});});
        await Check("Plugin configuration receives effective translation preferences",()=>{
            var p=ApiSettingsSnapshot.Copy(original);p.TargetLanguage="ja";p.CustomTranslationPrompt="Translate village as 村落.";
            var file=Path.Combine(output,"plugin-probe.cfg");FusionAdapterInstaller.WriteConfiguration(file,p,"F7");
            var lines=File.ReadAllLines(file);var encoded=lines.Single(x=>x.StartsWith("PromptBase64 = ")).Split(" = ",2)[1];
            var prompt=Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            Assert(prompt==FusionConfiguration.PluginPrompt(p)&&prompt.Contains(p.CustomTranslationPrompt)&&prompt.Contains("Target language: ja"),"Effective prompt mismatch");
            Assert(lines.Contains("ToggleKey = F7"),"Toggle key missing");File.Delete(file);
            return Task.FromResult<object>(new{PromptForwarded=true,Target="ja",Key="F7"});});
        await Check("Install, integrity rejection and two complete restore cycles",()=>{
            var root=Path.Combine(output,"installer-fixture");Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"MGI.exe"),"fixture");
            File.WriteAllText(Path.Combine(root,"winhttp.dll"),"preexisting loader");File.WriteAllText(Path.Combine(root,"unrelated.txt"),"preserve");
            var game=new GameInfo(Path.Combine(root,"MGI.exe"),"MGI","Unity Mono","64 位","fixture",SupportLevel.Supported,null);var installer=new FusionAdapterInstaller();
            var p=ApiSettingsSnapshot.Copy(original);p.ApiKey="invalid-test";
            for(int cycle=0;cycle<2;cycle++){
                installer.Install(game,p,"F7");installer.Verify(game);File.AppendAllText(Path.Combine(root,FusionAdapterInstaller.PluginRelative),"tampered");
                bool rejected=false;try{installer.Verify(game);}catch(InvalidDataException){rejected=true;}Assert(rejected,"Corrupt plugin passed integrity check");
                installer.Restore(game);Assert(File.ReadAllText(Path.Combine(root,"winhttp.dll"))=="preexisting loader","Original loader not restored");
                Assert(File.ReadAllText(Path.Combine(root,"unrelated.txt"))=="preserve","Unrelated file changed");Assert(!installer.IsInstalled(game),"Active manifest remained");}
            return Task.FromResult<object>(new{Cycles=2,CorruptPluginRejected=true,UnrelatedFilePreserved=true});});
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
        var report=new{Version=BuildIdentity.BuildVersion,PluginSha256=FusionPluginIdentity.Sha256,Timestamp=DateTimeOffset.Now,Failures=failures,Checks=rows};
        File.WriteAllText(Path.Combine(output,"fusion-verification.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        return failures==0?0:1;
    }
}
