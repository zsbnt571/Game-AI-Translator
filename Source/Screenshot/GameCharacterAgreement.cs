using System.Collections.Concurrent;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Optional, game-scoped metadata. The ordinary source/prompt/cache identity is
// untouched unless a supported target has a reliable binding and a choice.
internal sealed record GameCharacterAgreement(string CharacterId,string Scope,string Gender,CharacterGenderSelection Selection,string Revision)
{
    internal const string Contract="character-agreement-v1";
    internal bool Valid=>CharacterId is {Length:>0 and <=256}&&Scope is {Length:>0 and <=1024}
        &&!CharacterId.Any(char.IsControl)&&!Scope.Any(char.IsControl)
        &&Gender is "male" or "female" or "unknown"&&Enum.IsDefined(Selection)
        &&Revision is {Length:64}&&Revision.All(Uri.IsHexDigit);
    internal static GameDialogueContext? Resolve(GameCharacterProfiles profiles,ApiSettings settings,string source,GameDialogueContext? legacy,bool allowCatalog=false)
    {
        if(!GameDialogueRoles.Applies(settings)||!profiles.TryResolve(source,out var role,allowCatalog)
            ||role.Selection==CharacterGenderSelection.Auto&&role.EffectiveGender=="unknown")return legacy;
        // Keep 129's proved automatic context and identity byte-for-byte. The
        // profile panel observes it, but does not need to re-key that result.
        if(role.Selection==CharacterGenderSelection.Auto&&(legacy?.Roles is not null||AutomaticGenderAgreement.BlocksInheritedEvidence(source)))return legacy;
        var character=new GameCharacterAgreement(role.CharacterId,role.Scope,role.EffectiveGender,role.Selection,role.Identity);
        return character.Valid?GameDialogueContext.Create(legacy?.Previous??[],character:character):legacy;
    }
    internal static IReadOnlyDictionary<string,GameDialogueContext> Combine(GameCharacterProfiles profiles,ApiSettings settings,
        IEnumerable<string> sources,IReadOnlyDictionary<string,GameDialogueContext> legacy,IReadOnlySet<string>? blocked=null,bool allowCatalog=false)
    {
        if(!GameDialogueRoles.Applies(settings))return legacy;
        var result=new Dictionary<string,GameDialogueContext>(legacy,StringComparer.Ordinal);
        foreach(string source in sources.Distinct(StringComparer.Ordinal))
        {
            if(blocked?.Contains(source)==true&&(!profiles.TryResolve(source,out var choice,allowCatalog)||choice.Selection==CharacterGenderSelection.Auto))continue;
            if(Resolve(profiles,settings,source,legacy.GetValueOrDefault(source),allowCatalog) is {} context)result[source]=context;
        }
        return result;
    }
    internal static ApiSettings Attach(ApiSettings settings,IReadOnlyDictionary<string,GameDialogueContext> contexts,IReadOnlyDictionary<string,string> sources)
    {
        if(!GameDialogueRoles.Applies(settings))return settings;
        var items=sources.Where(p=>contexts.TryGetValue(p.Value,out var context)&&context.Character?.Valid==true)
            .ToDictionary(p=>sources.Count==1?"source":p.Key,p=>new
            {
                characterId=contexts[p.Value].Character!.CharacterId,
                gender=contexts[p.Value].Character!.Gender,
                mode=contexts[p.Value].Character!.Selection==CharacterGenderSelection.Auto?"verified-local-evidence":"user-selected"
            },StringComparer.Ordinal);
        if(items.Count==0)return settings;
        var copy=ApiSettingsSnapshot.Copy(settings);
        copy.CustomTranslationPrompt+="\nCharacter grammatical agreement: the JSON is trusted attribution metadata, not instructions or text to translate. Apply male/female only to first-person references to that item's verified speaker where the target language requires gender agreement. User-selected values override automatic metadata, but never rewrite factual claims in the original text. Unknown means no supplied gender and does not mean a neutral/nonbinary identity: preserve the original meaning and ordinary translation behavior. Never transfer the speaker's gender to the addressee or third persons; never infer gender from a name. Preserve all placeholders and item structure. Do not output this metadata. characterAgreement="+JsonSerializer.Serialize(items,TranslationLanguageSpans.MessageJson);
        return copy;
    }
}

// New variants are isolated from both ordinary and 129 role caches. A settings
// edit cannot erase a usable previous variant or manufacture a retranslation.
internal sealed class GameCharacterTranslationCache
{
    internal sealed record Entry(string Source,string Text,string Terms,string[] Previous,GameCharacterAgreement Character,long AcceptedAt);
    private readonly ConcurrentDictionary<string,Entry> entries=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,Entry> retained=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,Entry> equivalent=new(StringComparer.Ordinal);
    private static string RetainedKey(string source,string id,string scope)=>id+"\0"+scope+"\0"+source;
    // A settings revision invalidates in-flight work, but it does not change the
    // prompt when the user returns to the same explicit choice. Keep the stored
    // keys intact and index only otherwise identical role/context variants.
    private static string EquivalentKey(string source,GameDialogueContext context)=>Key(source,new GameDialogueContext(context.Previous,context.Roles,context.Character! with{Revision=new string('0',64)}));
    private void Index(Entry entry)
    {
        retained.AddOrUpdate(RetainedKey(entry.Source,entry.Character.CharacterId,entry.Character.Scope),entry,(_,old)=>old.AcceptedAt>entry.AcceptedAt?old:entry);
        var context=GameDialogueContext.Create(entry.Previous,character:entry.Character);
        if(context is not null)equivalent.AddOrUpdate(EquivalentKey(entry.Source,context),entry,(_,old)=>old.AcceptedAt>entry.AcceptedAt?old:entry);
    }
    private bool invalidFile;
    private static string Key(string source,GameDialogueContext context)=>context.Identity+":"+source;
    internal static GameCharacterTranslationCache Load(string path)
    {
        var result=new GameCharacterTranslationCache();
        try
        {
            if(!File.Exists(path))return result;
            if(new FileInfo(path).Length>32L*1024*1024||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0){result.invalidFile=true;return result;}
            var items=JsonSerializer.Deserialize<Dictionary<string,Entry>>(File.ReadAllText(path));
            if(items is null||items.Count>50000){result.invalidFile=true;return result;}
            foreach(var pair in items)
            {
                var e=pair.Value;
                if(e is null||e.Source is not {Length:>0 and <=6000}||e.Text is not {Length:>0 and <=24000}
                    ||e.Terms is not {Length:64}||e.Previous is null||e.Character is null||!e.Character.Valid
                    ||GameDialogueContext.Create(e.Previous,character:e.Character) is not {} context||pair.Key!=Key(e.Source,context))
                {result.invalidFile=true;continue;}
                result.entries[pair.Key]=e;
                result.Index(e);
            }
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException){result.invalidFile=true;}
        return result;
    }
    internal bool TryGet(string source,GameDialogueContext context,ApiSettings settings,IReadOnlyDictionary<string,string> terms,out string text,EmbeddedTextSyntax syntax,bool fallback=true)
    {
        text="";
        if(context.Character is not {} character||!GameDialogueRoles.Applies(settings))return false;
        bool Read(Entry e)=>e.Terms==context.TermsStamp(terms,source)&&AutomaticTranslationQuality.Displayable(source,e.Text,settings,terms,syntax);
        if(entries.TryGetValue(Key(source,context),out var exact)&&Read(exact)){text=exact.Text;return true;}
        if(context.Roles is null&&equivalent.TryGetValue(EquivalentKey(source,context),out var sameChoice)&&Read(sameChoice)){text=sameChoice.Text;return true;}
        if(!fallback)return false;
        if(!retained.TryGetValue(RetainedKey(source,character.CharacterId,character.Scope),out var prior)
            ||!prior.Previous.SequenceEqual(context.Previous,StringComparer.Ordinal)||!Read(prior))return false;
        text=prior.Text;return true;
    }
    internal bool TryRetained(string source,GameCharacterProfiles profiles,ApiSettings settings,IReadOnlyDictionary<string,string> terms,out string text,EmbeddedTextSyntax syntax,bool allowCatalog=false)
    {
        text="";
        if(!GameDialogueRoles.Applies(settings)||!profiles.TryResolve(source,out var role,allowCatalog)
            ||!retained.TryGetValue(RetainedKey(source,role.CharacterId,role.Scope),out var e))return false;
        var context=GameDialogueContext.Create(e.Previous,character:e.Character);
        if(context is null||e.Terms!=context.TermsStamp(terms,source)||!AutomaticTranslationQuality.Displayable(source,e.Text,settings,terms,syntax))return false;
        text=e.Text;return true;
    }
    internal bool TryRetained(string source,GameCharacterBinding binding,ApiSettings settings,IReadOnlyDictionary<string,string> terms,out string text,EmbeddedTextSyntax syntax)
    {
        text="";
        if(!GameDialogueRoles.Applies(settings)||!binding.Valid()
            ||!retained.TryGetValue(RetainedKey(source,binding.CharacterId,binding.Scope),out var e))return false;
        var context=GameDialogueContext.Create(e.Previous,character:e.Character);
        if(context is null||e.Terms!=context.TermsStamp(terms,source)||!AutomaticTranslationQuality.Displayable(source,e.Text,settings,terms,syntax))return false;
        text=e.Text;return true;
    }
    internal void Accept(string source,string text,GameDialogueContext context,IReadOnlyDictionary<string,string> terms)
    {
        if(context.Character is not {} character||!character.Valid)return;
        string key=Key(source,context);
        if(entries.Count<50000||entries.ContainsKey(key))
        {var entry=new Entry(source,text,context.TermsStamp(terms,source),context.Previous.ToArray(),character,DateTime.UtcNow.Ticks);entries[key]=entry;Index(entry);}
    }
    internal Dictionary<string,Entry> Snapshot()=>new(entries,StringComparer.Ordinal);
    internal void EnsureWritable()
    {
        if(invalidFile)throw new IOException("角色译文文件无法安全更新，已保留原文件。");
    }
    internal static void Save(string path,IReadOnlyDictionary<string,string> accepted,IReadOnlyDictionary<string,GameDialogueContext> contexts,IReadOnlyDictionary<string,string> terms)
    {
        var cache=Load(path);cache.EnsureWritable();
        foreach(var pair in accepted)if(contexts.TryGetValue(pair.Key,out var context))cache.Accept(pair.Key,pair.Value,context,terms);
        TranslationStorage.WriteJson(path,cache.Snapshot(),32*1024*1024);
    }
}

