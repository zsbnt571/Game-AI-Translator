using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Active native identities have their own request/result lane. Its results are
// never published as source-only memory, even when two speakers share a line.
internal sealed class GameCharacterRuntimeLane
{
    internal const string Contract="fusion-character-runtime-1";
    internal sealed record Request(string RequestId,string Source,GameCharacterBinding Binding,string WireCharacterId,bool Attributed);
    internal sealed record Failure(string Source,string CharacterId,string Code,int Attempts,string Reply,string Retained,bool Resolved=false);
    private sealed record Job(Request Request,GameDialogueContext Context,IReadOnlyDictionary<string,string> Terms,Task<Dictionary<string,string>> Work);
    private readonly GameCharacterProfiles profiles;
    private readonly GameCharacterTranslationCache cache;
    private readonly ApiSettings settings,single,batch;
    private readonly EmbeddedTextSyntax syntax;
    private readonly Func<TranslationWorkspaceStore.Document> document;
    private readonly Func<string,string?> manual,ordinary;
    private readonly Func<string,ApiSettings,CancellationToken,Task<string>> provider;
    private readonly Action accepted;
    private readonly CancellationToken token;
    private readonly string failurePath;
    private bool failureHistoryReadOnly;
    private bool failureFileExisted;
    private long failureFileLength;
    private DateTime failureFileStamp;
    internal string FailureHistoryWarning {get;private set;}="";
    private Dictionary<string,Request> current=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Failure> failures=new(StringComparer.Ordinal);
    private readonly Dictionary<string,DateTime> retryAfter=new(StringComparer.Ordinal);
    private readonly Dictionary<string,int> retryBase=new(StringComparer.Ordinal);
    private readonly Dictionary<string,JsonObject> replies=new(StringComparer.Ordinal);
    private Job? job;
    private int epoch=-1;
    internal int ActiveCount=>job is {Work.IsCompleted:false}?1:0;
    internal int FailedCount=>failures.Values.Count(f=>!f.Resolved);
    internal int StoppedCount=>failures.Count(p=>!p.Value.Resolved&&p.Value.Attempts-retryBase.GetValueOrDefault(p.Key)>=4);
    internal string[] FailureCodes=>failures.Values.Where(f=>!f.Resolved).Select(f=>f.Code).Distinct(StringComparer.Ordinal).ToArray();
    internal Task? PendingTask=>job?.Work;
    internal void Retry(){foreach(var p in failures.Where(p=>!p.Value.Resolved)){retryBase[p.Key]=p.Value.Attempts;retryAfter[p.Key]=DateTime.MinValue;}}
    internal GameCharacterRuntimeLane(GameCharacterProfiles profiles,GameCharacterTranslationCache cache,ApiSettings settings,ApiSettings single,ApiSettings batch,
        EmbeddedTextSyntax syntax,Func<TranslationWorkspaceStore.Document> document,Func<string,string?> manual,Func<string,string?> ordinary,
        Func<string,ApiSettings,CancellationToken,Task<string>> provider,Action accepted,string failurePath,CancellationToken token)
    {
        this.profiles=profiles;this.cache=cache;this.settings=settings;this.single=single;this.batch=batch;this.syntax=syntax;this.document=document;
        this.manual=manual;this.ordinary=ordinary;this.provider=provider;this.accepted=accepted;this.failurePath=failurePath;this.token=token;
        try
        {
            var info=new FileInfo(failurePath);failureFileExisted=info.Exists;
            if(info.Exists)
            {
                failureFileLength=info.Length;failureFileStamp=info.LastWriteTimeUtc;
                if(info.Length>4*1024*1024)throw new JsonException("Retry history exceeds its read limit.");
                using var parsed=JsonDocument.Parse(File.ReadAllBytes(failurePath));
                if(parsed.RootElement.ValueKind!=JsonValueKind.Object)throw new JsonException("Invalid retry history.");
                var loaded=new Dictionary<string,Failure>(StringComparer.Ordinal);
                foreach(var item in parsed.RootElement.EnumerateObject())
                {
                    if(item.Name.Length!=64||!item.Name.All(Uri.IsHexDigit)||loaded.ContainsKey(item.Name)||item.Value.ValueKind!=JsonValueKind.Object
                        ||item.Value.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=item.Value.EnumerateObject().Count())throw new JsonException("Invalid retry history row.");
                    var f=item.Value.Deserialize<Failure>();
                    if(f is null||f.Attempts is <0 or >1000||!GameCharacterBinding.ValidId(f.CharacterId)||f.Source is not {Length:>0 and <=6000}
                        ||f.Code is not {Length:>0 and <=4000}||f.Reply is null||f.Reply.Length>100000||f.Retained is null||f.Retained.Length>24000)throw new JsonException("Invalid retry history fields.");
                    loaded.Add(item.Name,f);
                }
                foreach(var item in loaded)failures.Add(item.Key,item.Value);
            }
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException)
        {failureHistoryReadOnly=true;FailureHistoryWarning="角色重试记录无法读取，已保留原记录并暂停角色重试。";AppLog.Write("character-runtime","Existing character retry evidence retained without overwrite",ex);}
    }
    internal static bool Negotiated(JsonElement receipt)=>receipt.ValueKind==JsonValueKind.Object&&receipt.TryGetProperty("characterRuntimeContract",out var c)&&c.ValueKind==JsonValueKind.String&&c.GetString()==Contract;
    internal void Observe(JsonElement receipt,int generation)
    {
        if(!GameDialogueRoles.Applies(settings))return;
        if(!Negotiated(receipt)){current.Clear();replies.Clear();profiles.ObserveRuntime(new Dictionary<string,GameCharacterBinding>());return;}
        if(epoch!=generation){epoch=generation;current.Clear();replies.Clear();profiles.ObserveRuntime(new Dictionary<string,GameCharacterBinding>());}
        if(!receipt.TryGetProperty("characterRequests",out var rows)||rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>32){current.Clear();replies.Clear();profiles.ObserveRuntime(new Dictionary<string,GameCharacterBinding>());return;}
        var next=new Dictionary<string,Request>(StringComparer.Ordinal);var duplicate=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in rows.EnumerateArray())
        {
            if(!Read(row,"requestId",80,out var id)||!System.Text.RegularExpressions.Regex.IsMatch(id,@"^[A-Za-z0-9_-]+$")
                ||!Read(row,"source",6000,out var source,true)||!Read(row,"characterId",160,out var character)||!GameCharacterBinding.ValidId(character))continue;
            string wireCharacter=character;bool attributed=profiles.TryMapRuntimeIdentity(row,wireCharacter,out character);
            Read(row,"displayName",200,out var name);var facts=new List<string>();
            if(row.TryGetProperty("evidence",out var evidence)&&evidence.ValueKind==JsonValueKind.Array&&evidence.GetArrayLength()<=3)
                foreach(var fact in evidence.EnumerateArray())if(fact.ValueKind==JsonValueKind.String&&fact.GetString() is {Length:<=600} text)facts.Add(text);
            if(AutomaticGenderAgreement.BlocksInheritedEvidence(source))facts.Clear();facts.Add(source);
            var binding=new GameCharacterBinding(character,name.Length>0?name:character,GameCharacterBinding.Hash(Contract+"\0"+character),GameCharacterBinding.DetectGender(facts));
            if(!binding.Valid())continue;
            if(!next.TryAdd(id,new(id,source,binding,wireCharacter,attributed)))duplicate.Add(id);
        }
        foreach(string id in duplicate)next.Remove(id);
        current=next;foreach(string id in replies.Keys.Except(next.Keys).ToArray())replies.Remove(id);
        profiles.ObserveRuntime(next.Where(p=>p.Value.Attributed).ToDictionary(p=>p.Key,p=>p.Value.Binding,StringComparer.Ordinal));
    }
    private static bool Read(JsonElement row,string property,int maximum,out string value,bool multiline=false)
    {
        value="";if(row.ValueKind!=JsonValueKind.Object||!row.TryGetProperty(property,out var item)||item.ValueKind!=JsonValueKind.String||item.GetString() is not {Length:>0} s||s.Length>maximum||s.Any(c=>char.IsControl(c)&&!(multiline&&c is '\r' or '\n' or '\t')))return false;value=s;return true;
    }
    private GameDialogueContext? Context(Request request)
    {
        if(!request.Attributed)return null;
        if(!profiles.TryResolveRuntime(request.RequestId,request.Binding,out var role)||role.Selection==CharacterGenderSelection.Auto&&role.EffectiveGender=="unknown")return null;
        return GameDialogueContext.Create([],character:new(role.CharacterId,role.Scope,role.EffectiveGender,role.Selection,role.Identity));
    }
    private static string Key(Request request,GameDialogueContext context)=>GameCharacterBinding.Hash(request.Source+"\0"+context.Identity);
    private void Reply(Request request,string? text,bool pending=false)
    {
        var reply=new JsonObject{["requestId"]=request.RequestId,["source"]=request.Source,["characterId"]=request.WireCharacterId};
        if(text is not null)reply["text"]=text;else if(pending)reply["pending"]=true;else reply["fallback"]=true;
        replies[request.RequestId]=reply;
    }
    internal void AddReplies(JsonObject request)
    {
        request["characterRuntimeContract"]=Contract;var items=new JsonArray();int bytes=0;
        foreach(var item in replies.Values.Take(16)){int length=System.Text.Encoding.UTF8.GetByteCount(item.ToJsonString());if(bytes+length>42000)break;bytes+=length;items.Add(item.DeepClone());}
        request["characterResults"]=items;
    }
    internal async Task Advance(int providerSlots)
    {
        if(!GameDialogueRoles.Applies(settings))return;
        if(job is {Work.IsCompleted:true} completed)
        {
            job=null;Dictionary<string,string> result;
            try{result=await completed.Work;}catch(OperationCanceledException)when(token.IsCancellationRequested||CharacterGenderEnhancementPolicy.Revoked(settings)){throw;}
            catch(Exception ex){result=new RpgTranslationBatchResult();((RpgTranslationBatchResult)result).Failures[completed.Request.Source]=SafeDiagnosticOutput.ExceptionSummary(ex);}
            if(!GameDialogueRoles.Applies(settings))return;
            token.ThrowIfCancellationRequested();
            string source=completed.Request.Source,key=Key(completed.Request,completed.Context);var quality=result as RpgTranslationBatchResult;
            string reason=quality?.SoftFailures.GetValueOrDefault(source,"")??"";
            if(result.TryGetValue(source,out var text)&&reason.Length==0&&AutomaticTranslationQuality.Valid(source,text,settings,completed.Terms,syntax))
            {
                cache.Accept(source,text,completed.Context,completed.Terms);accepted();
                if(failures.TryGetValue(key,out var old)){failures[key]=old with{Resolved=true};PersistFailures();}
            }
            else
            {
                if(reason.Length==0)reason=quality?.Failures.GetValueOrDefault(source,"quality-rejected")??"quality-rejected";
                int attempts=(failures.GetValueOrDefault(key)?.Attempts??0)+Math.Max(1,quality?.RequestAttempts.GetValueOrDefault(source,1)??1);
                string prior=ordinary(source)??(cache.TryRetained(source,completed.Request.Binding,settings,completed.Terms,out var retained,syntax)?retained:"");
                failures[key]=new(source,completed.Request.Binding.CharacterId,reason,Math.Min(attempts,1000),quality?.Replies.GetValueOrDefault(source,"")??"",prior);
                int cycle=attempts-retryBase.GetValueOrDefault(key);
                retryAfter[key]=cycle>=4?DateTime.MaxValue:DateTime.UtcNow.AddSeconds(Math.Min(60,Math.Pow(3,cycle)));
                PersistFailures();
            }
        }
        var doc=document();
        foreach(var request in current.Values)
        {
            string source=request.Source;
            if(manual(source) is {} corrected){Reply(request,corrected);continue;}
            var context=Context(request);
            string? ordinaryText=ordinary(source);
            if(!request.Attributed){Reply(request,ordinaryText);continue;}
            // Restoring automatic with no reliable role must release the role
            // lane, so the normal queue can translate a cold ordinary cache.
            // Retained variants remain on disk and can be reused for that role.
            if(context is null||!TranslationLanguagePolicy.ShouldTranslate(source,settings)){Reply(request,ordinaryText);continue;}
            string? prior=ordinaryText??(cache.TryRetained(source,request.Binding,settings,doc.Terms,out var retained,syntax)?retained:null);
            if(cache.TryGet(source,context,settings,doc.Terms,out var ready,syntax,fallback:false)){Reply(request,ready);continue;}
            if(failureHistoryReadOnly){Reply(request,prior);continue;}
            string key=Key(request,context);
            // Automatic enhancement never forces a retranslation of usable old
            // ordinary text. An explicit user choice may request its own variant.
            if(context.Character!.Selection==CharacterGenderSelection.Auto&&ordinaryText is not null){Reply(request,ordinaryText);continue;}
            bool stopped=failures.TryGetValue(key,out var failure)&&!failure.Resolved&&(failure.Attempts-retryBase.GetValueOrDefault(key)>=4||retryAfter.GetValueOrDefault(key)>DateTime.UtcNow);
            if(stopped){Reply(request,prior);continue;}
            if(job is null&&providerSlots>0)
            {
                var contexts=new Dictionary<string,GameDialogueContext>(StringComparer.Ordinal){{source,context}};
                var prompts=TranslationWorkspaceStore.WithTerms(doc,[source],single,batch);
                job=new(request,context,doc.Terms,RpgTranslationBatch.Translate([source],prompts.Single,prompts.Batch,provider,token,syntax:syntax,terms:doc.Terms,dialogueContexts:contexts));
                providerSlots--;
            }
            Reply(request,prior,pending:true);
        }
    }
    private void PersistFailures()
    {
        if(failureHistoryReadOnly)return;
        try
        {
            var before=new FileInfo(failurePath);
            if(before.Exists!=failureFileExisted||(before.Exists&&(before.Length!=failureFileLength||before.LastWriteTimeUtc!=failureFileStamp)))
                throw new IOException("Character retry evidence changed outside this session.");
            TranslationStorage.WriteJson(failurePath,failures);
            var after=new FileInfo(failurePath);failureFileExisted=after.Exists;failureFileLength=after.Length;failureFileStamp=after.LastWriteTimeUtc;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
        {failureHistoryReadOnly=true;FailureHistoryWarning="角色重试记录无法保存，当前译文已保留。";AppLog.Write("character-runtime","Character retry evidence could not be saved",ex);}
    }
}
