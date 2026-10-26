using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal static class EmbeddedTranslationText
{
    private static readonly Regex Markup = new(@"\G(?:<<[^<>\r\n]{1,256}>>|:[A-Za-z_][A-Za-z_0-9]*:|</?[A-Za-z][^>\r\n]{0,256}>|<#[0-9a-f]{6}(?:[0-9a-f]{2})?>|\[/?(?:b|i|u|s|color|font|font_size|size|scale|scalex|scaley|tag|offsetx|offsety|spacing|opacity|url|img|center|right|left|fill|table|cell|wave|shake|tornado|rainbow|pulse|fade|outline_size|outline_color|bgcolor|fgcolor|dropcap|p|br|lb|rb)(?:[ =][^\]\r\n]{0,256})?\]|%(?:\([A-Za-z_][A-Za-z0-9_]*\))?[-+#0 ]*\d*(?:\.\d+)?[sdifouxXeEgGc%]|\\[nrt\[\]])",RegexOptions.Compiled|RegexOptions.IgnoreCase);

    private static readonly Regex PercentFormat = new(@"\G%(?:\([A-Za-z_][A-Za-z0-9_]*\)|[1-9]\d*\$)?[-+#0 ]*(?:\d+|\*)?(?:\.(?:\d+|\*))?[sdifouxXeEgGc%]",RegexOptions.Compiled|RegexOptions.IgnoreCase);

    // A percent in prose is not a printf argument. In particular, matching
    // only the first letter of 'chance', 'defense' or 'of' corrupts translations.
    internal static int FormatLength(string value,int index)
    {
        if(index+1<value.Length&&value[index+1]=='%')return 2;
        var match=PercentFormat.Match(value,index);
        if(!match.Success||match.Index!=index)return 0;
        bool simple=Regex.IsMatch(match.Value,@"^% *[A-Za-z]$");
        if(simple&&index>0&&char.IsDigit(value[index-1]))return 0;
        int end=index+match.Length;
        if(simple&&match.Value.Contains(' ')&&end<value.Length&&value[end] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')return 0;
        return match.Length;
    }

    internal static (string Text,string[] Codes) Protect(string value,bool lineBreaks=false,bool yukar=false)
    {
        var codes=new List<string>();var text=new StringBuilder();
        // The display cache and provider protection must recognize the same
        // simple variables. These are literal tokens only: no game expression
        // is evaluated, and escaped/nested brackets or BBCode remain separate.
        var squareVariables=Fusion.UnityEmbedded.DisplayTemplateTokens.Matches(value)
            .Where(match=>Fusion.UnityEmbedded.DisplayTemplateTokens.IsSquareVariable(match.Value))
            .ToDictionary(match=>match.Index,match=>match.Value);
        string Token(string source){var token="⟦RPG"+codes.Count+"⟧";codes.Add(source);return token;}
        for(int i=0;i<value.Length;)
        {
            if(yukar&&value[i]=='\\')
            {
                var control=FusionYukar.TextCodes.At.Match(value,i);
                if(control.Success&&control.Index==i){text.Append(Token(control.Value));i+=control.Length;continue;}
            }
            if(lineBreaks&&(value[i]=='\r'||value[i]=='\n'))
            {
                int length=value[i]=='\r'&&i+1<value.Length&&value[i+1]=='\n'?2:1;
                text.Append(Token(value.Substring(i,length)));i+=length;continue;
            }
            if(value[i]=='%')
            {
                int length=FormatLength(value,i);
                if(length>0){text.Append(Token(value.Substring(i,length)));i+=length;}
                else{text.Append('%');i++;}
                continue;
            }
            if(value[i]=='{')
            {
                int end=i,depth=0;
                for(;end<value.Length;end++){if(value[end]=='{')depth++;if(value[end]=='}'&&--depth==0){end++;break;}}
                if(depth==0){text.Append(Token(value[i..end]));i=end;continue;}
            }
            var match=Markup.Match(value,i);
            if(match.Success&&match.Index==i){text.Append(Token(match.Value));i+=match.Length;}
            else if(squareVariables.TryGetValue(i,out string? variable)){text.Append(Token(variable));i+=variable.Length;}
            else{text.Append(value[i]);i++;}
        }
        return(text.ToString(),codes.ToArray());
    }
}
