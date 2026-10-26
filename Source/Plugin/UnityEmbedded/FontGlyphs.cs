using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Fusion.UnityEmbedded
{
    // Shared by all managed backends. Codepoints, not UTF-16 halves. This is
    // glyph availability only; it does not establish shaping or visible pixels.
    internal static class FontGlyphs
    {
        static readonly HashSet<string> Tags=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"b","i","u","s","color","size","font","font-weight","material","sprite","link","align","alpha","br","cspace","mspace","space","indent","line-indent","line-height","margin","margin-left","margin-right","mark","nobr","noparse","pos","rotate","voffset","width","style","sub","sup","uppercase","lowercase","smallcaps","allcaps","page","quad"};
        internal static IEnumerable<int> Visible(string text)
        {
            bool literal=false;
            text=text??"";
            for(int i=0;i<text.Length;i++)
            {
                if(text[i]=='<'&&i+1<text.Length)
                {
                    int end=text.IndexOf('>',i+1);
                    if(end>i&&end-i<=512)
                    {
                        string body=text.Substring(i+1,end-i-1);int start=body.StartsWith("/",StringComparison.Ordinal)?1:0,j=start;
                        while(j<body.Length&&(char.IsLetter(body[j])||body[j]=='-'))j++;
                        string name=body.Substring(start,j-start);
                        bool shortColor=start==0&&body.Length==7&&body[0]=='#';
                        if(shortColor)for(int k=1;k<body.Length;k++)if(!Uri.IsHexDigit(body[k])){shortColor=false;break;}
                        bool validTail=j==body.Length||char.IsWhiteSpace(body[j])||body[j]=='='||body[j]=='/';
                        if((!literal&&((validTail&&Tags.Contains(name))||shortColor))||(literal&&start==1&&name.Equals("noparse",StringComparison.OrdinalIgnoreCase)&&validTail))
                        {if(name.Equals("noparse",StringComparison.OrdinalIgnoreCase))literal=start==0;i=end;continue;}
                    }
                }
                int code=text[i];
                if(char.IsHighSurrogate(text[i])){if(i+1>=text.Length||!char.IsLowSurrogate(text[i+1]))throw new FormatException("Incomplete glyph codepoint");code=char.ConvertToUtf32(text[i],text[++i]);}
                else if(char.IsLowSurrogate(text[i]))throw new FormatException("Incomplete glyph codepoint");
                if(code==9||code==10||code==13||code==0x200b||code==0x200c||code==0x200d||code==0x2060||code==0xfeff||(code>=0xfe00&&code<=0xfe0f)||(code>=0xe0100&&code<=0xe01ef))continue;
                yield return code;
            }
        }
        internal static string Required(string text,string source="")
        {
            var original=source==null?null:new HashSet<int>(Visible(source));var seen=new HashSet<int>();var result=new StringBuilder();
            foreach(int code in Visible(text))
            {
                if(code<0x80||!seen.Add(code))continue;
                string value=char.ConvertFromUtf32(code);var category=CharUnicodeInfo.GetUnicodeCategory(value,0);
                if(category==UnicodeCategory.Control||category==UnicodeCategory.Format||category==UnicodeCategory.SpaceSeparator||category==UnicodeCategory.LineSeparator||category==UnicodeCategory.ParagraphSeparator)continue;
                // Existing sprite/private-use symbols may have a separate game
                // renderer. Their presence is not proof of coverage: leave them
                // to that renderer, while still checking every textual letter,
                // mark, number and punctuation even if it occurs in the source.
                if(original!=null&&original.Contains(code)&&(category==UnicodeCategory.PrivateUse||category==UnicodeCategory.OtherSymbol))continue;
                result.Append(value);
            }
            return result.ToString();
        }
        internal static string[] SystemNames(string required)
        {
            bool cjk=false,arabic=false,indic=false;
            foreach(int code in Visible(required)){cjk|=code>=0x2e80&&code<=0x9fff||code>=0xac00&&code<=0xd7af||code>=0x20000;arabic|=code>=0x600&&code<=0x8ff||code>=0xfb50&&code<=0xfeff;indic|=code>=0x900&&code<=0xdff;}
            return cjk?new[]{"Microsoft YaHei","DengXian","SimHei","Yu Gothic","Malgun Gothic","Segoe UI"}:arabic?new[]{"Segoe UI","Tahoma","Arial","Microsoft YaHei"}:indic?new[]{"Nirmala UI","Segoe UI","Arial","Microsoft YaHei"}:new[]{"Segoe UI","Arial","Tahoma","Microsoft YaHei","DengXian"};
        }
    }
}
