using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class UnrealPreparationCatalog
{
    internal static RpgTextCatalogResult Read(string exe,CancellationToken token)
    {
        var engine=UnrealGameAdapter.Detect(exe)??throw new IOException("未识别到可读取的虚幻引擎版本。");
        UnrealGameAdapter.PrepareCatalog(exe,engine,token);token.ThrowIfCancellationRequested();
        string file=Path.Combine(UnrealGameAdapter.Storage(exe),"catalog.json");
        if(new FileInfo(file).Length>32*1024*1024)throw new InvalidDataException("虚幻文本目录超过限制。");
        using var document=JsonDocument.Parse(File.ReadAllText(file));
        return Parse(document.RootElement,engine.Version,token);
    }
    internal static RpgTextCatalogResult Parse(JsonElement data,string version,CancellationToken token=default)
    {
        if(data.ValueKind!=JsonValueKind.Object||!data.TryGetProperty("protocol",out var protocol)||!protocol.TryGetInt32(out int value)||value!=76
            ||!data.TryGetProperty("entries",out var rows)||rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>100000)
            throw new InvalidDataException("虚幻文本目录格式不匹配。");
        var texts=new HashSet<string>(StringComparer.Ordinal);var origins=new List<CatalogTextOrigin>();var issues=new List<CatalogReadIssue>();
        string String(JsonElement row,string name)=>row.TryGetProperty(name,out var field)&&field.ValueKind==JsonValueKind.String?field.GetString()??"":"";
        int Count(string name)=>data.TryGetProperty(name,out var field)&&field.TryGetInt32(out int count)?Math.Max(0,count):0;
        int invalid=0;
        foreach(var row in rows.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if(row.ValueKind!=JsonValueKind.Object){invalid++;continue;}
            string source=String(row,"source"),ns=String(row,"ns"),key=String(row,"key");
            if(key.Length is 0 or >1024||ns.Length>1024||source.Length is 0 or >6000||source.Contains('\0')||ns.Contains('\0')||key.Contains('\0')){invalid++;continue;}
            texts.Add(source);
            origins.Add(new(source,"Unreal "+version+" / native sources",ns+" / "+key,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))),"unreal-localized-source"));
        }
        if(Count("skipped")>0||invalid>0)issues.Add(new(CatalogIssueKind.Incomplete,"Unreal "+version,"部分资源或文本身份未能读取，未计入可预译内容。"));
        if(data.TryGetProperty("errors",out var errors)&&errors.ValueKind==JsonValueKind.Array&&errors.GetArrayLength()>0)
            issues.Add(new(CatalogIssueKind.Failed,"Unreal "+version,"提取器报告了资源读取错误，原始目录保留。"));
        int deferred=data.TryGetProperty("displaySources",out var display)&&display.ValueKind==JsonValueKind.Array?display.GetArrayLength():0;
        if(deferred>0)issues.Add(new(CatalogIssueKind.Incomplete,"Unreal "+version,"另有显示候选文本需在游戏中确认身份，本次不整批预译。"));
        if(texts.Count==0)issues.Add(new(CatalogIssueKind.Incomplete,"Unreal "+version,"尚未读到带定位身份的原文，不代表游戏没有可翻译文字。"));
        // Only canonical originals are prepared. Localized alternatives and
        // identity-free display candidates never gain runtime write authority.
        var sources=texts.ToArray();bool completed=issues.Count==0;
        return new(sources,Count("scanned"),issues.Select(i=>i.Reason).ToArray()){
            Origins=origins.ToArray(),UnlocalizedSourceTexts=texts,ReadCompleted=completed,Report=new(completed,issues.ToArray())};
    }
}
