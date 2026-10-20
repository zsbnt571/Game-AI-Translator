using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Prepares reusable translations without pretending that a runtime is connected.
// A separate sidecar avoids overwriting a running game's native cache snapshot.
internal static class CatalogTranslationPreparation
{
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> jobs=new(StringComparer.OrdinalIgnoreCase);
    internal sealed record Progress(int Total,int Ready,int Added,int Failed,string Status);
    internal sealed record Entry(string Translation,string? ReplacesHash=null,string? GlossaryStamp=null);
    internal sealed record Document(int Version,EmbeddedTextSyntax Syntax,Dictionary<string,Entry> Entries);
    internal static bool Supports(string engine)=>engine is "Defold" or "WOLF RPG Editor" or "RPG Maker XP" or "RPG Maker VX" or "RPG Maker VX Ace" or "RPG Maker MV" or "RPG Maker MZ" or "TyranoBuilder" or "TyranoScript" or "Visual Novel Maker" or "ChoiceScript";
    internal static EmbeddedTextSyntax Syntax(string engine)=>engine=="WOLF RPG Editor"?EmbeddedTextSyntax.Wolf:engine.StartsWith("RPG Maker ",StringComparison.Ordinal)?EmbeddedTextSyntax.Rpg:EmbeddedTextSyntax.Generic;
    internal static ExperimentalRpgCatalog.Result ReadCatalog(GameInfo game,ApiSettings settings,CancellationToken token)
    {
        if(game.Engine=="Defold")return DefoldTextCatalog.Read(game.ExePath,token);
        if(game.Engine=="WOLF RPG Editor")return WolfGameAdapter.DisplayBranches(WolfEngineCollection.Read(game.ExePath,token,collect:true));
        if(game.Engine is "RPG Maker XP" or "RPG Maker VX" or "RPG Maker VX Ace")return ExperimentalRpgCatalog.Read(game.ExePath,token);
        RpgTextCatalogResult read=game.AdapterId=="web-rpg"?WebTextCatalog.Read(game.ExePath,token,settings.SourceLanguage,settings.CatalogLanguageHint):
            game.Engine is "RPG Maker MV" or "RPG Maker MZ"?RpgTextCatalog.Read(game.ExePath,token,settings.SourceLanguage,preferredLanguage:settings.CatalogLanguageHint):
            WebTextCatalog.Read(game.ExePath,token,settings.SourceLanguage,settings.CatalogLanguageHint);
        var selected=read.Texts.ToHashSet(StringComparer.Ordinal);var origins=(read.Origins??[]).Where(o=>selected.Contains(o.Text)).ToArray();
        var located=origins.Select(o=>o.Text).ToHashSet(StringComparer.Ordinal);
        var entries=origins.Select(o=>new ExperimentalRpgCatalog.Entry(o.File,o.Location,o.Text){ContentHash=o.ContentHash,Kind=o.Kind,Encoding=o.Encoding,SchemaFile=o.SchemaFile,SchemaHash=o.SchemaHash,Context=o.Context})
            .Concat(read.Texts.Where(t=>!located.Contains(t)).Select(t=>new ExperimentalRpgCatalog.Entry("catalog","display",t))).ToArray();
        var issues=read.Report?.Issues??[];
        bool completed=read.Report?.Completed??read.ReadCompleted;
        ExperimentalRpgCatalog.FileResult[] files=[new("资源目录（汇总）","读取完成",read.Texts.Length,$"已读取 {read.Files:N0} 个相关文件；详细来源保留在原文字段记录中。"),
            ..issues.Select(i=>new ExperimentalRpgCatalog.FileResult(i.Source,i.Kind==CatalogIssueKind.Limited?"达到限制":i.Kind==CatalogIssueKind.Failed?"解析失败":"格式未支持",0,i.Reason))];
        return new(game.Engine,entries,files,completed,"提取结果可整批预译；已保存译文在下次翻译启动时复用。") { SourceCatalog=read };
    }
    internal static string PathFor(string exe,ApiSettings settings)=>Path.Combine(TranslationCacheReset.DirectoryFor(exe),RpgTranslationCache.Id(settings,RpgTranslationCache.Endpoint(settings.ApiUrl))+".prepared.json");
    private static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static Document? ReadDocument(string path)
    {
        try
        {
            if(!File.Exists(path)||new FileInfo(path).Length>64L*1024*1024)return null;
            var doc=JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
            if(doc is null||doc.Version!=1||doc.Syntax is not (EmbeddedTextSyntax.Generic or EmbeddedTextSyntax.Rpg or EmbeddedTextSyntax.Wolf)||doc.Entries is null||doc.Entries.Count>100000
                ||doc.Entries.Any(p=>p.Value is null||!TranslationWorkspaceStore.Basic(p.Key,p.Value.Translation)
                    ||p.Value.ReplacesHash is {} hash&&(hash.Length!=64||hash.Any(c=>!Uri.IsHexDigit(c)))
                    ||p.Value.GlossaryStamp is {} stamp&&(stamp.Length!=64||stamp.Any(c=>!Uri.IsHexDigit(c)))))return null;
            return doc;
        }
        catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(JsonException){return null;}
    }
    private static Document? Read(string exe,ApiSettings settings)
    {
        string path=PathFor(exe,settings);return ReadDocument(path)??ReadDocument(path+".previous");
    }
    internal static RpgTranslationCache.Loaded Merge(string exe,ApiSettings settings,RpgTranslationCache.Loaded local)
    {
        var prepared=Read(exe,settings);if(prepared is null||prepared.Entries.Count==0)return local;
        var terms=TranslationWorkspaceStore.For(exe,settings).Snapshot().Terms;
        var receipts=TranslationGlossaryCache.Load(local.Path,local.Receipts);
        bool Valid(string source,string value)=>TranslationWorkspaceStore.Valid(source,value,prepared.Syntax)&&AutomaticTranslationQuality.Displayable(source,value,settings,terms,prepared.Syntax);
        Dictionary<string,string>? changed=null;
        foreach(var p in prepared.Entries)
        {
            // Old sidecars cannot prove whether a now-removed glossary was used.
            // Keep their files, but never mint new proof for an unknown request.
            // New sidecars bind additions and removals to request-time mappings.
            if(p.Value.GlossaryStamp is not {} stamp||stamp!=TranslationGlossary.Stamp(terms,p.Key))continue;
            if(!Valid(p.Key,p.Value.Translation))continue;
            bool present=local.Entries.TryGetValue(p.Key,out var current);
            if(present&&current==p.Value.Translation){receipts.Accept(p.Key,current!,terms);continue;}
            // Explicit retries may replace the exact old value they observed.
            // A subsequently changed native cache keeps priority.
            if(present&&Valid(p.Key,current!)&&(p.Value.ReplacesHash is null||Hash(current!)!=p.Value.ReplacesHash))continue;
            if(!present&&(changed?.Count??local.Entries.Count)>=100000)continue;
            changed??=new(local.Entries,StringComparer.Ordinal);changed[p.Key]=p.Value.Translation;
            receipts.Accept(p.Key,p.Value.Translation,terms);
        }
        return local with {Entries=changed??local.Entries,NeedsSave=local.NeedsSave||changed is not null,Receipts=receipts.Snapshot(changed??local.Entries)};
    }
    internal static string[] Sources(ExperimentalRpgCatalog.Result catalog,ApiSettings settings)
    {
        var policy=new EmbeddedSourceLanguagePolicy(settings);var syntax=Syntax(catalog.Engine);
        var read=catalog.SourceCatalog;if(read is not null)policy.SelectCatalog(read.Texts,read.SelectedLanguage);
        bool Allowed(string text)=>read is null?policy.AllowsCatalog(text):policy.AllowsSelectedText(text)&&
            (read.UnlocalizedSourceTexts?.Contains(text)==true||policy.AllowsActiveCatalog(text,read.TrustedSourceTexts?.Contains(text)==true)||policy.AllowsScriptCatalog(text,read.ScriptSourceLanguage));
        return catalog.Entries.Select(e=>e.Text).Where(t=>t.Length is >0 and <=6000&&!t.Contains('\0')&&Allowed(t)&&RpgTranslationText.HasTranslatableText(t,syntax)).Distinct(StringComparer.Ordinal).Take(100000).ToArray();
    }
    internal static bool NeedsPreparation(string exe,ExperimentalRpgCatalog.Result catalog,ApiSettings settings)
    {
        var syntax=Syntax(catalog.Engine);var existing=RpgTranslationCache.Effective(exe,settings,syntax);
        var document=TranslationWorkspaceStore.For(exe,settings).Snapshot();
        return Sources(catalog,settings).Any(t=>!existing.TryGetValue(t,out var value)||TranslationWorkspaceStore.Manual(document,t,syntax) is null&&
            (!AutomaticTranslationQuality.Valid(t,value,settings,document.Terms,syntax)||document.Requests.ContainsKey(t)));
    }
    internal static async Task<Progress> PrepareAsync(string exe,ExperimentalRpgCatalog.Result catalog,ApiSettings source,
        Func<string,ApiSettings,CancellationToken,Task<string>> translate,Action<Progress>? progress=null,CancellationToken token=default)
    {
        var settings=ApiSettingsSnapshot.Copy(source);string path=PathFor(exe,settings);var gate=jobs.GetOrAdd(path,_=>new(1,1));
        await gate.WaitAsync(token);
        try
        {
            var syntax=Syntax(catalog.Engine);string[] sources=Sources(catalog,settings);
            TranslationOriginIndex.TrySave(exe,settings,catalog.SourceCatalog??new(catalog.Entries.Select(e=>e.Text).Distinct(StringComparer.Ordinal).ToArray(),catalog.Files.Length,[]){
                Origins=catalog.Entries.Select(e=>ExperimentalRpgCatalog.Origin(e,"experimental-display-field")).ToArray(),
                ReadCompleted=catalog.Complete,Report=new(catalog.Complete,[])});
            var store=TranslationWorkspaceStore.For(exe,settings);var document=store.Snapshot();
            var terminology=GameTerminology.For(store);
            var acceptedSources=sources.ToHashSet(StringComparer.Ordinal);
            terminology.Observe(sources,catalog.Entries.Where(e=>acceptedSources.Contains(e.Text)).Select(e=>ExperimentalRpgCatalog.Origin(e,"experimental-display-field")));
            progress?.Invoke(new(sources.Length,0,0,0,"正在统一人物、地点和物品等名称…"));
            await terminology.PrepareAsync(settings,(prompt,profile,ct)=>TranslationFlightPool.Shared.Request(translate,prompt,profile,ct),token);
            document=store.Snapshot();
            var existing=RpgTranslationCache.Effective(exe,settings,syntax);
            var readySources=sources.Where(t=>existing.TryGetValue(t,out var value)&&
                (TranslationWorkspaceStore.Manual(document,t,syntax) is not null||AutomaticTranslationQuality.Valid(t,value,settings,document.Terms,syntax))).ToHashSet(StringComparer.Ordinal);
            string[] pending=sources.Where(t=>!readySources.Contains(t)||document.Requests.ContainsKey(t)&&TranslationWorkspaceStore.Manual(document,t,syntax) is null).ToArray();
            int ready=readySources.Count,added=0,failed=0,consecutiveFailures=0;
            Progress Report(string status){var p=new Progress(sources.Length,ready,added,failed,status);progress?.Invoke(p);return p;}
            Report("已有译文已读取。");if(pending.Length==0)return Report("已准备完成；已保存译文将在翻译启动时复用。");
            if(string.IsNullOrWhiteSpace(settings.ApiUrl)||string.IsNullOrWhiteSpace(settings.Model)||!settings.AllowEmptyApiKey&&string.IsNullOrWhiteSpace(settings.ApiKey))throw new IOException("请先在游戏页面选择并配置翻译方案，再准备译文。");
            var single=ApiSettingsSnapshot.Copy(settings);var batch=ApiSettingsSnapshot.Copy(settings);RpgTranslationPrompts.Configure(settings,single,batch);
            for(int index=0;index<pending.Length;)
            {
                token.ThrowIfCancellationRequested();var group=new List<string>();int chars=0;
                if(!PortableDataStorage.Same(path,PathFor(exe,settings)))throw new OperationCanceledException("机器译文已重置，停止保存此前的准备结果。");
                // Engine collection produces many short labels. A bounded
                // group of 24 avoids paying one provider round trip per eight
                // words; longer dialogue keeps the established eight-item cap.
                while(index<pending.Length&&group.Count<24&&(group.Count==0||chars+pending[index].Length<=6000))
                {
                    if(group.Count>=8&&(group.Any(t=>t.Length>80)||pending[index].Length>80||chars+pending[index].Length>1200))break;
                    string text=pending[index++];group.Add(text);chars+=text.Length;
                }
                Report($"正在准备译文：{ready:N0} / {sources.Length:N0}。");
                document=store.Snapshot();
                foreach(string text in group.Where(t=>TranslationWorkspaceStore.Manual(document,t,syntax) is not null).ToArray())
                {group.Remove(text);if(readySources.Add(text))ready++;}
                if(group.Count==0)continue;
                var requestTokens=group.ToDictionary(t=>t,t=>document.Requests.GetValueOrDefault(t,""),StringComparer.Ordinal);
                var native=RpgTranslationCache.LoadLocal(exe,settings,TranslationCacheScope.Native);
                var replaced=group.ToDictionary(t=>t,t=>native.Entries.TryGetValue(t,out var value)?Hash(value):null,StringComparer.Ordinal);
                var prompts=TranslationWorkspaceStore.WithTerms(document,group.ToArray(),single,batch);
                var requestTerms=document.Terms;
                var translated=await RpgTranslationBatch.Translate(group.ToArray(),prompts.Single,prompts.Batch,(prompt,profile,ct)=>TranslationFlightPool.Shared.Request(translate,prompt,profile,ct),token,syntax:syntax,terms:document.Terms);
                if(!PortableDataStorage.Same(path,PathFor(exe,settings)))throw new OperationCanceledException("机器译文已重置，停止保存此前的准备结果。");
                document=store.Snapshot(true);
                var accepted=translated.Where(p=>group.Contains(p.Key,StringComparer.Ordinal)&&TranslationWorkspaceStore.Manual(document,p.Key,syntax) is null
                    &&document.Requests.GetValueOrDefault(p.Key,"")==requestTokens[p.Key]
                    &&TranslationGlossary.Stamp(requestTerms,p.Key)==TranslationGlossary.Stamp(document.Terms,p.Key)
                    &&TranslationWorkspaceStore.Valid(p.Key,p.Value,syntax)&&AutomaticTranslationQuality.Displayable(p.Key,p.Value,settings,document.Terms,syntax)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
                var quality=translated as RpgTranslationBatchResult;
                var complete=accepted.Keys.Where(t=>quality?.SoftFailures.ContainsKey(t)!=true).ToArray();
                var retained=new Dictionary<string,string>(StringComparer.Ordinal);
                foreach(string text in accepted.Keys.ToArray())
                    if(quality?.SoftFailures.ContainsKey(text)==true&&existing.TryGetValue(text,out var old))
                    {retained[text]=old;accepted.Remove(text);}
                if(accepted.Count>0)
                {
                    Save(exe,settings,syntax,accepted,replaced,path,requestTerms);foreach(string text in complete)if(readySources.Add(text))ready++;
                    added+=accepted.Count;consecutiveFailures=0;
                    store.CompleteRequests(complete.Where(t=>requestTokens[t].Length>0).ToDictionary(t=>t,t=>requestTokens[t],StringComparer.Ordinal));
                }
                else consecutiveFailures++;
                string[] manualNow=group.Where(t=>TranslationWorkspaceStore.Manual(document,t,syntax) is not null).ToArray();
                foreach(string text in manualNow)if(readySources.Add(text))ready++;
                if(manualNow.Length>0)consecutiveFailures=0;
                failed+=group.Count-complete.Length-manualNow.Length;
                if(quality is not null)
                    store.RecordIssues(group.Where(t=>!manualNow.Contains(t)&&
                        TranslationGlossary.Stamp(requestTerms,t)==TranslationGlossary.Stamp(document.Terms,t)&&
                        document.Requests.GetValueOrDefault(t,"")==requestTokens[t]&&
                        (quality.Failures.ContainsKey(t)||quality.SoftFailures.ContainsKey(t)))
                        .Select(t=>TranslationAttemptStatus.Evidence(t,quality.SoftFailures.GetValueOrDefault(t)??quality.Failures.GetValueOrDefault(t,"unknown"),1,
                            quality.Replies.GetValueOrDefault(t,""),requestTerms,quality.SoftFailures.ContainsKey(t),retained.GetValueOrDefault(t)??accepted.GetValueOrDefault(t))),complete.Concat(manualNow));
                if(translated is RpgTranslationBatchResult detail&&detail.Failures.Values.Any(v=>v is "provider-authentication" or "provider-rate-limited"))return Report("翻译服务拒绝或限制了请求；已保留完成部分，请检查翻译方案后继续。");
                if(consecutiveFailures>=3)return Report("连续三批未能完成，已暂停；已有译文已保存，可稍后继续。");
            }
            return Report(failed==0?"译文已保存；下次翻译启动将优先复用。":"部分文字未能翻译；完成部分已保存，再次准备只补缺失译文。");
        }
        finally{gate.Release();}
    }
    private static void Save(string exe,ApiSettings settings,EmbeddedTextSyntax syntax,Dictionary<string,string> batch,Dictionary<string,string?> replaced,string path,IReadOnlyDictionary<string,string> terms)
    {
        // Keep the path captured before the request: a late pre-reset result
        // must remain in its old generation, never enter the freshly reset cache.
        string id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        using var mutex=new Mutex(false,"Local\\FusionR1.CatalogPreparation."+id);bool held=false;
        try
        {
            try{held=mutex.WaitOne(1000);}catch(AbandonedMutexException){held=true;}if(!held)throw new IOException("另一窗口正在保存译文，请稍后继续。");
            // A corrupt primary remains available for recovery and diagnosis.
            if(File.Exists(path)&&ReadDocument(path) is null)throw new IOException("准备译文缓存读取失败，未覆盖；请先检查该游戏的译文数据。");
            var document=ReadDocument(path)??ReadDocument(path+".previous")??new(1,syntax,new(StringComparer.Ordinal));
            if(document.Syntax!=syntax)
            {
                if(syntax!=EmbeddedTextSyntax.Wolf||document.Syntax!=EmbeddedTextSyntax.Rpg)
                    throw new IOException("原有译文的控制符格式不同，未覆盖；请检查游戏识别结果。");
                // Old WOLF preparation used RPG syntax. Revalidate retained
                // entries under WOLF rules before the new atomic generation.
                document=new(1,syntax,document.Entries.Where(p=>TranslationWorkspaceStore.Valid(p.Key,p.Value.Translation,syntax)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal));
            }
            foreach(var p in batch)document.Entries[p.Key]=new(p.Value,replaced[p.Key],TranslationGlossary.Stamp(terms,p.Key));
            if(document.Entries.Count>100000||JsonSerializer.SerializeToUtf8Bytes(document,new JsonSerializerOptions{WriteIndented=true}).Length>60L*1024*1024)throw new IOException("准备译文缓存已达到容量限制；此前保存的译文保持可用。");
            TranslationStorage.WriteJson(path,document);
        }
        finally{if(held)mutex.ReleaseMutex();}
        var receipts=TranslationGlossaryCache.Load(path);
        foreach(var p in batch)receipts.Accept(p.Key,p.Value,terms);
        TranslationMemory.Publish(exe,settings,TranslationCacheScope.Native,false,batch,receipts.Snapshot(batch));
    }
}
