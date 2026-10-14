using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ScreenshotTranslationUiTester;

static class Program
{
    static string output="";
    static readonly ConcurrentQueue<object> checks=new();
    static readonly ConcurrentQueue<object> cases=new();
    static int failures;
    static readonly Dictionary<string,string> Base=new()
    {
        ["en"]="You should bring the ancient key to the tower.",
        ["ja"]="古い鍵を持って塔の入口まで来てください。",
        ["ko"]="이 오래된 열쇠를 가지고 탑으로 오세요.",
        ["zh"]="请带着古老的钥匙来到塔的入口。",
        ["mixed"]="You can rest here. この部屋で休んでください。 这里很安全。"
    };
    static readonly Dictionary<string,string> Packs=new()
    {
        ["en"]="The unused English language pack has another locked gate.",
        ["ja"]="使用されていない日本語パックの門は閉まっています。",
        ["ko"]="사용하지 않는 한국어 언어 팩의 문은 닫혀 있습니다."
    };
    const string Shared="You can open this shared menu. ここは共通メニューです。";
    const string Paired="You should not translate this unused original column.";
    const string Control="You have \\V[1] gold, \\N[1].";
    static void Check(string id,string name,bool passed,object? detail=null)
    {checks.Enqueue(new{id,name,passed,detail});if(!passed)Interlocked.Increment(ref failures);}
    static HashSet<string> Proven(RpgTextCatalogResult result)
        =>(typeof(RpgTextCatalogResult).GetProperty("UnlocalizedSourceTexts",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(result) as IEnumerable<string>??[]).ToHashSet(StringComparer.Ordinal);
    static string Game(string id)
    {
        string root=Path.Combine(output,"fixtures",id);Directory.CreateDirectory(root);
        string exe=Path.Combine(root,"Game.exe");File.WriteAllText(exe,"Synthetic fixture; never executed.");return exe;
    }
    static RpgTextCatalogResult Rpg(string exe)
    {
        string root=Path.GetDirectoryName(exe)!;Directory.CreateDirectory(Path.Combine(root,"js"));Directory.CreateDirectory(Path.Combine(root,"data"));
        File.WriteAllText(Path.Combine(root,"package.json"),"{\"main\":\"index.html\"}");
        File.WriteAllText(Path.Combine(root,"index.html"),"<title>Fixture</title>");
        File.WriteAllText(Path.Combine(root,"js","rmmz_core.js"),"// Never executed");
        File.WriteAllText(Path.Combine(root,"data","System.json"),"{\"locale\":\"ja_JP\"}");
        File.WriteAllText(Path.Combine(root,"data","Actors.json"),JsonSerializer.Serialize(new object?[]{null,new{name="Guard",profile=Base.Values.Append(Control).ToArray(),description=new{en=Packs["en"],ja=Packs["ja"],ko=Packs["ko"],text=Shared}}}));
        File.WriteAllText(Path.Combine(root,"data","Menu.en.json"),JsonSerializer.Serialize(new{name="This locale file must wait for the active language."}));
        File.WriteAllText(Path.Combine(root,"data","UnknownPlugin.json"),JsonSerializer.Serialize(new{name="This unknown plugin resource is not confirmed base content."}));
        return RpgTextCatalog.Read(exe,CancellationToken.None);
    }
    static RpgTextCatalogResult Unity()
    {
        var selected=new UnityTextResourceLanguageSelection(SourceLanguageMode.Auto);
        selected.Read("dialogue.json",JsonSerializer.Serialize(Base.Values.Select(text=>new{text})),CancellationToken.None);
        selected.Read("multilingual.json",JsonSerializer.Serialize(new{en=Packs["en"],ja=Packs["ja"],ko=Packs["ko"],text=Shared}),CancellationToken.None);
        selected.Read("paired.json",JsonSerializer.Serialize(new{sourceText=Paired,translation="この原文欄は使われていません。",language="ja"}),CancellationToken.None);
        selected.Read("menu.en.json",JsonSerializer.Serialize(new{text="This locale file must wait for the active language."}),CancellationToken.None);
        var tables=new UnityLocalizedTableSelection(SourceLanguageMode.Auto);
        tables.Add("en","The bundled English table must not pretranslate without an active locale.");
        tables.Add("ja","同梱された日本語テーブルは言語確認が必要です。");
        return UnityEmbeddedCatalog.CompleteSelection(selected,tables,SourceLanguageMode.Auto,5,[],true);
    }
    static ApiSettings Settings(string target,bool mixed)=>new(){TargetLanguage=target,SourceLanguage=mixed?SourceLanguageMode.Mixed:SourceLanguageMode.Auto,SourceLanguages=mixed?["en","ja"]:[],AllowEmptyApiKey=true,ApiUrl="http://127.0.0.1:1",Model="controlled-no-network"};
    static async Task Queue(string engine,string target,bool mixed)
    {
        string id=engine+"-"+target+"-"+(mixed?"en-ja":"auto"),exe=Game(id);
        var extracted=engine=="rpg"?Rpg(exe):Unity();var settings=Settings(target,mixed);var proven=Proven(extracted);
        var sent=new ConcurrentQueue<string>();var bridge=new Bridge();
        using var session=new RpgTranslationSession(bridge,exe,settings,(text,s,ct)=>Respond(text,s,sent),new(engine=="rpg"?EmbeddedTextSyntax.Rpg:EmbeddedTextSyntax.Generic,false,DesktopCatalog:ct=>Task.FromResult(extracted)));
        await session.StartAsync();await Settle(bridge,7);
        string[] first=sent.ToArray();
        foreach(var row in Base)
        {
            bool eligible=TranslationLanguagePolicy.ShouldTranslate(row.Value,settings);
            Check(id,"Unseen base "+row.Key+" obeys source and target policy",first.Contains(row.Value)==eligible,new{eligible,requested=first.Contains(row.Value)});
        }
        Check(id,"Shared multilingual field is pretranslated",first.Contains(Shared));
        Check(id,"Inactive packs remain unsent",!first.Intersect(Packs.Values).Any());
        Check(id,"Paired original column remains unsent",!first.Contains(Paired));
        Check(id,"Active locale is not fabricated",!session.Snapshot.LanguageEvidence.Contains("active-game-locale",StringComparison.Ordinal));
        Check(id,"Directory has background work before visible dialogue",session.Snapshot.DirectoryTexts>0,new{session.Snapshot.DirectoryTexts,session.Snapshot.RuntimeCapturedTexts});
        if(engine=="rpg"&&TranslationLanguagePolicy.ShouldTranslate(Control,settings))
            Check(id,"Control tokens preserved in cached translation",bridge.Applied.TryGetValue(Control,out var translated)&&translated.Contains("\\V[1]")&&translated.Contains("\\N[1]"),bridge.Applied.GetValueOrDefault(Control));
        int count=sent.Count;await Settle(bridge,bridge.Polls+5);
        Check(id,"Stable screen does not resubmit background results",sent.Count==count,new{count,after=sent.Count});
        // The first request for this line must already have happened in the
        // background. Displaying it must reuse the cache, not call the provider.
        string visible=Base.Values.First(v=>TranslationLanguagePolicy.ShouldTranslate(v,settings));
        count=sent.Count;bridge.Texts=[visible];await Settle(bridge,bridge.Polls+5);
        Check(id,"Previously unseen dialogue uses pretranslation when displayed",sent.Count==count&&bridge.Applied.ContainsKey(visible));
        cases.Enqueue(new{id,extracted=extracted.Texts.Length,proven=proven.Count,requests=first.Length,directory=session.Snapshot.DirectoryTexts,deferred=Property(session.Snapshot,"CatalogDeferredTexts")});
        await session.StopAsync();
    }
    static int Property(object value,string name)=>(int)(value.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(value)??0);
    static Task<string> Respond(string text,ApiSettings settings,ConcurrentQueue<string> sent)
    {
        string Reply(string source)
        {
            sent.Enqueue(source);
            string answer=settings.TargetLanguage switch{"en"=>"The translated dialogue is ready.","ja"=>"ゲームの台詞の翻訳が完了しました。",_=>"游戏对话已经翻译完成。"};
            return answer+string.Concat(TranslationLanguageSpans.Required(source,settings))+string.Concat(Regex.Matches(source,@"⟦RPG\d+⟧").Select(m=>m.Value));
        }
        if(settings.RequestKind==TranslationRequestKind.GameJson)
        {var rows=JsonSerializer.Deserialize<Dictionary<string,string>>(text)!;return Task.FromResult(JsonSerializer.Serialize(rows.ToDictionary(p=>p.Key,p=>Reply(p.Value))));}
        return Task.FromResult(Reply(text));
    }
    static async Task Settle(Bridge bridge,int polls)
    {var until=DateTime.UtcNow.AddSeconds(7);while(bridge.Polls<polls&&DateTime.UtcNow<until)await Task.Delay(35);await Task.Delay(350);if(bridge.Polls<polls)throw new TimeoutException("Controlled queue stalled");}
    sealed class Bridge:IGameDataConnection
    {
        internal string[] Texts=[];int polls;public int Polls=>Volatile.Read(ref polls);public bool Connected=>true;
        internal readonly ConcurrentDictionary<string,string> Applied=new();
        public Task<JsonElement> RequestAsync(JsonObject request,CancellationToken token=default)
        {
            string op=request["op"]!.GetValue<string>();
            if(op=="translationPoll"){Interlocked.Increment(ref polls);return Task.FromResult(JsonSerializer.SerializeToElement(new{epoch=1,texts=Texts}));}
            int stored=0;
            if(op=="translationApply"&&request["entries"] is JsonArray entries)foreach(var item in entries)
            {Applied[item!["source"]!.GetValue<string>()]=item["text"]!.GetValue<string>();stored++;}
            return Task.FromResult(JsonSerializer.SerializeToElement(new{epoch=1,stored,applied=stored}));
        }
        public void Dispose(){}
    }
    static async Task<int> Main(string[] args)
    {
        output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        string dependencies=Path.GetFullPath(args[1]);
        AssemblyLoadContext.Default.Resolving+=(_,n)=>File.Exists(Path.Combine(dependencies,n.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dependencies,n.Name+".dll")):null;
        AppDataPaths.Initialize(["--data-root="+Path.Combine(output,"data"),"--disable-global-input"]);TranslationMemory.RootOverride=Path.Combine(output,"memory");
        if(args.Contains("--install-only"))TransferredInstallTests.Run(Path.Combine(output,"install-fixtures"),Check);
        else
        {
            using var slots=new SemaphoreSlim(3);
            var matrix=from engine in new[]{"rpg","unity"} from target in new[]{"zh-CN","en","ja"} from mixed in new[]{false,true} select (engine,target,mixed);
            await Task.WhenAll(matrix.Select(async v=>{await slots.WaitAsync();try{await Queue(v.engine,v.target,v.mixed);}catch(Exception ex){Check(v.engine+v.target+v.mixed,"Harness completed",false,ex.ToString());}finally{slots.Release();}}));
        }
        var summary=new{passed=failures==0,checks=checks.Count,failures,realApiCalls=0,commercialGamesRun=0,assemblyVersion=typeof(MainForm).Assembly.GetName().Version?.ToString(),assemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainForm).Assembly.Location))),cases=cases.ToArray(),results=checks.ToArray()};
        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        Console.WriteLine($"{(failures==0?"PASS":"FAIL")} {checks.Count-failures}/{checks.Count}");return failures==0?0:1;
    }
}
