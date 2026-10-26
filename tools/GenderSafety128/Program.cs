using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenshotTranslationUiTester;

internal static class Program
{
    const string Source="I am ready.",Ordinary="Estoy lista.",Enhanced="Estoy preparada.";
    static string output="";static int failures;
    static readonly List<object> checks=[];
    static void Check(string name,bool ok,object? detail=null){checks.Add(new{name,passed=ok,detail});if(!ok)failures++;}
    static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
    static string Arg(string[] args,string key)=>args.First(a=>a.StartsWith(key+"=",StringComparison.Ordinal))[(key.Length+1)..];
    static ApiSettings Settings(string language="es")=>new(){ApiUrl="http://127.0.0.1:1",AllowEmptyApiKey=true,Model="gender-safety128-local-stub",TargetLanguage=language,SourceLanguage=SourceLanguageMode.English,RequestKind=TranslationRequestKind.GameText};
    static string Exe(string name){string p=Path.Combine(output,name,"neutral.exe");Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,"Metadata-only fixture; never executed.");return p;}
    static async Task<bool> Wait(Func<bool> condition,int ms=3500){var end=Stopwatch.StartNew();while(end.ElapsedMilliseconds<ms){if(condition())return true;await Task.Delay(25);}return condition();}
    static string Reply(string payload,ApiSettings settings,string text)=>settings.RequestKind==TranslationRequestKind.GameJson?JsonSerializer.Serialize(JsonSerializer.Deserialize<Dictionary<string,string>>(payload)!.ToDictionary(p=>p.Key,p=>text)):text;
    static GameDialogueContext Role()=>GameDialogueContext.Create(["I am a woman."],new("r0",["r0"]))!;
    static JsonObject Receipt(bool ready=true,bool roles=true,string source=Source)=>new()
    {
        ["texts"]=new JsonArray(source),["ahead"]=new JsonArray(),["dialogueContextContract"]=GameDialogueContext.Contract,
        ["speakerAttributionContract"]=GameDialogueRoles.Contract,["speakerAttributionReady"]=ready,
        ["dialogueContexts"]=ready?new JsonArray(new JsonObject{["source"]=source,["unique"]=true,["previous"]=new JsonArray("I am a woman."),
            ["speakerAttribution"]=roles?new JsonObject{["contract"]=GameDialogueRoles.Contract,["speaker"]="r0",["previousSpeakers"]=new JsonArray("r0")}:null}):new JsonArray()
    };
    sealed class Bridge(JsonObject receipt):IGameDataConnection
    {
        internal volatile JsonObject Receipt=receipt;
        internal readonly ConcurrentDictionary<string,string> Applied=new();
        internal readonly ConcurrentQueue<(string Source,string Text)> AllApplied=new();
        internal int Polls,Epoch=1,PrepareCalls;
        public bool Connected=>true;
        public Task<JsonElement> RequestAsync(JsonObject request,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();string op=request["op"]!.GetValue<string>();
            if(op is "translationPoll" or "translationCatalog")
            {
                if(op=="translationPoll")Interlocked.Increment(ref Polls);
                var value=Receipt.DeepClone().AsObject();value["epoch"]=Epoch;
                if(op=="translationCatalog"){value["next"]=1;value["total"]=1;value["skipped"]=0;value["scanning"]=false;value["catalogComplete"]=true;value["catalogErrorCount"]=0;}
                return Task.FromResult(JsonSerializer.SerializeToElement(value));
            }
            if(op=="translationPrepare"){Interlocked.Increment(ref PrepareCalls);Epoch++;Applied.Clear();}
            if(op=="translationDisable"){Epoch++;Applied.Clear();}
            int count=0;
            if(op=="translationApply"&&request["entries"] is JsonArray rows)
                foreach(var row in rows){string s=row!["source"]!.GetValue<string>(),t=row["text"]!.GetValue<string>();Applied[s]=t;AllApplied.Enqueue((s,t));count++;}
            return Task.FromResult(JsonSerializer.SerializeToElement(new{epoch=Epoch,stored=count,applied=count}));
        }
        public void Dispose(){}
    }
    static async Task HotCaches()
    {
        foreach(string kind in new[]{"native","display","legacy-context","role","manual"})
        {
            string exe=Exe("hot-"+kind);var settings=Settings();var native=RpgTranslationCache.LoadLocal(exe,settings,TranslationCacheScope.Native);
            string path=kind=="display"?RpgTranslationCache.LoadDisplay(exe,settings).Path:native.Path;
            if(kind is "native" or "display" or "role")TranslationStorage.WriteJson(path,new Dictionary<string,string>{{Source,Ordinary}});
            if(kind=="legacy-context")
            {path=native.Path+".dialogue-context-v1.json";var legacy=new GameDialogueContextCache();legacy.Accept(Source,Ordinary,GameDialogueContext.Create(["I am a woman."])!,new Dictionary<string,string>());TranslationStorage.WriteJson(path,legacy.Snapshot());}
            if(kind=="role")
            {var roles=new GameDialogueRoleCache();roles.Accept(Source,Enhanced,Role(),new Dictionary<string,string>());TranslationStorage.WriteJson(native.Path+".dialogue-roles-v2.json",roles.Snapshot());}
            if(kind=="manual")TranslationWorkspaceStore.For(exe,settings).SetManual(Source,Ordinary,EmbeddedTextSyntax.Renpy);
            string? before=File.Exists(path)?Hash(path):null;int calls=0;using var bridge=new Bridge(Receipt());
            using var session=new RpgTranslationSession(bridge,exe,settings,(p,s,t)=>{Interlocked.Increment(ref calls);return Task.FromResult(Reply(p,s,Enhanced));},new(EmbeddedTextSyntax.Renpy,true,StartupCache:true));
            await session.StartAsync();string expected=kind=="role"?Enhanced:Ordinary;
            Check(kind+": existing translation remains immediately available",await Wait(()=>session.Lookup(Source)==expected&&bridge.Polls>=3),session.Lookup(Source));
            Check(kind+": no gender-only provider request",calls==0,calls);
            Check(kind+": no source reset was sent",bridge.AllApplied.All(p=>p.Source!=Source||p.Text!=Source));
            await session.StopAsync();
            if(before is not null)Check(kind+": ordinary cache bytes retained",Hash(path)==before);
            if(kind!="role")Check(kind+": no fabricated role cache",!File.Exists(native.Path+".dialogue-roles-v2.json"));
        }
    }
    static async Task PendingAndLateMetadata()
    {
        string exe=Exe("late-metadata");var settings=Settings();using var bridge=new Bridge(Receipt(false));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;bool rolePrompt=false;
        using var session=new RpgTranslationSession(bridge,exe,settings,async(p,s,t)=>
        {Interlocked.Increment(ref calls);rolePrompt|=s.CustomTranslationPrompt.Contains("speakerAttribution=");entered.TrySetResult();await release.Task.WaitAsync(t);return Reply(p,s,Ordinary);},new(EmbeddedTextSyntax.Renpy,true,StartupCache:true));
        var clock=Stopwatch.StartNew();await session.StartAsync();
        Check("pending role index does not delay ordinary provider request",await Wait(()=>entered.Task.IsCompleted,2500),clock.ElapsedMilliseconds);
        int polls=bridge.Polls;bridge.Receipt=Receipt();await Wait(()=>bridge.Polls>=polls+2);release.TrySetResult();
        Check("late metadata does not discard in-flight ordinary translation",await Wait(()=>bridge.Applied.GetValueOrDefault(Source)==Ordinary));
        Check("late metadata causes no duplicate gender-only request",calls==1,calls);
        Check("ordinary in-flight request kept its original prompt",!rolePrompt);
        bridge.Epoch++;bridge.Receipt=Receipt(false);polls=bridge.Polls;await Wait(()=>bridge.Polls>=polls+2);
        Check("new epoch retains normal cache without stale role waiting",session.Lookup(Source)==Ordinary&&calls==1);
        await session.StopAsync();var path=RpgTranslationCache.LoadLocal(exe,settings,TranslationCacheScope.Native).Path;
        Check("ordinary result saved under ordinary identity",RpgTranslationCache.Read(path)?.GetValueOrDefault(Source)==Ordinary);
        Check("ordinary result never relabeled as role success",!File.Exists(path+".dialogue-roles-v2.json"));
    }
    static async Task ExplicitRetryFailure()
    {
        string exe=Exe("explicit-retry");var settings=Settings();var native=RpgTranslationCache.LoadLocal(exe,settings,TranslationCacheScope.Native);
        TranslationStorage.WriteJson(native.Path,new Dictionary<string,string>{{Source,Ordinary}});string before=Hash(native.Path);
        using var bridge=new Bridge(Receipt());var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;
        using var session=new RpgTranslationSession(bridge,exe,settings,async(p,s,t)=>{Interlocked.Increment(ref calls);entered.TrySetResult();await release.Task.WaitAsync(t);return Reply(p,s,"错误译文");},new(EmbeddedTextSyntax.Renpy,true,StartupCache:true));
        await session.StartAsync();await Wait(()=>bridge.Polls>=3);var store=TranslationWorkspaceStore.For(exe,settings);
        Check("explicit retry starts from zero warm-cache calls",calls==0);
        store.RequestRetry([Source]);Check("explicit retry remains available",await Wait(()=>entered.Task.IsCompleted));
        Check("pending explicit enhancement retains ordinary display",session.Lookup(Source)==Ordinary&&bridge.AllApplied.All(p=>p.Text!=Source));
        release.TrySetResult();Check("failed explicit enhancement records actual failure",await Wait(()=>store.Issues().Any(p=>p.Source==Source&&p.Attempts>0)));
        var issue=store.Issues().FirstOrDefault(p=>p.Source==Source);Check("failure records reason and original usable display hash",issue is not null&&issue.Code=="target-script-mismatch"&&issue.TextHash==TranslationAttemptStatus.TextHash(Ordinary),issue);
        Check("failed enhancement retains ordinary lookup",session.Lookup(Source)==Ordinary);
        await session.StopAsync();Check("failed enhancement retains ordinary file",Hash(native.Path)==before);
        Check("failed enhancement creates no successful role cache",!File.Exists(native.Path+".dialogue-roles-v2.json"));
    }
    static async Task ExistingFailure()
    {
        string exe=Exe("existing-failure");var settings=Settings();var store=TranslationWorkspaceStore.For(exe,settings);store.SetTerm(Source,"Estoy dispuesta.","literal");
        var terms=store.Snapshot().Terms;string reason=AutomaticTranslationQuality.Reason(Source,Ordinary,settings,terms,EmbeddedTextSyntax.Renpy);
        var issue=TranslationAttemptStatus.Evidence(Source,reason,4,Ordinary,terms,true);store.RecordIssues([issue]);
        var path=RpgTranslationCache.LoadLocal(exe,settings,TranslationCacheScope.Native).Path;TranslationStorage.WriteJson(path,new Dictionary<string,string>{{Source,Ordinary}});
        var legacy=new GameDialogueContextCache();legacy.Accept(Source,Ordinary,GameDialogueContext.Create(["I am a woman."])!,terms);TranslationStorage.WriteJson(path+".dialogue-context-v1.json",legacy.Snapshot());
        using var bridge=new Bridge(Receipt());int calls=0;using var session=new RpgTranslationSession(bridge,exe,settings,(p,s,t)=>{calls++;return Task.FromResult(Reply(p,s,Enhanced));},new(EmbeddedTextSyntax.Renpy,true,StartupCache:true));
        await session.StartAsync();await Wait(()=>bridge.Polls>=4);
        Check("exhausted existing failure does not trigger gender retry",calls==0);
        Check("existing reason and attempts remain exact",store.Issues().Contains(issue),store.Issues());
        Check("existing usable failed-quality translation stays visible",session.Lookup(Source)==Ordinary);
        await session.StopAsync();
    }
    static void ContractsAndSyntax()
    {
        var settings=Settings();var terms=new Dictionary<string,string>();
        foreach(string language in new[]{"zh-CN","zh-TW","ja","ko"})
        {
            var s=Settings(language);var now=new GameDialogueContexts();var old=new GameDialogueContexts();
            now.Read(JsonSerializer.SerializeToElement(Receipt()),s);old.Read(JsonSerializer.SerializeToElement(Receipt()));
            now.TryGet(Source,out var a);old.TryGet(Source,out var b);
            Check(language+": prior no-role context identity unchanged",a.Identity==b.Identity&&a.Roles is null);
            var sources=new Dictionary<string,string>{{"t0",Source}};
            Check(language+": old provider prompt remains byte-equivalent",JsonSerializer.Serialize(GameDialogueContexts.Attach(s,now.ForSources([Source]),sources))==JsonSerializer.Serialize(GameDialogueContexts.Attach(s,old.ForSources([Source]),sources)));
        }
        foreach(string current in new[]{"I am a man.","I am a man. I am ready.","I am not a woman."})
        {var contexts=new GameDialogueContexts();contexts.Read(JsonSerializer.SerializeToElement(Receipt(source:current)),settings);contexts.TryGet(current,out var value);Check("current fact/negation cannot inherit prior female role: "+current,value is not null&&value.Roles is null);}
        foreach(var syntax in new[]{EmbeddedTextSyntax.Rpg,EmbeddedTextSyntax.Wolf,EmbeddedTextSyntax.Yukar})
        {
            const string source="\\V[1] I am ready.",bad="\\W[1] Estoy lista.",good="\\V[1] Estoy lista.";
            var role=new GameDialogueRoleCache();role.Accept(source,bad,Role(),terms);
            Check(syntax+": fixture distinguishes legacy Renpy marker grammar",role.TryGet(source,Role(),settings,terms,out _));
            Check(syntax+": role cache revalidates actual syntax after Renpy memoization",!role.TryGet(source,Role(),settings,terms,out _,syntax));
            var legacy=new GameDialogueContextCache();var context=GameDialogueContext.Create(["I am a woman."])!;legacy.Accept(source,bad,context,terms);
            Check(syntax+": ordinary context fixture was admitted by old grammar",legacy.TryGet(source,context,settings,terms,out _));
            Check(syntax+": ordinary context uses actual control syntax",!legacy.TryGet(source,context,settings,terms,out _,syntax));
            role.Accept(source,good,Role(),terms);Check(syntax+": intact controls remain reusable",role.TryGet(source,Role(),settings,terms,out var text,syntax)&&text==good);
        }
    }
    static async Task<int> Main(string[] args)
    {
        output=Path.GetFullPath(Arg(args,"--output"));Directory.CreateDirectory(output);AppDataPaths.Initialize(["--data-root="+Path.Combine(output,"data"),"--disable-global-input"]);TranslationMemory.RootOverride=Path.Combine(output,"memory");
        string assembly=typeof(ApiSettings).Assembly.Location;Check("exact product assembly loaded",Hash(assembly)==Hash(Arg(args,"--assembly")));
        string error="";try{ContractsAndSyntax();await HotCaches();await PendingAndLateMetadata();await ExplicitRetryFailure();await ExistingFailure();}catch(Exception ex){error=ex.ToString();Check("runner completes",false,error);}
        var loadedModules=new[]{typeof(object).Assembly,typeof(JsonSerializer).Assembly,typeof(ApiSettings).Assembly}
            .Select(a=>new{name=a.GetName().Name,path=a.Location,sha256=Hash(a.Location),fileVersion=FileVersionInfo.GetVersionInfo(a.Location).FileVersion}).ToArray();
        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new{assembly,assemblySha256=Hash(assembly),runtimeVersion=Environment.Version.ToString(),loadedModules,checks=checks.Count,failures,networkRequests=0,originalGameLaunches=0,error,scope="Actual shared queue/prompt/cache code with neutral controlled transport/provider; not a real game acceptance test",items=checks},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(new{checks=checks.Count,failures,error}));return failures==0?0:1;
    }
}
