using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class RemoteSourceCatalog
{
    internal static string[] ActiveSources(JsonElement page, JsonElement rows, int epoch)
    {
        if (!page.TryGetProperty("activeSourceTexts", out var sources)) return [];
        if (!page.TryGetProperty("epoch", out var generation) || !generation.TryGetInt32(out int actual) || actual != epoch)
            throw new IOException("游戏文本目录已切换，正在重新读取。");
        if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > rows.GetArrayLength())
            throw new IOException("游戏文本目录的来源标记不完整。");
        var allowed = rows.EnumerateArray().Select(row => row.GetString()!).ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.String || source.GetString() is not { } text || !allowed.Contains(text) || !result.Add(text))
                throw new IOException("游戏文本目录的来源标记与本页不一致。");
        }
        return result.ToArray();
    }
}
