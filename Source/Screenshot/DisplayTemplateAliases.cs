namespace ScreenshotTranslationUiTester;

// The host and Unity renderer use the same bounded, whole-line matcher. Only
// validated native/manual entries may lend a translation; OCR/display-only
// caches never acquire writeback authority through this index.
internal sealed class DisplayTemplateAliases
{
    private readonly object gate=new();
    private readonly Fusion.UnityEmbedded.TextLookup lookup=new();
    internal bool Add(string source)
    {
        var tokens=Fusion.UnityEmbedded.DisplayTemplateTokens.Matches(source);
        if(tokens.Length is <1 or >8||source.Length-tokens.Sum(t=>t.Length)<12)return false;
        lock(gate)lookup.Update(source,source);
        return true;
    }
    internal bool TryGet(string source,Func<string,string?> read,out string text)
    {
        lock(gate)return lookup.TryGetTemplate(source,owner=>read(owner)!,out text);
    }
}
