using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace ScreenshotTranslationUiTester;

// Share human edits only for verified content identity and target language.
// Existing local workspaces win. Deleted local edits are never re-imported.
internal static class HumanTranslationMemory
{
    private sealed record Human(int Version,Dictionary<string,string> Manual,Dictionary<string,string> Terms)
    {
        public Dictionary<string,TranslationGlossary.TermRule> TermRules {get;init;}=new(StringComparer.Ordinal);
    }
    private static string? PathFor(string exe,string language)
    {
        string? id=TranslationMemory.Identity(exe);if(id is null)return null;
        string lang=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FusionConfiguration.NormalizeLanguage(language))));
        string folder=AppDataPaths.ValidateExplicitRoot(Path.Combine(TranslationMemory.Root,id,"human"));
        string path=Path.Combine(folder,lang+".json");
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("人工译文存储包含链接。");
        return path;
    }
    private static Human? Read(string path)
    {
        if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>32*1024*1024)throw new IOException("人工译文存储超过大小限制。");
        var result=JsonSerializer.Deserialize<Human>(File.ReadAllText(path));
        if(result is null||result.Version!=1||result.Manual is null||result.Terms is null||result.Manual.Count>100000||result.Terms.Count>2000
            ||result.Manual.Any(p=>!TranslationWorkspaceStore.Basic(p.Key,p.Value))||result.Terms.Any(p=>!TranslationWorkspaceStore.TermValid(p.Key,p.Value))
            ||result.TermRules is null||result.TermRules.Count>2000||result.TermRules.Any(p=>!result.Terms.ContainsKey(p.Key)||p.Value is null||
                p.Value.Scope is not ("entity" or "label" or "literal")||p.Value.Origin is not ("automatic" or "manual" or "imported" or "legacy")))throw new IOException("人工译文存储无效，未覆盖。");
        return result;
    }
    internal static TranslationWorkspaceStore.Document? Reuse(string exe,string language)
    {
        try
        {
            var path=PathFor(exe,language);var found=path is null?null:Read(path);
            return found is null?null:new(){Manual=new(found.Manual,StringComparer.Ordinal),Terms=new(found.Terms,StringComparer.Ordinal),TermRules=new(found.TermRules,StringComparer.Ordinal)};
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {AppLog.Write("translation-memory","Human edit reuse unavailable; local workspace retained.",ex);return null;}
    }
    internal static void Publish(string exe,string language,TranslationWorkspaceStore.Document before,TranslationWorkspaceStore.Document after,bool seedOnly=false,bool automaticTermCleanup=false)
    {
        try
        {
            string? path=PathFor(exe,language);if(path is null)return;
            string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            using var mutex=new Mutex(false,"Local\\FusionR1.HumanMemory."+key);bool held=false;
            try
            {
                try{held=mutex.WaitOne(1000);}catch(AbandonedMutexException){held=true;}if(!held)return;
                var previous=Read(path);if(seedOnly&&previous is not null)return;var merged=previous??new Human(1,new(StringComparer.Ordinal),new(StringComparer.Ordinal));
                void Patch(Dictionary<string,string> old,Dictionary<string,string> now,Dictionary<string,string> destination)
                {
                    foreach(string source in old.Keys.Concat(now.Keys).Distinct(StringComparer.Ordinal))
                    {
                        if(previous is not null&&old.GetValueOrDefault(source)==now.GetValueOrDefault(source))continue;
                        if(now.TryGetValue(source,out string? text))destination[source]=text;else destination.Remove(source);
                    }
                }
                Patch(before.Manual,after.Manual,merged.Manual);
                if(automaticTermCleanup&&previous is not null)
                {
                    // A different installation can already have a newer human
                    // choice. Cleanup of a stale automatic local copy must not
                    // delete that shared choice or a newer automatic value.
                    foreach(var entry in before.Terms)
                        if(!after.Terms.ContainsKey(entry.Key)&&before.TermRules.GetValueOrDefault(entry.Key)?.Origin=="automatic"&&
                            merged.Terms.GetValueOrDefault(entry.Key)==entry.Value&&merged.TermRules.GetValueOrDefault(entry.Key)?.Origin=="automatic")
                        {merged.Terms.Remove(entry.Key);merged.TermRules.Remove(entry.Key);}
                }
                else
                {
                    Patch(before.Terms,after.Terms,merged.Terms);
                    foreach(string source in before.Terms.Keys.Concat(after.Terms.Keys).Distinct(StringComparer.Ordinal))
                    {
                        if(previous is not null&&before.Terms.GetValueOrDefault(source)==after.Terms.GetValueOrDefault(source)&&
                            before.TermRules.GetValueOrDefault(source)==after.TermRules.GetValueOrDefault(source))continue;
                        if(after.Terms.ContainsKey(source)&&after.TermRules.TryGetValue(source,out var rule))merged.TermRules[source]=rule;
                        else merged.TermRules.Remove(source);
                    }
                }
                if(merged.Manual.Count>100000||merged.Terms.Count>2000||JsonSerializer.SerializeToUtf8Bytes(merged).Length>30*1024*1024)throw new IOException("人工译文共用副本超过限制，当前游戏版本仍已保存。");
                TranslationStorage.WriteJson(path,merged);
            }
            finally{if(held)mutex.ReleaseMutex();}
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {AppLog.Write("translation-memory","Shared human edits unavailable; local workspace retained.",ex);}
    }
}
