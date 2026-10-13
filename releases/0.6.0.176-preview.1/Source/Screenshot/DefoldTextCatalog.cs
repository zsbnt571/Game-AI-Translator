using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace ScreenshotTranslationUiTester;

// Defold v4 resource names come from its manifest; no script is executed and
// no archive member is extracted to a path supplied by the game.
internal static class DefoldTextCatalog
{
    internal static string? Detect(string exe)
    {
        try
        {
            string root=Path.GetDirectoryName(exe)!;
            if(!File.Exists(Path.Combine(root,"game.projectc"))||!File.Exists(Path.Combine(root,"game.dmanifest"))||!File.Exists(Path.Combine(root,"game.arcd")))return null;
            using var f=File.OpenRead(Path.Combine(root,"game.arci"));Span<byte> header=stackalloc byte[32];f.ReadExactly(header);
            uint version=U32(header,0),count=U32(header,16),entries=U32(header,20),hashes=U32(header,24),hashSize=U32(header,28);
            if(version is not (4 or 5)||count>100000||hashSize is <16 or >64||entries+count*16L>f.Length||hashes+count*64L>f.Length)return null;
            return "Defold";
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){return null;}
    }
    private static uint U32(ReadOnlySpan<byte> b,int p)=>BinaryPrimitives.ReadUInt32BigEndian(b.Slice(p,4));
    private sealed record Wire(int Number,int Kind,ulong NumberValue,byte[] Bytes);
    private static List<Wire> Fields(byte[] data)
    {
        var fields=new List<Wire>();int p=0;
        ulong Var()
        {
            ulong value=0;for(int i=0;i<10;i++){if(p>=data.Length)throw new InvalidDataException("资源字段不完整。");byte b=data[p++];if(i==9&&b>1)throw new InvalidDataException("资源字段过大。");value|=(ulong)(b&127)<<(7*i);if(b<128)return value;}throw new InvalidDataException("资源字段无效。");
        }
        while(p<data.Length)
        {
            if(fields.Count>=100000)throw new InvalidDataException("资源字段过多。");ulong tag=Var();int kind=(int)(tag&7),number=checked((int)(tag>>3));if(number==0)throw new InvalidDataException("资源字段编号无效。");
            if(kind==0){fields.Add(new(number,kind,Var(),[]));continue;}
            int length=kind switch{1=>8,5=>4,2=>checked((int)Var()),_=>throw new InvalidDataException("资源字段类型不支持。")};
            if(length<0||length>data.Length-p)throw new InvalidDataException("资源字段长度无效。");fields.Add(new(number,kind,0,data.AsSpan(p,length).ToArray()));p+=length;
        }
        return fields;
    }
    private static byte[] Single(List<Wire> fields,int number)=>fields.Single(f=>f.Number==number&&f.Kind==2).Bytes;
    private static byte[] ReadBounded(string path,int limit){using var f=File.OpenRead(path);if(f.Length>limit)throw new InvalidDataException("资源超过读取上限。");byte[] bytes=new byte[(int)f.Length];f.ReadExactly(bytes);return bytes;}
    // The standard v4 archive's publicly documented XTEA-CTR obfuscation.
    // Custom keys/formats still fail validation; this never executes Lua.
    internal static byte[] DecodeScript(byte[] stored,Action check)=>ScriptCipher(stored,"aQj8CScgNP4VsfXK"u8,check);
    internal static byte[] ScriptCipher(byte[] stored,ReadOnlySpan<byte> key,Action check)
    {
        if(key.Length!=16)throw new ArgumentException("需要 16 字节资源键。");byte[] bytes=(byte[])stored.Clone();uint[] k=new uint[4];
        for(int i=0;i<4;i++)k[i]=BinaryPrimitives.ReadUInt32BigEndian(key.Slice(i*4,4));
        Span<byte> stream=stackalloc byte[8];
        for(int p=0;p<bytes.Length;p+=8)
        {
            check();uint a=0,b=(uint)(p/8),sum=0;
            unchecked{for(int round=0;round<32;round++){a+=(((b<<4)^(b>>5))+b)^(sum+k[sum&3]);sum+=0x9e3779b9;b+=(((a<<4)^(a>>5))+a)^(sum+k[(sum>>11)&3]);}}
            BinaryPrimitives.WriteUInt32BigEndian(stream,a);BinaryPrimitives.WriteUInt32BigEndian(stream[4..],b);
            for(int j=0;j<8&&p+j<bytes.Length;j++)bytes[p+j]^=stream[j];
        }
        return bytes;
    }
    internal static IEnumerable<(string Location,string Text)> GuiTexts(byte[] bytes,Action check)
    {
        var scene=Fields(bytes);int index=0;
        IEnumerable<Wire> Nodes()=>scene.Where(f=>f.Number==6&&f.Kind==2).Concat(scene.Where(f=>f.Number==9&&f.Kind==2).SelectMany(f=>Fields(f.Bytes).Where(n=>n.Number==2&&n.Kind==2)));
        foreach(var node in Nodes())
        {
            check();var fields=Fields(node.Bytes);int n=index++;
            if(fields.SingleOrDefault(f=>f.Number==6&&f.Kind==0)?.NumberValue!=1)continue;
            var textField=fields.SingleOrDefault(f=>f.Number==8&&f.Kind==2);if(textField is null)continue;
            string text=new UTF8Encoding(false,true).GetString(textField.Bytes);
            if(text.Length is >0 and <=6000&&RpgTranslationText.HasTranslatableText(text,EmbeddedTextSyntax.Generic))yield return("gui-node:"+n,text);
        }
    }
    internal static byte[] Decompress(byte[] data,int size)
    {
        if(size<0||size>16*1024*1024)throw new InvalidDataException("资源展开大小超过限制。");byte[] output=new byte[size];int p=0,q=0;
        int Length(int value){if(value!=15)return value;byte b;do{if(p>=data.Length)throw new InvalidDataException("压缩长度不完整。");b=data[p++];value=checked(value+b);}while(b==255);return value;}
        while(p<data.Length)
        {
            int token=data[p++],literals=Length(token>>4);if(literals>data.Length-p||literals>size-q)throw new InvalidDataException("压缩内容超出范围。");data.AsSpan(p,literals).CopyTo(output.AsSpan(q));p+=literals;q+=literals;if(p==data.Length)break;
            if(p+2>data.Length)throw new InvalidDataException("压缩引用不完整。");int offset=data[p]|data[p+1]<<8;p+=2;int count=checked(Length(token&15)+4);
            if(offset<=0||offset>q||count>size-q)throw new InvalidDataException("压缩引用超出范围。");for(int i=0;i<count;i++){output[q]=output[q-offset];q++;}
        }
        if(q!=size)throw new InvalidDataException("资源展开长度不符。");return output;
    }
    internal static IEnumerable<(int Offset,string Text)> LuaTexts(string code,Action check)
    {
        int p=0;
        bool LongStart(int at,out int equals,out int end){equals=0;end=at;if(at>=code.Length||code[at]!='[')return false;end=at+1;while(end<code.Length&&code[end]=='='){equals++;end++;}return end<code.Length&&code[end++]=='[';}
        string? Literal()
        {
            if(p>=code.Length)return null;
            if(LongStart(p,out int eq,out int start)){string close="]"+new string('=',eq)+"]";int end=code.IndexOf(close,start,StringComparison.Ordinal);if(end<0)throw new InvalidDataException("Lua 长文本不完整。");p=end+close.Length;return code[start..end].TrimStart('\r','\n');}
            char quote=code[p];if(quote is not ('\'' or '"'))return null;p++;var s=new StringBuilder();
            while(p<code.Length)
            {
                char c=code[p++];if(c==quote)return s.ToString();if(c!='\\'){s.Append(c);continue;}if(p>=code.Length)break;c=code[p++];
                if(char.IsAsciiDigit(c)){int value=c-'0',n=1;while(n++<3&&p<code.Length&&char.IsAsciiDigit(code[p]))value=value*10+code[p++]-'0';if(value>255)throw new InvalidDataException("Lua 转义无效。");s.Append((char)value);}
                else s.Append(c switch{'n'=>'\n','r'=>'\r','t'=>'\t','a'=>'\a','b'=>'\b','f'=>'\f','v'=>'\v',_=>c});
            }
            throw new InvalidDataException("Lua 引号文本不完整。");
        }
        while(p<code.Length)
        {
            check();char c=code[p];
            if(c=='-'&&p+1<code.Length&&code[p+1]=='-'){p+=2;if(LongStart(p,out _,out _))Literal();else{int end=code.IndexOf('\n',p);p=end<0?code.Length:end+1;}continue;}
            if(c is '\'' or '"'||LongStart(p,out _,out _)){Literal();continue;}
            if(!char.IsAsciiLetter(c)&&c!='_'){p++;continue;}
            int begin=p++;while(p<code.Length&&(char.IsAsciiLetterOrDigit(code[p])||code[p]=='_'))p++;string key=code[begin..p];
            if(key is not ("text" or "caption" or "label" or "title" or "message" or "description"))continue;
            while(p<code.Length&&char.IsWhiteSpace(code[p]))p++;if(p>=code.Length||code[p++]!='=')continue;
            while(p<code.Length&&char.IsWhiteSpace(code[p]))p++;int offset=p;string? value=Literal();
            // A literal is a candidate only as a complete value, not the first
            // fragment of a computed expression (such as "Hello " .. name).
            int next=p;while(next<code.Length&&char.IsWhiteSpace(code[next]))next++;
            if(value is {Length:>0 and <=6000}&&(next>=code.Length||code[next] is ',' or '}' or ';')&&RpgTranslationText.HasTranslatableText(value,EmbeddedTextSyntax.Generic))yield return(offset,value);
        }
    }
    internal static ExperimentalRpgCatalog.Result Read(string exe,CancellationToken token=default)
    {
        string root=Path.GetDirectoryName(exe)!;var texts=new List<ExperimentalRpgCatalog.Entry>();var reports=new List<ExperimentalRpgCatalog.FileResult>();var timer=Stopwatch.StartNew();
        void Check(){token.ThrowIfCancellationRequested();if(timer.Elapsed.TotalSeconds>20||texts.Count>=100000)throw new InvalidDataException("达到读取时间或数量上限。");}
        byte[] index=ReadBounded(Path.Combine(root,"game.arci"),16*1024*1024);if(index.Length<32||U32(index,0)!=4)throw new InvalidDataException("当前只读提取支持 Defold v4 资源索引。");
        int count=checked((int)U32(index,16)),entryOffset=checked((int)U32(index,20)),hashOffset=checked((int)U32(index,24)),hashSize=checked((int)U32(index,28));
        if(count>100000||hashSize is <16 or >64||entryOffset<32||hashOffset<32||entryOffset+count*16L>index.Length||hashOffset+count*64L>index.Length)throw new InvalidDataException("资源索引范围无效。");
        var entries=new Dictionary<string,(uint Offset,uint Size,uint Compressed,uint Flags)>();
        // Separate logical resources can share the same content hash while the
        // bundle keeps identical copies at different offsets.
        for(int i=0;i<count;i++){int at=entryOffset+i*16;string key=Convert.ToHexString(index.AsSpan(hashOffset+i*64,hashSize));var entry=(Offset:U32(index,at),Size:U32(index,at+4),Compressed:U32(index,at+8),Flags:U32(index,at+12));if(entries.TryGetValue(key,out var existing)&&existing.Size!=entry.Size)throw new InvalidDataException("资源内容标识冲突。");entries.TryAdd(key,entry);}
        var manifest=Fields(Single(Fields(ReadBounded(Path.Combine(root,"game.dmanifest"),32*1024*1024)),1));
        using var archive=File.OpenRead(Path.Combine(root,"game.arcd"));long readBytes=0;
        foreach(var resource in manifest.Where(f=>f.Number==3&&f.Kind==2))
        {
            Check();var fields=Fields(resource.Bytes);string name=new UTF8Encoding(false,true).GetString(Single(fields,1));
            if(name.Length>512||!name.StartsWith('/')||name.Contains('\\')||name.Split('/').Any(s=>s is "." or ".."))throw new InvalidDataException("资源名称无效。");
            bool compiled=name.EndsWith(".scriptc",StringComparison.Ordinal)||name.EndsWith(".gui_scriptc",StringComparison.Ordinal)||name.EndsWith(".luac",StringComparison.Ordinal);
            bool gui=name.EndsWith(".guic",StringComparison.Ordinal),font=name.EndsWith(".fontc",StringComparison.Ordinal);
            if(!compiled&&!gui&&!font&&!name.EndsWith(".script",StringComparison.Ordinal)&&!name.EndsWith(".lua",StringComparison.Ordinal))continue;
            if(reports.Count>=2000)throw new InvalidDataException("脚本数量超过限制。");
            int before=texts.Count;
            try
            {
                string hash=Convert.ToHexString(Single(Fields(Single(fields,3)),1));if(!entries.TryGetValue(hash,out var e))throw new InvalidDataException("资源引用未找到。");
                if((e.Flags&~1u)!=0)throw new InvalidDataException("此资源包含尚未支持的标志。");
                long stored=e.Compressed==uint.MaxValue?e.Size:e.Compressed;
                if(e.Size>16*1024*1024||stored>16*1024*1024||e.Offset>archive.Length-stored||(readBytes+=e.Size)>128*1024*1024)throw new InvalidDataException("资源范围或大小超过限制。");
                archive.Position=e.Offset;byte[] bytes=new byte[(int)stored];archive.ReadExactly(bytes);if((e.Flags&1)!=0)bytes=DecodeScript(bytes,Check);if(e.Compressed!=uint.MaxValue)bytes=Decompress(bytes,(int)e.Size);
                if(font)
                {
                    int chinese=Fields(bytes).Where(f=>f.Number==1&&f.Kind==2).Select(f=>Fields(f.Bytes).SingleOrDefault(v=>v.Number==1&&v.Kind==0)?.NumberValue??0).Count(v=>v is >=0x3400 and <=0x9fff);
                    reports.Add(new(name,"字库检查",0,chinese==0?"未包含中文字形；译文不能直接显示为中文。":$"包含 {chinese} 个中文字形；尚未确认译文字符是否齐全。"));continue;
                }
                if(gui)
                {
                    foreach(var pair in GuiTexts(bytes,Check))texts.Add(new(name,pair.Location,pair.Text));
                    reports.Add(new(name,"读取完成",texts.Count-before,"已读取文本节点；节点标识、贴图和布局参数保持不变。"));continue;
                }
                if(compiled)
                {
                    var source=Fields(Single(Fields(bytes),1));var script=source.SingleOrDefault(f=>f.Number==1&&f.Kind==2);
                    if(script is null||script.Bytes.Length==0)throw new InvalidDataException("此脚本只有编译后的字节码，未执行或猜测其中的字符串用途。");bytes=script.Bytes;
                }
                string code=new UTF8Encoding(false,true).GetString(bytes);if(code.Contains('\0'))throw new InvalidDataException("编译后的脚本未按明文解析。");
                foreach(var pair in LuaTexts(code,Check))texts.Add(new(name,"char:"+pair.Offset,pair.Text));
                reports.Add(new(name,"读取完成",texts.Count-before,"已读取文本字段；动态表达式未执行。"));
            }
            catch(Exception e)when(e is InvalidDataException or DecoderFallbackException or IOException or InvalidOperationException){reports.Add(new(name,"未完整读取",texts.Count-before,e.Message));}
        }
        return new("Defold",texts.Distinct().ToArray(),reports.ToArray(),false,"已支持的 Lua 文本和界面文本可准备译文；中文字库、运行时写回和游戏内显示尚未接入。");
    }
}
