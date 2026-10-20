using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class ReadableModificationKey
{
    // Display only. The original engine identifier remains the write target.
    internal static string Format(string key)
    {
        if(key.StartsWith("field:",StringComparison.Ordinal))
        {
            try
            {
                using var document=JsonDocument.Parse(Uri.UnescapeDataString(key[6..]));
                if(document.RootElement.ValueKind==JsonValueKind.Array)
                    return string.Join(" › ",document.RootElement.EnumerateArray().Select(x=>x.ValueKind==JsonValueKind.String?x.GetString():x.GetRawText()));
            }
            catch(Exception ex) when(ex is JsonException or UriFormatException) { }
            return UiLanguage.Text("通用数据");
        }
        var parts=key.Split(':');
        string kind=parts[0] switch {"variable"=>"变量","switch"=>"开关","actor"=>"角色","item"=>"物品","weapon"=>"武器","armor"=>"防具","event"=>"地图事件","commonEvent"=>"公共事件","db"=>"可变数据库",_=>""};
        return kind.Length>0?UiLanguage.Text(kind)+" #"+string.Join(" / ",parts.Skip(1)):key;
    }
}
