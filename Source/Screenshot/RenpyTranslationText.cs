using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class RenpyTranslationText
{
    // Protect the literal template before any game variables are evaluated. Nested
    // indexing and quoted brackets are legal inside Ren'Py interpolation expressions.
    internal static (string Text,string[] Codes) Protect(string value)
    {
        var text=new StringBuilder();var codes=new List<string>();
        for(int i=0;i<value.Length;i++)
        {
            char opening=value[i];
            if(opening is not ('[' or '{')){text.Append(opening);continue;}
            int end=i+1;
            if(end<value.Length&&value[end]==opening)end++;
            else
            {
                char closing=opening=='['?']':'}',quote='\0';int depth=1;
                for(;end<value.Length;end++)
                {
                    char c=value[end];
                    if(quote!='\0'){if(c=='\\'){end++;continue;}if(c==quote)quote='\0';continue;}
                    if(opening=='['&&c is ('\'' or '"')){quote=c;continue;}
                    if(c==opening)depth++;
                    if(c==closing&&--depth==0)break;
                }
                if(end>=value.Length){text.Append(opening);continue;}
                end++;
            }
            text.Append("⟦RPG").Append(codes.Count).Append('⟧');codes.Add(value[i..end]);i=end-1;
        }
        return(text.ToString(),codes.ToArray());
    }
}
