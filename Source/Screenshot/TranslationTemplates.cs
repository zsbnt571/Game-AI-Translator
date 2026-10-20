using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Owned by one game session, never a cross-game or persistent translation cache.
// Only Chinese quantity sentences qualify. Names, versions, dates, decimals and
// numeric identifiers are deliberately left to the ordinary exact-source path.
internal sealed class TranslationTemplates
{
    internal sealed record Request(string Text,string[] Codes,string Key);
    private readonly object gate=new();
    private readonly Dictionary<string,string> values=new(StringComparer.Ordinal);
    private readonly Queue<string> order=new();
    private int characters;
    private static readonly Regex Token=new(@"⟦RPG\d+⟧",RegexOptions.Compiled);
    private static readonly Regex Number=new(@"(?<![\p{L}\p{N}_.,:/#%+\-])\d{1,9}(?![\p{L}\p{N}_.,:/%\-])",RegexOptions.Compiled);
    private static readonly Regex Word=new(@"[A-Za-z]{2,}",RegexOptions.Compiled);
    private static readonly Regex Identifier=new(@"(?i)\b(?:version|build|chapter|episode|room|level|stage|floor|id|serial|phone|year|page|error|code)\s*\d|\d[./:\-]\d|\b(?:january|february|march|april|may|june|july|august|september|october|november|december)\b|\d\s*(?:am|pm)\b",RegexOptions.Compiled);
    internal static Request Prepare(string text,string[] codes,ApiSettings settings,EmbeddedTextSyntax syntax)
    {
        string visible=Token.Replace(text,"");
        if(!settings.PreserveNumbers||!FusionConfiguration.NormalizeLanguage(settings.TargetLanguage).StartsWith("zh",StringComparison.OrdinalIgnoreCase)
            ||Word.Matches(visible).Count<3||Identifier.IsMatch(visible))return new(text,codes,"");
        // Replace only outside existing engine placeholders. Their digits are
        // indices, not quantities, and actual control values are part of the key.
        var all=new List<string>(codes);int count=0;
        string Replace(string part)=>Number.Replace(part,m=>{count++;int index=all.Count;all.Add(m.Value);return "⟦RPG"+index+"⟧";});
        var b=new StringBuilder();int position=0;
        foreach(Match token in Token.Matches(text)){b.Append(Replace(text[position..token.Index]));b.Append(token.Value);position=token.Index+token.Length;}
        b.Append(Replace(text[position..]));
        if(count is <1 or >3)return new(text,codes,"");
        string request=b.ToString();
        if(Regex.IsMatch(Token.Replace(request,""),@"\d"))return new(text,codes,"");
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{syntax,request,controls=codes,settings}))));
        return new(request,all.ToArray(),key);
    }
    internal bool TryGet(string key,out string value){lock(gate)return values.TryGetValue(key,out value!);}
    internal static bool ValidResult(string value)=>!Regex.IsMatch(Token.Replace(value,""),@"\d");
    internal void Remember(string key,string value)
    {
        if(key.Length==0||value.Length>12000)return;
        lock(gate)
        {
            if(values.ContainsKey(key))return;
            while(order.Count>0&&(values.Count>=512||characters+value.Length>1024*1024))
            {string old=order.Dequeue();if(values.Remove(old,out var removed))characters-=removed.Length;}
            values[key]=value;order.Enqueue(key);characters+=value.Length;
        }
    }
    internal void Clear(){lock(gate){values.Clear();order.Clear();characters=0;}}
}
