using System.Text;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

// WOLF commands are case sensitive. In particular, \E and \N are single
// character style switches, not arbitrary greedy backslash words. Bracketed
// substitutions can be nested; preserve the entire expression without eval.
internal static class WolfTranslationText
{
    internal static (string Text,string[] Codes) Protect(string source,bool lineBreaks=false)
    {
        var text=new StringBuilder();var codes=new List<string>();
        for(int i=0;i<source.Length;)
        {
            int end=i;
            if(lineBreaks&&source[i] is '\r' or '\n')
                end=source[i]=='\r'&&i+1<source.Length&&source[i+1]=='\n'?i+2:i+1;
            if(source[i]=='\\')
            {
                // External/database descriptions use a literal backslash at
                // line endings and at end-of-string. Protect that byte alone
                // so JSON translation never has to reconstruct its escaping.
                if(i+1==source.Length||source[i+1] is '\r' or '\n')end=i+1;
                else if(source[i+1] is 'E' or 'N' or '\\' or '!' or '.' or '|' or '<' or '>' or '^')end=i+2;
                else
                {
                    int name=i+1;while(name<source.Length&&source[name] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '+')name++;
                    if(name>i+1&&name<source.Length&&source[name]=='[')
                    {
                        int depth=0;for(int p=name;p<source.Length&&p-i<=2048;p++)
                        {
                            if(source[p] is '\r' or '\n')break;
                            if(source[p]=='['&&++depth>100)break;
                            if(source[p]==']'&&--depth==0){end=p+1;break;}
                        }
                    }
                }
            }
            if(end>i){text.Append("⟦RPG").Append(codes.Count).Append('⟧');codes.Add(source[i..end]);i=end;}
            else text.Append(source[i++]);
        }
        return(text.ToString(),codes.ToArray());
    }
    internal static bool Variable(string code)=>Regex.IsMatch(code,@"^\\(?:v\d*|s|self|cself|sys|sysS|udb|cdb|sdb|xy\w*)\[",RegexOptions.CultureInvariant);
}
