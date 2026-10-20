using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal static class RpgTranslationText
{
    // Compact counters such as 7.3K are quantities, not foreign words. Keep
    // this whole-string rule narrow: names such as 2B, versions and sentences
    // containing a quantity still need normal language handling.
    private static readonly Regex CompactQuantity = new(
        @"^[+\-−]?\s*\p{Sc}?\s*(?:\d+(?:[.,]\d+)?[kKmM]|\d+[.,]\d+[bBtT])\s*\p{Sc}?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    internal static bool ValidCachedPair(string source,string text,EmbeddedTextSyntax syntax)
    {
        if(string.IsNullOrWhiteSpace(text)||text.Length>12000||text.Contains('\0')||text.Contains("⟦RPG"))return false;
        string[] CodesOf(string value)=>syntax switch
        {
            EmbeddedTextSyntax.Renpy=>RenpyTranslationText.Protect(value).Codes,
            EmbeddedTextSyntax.Wolf=>WolfTranslationText.Protect(value).Codes,
            EmbeddedTextSyntax.Generic=>EmbeddedTranslationText.Protect(value).Codes,
            EmbeddedTextSyntax.Yukar=>EmbeddedTranslationText.Protect(value,yukar:true).Codes,
            _=>Protect(value).Codes
        };
        var originalCodes=CodesOf(source);var translatedCodes=CodesOf(text);
        if(!originalCodes.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(translatedCodes.OrderBy(x=>x,StringComparer.Ordinal),StringComparer.Ordinal))return false;
        bool Variable(string code)=>syntax switch
        {
            EmbeddedTextSyntax.Renpy=>code.StartsWith('[')&&!code.StartsWith("[[",StringComparison.Ordinal),
            EmbeddedTextSyntax.Wolf=>WolfTranslationText.Variable(code),
            EmbeddedTextSyntax.Generic or EmbeddedTextSyntax.Yukar=>Regex.IsMatch(code,@"^\{(?:[A-Za-z_][A-Za-z_0-9]*|\d+)(?:[, :][^{}]*)?\}$")
                ||Regex.IsMatch(code,@"^%\([A-Za-z_][A-Za-z_0-9]*\)"),
            _=>Regex.IsMatch(code,@"^(?:\\|\x1b)[VN]\[\d+\]$",RegexOptions.IgnoreCase)
        };
        // Variables may move with the translated sentence; formatting/control
        // instructions must retain their relative order, including closing tags.
        return originalCodes.Where(x=>!Variable(x)).SequenceEqual(translatedCodes.Where(x=>!Variable(x)),StringComparer.Ordinal);
    }
    internal static bool HasTranslatableText(string value,EmbeddedTextSyntax syntax)
    {
        var protectedText=syntax switch
        {
            EmbeddedTextSyntax.Renpy=>RenpyTranslationText.Protect(value).Text,
            EmbeddedTextSyntax.Wolf=>WolfTranslationText.Protect(value).Text,
            EmbeddedTextSyntax.Generic=>EmbeddedTranslationText.Protect(value).Text,
            EmbeddedTextSyntax.Yukar=>EmbeddedTranslationText.Protect(value,yukar:true).Text,
            _=>Protect(value).Text
        };
        string plain=Regex.Replace(protectedText,@"⟦RPG\d+⟧","").Trim();
        return !CompactQuantity.IsMatch(plain)&&Regex.IsMatch(plain,@"\p{L}");
    }
    // Chinese words need no separating spaces. Unicode word boundaries treated
    // "BGM音量" as missing BGM, invalidating an otherwise reusable cached pair.
    // ASCII boundaries still avoid matching inside identifiers such as MAX_HP.
    private static readonly Regex Codes=new(@"</?[A-Za-z][^>\r\n]{0,160}>|(?:\\|\x1b)(?:(?i:RESETSHAKE|SHAKE|WAVE|SLIDE|CIRCLE)(?:<[^>\r\n]{0,160}>)?|[A-Za-z]+(?:\[[^\]\r\n]*\])?|[.!|><^{}$\\])|(?<![A-Za-z0-9_])(?:BGM|BGS|ME|SE|HP|MP|TP|ATK|DEF|MAT|MDF|EXP|LV|FPS)(?![A-Za-z0-9_])",RegexOptions.Compiled);
    internal static (string Text,string[] Codes) Protect(string value)
    {
        var codes=new List<string>();var text=Codes.Replace(value,m=>{var token="⟦RPG"+codes.Count+"⟧";codes.Add(m.Value);return token;});return(text,codes.ToArray());
    }
    internal static string Restore(string value,string[] codes)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>12000)throw new InvalidDataException("译文长度无效。");
        for(int i=0;i<codes.Length;i++){var token="⟦RPG"+i+"⟧";if(value.Split(token,StringSplitOptions.None).Length!=2)throw new InvalidDataException("译文没有保留游戏控制符，已保留原文。");value=value.Replace(token,codes[i]);}
        if(value.Contains("⟦RPG"))throw new InvalidDataException("译文包含未知控制符。");return value;
    }
}

