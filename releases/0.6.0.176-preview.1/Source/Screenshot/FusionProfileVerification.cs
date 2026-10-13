using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class FusionProfileVerification
{
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var config=new FusionConfiguration();var initial=FusionConfiguration.Clone(config.State);
        var rows=new List<object>();var failures=0;
        void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        async Task Check(string name,Func<Task<object>> action)
        {try{rows.Add(new{Name=name,Pass=true,Details=await action()});}catch(Exception ex){failures++;rows.Add(new{Name=name,Pass=false,Error=SafeDiagnosticOutput.ExceptionSummary(ex)});}}
        Task<object> Done(object o)=>Task.FromResult(o);
        var common=config.State.CommonProfile;var basis=ApiSettingsSnapshot.Copy(config.State.Profiles[common]);
        var id=Guid.NewGuid().ToString("N");
        try
        {
            await Check("Migration preserves explicit screenshot reference and following embedded",()=>{
                Assert(config.State.SchemaVersion==2,"Schema not migrated");
                Assert(config.State.ScreenshotProfile==common&&config.State.EmbeddedProfile is null,"Fixed versus following reference lost");
                Assert(config.State.ProfileInfo.All(p=>p.Key.Length==32),"Missing stable ID");
                Assert(config.State.ArchivedTemplates.ContainsKey("内嵌 · OpenAI"),"Proven unchanged empty preset not archived");
                return Done(new{Saved=config.State.Profiles.Count,Archived=config.State.ArchivedTemplates.Count,config.State.LastGame});});
            await Check("CRUD, complete roundtrip, rename stable identity, references and immutable snapshot",()=>{
                var p=ApiSettingsSnapshot.Copy(basis);p.TargetLanguage="ja";p.CustomTranslationPrompt="Temporary verification preference";p.RequestTimeoutSeconds=75;
                config.SaveProfile(id,"测试·独立方案",p);config.Change(s=>s.CommonProfile=id);
                Assert(config.SelectedId(false)==common&&config.SelectedId(true)==id,"Changing common changed fixed selection");
                var snap=config.Effective(basis,true);var identity=FusionConfiguration.TranslationIdentity(snap);
                config.SaveProfile(id,"测试·重命名",p);
                var loaded=new FusionConfiguration();Assert(loaded.Name(id)=="测试·重命名"&&loaded.SelectedId(true)==id,"Rename references lost on reload");
                Assert(loaded.State.Profiles[id].RequestTimeoutSeconds==75&&loaded.State.Profiles[id].ApiKey==basis.ApiKey,"Incomplete roundtrip");
                Assert(identity==FusionConfiguration.TranslationIdentity(loaded.Effective(basis,true)),"Rename changed translation identity");
                p.TargetLanguage="en";config.SaveProfile(id,"测试·重命名",p);Assert(snap.TargetLanguage=="ja","In-flight settings snapshot mutated");
                var copy=Guid.NewGuid().ToString("N");config.SaveProfile(copy,"测试·副本",p);p.Model="copy-model";config.SaveProfile(copy,"测试·副本",p);
                Assert(config.State.Profiles[id].Model==basis.Model,"Copy modified source");config.DeleteProfile(copy);
                bool blocked=false;try{config.DeleteProfile(id);}catch(InvalidOperationException){blocked=true;}Assert(blocked,"Referenced profile deleted");
                config.Change(s=>s.CommonProfile=common);config.DeleteProfile(id);
                Assert(!new FusionConfiguration().State.Profiles.ContainsKey(id),"Delete not persisted");
                return Done(new{RenameRetainedId=true,SnapshotTarget=snap.TargetLanguage,ReferencedDeleteBlocked=blocked});});
            await Check("Protected credentials, failed save keeps state and old file",()=>{
                var path=Path.Combine(AppDataPaths.Root,"fusion-settings.json");var before=File.ReadAllText(path);
                Assert(!before.Contains(basis.ApiKey)&&before.Contains("dpapi:v1:"),"Unprotected credentials");
                var state=JsonSerializer.Serialize(config.State);bool rejected=false;
                using(var locked=new FileStream(path+".tmp",FileMode.Create,FileAccess.ReadWrite,FileShare.None))
                    try{config.SaveProfile(id,"测试·失败",basis);}catch(IOException){rejected=true;}
                File.Delete(path+".tmp");Assert(rejected&&File.ReadAllText(path)==before&&JsonSerializer.Serialize(config.State)==state,"Failed save altered durable or live state");
                return Done(new{FailureRetainedState=true,Encrypted=true});});
            await Check("Incomplete draft and explicitly unauthenticated interface",()=>{
                var p=ApiSettingsSnapshot.Copy(basis);p.ApiKey="";
                config.SaveProfile(id,"测试·草稿",p);Assert(config.State.ProfileInfo[id].Draft,"Incomplete profile not marked draft");
                p.AllowEmptyApiKey=true;config.SaveProfile(id,"测试·无鉴权",p);Assert(!config.State.ProfileInfo[id].Draft,"Explicit no-auth profile marked incomplete");
                config.DeleteProfile(id);return Done(new{DraftPreserved=true,NoAuthExplicit=true});});
            await Check("Font size does not compound with container depth",()=>{
                using var f=new Form{Font=new System.Drawing.Font("Microsoft YaHei UI",10)};Control parent=f;
                for(int i=0;i<8;i++){var panel=new Panel();parent.Controls.Add(panel);parent=panel;}
                var label=new Label{Text="测试正文"};parent.Controls.Add(label);
                var p=new ApiSettings{UiFontSize=14};FontManager.ApplyUi(f,p);
                Assert(Math.Abs(label.Font.Size-14)<.1,"Nested font scaled multiple times");
                p.UiFontSize=10;FontManager.ApplyUi(f,p);Assert(Math.Abs(label.Font.Size-10)<.1,"Default restoration failed");
                return Done(new{Levels=8,Size14=true,Default10=true});});
            var service=new TranslationService();
            await Check("Real request uses saved temporary scheme and observable terminology",async()=>{
                var p=ApiSettingsSnapshot.Copy(basis);p.TargetLanguage="zh-CN";p.CustomTranslationPrompt="Translate lantern as 星灯. Only return translated text.";
                config.SaveProfile(id,"测试·实际请求",p);config.Change(s=>s.ScreenshotProfile=id);
                var snap=config.Effective(basis,false);var before=File.ReadAllText(Path.Combine(AppDataPaths.Root,"fusion-settings.json"));
                var result=await service.TranslatePlainAsync("Bring the lantern to the village in 2 weeks.",snap);
                Assert(result.Text.Contains("星灯"),"Saved preference did not reach response");
                Assert(before==File.ReadAllText(Path.Combine(AppDataPaths.Root,"fusion-settings.json")),"Request changed profiles");
                config.Change(s=>s.ScreenshotProfile=common);config.DeleteProfile(id);return new{result.Text,SnapshotUsesSavedProfile=true};});
            foreach(var body in new[]{"{\"choices\":[{\"message\":{\"content\":\"\"}}]}","{\"unexpected\":true}"})
                await Check("HTTP 200 invalid response rejected: "+(body.Contains("choices")?"empty":"structure"),async()=>{
                    var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();var port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
                    using var listener=new HttpListener();listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
                    var server=Task.Run(async()=>{var ctx=await listener.GetContextAsync();Assert(ctx.Request.Headers["Authorization"] is null,"No-auth sends credential header");var bytes=Encoding.UTF8.GetBytes(body);ctx.Response.ContentType="application/json";ctx.Response.ContentLength64=bytes.Length;await ctx.Response.OutputStream.WriteAsync(bytes);ctx.Response.Close();});
                    var p=ApiSettingsSnapshot.Copy(basis);p.ApiUrl=$"http://127.0.0.1:{port}";p.ApiKey="";p.AllowEmptyApiKey=true;
                    using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(5));bool rejected=false;
                    try{var result=await service.TranslatePlainAsync("hello",p,cancel.Token);if(string.IsNullOrWhiteSpace(result.Text))rejected=true;}catch{rejected=true;}
                    await server.WaitAsync(TimeSpan.FromSeconds(5));Assert(rejected,"Invalid response accepted");return new{Rejected=true,NoAuthorizationHeader=true};});
        }
        finally{config.Change(s=>{s.CommonProfile=initial.CommonProfile;s.ScreenshotProfile=initial.ScreenshotProfile;s.EmbeddedProfile=initial.EmbeddedProfile;s.Profiles=initial.Profiles;s.ProfileInfo=initial.ProfileInfo;});}
        File.WriteAllText(Path.Combine(output,"profile-verification.json"),JsonSerializer.Serialize(new{Version=BuildIdentity.BuildVersion,Failures=failures,Checks=rows},new JsonSerializerOptions{WriteIndented=true}));return failures==0?0:1;
    }
}
