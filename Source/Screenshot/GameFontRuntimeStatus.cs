using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// A font-choice limitation is independent of missing glyphs or failed translation.
// Partial receipts retain the last observation; an explicit zero clears it.
internal sealed record GameFontRuntimeStatus(int? DeferredComponents=null,string Warning="")
{
    internal GameFontRuntimeStatus Read(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Object)return this;
        int? count=DeferredComponents;
        if(value.TryGetProperty("deferredComponents",out var observed)
            &&observed.ValueKind==JsonValueKind.Number&&observed.TryGetInt32(out int number)&&number>=0)count=number;
        string warning=Warning;
        if(value.TryGetProperty("fontWarning",out var raw)&&raw.ValueKind==JsonValueKind.String)
            warning=raw.GetString()=="Custom font unavailable for this component; default translation font retained."
                ?GameFontRuntimeMessages.Canonical(raw.GetString())??"":"";
        // Never retain unknown runtime text, paths or font names in this status.
        return new(count,count is >0?warning:"");
    }
}
