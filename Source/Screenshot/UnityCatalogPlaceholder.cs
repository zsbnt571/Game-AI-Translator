using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace ScreenshotTranslationUiTester;

// Exact stock paragraphs in default Naninovel UI/printer prefabs. These hashes
// are not language detection or a general "contains lorem" rule. Keep quoted,
// edited, mixed-language and game-authored text on the normal source path.
internal static class UnityCatalogPlaceholder
{
    private static readonly HashSet<string> Samples=new(StringComparer.Ordinal)
    {
        "BF15E2A457DB1E0115CE78EDEA2FD3F3A080EE1261E7A3646467C2381BFBC59F",
        "B2A414CCA6F63A41339A658719DC3F5B04712C5B0C66DFE3569C868AF2C1FC21",
        "5CE4919C2979895871611F79D31D571E62332795487912001AC18E1A7ECA4A97",
        "D6F152C9371562C607DBF3034B0FCF20E0502224C19FFE2FA33C58D5AAED62E8",
        "45A8C490B77306AE1E6029B0D8A28BD0936B6E25D0CDAE36E0601A9FE0F6F591",
        "BCD9950D4B47D9333CA35A2EFF3D75704EFF717210424EEB0BD71EF8B48FC28D",
        "C143E7C5926E16A446D8345160920D1F481148D9C0847BDAF8EA4A77E2FAA9A6",
        "0C4EEEE2A882B23392B3EF479ED2C8AC17BEADC9AF3B2E6135BC9E04ED10555C"
    };
    private static readonly HashSet<string> Components=new(StringComparer.Ordinal)
    {"UnityEngine.UI|UnityEngine.UI|Text","Unity.TextMeshPro|TMPro|TextMeshProUGUI","Unity.TextMeshPro|TMPro|TextMeshPro",
     "Elringus.Naninovel.Runtime|Naninovel.UI|RevealableUIText","Elringus.Naninovel.Runtime|Naninovel.UI|RevealableTMProText",
     "Elringus.Naninovel.Runtime|Naninovel.UI|NaninovelTMProText"};
    internal static string Component(AssetsManager manager,AssetsFileInstance file,AssetTypeValueField basic)
    {
        if(basic.IsDummy||!basic.Children.Any(c=>c.FieldName=="m_Script"))return "";
        try
        {
            var script=manager.GetExtAsset(file,basic["m_Script"]).baseField;
            if(script.IsDummy)return "";
            return script["m_AssemblyName"].AsString.Replace(".dll","",StringComparison.Ordinal)+"|"+script["m_Namespace"].AsString+"|"+script["m_ClassName"].AsString;
        }
        catch(Exception ex)when(ex is not OperationCanceledException and not OutOfMemoryException){return "";}
    }
    internal static bool IsStock(string value)
        =>value.Length>=100&&value.Length<=6000&&Samples.Contains(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Regex.Replace(value.Trim(),@"\s+"," ")))));
    internal static bool TemplateOrigin(CatalogTextOrigin origin)
        =>origin.Kind=="unity-resource"&&origin.ContentHash.Length==64&&Components.Contains(origin.Context)&&
            Regex.IsMatch(origin.File.Replace('\\','/'),@"(?:^|/)naninovel_assets_naninovel/(?:ui|textprinters)/[a-z0-9_]+\.bundle$",RegexOptions.IgnoreCase)&&
            Regex.IsMatch(origin.Location,@"^pathid:-?\d+/m_[Tt]ext\[\d+\]$");
    internal static HashSet<string> Excluded(RpgTextCatalogResult catalog)
    {
        // A missing later occurrence might be genuine game dialogue. A bounded
        // source index cannot establish that every occurrence is a stock prefab.
        if(catalog.OriginsTruncated)return new(StringComparer.Ordinal);
        var byText=catalog.Origins.GroupBy(o=>o.Text,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
        var scriptTexts=catalog.Sources.Select(s=>s.Text).ToHashSet(StringComparer.Ordinal);
        return catalog.Texts.Where(text=>IsStock(text)&&!scriptTexts.Contains(text)&&byText.TryGetValue(text,out var origins)&&origins.Length>0&&origins.All(TemplateOrigin))
            .ToHashSet(StringComparer.Ordinal);
    }
}
