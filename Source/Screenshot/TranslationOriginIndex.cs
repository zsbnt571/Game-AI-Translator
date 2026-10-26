using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Private, per-game/per-profile evidence. This is never imported into the
// source->translation cache and cannot grant a bridge new writeback authority.
internal static class TranslationOriginIndex
{
    internal sealed record Entry(string Id,string SourceHash,string Source,string File,string Location,string FileHash,
        string Kind,string Encoding,string Container,string Language,string SchemaFile="",string SchemaHash="",string Context="");
    internal sealed record Document(int Version,string Profile,string TargetLanguage,DateTime ScannedAtUtc,
        bool ScanCompleted,bool OriginsTruncated,int UniqueTexts,int LocatedOccurrences,Entry[] Entries,
        bool ReadCompleted=false,int UnlocatedTexts=0,CatalogReadIssue[]? ReadIssues=null);
    internal static string PathFor(string exe,ApiSettings settings)=>Path.Combine(TranslationRoute.DataRoot(exe),"source-origins",
        RpgTranslationCache.Id(settings,RpgTranslationCache.Endpoint(settings.ApiUrl))+".json");
    internal static Document Create(RpgTextCatalogResult catalog,ApiSettings settings)
    {
        var allowed=catalog.Texts.ToHashSet(StringComparer.Ordinal);var origins=new CatalogOrigins();
        foreach(var value in catalog.Origins)if(allowed.Contains(value.Text))origins.Add(value);
        foreach(var value in catalog.Sources)
            if(allowed.Contains(value.Text))origins.Add(new(value.Text,value.File,$"method:{value.MethodToken:x8}/il:{value.Offset:x4}",value.ContentHash,value.Kind,Container:value.ModuleId));
        var known=origins.Items.Select(o=>o.Text).ToHashSet(StringComparer.Ordinal);
        foreach(string text in catalog.Texts)if(!known.Contains(text))origins.Add(new(text,"","","","catalog-position-unknown"));
        var values=origins.Items;
        // Completing the bounded text read and knowing resource positions are
        // independent facts. Neither claims complete coverage of a game, and a
        // location without verified resource/schema hashes is not write access.
        var located=values.Where(v=>v.Location.Length>0).Select(v=>v.Text).ToHashSet(StringComparer.Ordinal);
        return new(1,RpgTranslationCache.Id(settings,RpgTranslationCache.Endpoint(settings.ApiUrl)),
            FusionConfiguration.NormalizeLanguage(settings.TargetLanguage),DateTime.UtcNow,
            catalog.ReadCompleted&&catalog.Report?.Completed!=false&&catalog.Unreadable.Length==0,
            catalog.OriginsTruncated||origins.Truncated,catalog.Texts.Distinct(StringComparer.Ordinal).Count(),values.Count(v=>v.Location.Length>0),
            values.Select(v=>new Entry(CatalogOrigins.Identity(v),CatalogOrigins.TextHash(v.Text),v.Text,v.File,v.Location,v.ContentHash,v.Kind,v.Encoding,v.Container,v.Language,v.SchemaFile,v.SchemaHash,v.Context)).ToArray(),
            catalog.ReadCompleted,allowed.Count(text=>!located.Contains(text)),catalog.Report?.Issues??[]);
    }
    internal static void Save(string exe,ApiSettings settings,RpgTextCatalogResult catalog)
        =>TranslationStorage.WriteJson(PathFor(exe,settings),Create(catalog,settings));
    internal static void TrySave(string exe,ApiSettings settings,RpgTextCatalogResult catalog)
    {
        try{Save(exe,settings,catalog);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {AppLog.Write("translation-origins","来源索引未能保存；当前提取和翻译继续",ex);}
    }
}
