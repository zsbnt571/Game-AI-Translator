using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Inert Marshal nodes + bounded zlib + a restricted Ruby lexer. No Ruby VM,
// Marshal.load, eval, require, script initialization or game code is invoked.
internal static class RgssScriptCatalog
{
    internal sealed record Field(string Location,string Text,string Encoding,string Context);
    internal sealed record Result(Field[] Fields,string[] Issues);
    internal static Result Read(RubyMarshalData.Node document,Action check)
    {
        if(document.Kind!='['||document.Items.Count>10000)throw new InvalidDataException("RGSS 脚本列表结构无效。");
        foreach(var row in document.Items)
            if(row.Kind!='['||row.Items.Count!=3||row.Items[0].Kind!='i'||row.Items[1].Kind!='"'||row.Items[2].Kind!='"')
                throw new InvalidDataException("RGSS 脚本条目应为编号、名称和压缩正文。");
        List<Field> fields=[];List<string> issues=[];long expanded=0;
        for(int i=0;i<document.Items.Count;i++)
        {
            check();var row=document.Items[i];
            try
            {
                byte[] image=Inflate(row.Items[2].Bytes,check);
                if((expanded+=image.Length)>64L*1024*1024)throw new ExperimentalRpgCatalog.LimitException("RGSS 脚本累计展开超过 64 MB。");
                var decoded=Decode(image);string hash=CatalogOrigins.Hash(image);
                string label;
                try{label=Decode(row.Items[1].Bytes).Code;}catch(DecoderFallbackException){label="";}
                label=label.Length>160?label[..160]:label;
                var values=DisplayFields(decoded.Code,decoded.Encoding,check);
                foreach(var value in values)
                {
                    check();if(fields.Count>=100000)throw new ExperimentalRpgCatalog.LimitException("RGSS 脚本显示字段超过限制。");
                    fields.Add(new($"script:{i}/id:{row.Items[0].Number}/line:{value.Line}/char:{value.Offset}",
                        value.Text,decoded.Encoding.WebName,$"script={label}; expanded-sha256={hash}; sink={value.Sink}"));
                }
            }
            catch(ExperimentalRpgCatalog.LimitException){throw;}
            catch(Exception e)when(e is InvalidDataException or NotSupportedException or DecoderFallbackException or EncoderFallbackException)
            {if(issues.Count<128)issues.Add($"script:{i}: {SafeDiagnosticOutput.ExceptionSummary(e)}");}
        }
        return new(fields.ToArray(),issues.ToArray());
    }

    // One-byte input prevents read-ahead from disguising extra members/tails.
    private sealed class DeflateInput(byte[] data):MemoryStream(data,2,data.Length-6,false)
    {
        public override int Read(byte[] buffer,int offset,int count)=>base.Read(buffer,offset,Math.Min(count,1));
        public override int Read(Span<byte> buffer)=>base.Read(buffer[..Math.Min(buffer.Length,1)]);
    }
    internal static byte[] Inflate(byte[] stored,Action check)
    {
        if(stored.Length is <6 or >4*1024*1024)throw new InvalidDataException("RGSS 压缩脚本长度无效。");
        int header=stored[0]*256+stored[1];
        if((stored[0]&15)!=8||(stored[0]>>4)>7||header%31!=0||(stored[1]&32)!=0)
            throw new NotSupportedException("RGSS 脚本不是已支持的无字典 zlib 数据。");
        using var input=new DeflateInput(stored);using var output=new MemoryStream();
        using(var deflate=new DeflateStream(input,CompressionMode.Decompress,true))
        {
            byte[] buffer=new byte[8192];int count;uint a=1,b=0;
            while((count=deflate.Read(buffer))>0)
            {
                check();if(output.Length+count>2*1024*1024)throw new ExperimentalRpgCatalog.LimitException("单份 RGSS 脚本展开超过 2 MB。");
                output.Write(buffer,0,count);
                for(int i=0;i<count;i++){a=(a+buffer[i])%65521;b=(b+a)%65521;}
            }
            if(input.Position!=input.Length||((b<<16)|a)!=BinaryPrimitives.ReadUInt32BigEndian(stored.AsSpan(stored.Length-4)))
                throw new InvalidDataException("RGSS 脚本压缩尾部或校验不一致。");
        }
        return output.ToArray();
    }
    private static (string Code,Encoding Encoding) Decode(byte[] image)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var utf8=new UTF8Encoding(false,true);
        int start=image.AsSpan().StartsWith(new byte[]{0xef,0xbb,0xbf})?3:0;
        int end=Array.IndexOf(image,(byte)'\n',start);if(end<0)end=image.Length;
        string first=Encoding.ASCII.GetString(image,start,end-start);
        if(first.StartsWith("#!",StringComparison.Ordinal)&&end<image.Length)
        {int next=Array.IndexOf(image,(byte)'\n',end+1);first=Encoding.ASCII.GetString(image,end+1,(next<0?image.Length:next)-end-1);}
        var declared=Regex.Match(first,@"^\s*#.*?(?:coding|encoding)\s*[:=]\s*([A-Za-z0-9_-]+)",RegexOptions.CultureInvariant);
        Encoding encoding=utf8;
        if(declared.Success)
        {
            string name=declared.Groups[1].Value.ToLowerInvariant();
            encoding=name switch{
                "utf-8" or "utf8"=>utf8,
                "windows-31j" or "cp932" or "shift_jis" or "sjis"=>Encoding.GetEncoding(932,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback),
                "us-ascii" or "ascii"=>Encoding.GetEncoding("us-ascii",EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback),
                _=>throw new NotSupportedException("RGSS 脚本声明的编码尚未支持。")};
            if(start!=0&&encoding.CodePage!=65001)throw new InvalidDataException("RGSS 脚本 BOM 与编码声明不一致。");
        }
        try{return(encoding.GetString(image,start,image.Length-start),encoding);}
        catch(DecoderFallbackException)when(!declared.Success&&start==0)
        {encoding=Encoding.GetEncoding(932,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);return(encoding.GetString(image),encoding);}
    }

    internal sealed record DisplayField(int Offset,int Line,string Text,string Sink);
    private sealed record Token(string Value,char Kind,int Offset,int Line);
    private static bool Is(Token t,string value)=>t.Kind=='p'&&t.Value==value;
    private static readonly HashSet<string> VocabNames=new(StringComparer.Ordinal){
        "ShopBuy","ShopSell","ShopCancel","Possession","ExpTotal","ExpNext","SaveMessage","LoadMessage",
        "File","PartyName","Emerge","Preemptive","Surprise","EscapeStart","EscapeFailure","Victory",
        "Defeat","ObtainExp","ObtainGold","ObtainItem","LevelUp","ObtainSkill","UseItem","CriticalToEnemy",
        "CriticalToActor","ActorDamage","ActorRecovery","ActorGain","ActorLoss","ActorDrain","ActorNoDamage",
        "ActorNoHit","EnemyDamage","EnemyRecovery","EnemyGain","EnemyLoss","EnemyDrain","EnemyNoDamage",
        "EnemyNoHit","Evasion","MagicEvasion","MagicReflection","CounterAttack","Substitute","BuffAdd",
        "DebuffAdd","BuffRemove","ActionFailure","PlayerPos","NewGame","Continue","Shutdown","ToTitle",
        "Cancel","AlwaysDash","CommandRemember","BgmVolume","BgsVolume","MeVolume","SeVolume"};
    internal static DisplayField[] DisplayFields(string code,Encoding encoding,Action check)
    {
        var tokens=new Lexer(code,encoding,check).Read();List<DisplayField> fields=[];
        void Add(int start,int end,string sink)
        {
            var value=Literal(tokens,start,end);
            if(value is {Length:>0 and <=6000}&&!value.Contains('\0')&&RpgTranslationText.HasTranslatableText(value,EmbeddedTextSyntax.Rpg))
                fields.Add(new(tokens[start].Offset,tokens[start].Line,value,sink));
        }
        bool vocab=false;
        for(int i=0;i<tokens.Count;i++)
        {
            check();var t=tokens[i];
            bool lineStart=i==0||tokens[i-1].Kind=='p'&&tokens[i-1].Value is "\n" or ";";
            if(lineStart&&t.Value=="module"&&t.Kind=='i'&&i+2<tokens.Count&&tokens[i+1].Value=="Vocab"&&Is(tokens[i+2],"\n"))
            {vocab=true;i+=2;continue;}
            if(vocab&&lineStart&&!Is(t,"\n"))
            {
                int end=i;while(end<tokens.Count&&!(tokens[end].Kind=='p'&&tokens[end].Value is "\n" or ";"))end++;
                if(i+2<end&&tokens[i].Kind=='i'&&Is(tokens[i+1],"="))
                {if(VocabNames.Contains(t.Value))Add(i+2,end,"Vocab."+t.Value);i=end-1;continue;}
                vocab=false;
            }
            if(t.Kind!='i'||i+1>=tokens.Count||!Is(tokens[i+1],"("))continue;
            int line=i;while(line>0&&!(tokens[line-1].Kind=='p'&&tokens[line-1].Value is "\n" or ";"))line--;
            if(tokens.Skip(line).Take(i-line).Any(x=>x.Kind=='i'&&x.Value=="def")||i>0&&tokens[i-1].Kind=='p'&&tokens[i-1].Value is ":" or "::")continue;
            if(t.Value is not ("draw_text" or "draw_text_ex" or "set_text" or "add" or "add_command"))continue;
            int close=Matching(tokens,i+1);var arguments=Arguments(tokens,i+2,close);
            int index=-1;
            if(t.Value=="draw_text"&&arguments.Count is 5 or 6)index=4;
            else if(t.Value=="draw_text"&&arguments.Count is 2 or 3)index=1;
            else if(t.Value=="draw_text_ex"&&arguments.Count==3)index=2;
            else if(t.Value=="add_command"&&arguments.Count is >=2 and <=4)index=0;
            else if(t.Value=="set_text"&&arguments.Count==1&&i>=2&&Is(tokens[i-1],".")&&tokens[i-2].Value.EndsWith("window",StringComparison.Ordinal))index=0;
            else if(t.Value=="add"&&arguments.Count==1&&i>=2&&Is(tokens[i-1],".")&&tokens[i-2].Value=="$game_message")index=0;
            if(index>=0){var arg=arguments[index];if(arg.Start<arg.End)Add(arg.Start,arg.End,t.Value);}
        }
        return fields.Distinct().ToArray();
    }
    private static int Matching(List<Token> tokens,int open)
    {
        var stack=new Stack<string>();stack.Push(tokens[open].Value);
        for(int p=open+1;p<tokens.Count;p++)
        {
            if(tokens[p].Kind!='p')continue;string t=tokens[p].Value;
            if(t is "(" or "[" or "{"){if(stack.Count>=128)throw new InvalidDataException("Ruby 嵌套达到限制。");stack.Push(t);}
            else if(t is ")" or "]" or "}")
            {if(stack.Count==0||stack.Pop()!=(t==")"?"(":t=="]"?"[":"{"))throw new InvalidDataException("Ruby 括号不匹配。");if(stack.Count==0)return p;}
        }
        throw new InvalidDataException("Ruby 显示调用未闭合。");
    }
    private static List<(int Start,int End)> Arguments(List<Token> tokens,int start,int end)
    {
        var result=new List<(int,int)>();int p=start;
        while(p<end)
        {
            if(tokens[p].Kind=='p'&&tokens[p].Value is "(" or "[" or "{"){p=Matching(tokens,p)+1;continue;}
            if(Is(tokens[p],",")){result.Add((start,p));start=p+1;}p++;
        }
        if(start<end)result.Add((start,end));return result;
    }
    private static string? Literal(List<Token> tokens,int start,int end,int depth=0)
    {
        if(depth>32)return null;
        while(start<end&&Is(tokens[start],"\n"))start++;
        while(start<end&&Is(tokens[end-1],"\n"))end--;
        if(start>=end)return null;
        if(Is(tokens[start],"(")&&Matching(tokens,start)==end-1)return Literal(tokens,start+1,end-1,depth+1);
        var text=new StringBuilder();bool expect=true;
        for(int p=start;p<end;p++)
        {
            if(Is(tokens[p],"\n"))continue;
            if(expect&&tokens[p].Kind=='s'){text.Append(tokens[p].Value);expect=false;}
            else if(!expect&&Is(tokens[p],"+"))expect=true;
            else return null;
            if(text.Length>6000)return null;
        }
        return expect?null:text.ToString();
    }

    private sealed class Lexer(string code,Encoding encoding,Action check)
    {
        private int p,line=1;private readonly List<Token> tokens=[];
        private void Tick(){check();if(tokens.Count>=250000||code.Length>2*1024*1024)throw new ExperimentalRpgCatalog.LimitException("Ruby 脚本词法读取达到限制。");}
        private string? Quoted(char quote,int depth=0)
        {
            if(depth>32)throw new InvalidDataException("Ruby 插值嵌套达到限制。");
            p++;using var bytes=new MemoryStream();bool dynamic=quote=='\u0060';
            void Append(string s){bytes.Write(encoding.GetBytes(s));if(bytes.Length>24000)dynamic=true;}
            while(p<code.Length)
            {
                Tick();char c=code[p++];
                if(c==quote)
                {
                    if(dynamic)return null;
                    try{return encoding.GetString(bytes.ToArray());}catch(DecoderFallbackException){return null;}
                }
                if(c=='\n')line++;
                if(quote!='\''&&c=='#'&&p<code.Length&&code[p]=='{')
                {
                    dynamic=true;p++;int braces=1;
                    while(p<code.Length&&braces>0)
                    {
                        Tick();c=code[p];
                        if(c is '\'' or '"' or '\u0060'){Quoted(c,depth+1);continue;}
                        if(c=='#'){while(p<code.Length&&code[p]!='\n')p++;continue;}
                        if(c is '/' or '%')throw new NotSupportedException("复杂 Ruby 插值保留为运行时采集。");
                        p++;if(c=='{')braces++;else if(c=='}')braces--;else if(c=='\n')line++;
                        if(braces>64)throw new InvalidDataException("Ruby 插值括号达到限制。");
                    }
                    if(braces!=0)throw new InvalidDataException("Ruby 插值未闭合。");continue;
                }
                if(quote!='\''&&c=='#'&&p<code.Length&&code[p] is '@' or '$'){dynamic=true;continue;}
                if(c!='\\')
                {if(char.IsHighSurrogate(c)&&p<code.Length&&char.IsLowSurrogate(code[p]))Append(new string([c,code[p++]]));else Append(c.ToString());continue;}
                if(p>=code.Length)break;c=code[p++];
                if(quote=='\''){Append(c is '\\' or '\''?c.ToString():"\\"+c);continue;}
                if(c=='\r'||c=='\n'){if(c=='\r'&&p<code.Length&&code[p]=='\n')p++;line++;continue;}
                if(c is >= '0' and <= '7')
                {int value=c-'0';for(int i=1;i<3&&p<code.Length&&code[p] is >= '0' and <= '7';i++)value=value*8+code[p++]-'0';bytes.WriteByte((byte)value);continue;}
                if(c=='x')
                {int value=0,n=0;while(n<2&&p<code.Length&&Uri.IsHexDigit(code[p])){value=value*16+Convert.ToInt32(code[p++].ToString(),16);n++;}if(n==0)throw new InvalidDataException("Ruby 十六进制转义无效。");bytes.WriteByte((byte)value);continue;}
                if(c=='u')
                {
                    if(encoding.CodePage!=65001||p+4>code.Length||!code.AsSpan(p,4).ToArray().All(Uri.IsHexDigit))
                        throw new NotSupportedException("Ruby Unicode 转义形式尚未支持。");
                    Append(((char)Convert.ToInt32(code.Substring(p,4),16)).ToString());p+=4;continue;
                }
                if(c is 'c' or 'C' or 'M')throw new NotSupportedException("Ruby 控制字符转义形式尚未支持。");
                Append(c switch{'n'=>"\n",'r'=>"\r",'t'=>"\t",'a'=>"\a",'b'=>"\b",'e'=>"\x1b",'f'=>"\f",'v'=>"\v",'s'=>" ",_=>c.ToString()});
            }
            throw new InvalidDataException("Ruby 引号文本未闭合。");
        }
        private void Percent()
        {
            p++;if(p<code.Length&&char.IsAsciiLetter(code[p]))p++;
            if(p>=code.Length||char.IsLetterOrDigit(code[p])||char.IsWhiteSpace(code[p]))throw new NotSupportedException("Ruby 百分号字面量不明确。");
            char open=code[p++],close=open switch{'('=>')','['=>']','{'=>'}','<'=>'>',_=>open};int level=1;
            while(p<code.Length)
            {
                Tick();char c=code[p++];if(c=='\\'){if(p<code.Length){if(code[p]=='\n')line++;p++;}continue;}
                if(c=='\n')line++;if(c==close){if(--level==0)return;}else if(c==open&&open!=close)level++;
                if(level>128)throw new InvalidDataException("Ruby 百分号字面量嵌套达到限制。");
            }
            throw new InvalidDataException("Ruby 百分号字面量未闭合。");
        }
        internal List<Token> Read()
        {
            while(p<code.Length)
            {
                Tick();char c=code[p];int at=p,ln=line;
                void Add(string value,char kind='p')=>tokens.Add(new(value,kind,at,ln));
                if(c=='\r'){p++;continue;}
                if(c=='\n'){p++;line++;Add("\n");continue;}
                if(char.IsWhiteSpace(c)){p++;continue;}
                if(c=='#'){while(p<code.Length&&code[p]!='\n')p++;continue;}
                if((p==0||code[p-1]=='\n')&&code.AsSpan(p).StartsWith("=begin")&&(p+6==code.Length||char.IsWhiteSpace(code[p+6])))
                {
                    int end=code.IndexOf("\n=end",p+6,StringComparison.Ordinal);
                    if(end<0)throw new InvalidDataException("Ruby 文档注释未闭合。");
                    int next=code.IndexOf('\n',end+5);if(next<0)next=code.Length;
                    line+=code.AsSpan(p,next-p).Count('\n');p=next;continue;
                }
                if(c is '\'' or '"' or '\u0060'){string? value=Quoted(c);Add(value??"",value is null?'v':'s');continue;}
                if(c=='%'&&p+1<code.Length&&(code[p+1] is 'q' or 'Q' or 'w' or 'W' or 'r' or 'x' or 'i' or 'I' or '(' or '[' or '{' or '<'))
                {Percent();Add("dynamic",'v');continue;}
                if(c=='/'&&(tokens.Count==0||tokens[^1].Value is "\n" or ";" or "=" or "=~" or "!~" or "(" or "," or "[" or "{" or "return" or "when"))
                {
                    p++;bool bracket=false,closed=false;
                    while(p<code.Length)
                    {Tick();char r=code[p++];if(r=='\\'){if(p<code.Length)p++;continue;}if(r=='\n')line++;if(r=='[')bracket=true;else if(r==']')bracket=false;else if(r=='/'&&!bracket){closed=true;break;}}
                    if(!closed)throw new InvalidDataException("Ruby 正则字面量未闭合。");
                    while(p<code.Length&&char.IsAsciiLetter(code[p]))p++;Add("regexp",'v');continue;
                }
                if(c=='/')throw new NotSupportedException("Ruby 除法/命令式正则歧义保留为运行时采集。");
                if(c=='<'&&p+1<code.Length&&code[p+1]=='<')
                    throw new NotSupportedException("含 Ruby heredoc/位移的脚本保留为运行时采集。");
                if(char.IsLetter(c)||c is '_' or '@' or '$')
                {
                    p++;while(p<code.Length&&(char.IsLetterOrDigit(code[p])||code[p] is '_' or '@' or '!' or '?'))p++;
                    Add(code[at..p],'i');continue;
                }
                if(char.IsDigit(c)){p++;while(p<code.Length&&(char.IsLetterOrDigit(code[p])||code[p]=='.'))p++;Add(code[at..p],'n');continue;}
                if(p+1<code.Length&&code.Substring(p,2) is "::" or "=~" or "!~" or "=>" or "==" or "!="){p+=2;Add(code[at..p]);continue;}
                p++;Add(c.ToString());
            }
            return tokens;
        }
    }
}
