using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal enum CharacterGenderSelection { Auto, Male, Female, Unspecified }
// Native identity and unique source provenance are independent from the legacy
// three-line context. A name is a label, never an identity or gender guess.
internal sealed record GameCharacterBinding(string CharacterId,string DisplayName,string Scope,string AutomaticGender="unknown",bool CatalogOnly=false)
{
    internal static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static bool ValidId(string? value)=>value is {Length:>0 and <=160}&&Regex.IsMatch(value,@"^(?:(?:renpy|mv|mz|rgss|wolf|tyrano|naninovel|unityds|vnmaker):[A-Za-z_0-9][A-Za-z_0-9.:/-]{0,140}|(?:dialogic|dialogic1|chatterbox):[A-Fa-f0-9]{64}|matcha:(?:[A-Fa-f0-9]{2}){1,64}|yukar:cast:[A-Fa-f0-9]{32})$",RegexOptions.CultureInvariant);
    internal bool Valid()=>ValidId(CharacterId)&&DisplayName is {Length:<=200}&&!DisplayName.Any(char.IsControl)
        &&Scope is {Length:64}&&Scope.All(Uri.IsHexDigit)&&AutomaticGender is "unknown" or "male" or "female";
    // Only complete affirmative original self-statements qualify. Name, voice,
    // pronouns, appearance and an arbitrary game variable do not infer gender.
    internal static string DetectGender(IEnumerable<string> statements)
    {
        var values=new HashSet<string>(StringComparer.Ordinal);
        foreach(string statement in statements.Take(4))
        {
            if(statement.Length>600)continue;
            var match=Regex.Match(statement,@"^\s*I(?: am|['’]m)\s+(?:(?:a|an)\s+)?(?:trans(?:gender)?\s+)?(man|woman|male|female|boy|girl)\s*[.!]?\s*$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            if(match.Success)values.Add(match.Groups[1].Value.ToLowerInvariant() is "woman" or "female" or "girl"?"female":"male");
        }
        return values.Count==1?values.Single():"unknown";
    }
}
internal sealed record GameCharacterResolution(string CharacterId,string DisplayName,string Scope,CharacterGenderSelection Selection,string AutomaticGender,string EffectiveGender,long Revision,string Identity);
internal sealed record GameCharacterProfileView(string CharacterId,string DisplayName,CharacterGenderSelection Selection,string AutomaticGender,string EffectiveGender,bool Conflict,long Revision,bool CatalogOnly=false,bool RosterOnly=false,string[]? DisplayNames=null);

// An independent game-scoped preference file. No ordinary translation cache,
// target language, API settings or save file is modified here. Automatic facts
// remain transient and local to proved source bindings; persisted preferences
// never promote a past scene's inference to a whole-game permanent gender.
internal sealed class GameCharacterProfiles
{
    internal const string Contract="fusion-character-bindings-1";
    private sealed record SavedProfile(string DisplayName,CharacterGenderSelection Selection,long Revision);
    private sealed record Document(int Version,Dictionary<string,SavedProfile> Profiles);
    private static readonly ConcurrentDictionary<string,GameCharacterProfiles> stores=new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate=new();
    private readonly string path;
    private Dictionary<string,SavedProfile> preferences=new(StringComparer.Ordinal);
    private readonly Dictionary<string,GameCharacterBinding> catalogBindings=new(StringComparer.Ordinal),receiptBindings=new(StringComparer.Ordinal);
    // Explicit active native bindings are not source-only aliases. Equal text
    // from two characters remains two independent runtime observations.
    private readonly Dictionary<string,GameCharacterBinding> runtimeBindings=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> catalogRoster=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string[]> rosterAliases=new(StringComparer.Ordinal);
    private readonly HashSet<string> ambiguousReceipts=new(StringComparer.Ordinal);
    private DateTime fileStamp=DateTime.MinValue;
    private DateTime checkAfter=DateTime.MinValue;
    private long fileLength=-1,version;
    private bool loaded,readable=true;
    private string lastError="",receiptEpoch="";
    internal string FilePath=>path;
    internal string LastError {get{lock(gate)return lastError;}}
    internal long Revision {get{lock(gate){Load();return version;}}}
    internal event Action? Changed;
    private GameCharacterProfiles(string path){this.path=path;}
    internal static string PathFor(string exe)=>Path.Combine(TranslationRoute.DataRoot(exe),"character-profiles","profiles-v1.json");
    internal static GameCharacterProfiles For(string exe)=>Open(PathFor(exe));
    internal static GameCharacterProfiles Open(string path)
    {string full=Path.GetFullPath(path);return stores.GetOrAdd(full,p=>new(p));}
    private void Notify()
    {
        var handlers=Changed;if(handlers is null)return;
        foreach(Action handler in handlers.GetInvocationList())try{handler();}catch{}
    }
    private void Load(bool force=false)
    {
        if(loaded&&!force&&DateTime.UtcNow<checkAfter)return;
        checkAfter=DateTime.UtcNow.AddMilliseconds(250);
        try
        {
            var info=new FileInfo(path);DateTime stamp=info.Exists?info.LastWriteTimeUtc:DateTime.MinValue;long length=info.Exists?info.Length:-1;
            if(loaded&&stamp==fileStamp&&length==fileLength)return;
            loaded=true;fileStamp=stamp;fileLength=length;
            if(!info.Exists){preferences=new(StringComparer.Ordinal);readable=true;lastError="";version++;return;}
            if(length>2*1024*1024)throw new InvalidDataException();
            var document=JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
            if(document is not {Version:1}||document.Profiles is null||document.Profiles.Count>5000
                ||document.Profiles.Any(p=>!GameCharacterBinding.ValidId(p.Key)||p.Value is null||!Enum.IsDefined(p.Value.Selection)||p.Value.Revision<0
                    ||p.Value.DisplayName is not {Length:<=200}||p.Value.DisplayName.Any(char.IsControl)))throw new InvalidDataException();
            preferences=new(document.Profiles,StringComparer.Ordinal);readable=true;lastError="";version++;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {readable=false;lastError="unreadable";preferences=new(StringComparer.Ordinal);version++;}
    }
    internal GameCharacterProfileView[] Snapshot(bool force=false)
    {
        lock(gate)
        {
            Load(force);var observed=catalogBindings.Values.Concat(receiptBindings.Values).Concat(runtimeBindings.Values).GroupBy(b=>b.CharacterId,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
            return observed.Keys.Concat(catalogRoster.Keys).Concat(preferences.Keys).Distinct(StringComparer.Ordinal).OrderBy(id=>id,StringComparer.Ordinal).Take(5000).Select(id=>
            {
                observed.TryGetValue(id,out var rows);preferences.TryGetValue(id,out var saved);
                string[] genders=(rows??[]).Select(b=>b.AutomaticGender).Where(g=>g!="unknown").Distinct(StringComparer.Ordinal).ToArray();
                string automatic=genders.Length==1?genders[0]:"unknown";
                string name=(rows??[]).Select(b=>b.DisplayName).FirstOrDefault(n=>n.Length>0)??catalogRoster.GetValueOrDefault(id)??saved?.DisplayName??id;
                var selection=saved?.Selection??CharacterGenderSelection.Auto;
                return new GameCharacterProfileView(id,name,selection,automatic,Effective(selection,automatic),genders.Length>1,saved?.Revision??0,
                    rows is {Length:>0}?rows.All(b=>b.CatalogOnly):catalogRoster.ContainsKey(id),rows is not {Length:>0},
                    (rows??[]).Select(b=>b.DisplayName).Concat(rosterAliases.GetValueOrDefault(id)??[]).Append(name).Where(n=>n.Length>0).Distinct(StringComparer.Ordinal).Take(16).ToArray());
            }).ToArray();
        }
    }
    private static string Effective(CharacterGenderSelection selection,string automatic)=>selection switch
    {CharacterGenderSelection.Male=>"male",CharacterGenderSelection.Female=>"female",CharacterGenderSelection.Unspecified=>"unknown",_=>automatic};
    internal bool SetSelection(string id,CharacterGenderSelection selection)
    {
        bool changed=false;
        lock(gate)
        {
            Load(true);if(!readable)return false;
            if(!GameCharacterBinding.ValidId(id)||!Enum.IsDefined(selection)){lastError="invalid-character";return false;}
            var known=catalogBindings.Values.Concat(receiptBindings.Values).Concat(runtimeBindings.Values).FirstOrDefault(b=>b.CharacterId==id);
            if(known is null&&!catalogRoster.ContainsKey(id)&&!preferences.ContainsKey(id)){lastError="invalid-character";return false;}
            if((preferences.GetValueOrDefault(id)?.Selection??CharacterGenderSelection.Auto)==selection){lastError="";return true;}
            string mutexName="Local\\Fusion.CharacterProfiles."+GameCharacterBinding.Hash(path.ToUpperInvariant());
            using var mutex=new Mutex(false,mutexName);bool held=false;
            try
            {
                try{held=mutex.WaitOne(1000);}catch(AbandonedMutexException){held=true;}
                if(!held)throw new IOException();
                loaded=false;Load(true);if(!readable)return false;
                var previous=preferences.GetValueOrDefault(id);
                var next=new Dictionary<string,SavedProfile>(preferences,StringComparer.Ordinal);
                if(next.Count>=5000&&!next.ContainsKey(id))throw new IOException();
                next[id]=new(known?.DisplayName??catalogRoster.GetValueOrDefault(id)??previous?.DisplayName??id,selection,checked((previous?.Revision??0)+1));
                TranslationStorage.WriteJson(path,new Document(1,next));
                preferences=next;var info=new FileInfo(path);fileStamp=info.LastWriteTimeUtc;fileLength=info.Length;loaded=true;lastError="";version++;changed=true;
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
            {lastError="save-failed";return false;}
            finally{if(held)mutex.ReleaseMutex();}
        }
        if(changed)Notify();return true;
    }
    internal bool TryResolve(string source,out GameCharacterResolution resolution,bool allowCatalog=false)
    {
        lock(gate)
        {
            Load();resolution=null!;
            if(ambiguousReceipts.Contains(source))return false;
            catalogBindings.TryGetValue(source,out var catalog);receiptBindings.TryGetValue(source,out var runtime);
            if(catalog is not null&&runtime is not null&&catalog.CharacterId!=runtime.CharacterId)return false;
            var binding=runtime??catalog;if(binding is null)return false;
            // A unique static string cannot prove who is speaking at runtime:
            // another actor may assemble the same wording dynamically. Only
            // explicit catalogue callers may use newly collected static IDs.
            // Runtime receipts remain restricted to the independently checked
            // native Ren'Py protocol below; a source echo is never an upgrade.
            if(binding.CatalogOnly&&!allowCatalog)return false;
            preferences.TryGetValue(binding.CharacterId,out var saved);var selection=saved?.Selection??CharacterGenderSelection.Auto;
            string effective=Effective(selection,binding.AutomaticGender);long revision=saved?.Revision??0;
            string identity=GameCharacterBinding.Hash(JsonSerializer.Serialize(new{binding.CharacterId,binding.Scope,selection,effective,revision}));
            resolution=new(binding.CharacterId,binding.DisplayName,binding.Scope,selection,binding.AutomaticGender,effective,revision,identity);return true;
        }
    }
    internal void ObserveRuntime(IReadOnlyDictionary<string,GameCharacterBinding> bindings)
    {
        bool changed;
        lock(gate)
        {
            var next=bindings.Take(32).Where(p=>p.Key.Length<=80&&p.Value.Valid()).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
            changed=next.Count!=runtimeBindings.Count||next.Any(p=>runtimeBindings.GetValueOrDefault(p.Key)!=p.Value);
            if(changed){runtimeBindings.Clear();foreach(var pair in next)runtimeBindings.Add(pair.Key,pair.Value);version++;}
        }
        if(changed)Notify();
    }
    // Some frameworks expose the native actor number but not the serialized
    // database provenance used by the resource reader. Resolve only a single
    // matching catalogue identity; display names and saved choices cannot
    // create this association. An ambiguous or not-yet-read catalogue falls
    // back to ordinary translation until the native relationship is proved.
    internal bool TryMapRuntimeIdentity(JsonElement row,string wireId,out string characterId)
    {
        characterId=wireId;
        if(!wireId.StartsWith("unityds:actor:",StringComparison.Ordinal))return true;
        if(!row.TryGetProperty("framework",out var framework)||framework.ValueKind!=JsonValueKind.String||framework.GetString()!="unityds"
            ||!row.TryGetProperty("nativeActorId",out var actor)||actor.ValueKind!=JsonValueKind.Number||!actor.TryGetInt32(out int id)||id<=0
            ||wireId!="unityds:actor:"+id.ToString(System.Globalization.CultureInfo.InvariantCulture))return false;
        string suffix=":actor:"+id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        lock(gate)
        {
            var matches=catalogBindings.Values.Select(v=>v.CharacterId).Concat(catalogRoster.Keys)
                .Where(value=>Regex.IsMatch(value,@"^unityds:[A-Fa-f0-9]{64}:actor:[0-9]+$",RegexOptions.CultureInvariant)
                    &&value.EndsWith(suffix,StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if(matches.Length!=1)return false;
            characterId=matches[0];return true;
        }
    }
    internal bool TryResolveRuntime(string requestId,GameCharacterBinding binding,out GameCharacterResolution resolution)
    {
        lock(gate)
        {
            Load();resolution=null!;
            if(!binding.Valid()||runtimeBindings.GetValueOrDefault(requestId)!=binding)return false;
            preferences.TryGetValue(binding.CharacterId,out var saved);var selection=saved?.Selection??CharacterGenderSelection.Auto;
            string effective=Effective(selection,binding.AutomaticGender);long revision=saved?.Revision??0;
            string identity=GameCharacterBinding.Hash(JsonSerializer.Serialize(new{binding.CharacterId,binding.Scope,selection,effective,revision}));
            resolution=new(binding.CharacterId,binding.DisplayName,binding.Scope,selection,binding.AutomaticGender,effective,revision,identity);return true;
        }
    }
    internal string[] ObserveCatalog(RpgTextCatalogResult catalog)
    {
        string[] changed;
        lock(gate)
        {
            var allowed=catalog.Texts.ToHashSet(StringComparer.Ordinal);
            var next=catalog.CharacterBindings.Take(50000).Where(p=>allowed.Contains(p.Key)&&p.Key is {Length:>0 and <=6000}&&p.Value is not null&&p.Value.Valid()).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
            var aliases=CharacterCatalogRoster.Read(catalog);
            var roster=aliases.ToDictionary(p=>p.Key,p=>p.Value[0],StringComparer.Ordinal);
            changed=catalogBindings.Keys.Concat(next.Keys).Distinct(StringComparer.Ordinal).Where(s=>catalogBindings.GetValueOrDefault(s)!=next.GetValueOrDefault(s)).ToArray();
            bool rosterChanged=catalogRoster.Count!=roster.Count||catalogRoster.Any(p=>roster.GetValueOrDefault(p.Key)!=p.Value)
                ||aliases.Any(p=>!p.Value.SequenceEqual(rosterAliases.GetValueOrDefault(p.Key)??[]));
            if(changed.Length==0&&!rosterChanged)return [];
            catalogBindings.Clear();foreach(var pair in next)catalogBindings.Add(pair.Key,pair.Value);version++;
            catalogRoster.Clear();foreach(var pair in roster)catalogRoster.Add(pair.Key,pair.Value);
            rosterAliases.Clear();foreach(var pair in aliases)rosterAliases.Add(pair.Key,pair.Value);
        }
        Notify();return changed;
    }
    // Call on a new bridge session or an actual game reload. Manual choices
    // persist, but transient attribution cannot survive a different AST epoch.
    internal string[] BeginSession()
    {
        string[] changed;
        lock(gate){changed=receiptBindings.Keys.Concat(ambiguousReceipts).Distinct(StringComparer.Ordinal).ToArray();receiptBindings.Clear();runtimeBindings.Clear();ambiguousReceipts.Clear();receiptEpoch="";version++;}
        Notify();return changed;
    }
    internal string[] ReadReceipt(JsonElement receipt)
    {
        if(receipt.ValueKind!=JsonValueKind.Object||!receipt.TryGetProperty("characterBindingContract",out var contract)||contract.ValueKind!=JsonValueKind.String||contract.GetString()!=Contract
            ||!receipt.TryGetProperty("characterBindings",out var rows)||rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>256)return [];
        var allowed=new HashSet<string>(StringComparer.Ordinal);
        foreach(string field in new[]{"texts","ahead"})if(receipt.TryGetProperty(field,out var texts)&&texts.ValueKind==JsonValueKind.Array)
            foreach(var value in texts.EnumerateArray().Take(2048))if(value.ValueKind==JsonValueKind.String)allowed.Add(value.GetString()!);
        var changed=new HashSet<string>(StringComparer.Ordinal);
        lock(gate)
        {
            if(receipt.TryGetProperty("characterBindingEpoch",out var epoch)&&epoch.ValueKind==JsonValueKind.String&&epoch.GetString() is {Length:>0 and <=128} generation)
            {
                if(receiptEpoch.Length>0&&receiptEpoch!=generation){changed.UnionWith(receiptBindings.Keys);receiptBindings.Clear();ambiguousReceipts.Clear();}
                receiptEpoch=generation;
            }
            foreach(var row in rows.EnumerateArray())
            {
                if(row.ValueKind!=JsonValueKind.Object||!String(row,"source",6000,out string source)||!allowed.Contains(source)
                    ||!row.TryGetProperty("unique",out var unique)||unique.ValueKind!=JsonValueKind.True
                    ||!String(row,"characterId",160,out string id)||!id.StartsWith("renpy:",StringComparison.Ordinal)||!String(row,"scope",64,out string scope))continue;
                String(row,"displayName",200,out string name);
                var evidence=new List<string>();
                if(row.TryGetProperty("evidence",out var facts)&&facts.ValueKind==JsonValueKind.Array&&facts.GetArrayLength()<=3)
                    foreach(var fact in facts.EnumerateArray())if(fact.ValueKind==JsonValueKind.String&&fact.GetString() is {Length:<=600} text)evidence.Add(text);
                if(AutomaticGenderAgreement.BlocksInheritedEvidence(source))evidence.Clear();evidence.Add(source);
                var binding=new GameCharacterBinding(id,name.Length>0?name:id,scope,GameCharacterBinding.DetectGender(evidence));if(!binding.Valid())continue;
                if(receiptBindings.TryGetValue(source,out var before)&&before.CharacterId!=binding.CharacterId){ambiguousReceipts.Add(source);changed.Add(source);continue;}
                if(ambiguousReceipts.Contains(source)||before==binding)continue;
                if(receiptBindings.Count>=50000&&!receiptBindings.ContainsKey(source))continue;
                receiptBindings[source]=binding;changed.Add(source);
            }
            if(changed.Count>0)version++;
        }
        if(changed.Count>0)Notify();return changed.ToArray();
    }
    private static bool String(JsonElement value,string name,int max,out string text)
    {text="";if(!value.TryGetProperty(name,out var field)||field.ValueKind!=JsonValueKind.String||field.GetString() is not {} data||data.Length>max||data.Any(char.IsControl))return false;text=data;return data.Length>0;}
}
