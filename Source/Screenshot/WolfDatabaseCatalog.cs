using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Read-only paired database layout, independently implemented from the public
// WolfTL Database.hpp and Anylang reader. No game code, decryption or writeback.
internal static class WolfDatabaseCatalog
{
    internal sealed record Field(string Location,string Text,string Context);
    internal sealed record Result(Field[] Fields,string Encoding,byte Version,int Excluded)
    {internal int UnreadableStrings {get;init;}internal Dictionary<int,string> MapFiles {get;init;}=[];internal Dictionary<string,string> NumericNames {get;init;}=[];}
    private sealed record TypePlan(string Name,string[] Names,string[] Rows,byte[] Kinds);
    internal static Result Read(byte[] project,byte[] data,Action check,bool skipMalformedStrings=false)
    {
        if(project.Length>32*1024*1024||data.Length>32*1024*1024)
            throw new ExperimentalRpgCatalog.LimitException("WOLF 数据库配对文件超过 32 MB。");
        if(data.Length<11||!data.AsSpan(0,6).SequenceEqual(new byte[]{0,0x57,0,0,0x4f,0x4c})
            ||data[6] is not (0 or 0x55)||!data.AsSpan(7,3).SequenceEqual(new byte[]{0x46,0x4d,0}))
            throw new NotSupportedException("WOLF 数据库头或加密格式尚未支持。");
        bool utf8=data[6]==0x55;byte version=data[10];
        if(version is not (0xc1 or 0xc2 or 0xc4))throw new NotSupportedException("WOLF 数据库版本尚未支持。");
        var p=new Reader(project,utf8,check,skipMalformedStrings);int typeCount=p.Count(512);var types=new TypePlan[typeCount];
        for(int t=0;t<typeCount;t++)
        {
            string name=p.String();int count=p.Count(4096);var names=new string[count];
            for(int i=0;i<count;i++)names[i]=p.String();
            int rows=p.Count(100000);var rowNames=new string[rows];for(int i=0;i<rows;i++)rowNames[i]=p.String();
            p.String();int kinds=p.Count(65536);if(kinds<count)throw new InvalidDataException("WOLF 数据库字段类型数量不足。");
            byte[] fieldKinds=p.Bytes(kinds)[..count];
            int n=p.Count(count);for(int i=0;i<n;i++)p.String();
            n=p.Count(count);for(int i=0;i<n;i++){int size=p.Count(100000);for(int j=0;j<size;j++)p.String();}
            n=p.Count(count);for(int i=0;i<n;i++)p.Skip(checked(p.Count(100000)*4));
            p.Skip(checked(p.Count(count)*4));types[t]=new(name,names,rowNames,fieldKinds);
        }
        p.End();
        byte[] plain;
        if(version==0xc4)
        {
            if(data.Length<19)throw new InvalidDataException("WOLF 压缩数据库截断。");
            uint size=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(11,4)),stored=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(15,4));
            if(size>16*1024*1024)throw new ExperimentalRpgCatalog.LimitException("WOLF 解压数据库超过 16 MB。");
            if(stored!=data.Length-19L)throw new InvalidDataException("WOLF 压缩数据库范围不符。");
            check();plain=DefoldTextCatalog.Decompress(data[19..],checked((int)size));check();
        }
        else plain=data[11..];
        var r=new Reader(plain,utf8,check,skipMalformedStrings);if(r.Count(512)!=typeCount)throw new InvalidDataException("WOLF 数据库与 project 类型数量不符。");
        var result=new List<Field>();var mapFiles=new Dictionary<int,string>();var numericNames=new Dictionary<string,string>();int cells=0,excluded=0;
        for(int t=0;t<typeCount;t++)
        {
            var type=types[t];r.Expect(0xfe,0xff,0xff,0xff);uint named=r.Word();int fields=r.Count(type.Names.Length);
            bool opaqueSchema=type.Name.Contains('\0')||type.Names.Any(v=>v.Contains('\0'))||type.Rows.Any(v=>v.Contains('\0'));
            if(named==0x0001d4c0)r.String();
            var fieldOf=Enumerable.Repeat(-1,fields).ToArray();var numericFields=new List<int>();int strings=0;
            for(int f=0;f<fields;f++)
            {
                uint slot=r.Word();if(slot<2000){if(slot>=1000)numericFields.Add(f);continue;}slot-=2000;
                if(slot>=fields||fieldOf[(int)slot]!=-1)throw new InvalidDataException("WOLF 数据库字符串槽位重复或越界。");
                fieldOf[(int)slot]=f;strings++;
            }
            if(fieldOf.Take(strings).Any(f=>f<0))throw new InvalidDataException("WOLF 数据库字符串槽位存在空洞。");
            int rows=r.Count(100000);if(rows!=type.Rows.Length)throw new InvalidDataException("WOLF 数据库与 project 行数量不符。");
            if(!opaqueSchema)for(int row=0;row<rows&&numericNames.Count<20000;row++)foreach(int f in numericFields)
                if(numericNames.Count<20000)numericNames[$"db:{t}:{row}:{f}"]=$"{type.Name} / {type.Rows[row]} / {type.Names[f]} [CDB {t}:{row}:{f}]";
            cells=checked(cells+rows*strings);if(cells>100000)throw new ExperimentalRpgCatalog.LimitException("WOLF 数据库字符串数量超过 100000。");
            var values=new string[rows][];
            for(int row=0;row<rows;row++)
            {
                r.Skip(checked((fields-strings)*4));values[row]=new string[strings];
                for(int s=0;s<strings;s++)values[row][s]=r.String();
            }
            // Editor row names may be used to find records. Matching columns are
            // context/keys, never automatically treated as display-name fields.
            var naming=new HashSet<int>();
            for(int s=0;s<strings;s++)
            {
                bool matched=false,mismatch=false;
                for(int row=0;row<rows;row++)
                {
                    string value=values[row][s].Trim();if(value.Length==0)continue;
                    if(value!=type.Rows[row].Trim()){mismatch=true;break;}matched=true;
                }
                if(matched&&!mismatch)naming.Add(s);
            }
            for(int row=0;row<rows;row++)for(int s=0;s<strings;s++)
            {
                check();int f=fieldOf[s];string text=values[row][s];
                // Only consumed as map identities by the System database reader.
                // Map registration is type 0, field 0; filenames stay out of translation.
                if(t==0&&f==0&&text.EndsWith(".mps",StringComparison.OrdinalIgnoreCase)&&!text.Contains('\0'))mapFiles[row]=text.Replace('\\','/');
                // Undecodable row/schema names cannot establish which column
                // is an engine lookup key. Keep that entire type opaque while
                // other independently framed types remain extractable.
                if(opaqueSchema){excluded++;continue;}
                if(type.Kinds[f]!=0||naming.Contains(s)||!DisplayCandidate(text)){excluded++;continue;}
                if(text.Length>6000)throw new ExperimentalRpgCatalog.LimitException("WOLF 数据库文本超过 6000 字符。");
                result.Add(new($"database/type:{t}/row:{row}/field:{f}",text,
                    $"type={type.Name}; row={type.Rows[row]}; field={type.Names[f]}; version=0x{version:x2}"));
            }
        }
        if(r.Byte()!=version)throw new InvalidDataException("WOLF 数据库首尾版本标记不符。");r.End();
        return new(result.ToArray(),utf8?"utf-8":"windows-932",version,excluded){UnreadableStrings=p.UnreadableStrings+r.UnreadableStrings,MapFiles=mapFiles,NumericNames=numericNames};
    }
    private static bool DisplayCandidate(string text)
        =>!string.IsNullOrWhiteSpace(text)&&!text.Contains('\0')&&Regex.IsMatch(text,@"\p{L}")
        &&!Regex.IsMatch(text,@"^(?:[a-z]+://|[^\r\n]+\.(?:png|jpe?g|bmp|gif|ogg|wav|mp3|m4a|mid|mps|dat|project|ttf|otf|exe|dll|zip|wolf)$)",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(100));
    private sealed class Reader
    {
        private readonly byte[] data;private readonly Encoding encoding;private readonly Action check;private readonly bool skipMalformed;private int offset,nodes;
        internal int UnreadableStrings {get;private set;}
        internal Reader(byte[] data,bool utf8,Action check,bool skipMalformed=false)
        {
            this.data=data;this.check=check;this.skipMalformed=skipMalformed;Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding=utf8?new UTF8Encoding(false,true):Encoding.GetEncoding(932,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
        }
        internal void Skip(int count)
        {check();if(++nodes>2000000)throw new ExperimentalRpgCatalog.LimitException("WOLF 数据库读取节点超过限制。");if(count<0||count>data.Length-offset)throw new InvalidDataException("WOLF 数据库截断。");offset+=count;}
        internal byte Byte(){int at=offset;Skip(1);return data[at];}
        internal byte[] Bytes(int count){int at=offset;Skip(count);return data.AsSpan(at,count).ToArray();}
        internal uint Word(){int at=offset;Skip(4);return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at,4));}
        internal int Count(int maximum){uint count=Word();if(count>maximum)throw new ExperimentalRpgCatalog.LimitException("WOLF 数据库声明数量超过支持范围。");return (int)count;}
        internal string String()
        {
            int size=Count(1024*1024);if(size<1)throw new InvalidDataException("WOLF 数据库字符串长度无效。");int at=offset;Skip(size);if(data[offset-1]!=0)throw new InvalidDataException("WOLF 数据库字符串缺少结尾。");
            try{return encoding.GetString(data,at,size-1);}
            catch(DecoderFallbackException)when(skipMalformed){UnreadableStrings++;return "\0unreadable:"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.AsSpan(at,size-1)));}
        }
        internal void Expect(params byte[] expected){foreach(byte b in expected)if(Byte()!=b)throw new InvalidDataException("WOLF 数据库类型标记不符。");}
        internal void End(){if(offset!=data.Length)throw new InvalidDataException("WOLF 数据库含未识别尾部数据。");}
    }
}
