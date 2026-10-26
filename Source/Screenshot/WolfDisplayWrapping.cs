using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal static class WolfDisplayWrapping
{
    // Older caches contain flattened, originally hard-wrapped tutorials. Repair
    // only the display copy, using the original's longest line as a conservative
    // width envelope. The user's translation, glossary receipt and wording stay
    // intact. New requests protect explicit line breaks before calling a model.
    internal static string Fit(string source,string translated)
    {
        // An already line-preserving translation owns its authored boundaries.
        // Verified typewriters consume those lines as persistent character
        // pictures and validate their count before replacing the whole source.
        // Rewrapping one long line here can turn a valid cached paragraph into
        // an invalid display copy. Keep its bytes (including CRLF and controls)
        // intact; the native source/control checks still decide whether to use it.
        int lineBreaks=source.Count(c=>c=='\n');
        if(lineBreaks>0&&translated.Count(c=>c=='\n')==lineBreaks&&
            WolfTranslationText.Protect(source).Codes.SequenceEqual(
                WolfTranslationText.Protect(translated).Codes,StringComparer.Ordinal))return translated;
        source=source.Replace("\r\n","\n");
        if(!source.Contains('\n')||translated.Length==0)return translated;
        var original=WolfTranslationText.Protect(source);
        double limit=original.Text.Split('\n').Max(line=>Width(Regex.Replace(line,@"⟦RPG\d+⟧","")));
        if(limit<8)return translated;
        var value=WolfTranslationText.Protect(translated.Replace("\r\n","\n"));
        var result=new StringBuilder();
        int paragraphIndex=0;
        foreach(var paragraph in value.Text.Split('\n'))
        {
            if(paragraphIndex++>0)result.Append('\n');
            var line=new StringBuilder();double width=0;
            var tokens=Regex.Matches(paragraph,@"⟦RPG\d+⟧|[A-Za-z0-9]+(?:['’.-][A-Za-z0-9]+)*|[ \t]+|\P{M}\p{M}*",RegexOptions.CultureInvariant);
            foreach(Match token in tokens)
            {
                string part=token.Value;double next=part.StartsWith("⟦RPG",StringComparison.Ordinal)?0:Width(part);
                if(width>0&&width+next>limit&&!string.IsNullOrWhiteSpace(part))
                {result.Append(line.ToString().TrimEnd()).Append('\n');line.Clear();width=0;}
                if(width==0&&string.IsNullOrWhiteSpace(part))continue;
                line.Append(part);width+=next;
            }
            result.Append(line.ToString().TrimEnd());
        }
        return RpgTranslationText.Restore(result.ToString(),value.Codes);
    }
    private static double Width(string text)
    {
        double width=0;
        foreach(var rune in text.EnumerateRunes())
        {
            if(Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)continue;
            width+=rune.Value>=0x2e80?2:rune.Value is 'i' or 'l' or 'I' or '.' or ',' or '!' or ' '?0.55:1;
        }
        return width;
    }
}
