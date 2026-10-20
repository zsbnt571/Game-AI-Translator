using System.Buffers.Binary;
using System.Text;

namespace ScreenshotTranslationUiTester;

// Read OpenType cmap data, without loading an untrusted font into a renderer.
// Coverage is not shaping, line layout or proof that the game uses this font.
internal static class GameFontCoverage
{
    internal const string ChineseSample="中文翻译测试你好谢谢攻略保存开始继续";
    internal sealed record Result(string FontHash,int Checked,int Present,int[] Missing,bool Truncated);
    internal static Result Check(string path,string text,int maximumCharacters=4096)
    {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length is <12 or >64*1024*1024)throw new InvalidDataException("字体文件大小不在支持范围。");
        byte[] data=new byte[checked((int)input.Length)];input.ReadExactly(data);return Check(data,text,maximumCharacters);
    }
    internal static Result Check(byte[] data,string text,int maximumCharacters=4096)
    {
        for(int i=0;i<text.Length;i++)
            if(char.IsHighSurrogate(text[i])){if(++i>=text.Length||!char.IsLowSurrogate(text[i]))throw new InvalidDataException("字体检查文本包含不完整的 Unicode 字符。");}
            else if(char.IsLowSurrogate(text[i]))throw new InvalidDataException("字体检查文本包含不完整的 Unicode 字符。");
        if(maximumCharacters is <1 or >65536)throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        ushort U16(int p){if(p<0||p>data.Length-2)throw new InvalidDataException("字体表范围无效。");return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p,2));}
        uint U32(int p){if(p<0||p>data.Length-4)throw new InvalidDataException("字体表范围无效。");return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p,4));}
        int Pos(uint n)=>n<=int.MaxValue?(int)n:throw new InvalidDataException("字体表偏移无效。");
        int face=0;
        if(U32(0)==0x74746366){if(U32(8) is <1 or >256)throw new InvalidDataException("字体集合无效。");face=Pos(U32(12));}
        uint flavor=U32(face);if(flavor is not (0x00010000 or 0x4f54544f or 0x74727565))throw new InvalidDataException("不是支持的 TTF/OTF 字体。");
        int tables=U16(face+4);if(tables is <1 or >512||face+12L+16L*tables>data.Length)throw new InvalidDataException("字体目录无效。");
        int cmap=-1,cmapBytes=0,glyphCount=65536;
        for(int i=0;i<tables;i++)
        {
            int p=face+12+16*i;uint tag=U32(p);int start=Pos(U32(p+8)),size=Pos(U32(p+12));
            if(start>data.Length-size)throw new InvalidDataException("字体表超出文件范围。");
            if(tag==0x636d6170){cmap=start;cmapBytes=size;}
            if(tag==0x6d617870&&size>=6)glyphCount=U16(start+4);
        }
        if(cmap<0||cmapBytes<4||U16(cmap)!=0)throw new InvalidDataException("字体缺少 Unicode cmap。");
        int maps=U16(cmap+2);if(maps is <1 or >128||4+8L*maps>cmapBytes)throw new InvalidDataException("字体 cmap 目录无效。");
        var readers=new List<Func<int,bool>>();
        for(int m=0;m<maps;m++)
        {
            int entry=cmap+4+8*m,platform=U16(entry),encoding=U16(entry+2);
            if(platform!=0&&!(platform==3&&encoding is 1 or 10))continue;
            int relative=Pos(U32(entry+4));if(relative>cmapBytes-2)throw new InvalidDataException("字体 cmap 子表范围无效。");int p=cmap+relative;
            bool Valid(uint glyph)=>glyph>0&&glyph<glyphCount;
            if(U16(p)==12)
            {
                if(relative>cmapBytes-16)throw new InvalidDataException("字体 cmap12 截断。");
                int length=Pos(U32(p+4)),count=Pos(U32(p+12));
                if(count>65536||length<16||length>cmapBytes-relative||16L+12L*count>length)throw new InvalidDataException("字体 cmap12 范围无效。");
                var groups=new (uint First,uint Last,uint Glyph)[count];uint last=0;
                for(int i=0;i<count;i++)
                {
                    int at=p+16+12*i;uint first=U32(at),end=U32(at+4),glyph=U32(at+8);
                    if(first>end||end>0x10ffff||i>0&&first<=last)throw new InvalidDataException("字体 cmap12 字符范围无效。");
                    groups[i]=(first,end,glyph);last=end;
                }
                readers.Add(code=>
                {
                    int lo=0,hi=groups.Length-1;
                    while(lo<=hi){int mid=lo+(hi-lo)/2;var group=groups[mid];if(code<group.First)hi=mid-1;else if(code>group.Last)lo=mid+1;
                        else{ulong glyph=(ulong)group.Glyph+(uint)code-group.First;return glyph<=uint.MaxValue&&Valid((uint)glyph);}}
                    return false;
                });
            }
            else if(U16(p)==4)
            {
                if(relative>cmapBytes-16)throw new InvalidDataException("字体 cmap4 截断。");
                int length=U16(p+2),twice=U16(p+6),count=twice/2;
                if(twice%2!=0||count is <1 or >8192||length<16+8L*count||length>cmapBytes-relative)throw new InvalidDataException("字体 cmap4 范围无效。");
                int ends=p+14,starts=ends+2*count+2,deltas=starts+2*count,ranges=deltas+2*count;
                var endCodes=new int[count];
                for(int i=0;i<count;i++){endCodes[i]=U16(ends+2*i);int startCode=U16(starts+2*i);if(startCode>endCodes[i]||i>0&&startCode<=endCodes[i-1])throw new InvalidDataException("字体 cmap4 字符范围无效。");}
                readers.Add(code=>
                {
                    if(code>65535)return false;int index=Array.BinarySearch(endCodes,code);if(index<0)index=~index;
                    if(index>=count||code<U16(starts+2*index))return false;
                    int delta=unchecked((short)U16(deltas+2*index)),range=U16(ranges+2*index);uint glyph;
                    if(range==0)glyph=(uint)((code+delta)&65535);
                    else
                    {
                        long at=(long)ranges+2*index+range+2*(code-U16(starts+2*index));
                        if(at<p||at+2>p+length)throw new InvalidDataException("字体 cmap4 字形范围无效。");
                        glyph=U16((int)at);if(glyph!=0)glyph=(uint)(((int)glyph+delta)&65535);
                    }
                    return Valid(glyph);
                });
            }
        }
        if(readers.Count==0)throw new InvalidDataException("字体没有已支持的 Unicode cmap4/cmap12。");
        var characters=new HashSet<int>();bool truncated=false;
        foreach(var rune in text.EnumerateRunes())
        {
            if(Rune.IsControl(rune)||Rune.IsWhiteSpace(rune)||characters.Contains(rune.Value))continue;
            if(characters.Count>=maximumCharacters){truncated=true;break;}characters.Add(rune.Value);
        }
        int[] missing=characters.Where(c=>!readers.Any(r=>r(c))).Order().ToArray();
        return new(CatalogOrigins.Hash(data),characters.Count,characters.Count-missing.Length,missing,truncated);
    }
    internal static void RequireChinesePayload(string path)
    {
        var result=Check(path,ChineseSample);
        if(result.Missing.Length>0)throw new IOException("翻译字体缺少中文样字，未应用该字体；请检查本候选的字体文件。");
    }
}
