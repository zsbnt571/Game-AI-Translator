using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal static class EmbeddedTranslationText
{
    private static readonly Regex Markup = new(@"\G(?:<<[^<>\r\n]{1,256}>>|:[A-Za-z_][A-Za-z_0-9]*:|</?[A-Za-z][^>\r\n]{0,256}>|<#[0-9a-f]{6}(?:[0-9a-f]{2})?>|\[/?(?:b|i|u|s|color|font|font_size|url|img|center|right|left|fill|table|cell|wave|shake|tornado|rainbow|pulse|fade|outline_size|outline_color|bgcolor|fgcolor|dropcap|p|br|lb|rb)(?:[ =][^\]\r\n]{0,256})?\]|%(?:\([A-Za-z_][A-Za-z0-9_]*\))?[-+#0 ]*\d*(?:\.\d+)?[sdifouxXeEgGc%]|\\[nrt])",RegexOptions.Compiled|RegexOptions.IgnoreCase);

    internal static (string Text,string[] Codes) Protect(string value)
    {
        var codes=new List<string>();var text=new StringBuilder();
        string Token(string source){var token="⟦RPG"+codes.Count+"⟧";codes.Add(source);return token;}
        for(int i=0;i<value.Length;)
        {
            if(value[i]=='{')
            {
                int end=i,depth=0;
                for(;end<value.Length;end++){if(value[end]=='{')depth++;if(value[end]=='}'&&--depth==0){end++;break;}}
                if(depth==0){text.Append(Token(value[i..end]));i=end;continue;}
            }
            var match=Markup.Match(value,i);
            if(match.Success&&match.Index==i){text.Append(Token(match.Value));i+=match.Length;}
            else{text.Append(value[i]);i++;}
        }
        return(text.ToString(),codes.ToArray());
    }
}
