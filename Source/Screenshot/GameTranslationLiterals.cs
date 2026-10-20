using System.Text;
using System.Text.RegularExpressions;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

// Request-time literal protection and cache admission share the same engine
// syntax. Only visible numeric literals and explicit keep-as-written glossary
// terms qualify; this never infers character names or evaluates game variables.
internal static class GameTranslationLiterals
{
    private static readonly Regex Token=new(@"⟦RPG\d+⟧",RegexOptions.Compiled);
    // Normalize only explicit numeric range connectors. This preserves the
    // existing compact date/time/fraction checks and lets target grammar use
    // common connectors instead of copying an English "to" literally.
    private static readonly Regex RangeWords=new(@"(?<=[0-9%％])(?:\s+(?:to|through|bis|à|au|a|até)\s+|\s*(?:至|到|до|по|から|まで|부터|에서)\s*)(?=[+\-−±]?[0-9])",RegexOptions.Compiled|RegexOptions.CultureInvariant|RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(100));
    internal static (string Text,string[] Codes) Syntax(string source,EmbeddedTextSyntax syntax,bool lineBreaks=false)=>syntax switch
    {
        EmbeddedTextSyntax.Renpy=>RenpyTranslationText.Protect(source),
        EmbeddedTextSyntax.Wolf=>WolfTranslationText.Protect(source,lineBreaks),
        EmbeddedTextSyntax.Generic=>EmbeddedTranslationText.Protect(source,lineBreaks:lineBreaks),
        EmbeddedTextSyntax.Yukar=>EmbeddedTranslationText.Protect(source,lineBreaks:lineBreaks,yukar:true),
        _=>RpgTranslationText.Protect(source)
    };
    private static string OutsideTokens(string text,Func<string,string> transform)
    {
        var result=new StringBuilder();int position=0;
        foreach(Match token in Token.Matches(text)){result.Append(transform(text[position..token.Index]));result.Append(token.Value);position=token.Index+token.Length;}
        return result.Append(transform(text[position..])).ToString();
    }
    internal static (string Text,string[] Codes) Protect(string text,string[] codes,ApiSettings settings,IReadOnlyDictionary<string,string>? terms)
    {
        var all=new List<string>(codes);
        string Add(string literal){string marker="⟦RPG"+all.Count+"⟧";all.Add(literal);return marker;}
        string Number(string literal)
        {
            // Natural-language range connectors still need localization. Keep
            // endpoint spellings, while leaving "to/through/至/到" as prose.
            var connectors=Regex.Matches(literal,@"(?<=[0-9%％])\s*(?:to|through|至|到)\s*(?=[+\-−±]?[0-9])",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
            if(connectors.Count==0)return Add(literal);
            var result=new StringBuilder();int position=0;
            foreach(Match connector in connectors){result.Append(Add(literal[position..connector.Index])).Append(connector.Value);position=connector.Index+connector.Length;}
            return result.Append(Add(literal[position..])).ToString();
        }
        if(terms is {Count:>0})text=OutsideTokens(text,part=>TranslationGlossary.ProtectIdentity(part,terms,Add));
        if(settings.PreserveNumbers)text=OutsideTokens(text,part=>
        {
            var result=new StringBuilder();int position=0;
            foreach(var token in NumericFidelityV2.Tokens(part,"",true))
            {
                // Surrounding spaces follow sentence grammar; protect only the
                // numeric spelling, including its internal separators/signs.
                int leading=token.Token.Length-token.Token.TrimStart().Length;
                string literal=token.Token.Trim();int start=token.Start+leading;
                result.Append(part[position..start]).Append(Number(literal));position=start+literal.Length;
            }
            return result.Append(part[position..]).ToString();
        });
        return(text,all.ToArray());
    }
    internal static string Visible(string text,EmbeddedTextSyntax syntax)=>Token.Replace(Syntax(text,syntax).Text," ");
    internal static bool NumbersPreserved(string source,string translated,ApiSettings settings,EmbeddedTextSyntax syntax)
        =>!settings.PreserveNumbers||NumericFidelityV2.Matches(RangeWords.Replace(Visible(source,syntax),"~"),RangeWords.Replace(Visible(translated,syntax),"~"));
    internal static string[] IdentityPassages(string source,IReadOnlyDictionary<string,string>? terms,EmbeddedTextSyntax syntax)
    {
        if(terms is not {Count:>0})return [];
        var values=new List<string>();
        OutsideTokens(Syntax(source,syntax).Text,part=>{TranslationGlossary.ProtectIdentity(part,terms,value=>{values.Add(value);return value;});return part;});
        return values.ToArray();
    }
    internal static bool IdentityPreserved(string source,string translated,IReadOnlyDictionary<string,string>? terms,EmbeddedTextSyntax syntax)
    {
        string visible=Visible(translated,syntax);
        return IdentityPassages(source,terms,syntax).GroupBy(s=>s,StringComparer.Ordinal)
            .All(group=>
            {
                static bool Word(char value)=>value<128&&(char.IsLetterOrDigit(value)||value=='_');
                string pattern=(Word(group.Key[0])?@"(?<![A-Za-z0-9_])":"")+Regex.Escape(group.Key)+(Word(group.Key[^1])?@"(?![A-Za-z0-9_])":"");
                return Regex.Matches(visible,pattern,RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)).Count>=group.Count();
            });
    }
}
