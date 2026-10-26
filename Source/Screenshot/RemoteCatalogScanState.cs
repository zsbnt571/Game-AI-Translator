using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Reaching the end of pagination is different from reading every resource.
// Older bridges omit these fields and retain their existing completion rule.
internal sealed record RemoteCatalogScanState(bool Complete, int IssueCount)
{
    internal static RemoteCatalogScanState Read(JsonElement page, bool finished)
    {
        int errors=0;
        if(page.TryGetProperty("catalogErrorCount",out var count)
            &&(count.ValueKind!=JsonValueKind.Number||!count.TryGetInt32(out errors)||errors<0))
            throw new IOException("游戏文本目录返回的读取状态无效。");
        bool complete=finished;
        if(page.TryGetProperty("catalogComplete",out var value))
        {
            if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new IOException("游戏文本目录返回的读取状态无效。");
            complete=finished&&value.GetBoolean();
        }
        return new(complete&&errors==0,Math.Max(errors,finished&&!complete?1:0));
    }
}
