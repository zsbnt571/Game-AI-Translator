using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class RemoteSourceCatalog
{
    internal sealed record ResourceSource(string Text,string Locale);
    internal static string[] ActiveSources(JsonElement page, JsonElement rows, int epoch)
        => Sources(page,rows,epoch,"activeSourceTexts");

    // This opt-in protocol is separate from historical origin metadata. It proves
    // a concrete locale resource, not that this resource is currently displayed.
    // The caller must additionally require the user's explicit single selection.
    internal static ResourceSource[] SelectedResources(JsonElement page,JsonElement rows,int epoch)
    {
        var selected=Sources(page,rows,epoch,"selectedResourceTexts");
        if(selected.Length==0)return [];
        if(!page.TryGetProperty("metadata",out var metadata)||metadata.ValueKind!=JsonValueKind.Array||
            metadata.GetArrayLength()!=rows.GetArrayLength()||metadata.GetArrayLength()>2048)
            throw new IOException("游戏语言资源的来源标记不完整。");
        var proofs=new Dictionary<string,string>(StringComparer.Ordinal);
        int index=0;
        foreach(var row in metadata.EnumerateArray())
        {
            var text=rows[index++];
            if(row.ValueKind!=JsonValueKind.Object||text.ValueKind!=JsonValueKind.String||
                !row.TryGetProperty("source",out var source)||source.ValueKind!=JsonValueKind.String||source.GetString()!=text.GetString()||
                !row.TryGetProperty("kind",out var kind)||kind.ValueKind!=JsonValueKind.String||
                kind.GetString() is not ("translation_text" or "gettext" or "optimized_value" or "translation" or "translation_plural")||
                !row.TryGetProperty("resourceLocale",out var locale)||locale.ValueKind!=JsonValueKind.String||locale.GetString()!.Length>64||
                !row.TryGetProperty("path",out var path)||path.ValueKind!=JsonValueKind.String||!ResourcePath(path.GetString()!))continue;
            string actual=GameSourceLanguageTracker.CanonicalLocale(locale.GetString());
            // Text inference and unknown tags cannot substitute for a resource locale.
            if(actual.Length==0||actual is "und" or "han" or "latin" or "und-declared")continue;
            proofs.TryAdd(text.GetString()!,actual);
        }
        if(selected.Any(text=>!proofs.ContainsKey(text)))throw new IOException("游戏语言资源的来源标记与本页不一致。");
        return selected.Select(text=>new ResourceSource(text,proofs[text])).ToArray();
    }
    private static bool ResourcePath(string path)
        =>path.StartsWith("res://",StringComparison.Ordinal)&&path.Length>6&&path.Length<=2048&&!path.Any(char.IsControl)&&
            !path.Contains('\\')&&path.IndexOf(':',6)<0&&path[6..].Split('/').All(part=>part.Length>0&&part is not ("." or ".."));

    private static string[] Sources(JsonElement page,JsonElement rows,int epoch,string name)
    {
        if(!page.TryGetProperty(name,out var sources))return [];
        if(!page.TryGetProperty("epoch",out var generation)||generation.ValueKind!=JsonValueKind.Number||!generation.TryGetInt32(out int actual)||actual!=epoch)
            throw new IOException("游戏文本目录已切换，正在重新读取。");
        if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>2048||sources.ValueKind!=JsonValueKind.Array||sources.GetArrayLength()>rows.GetArrayLength())
            throw new IOException("游戏文本目录的来源标记不完整。");
        if(rows.EnumerateArray().Any(row=>row.ValueKind!=JsonValueKind.String))throw new IOException("游戏文本目录的来源标记不完整。");
        var allowed=rows.EnumerateArray().Select(row=>row.GetString()!).ToHashSet(StringComparer.Ordinal);
        var result=new HashSet<string>(StringComparer.Ordinal);
        foreach(var source in sources.EnumerateArray())
        {
            if(source.ValueKind!=JsonValueKind.String||source.GetString() is not {} text||!allowed.Contains(text)||!result.Add(text))
                throw new IOException("游戏文本目录的来源标记与本页不一致。");
        }
        return result.ToArray();
    }
}
