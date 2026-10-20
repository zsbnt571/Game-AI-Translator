using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Read-only implementation of the WOLF command layout documented by WolfTL
// (Sinflower, MIT). Display commands and literal text feeding them grant text
// eligibility. Conditions, file names and scripts are not display candidates;
// no runtime variable or database value is rewritten by this reader.
internal static class WolfTextCatalog
{
    internal sealed record TextField(string Location,string Text);
    internal sealed record MapCommand(int Index,int Code,int[] Arguments,string[] Texts);
    internal sealed record MapPage(int Index,MapCommand[] Commands);
    internal sealed record MapEvent(int Id,string Name,int X,int Y,MapPage[] Pages)
    {public override string ToString()=>$"{Id:000} · {Name} ({X}, {Y})";}
    internal sealed record MapStructure(string Name,int Width,int Height,MapEvent[] Events);
    internal static TextField[] ReadGame(byte[] bytes,Action check)
    {
        // Game.dat contains title/startup display fields alongside fonts,
        // graphic paths and an opaque key. Never expose the latter to prompts.
        if(bytes.Length<10||bytes.Length>4*1024*1024||!bytes.AsSpan(0,9).SequenceEqual(new byte[]{0,0x57,0,0,0x4f,0x4c,0,0x46,0x4d})||bytes[9] is not (0 or 0x55))
            throw new NotSupportedException("WOLF Game.dat 头或加密格式尚未支持。");
        var r=new Reader(bytes[10..],bytes[9]==0x55,check);var fields=new List<TextField>();
        r.Skip(r.Count(1024*1024));int count=r.Count(14);if(count is not (8 or 9 or 13 or 14))throw new NotSupportedException("WOLF Game.dat 字符串布局尚未支持。");
        void Display(string location){string text=r.String();if(text.Length is >0 and <=6000&&RpgTranslationText.HasTranslatableText(text,EmbeddedTextSyntax.Wolf))fields.Add(new(location,text));}
        Display("game/title");if(r.String()!="0000-0000")throw new InvalidDataException("WOLF Game.dat 标记不符。");
        r.Skip(r.Count(1024*1024));for(int i=3;i<=7;i++)r.String();
        if(count>=9)Display("game/title-plus");
        if(count>9){r.String();r.String();Display("game/startup-message");Display("game/title-message");}
        if(count>13)r.String();
        r.Int();r.Int();r.Skip(checked(r.Count(1000000)*2));r.Int();r.Int();r.Skip(r.Remaining);
        return fields.ToArray();
    }
    internal static TextField[] Read(byte[] bytes,bool map,Action check,Action<MapStructure>? observeMap=null)
    {
        if(bytes.Length>32*1024*1024)throw new InvalidDataException("WOLF 文件超过读取限制。");
        static uint U32(byte[] b,int p)=>p>=0&&p<=b.Length-4?BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p,4)):throw new InvalidDataException("WOLF 文件截断。");
        byte[] Unpack(int offset)
        {
            uint size=U32(bytes,offset),stored=U32(bytes,offset+4);
            if(size>16*1024*1024||stored!=bytes.Length-offset-8L)throw new InvalidDataException("WOLF 压缩范围无效。");
            return DefoldTextCatalog.Decompress(bytes.AsSpan(offset+8,(int)stored).ToArray(),(int)size);
        }
        var fields=new List<TextField>();Reader r;bool modern;MapStructure? structure=null;
        if(map)
        {
            if(bytes.Length<26||!bytes.AsSpan(10,5).SequenceEqual("WOLFM"u8)||bytes.Take(10).Any(b=>b!=0)||bytes[16] is not (0 or 0x55))throw new InvalidDataException("WOLF 地图头或加密格式尚未支持。");
            uint version=U32(bytes,20);if(version<100||version>103)throw new InvalidDataException("WOLF 地图版本尚未支持。");
            modern=version>=103;r=new(version>=101?Unpack(25):bytes[25..],bytes[16]==0x55,check);
            string mapName=r.String();r.Int();int width=r.Count(8192),height=r.Count(8192),events=r.Count(50000),layers=3;
            var mapEvents=new List<MapEvent>();
            if(modern){r.Int();layers=r.Count(16);}
            if(r.Remaining>1)
            {
                if(r.Utf8&&r.Peek()==-1)r.Int();else r.Skip(checked(width*height*layers*4));
                for(int i=0;i<events;i++)
                {
                    r.Expect(0x6f);r.Expect(0x39,0x30,0,0);int id=r.Int();string eventName=r.String();int x=r.Int(),y=r.Int();int pages=r.Count(4096);r.Expect(0,0,0,0);
                    var eventPages=new List<MapPage>();
                    for(int page=0;page<pages;page++)
                    {
                        r.Expect(0x79);r.Int();r.String();r.Skip(4+37+4+2);Routes();
                        var commands=observeMap is null?null:new List<MapCommand>();Commands($"event:{id}/page:{page}",commands);
                        if(commands is not null)eventPages.Add(new(page,commands.ToArray()));
                        int features=r.Int();r.Skip(3);if(features>3)r.Skip(1);r.Expect(0x7a);
                    }
                    r.Expect(0x70);
                    if(observeMap is not null)mapEvents.Add(new(id,eventName,x,y,eventPages.ToArray()));
                }
            }
            else if(events!=0)throw new InvalidDataException("WOLF 地图事件数量不符。");
            r.Expect(0x66);
            structure=new(mapName,width,height,mapEvents.ToArray());
        }
        else
        {
            if(bytes.Length<12||!bytes.AsSpan(0,6).SequenceEqual(new byte[]{0,0x57,0,0,0x4f,0x4c})||bytes[6] is not (0 or 0x55)||!bytes.AsSpan(7,3).SequenceEqual(new byte[]{0x46,0x43,0}))throw new InvalidDataException("WOLF 公共事件头或加密格式尚未支持。");
            byte version=bytes[10];modern=version is 0x93 or 0xcc;
            if(!modern&&version is not (0x8f or 0xc9))throw new InvalidDataException("WOLF 公共事件版本尚未支持。");
            r=new(modern?Unpack(11):bytes[11..],bytes[6]==0x55,check);
            int count=r.Count(50000);
            for(int i=0;i<count;i++)
            {
                r.Expect(0x8e);int id=r.Int();r.Skip(11);r.String();Commands($"common:{id}");r.String();r.String();r.Expect(0x8f);
                Strings();r.Skip(r.Count());int groups=r.Count(4096);for(int g=0;g<groups;g++)Strings();
                groups=r.Count(4096);for(int g=0;g<groups;g++)r.Skip(checked(r.Count()*4));
                r.Skip(29);for(int j=0;j<100;j++)r.String();r.Expect(0x91);r.String();byte end=r.Byte();
                if(end==0x92){r.String();r.Int();r.Expect(0x92);}else if(end!=0x91)throw new InvalidDataException("WOLF 公共事件结尾无效。");
            }
            if(r.Byte()<0x89)throw new InvalidDataException("WOLF 公共事件尾标无效。");
        }
        if(r.Remaining!=0)throw new InvalidDataException("WOLF 文件含尚未识别的数据，未采纳该文件的文本。");
        if(structure is not null)observeMap?.Invoke(structure);
        return fields.ToArray();

        void Strings(){int count=r.Count(100000);for(int i=0;i<count;i++)r.String();}
        void Routes(){int count=r.Count(100000);for(int i=0;i<count;i++){r.Byte();r.Skip(r.Byte()*4);r.Expect(1,0);}}
        void Commands(string location,List<MapCommand>? captured=null)
        {
            int count=r.Count(100000);
            var databaseWrites=new List<(int Index,int[] Args,string Text)>();
            var databaseVariableWrites=new List<(int Index,int[] Args)>();
            var assignments=new List<(int Index,int Variable,string Text)>();
            var assignmentFields=new Dictionary<int,int>();
            var databaseReads=new List<int[]>();var displays=new List<string>();
            for(int i=0;i<count;i++)
            {
                int n=r.Byte()-1;if(n<0)throw new InvalidDataException("WOLF 指令参数数量无效。");int command=r.Int();int[] args=new int[n];for(int a=0;a<n;a++)args[a]=r.Int();r.Byte();
                n=r.Byte();string[] strings=new string[n];for(int a=0;a<n;a++)strings[a]=r.String();
                byte end=r.Byte();if(end>1)throw new InvalidDataException("WOLF 指令结尾无效。");
                if(end==1||command==201){r.Skip(6);Routes();}
                if(modern)r.Skip(r.Byte());
                captured?.Add(new(i,command,args,strings));
                if(command==250&&args.Length>=4&&strings.Length>0&&(args[3]&0xffff)==2)
                    databaseWrites.Add((i,args,strings[0]));
                if(command==250&&args.Length>=5&&(args[3]&0xffff)==0)
                    databaseVariableWrites.Add((i,args));
                if(command==250&&args.Length>=5&&(args[3]&0xffff)==0x1000)databaseReads.Add(args);
                if(command is 101 or 102||command==150&&args.Length>0&&((args[0]>>4)&7)==2)displays.AddRange(strings.Take(command==102?strings.Length:1));
                // String operations often build the complete dialogue/menu
                // before it reaches a common-event renderer. Keep prose
                // literals as display-cache candidates only: no variable or
                // resource string is ever rewritten in game state.
                bool assignment=command==122&&args.Length>0;
                if(assignment&&strings.Length>0)assignments.Add((i,args[0],strings[0]));
                bool display=command is 101 or 102||command==150&&args.Length>0&&((args[0]>>4)&7)==2||assignment;
                if(!display)continue;
                // Choices expose every string; message/picture commands use
                // only their first string as display text.
                for(int a=0;a<(command==102?strings.Length:Math.Min(1,strings.Length));a++)
                {
                    string text=strings[a];if(assignment&&!AssignmentProse(text))continue;
                    if(text.Length is >0 and <=6000&&RpgTranslationText.HasTranslatableText(text,EmbeddedTextSyntax.Wolf))
                    {if(fields.Count>=100000)throw new InvalidDataException("WOLF 文本数量超过限制。");if(assignment)assignmentFields[i]=fields.Count;fields.Add(new($"{location}/command:{i}:{command}/text:{a}",text));}
                }
            }
            // Temporary DB cells feeding a displayed self-string are UI text.
            // Require the read/display chain in this event; unrelated DB keys,
            // comparisons, asset paths and scripts do not gain eligibility.
            var displaySelfs=new HashSet<int>();
            foreach(var text in displays)
                foreach(Match match in Regex.Matches(text,@"\\cself\[(\d{1,2})\]",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
                    displaySelfs.Add(1600000+int.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture));
            var renderedColumns=databaseReads.Where(a=>displaySelfs.Contains(a[4]))
                .Select(a=>(a[0],a[2],a[3]&0xf0000)).ToHashSet();
            foreach(var write in databaseWrites)
                if(renderedColumns.Contains((write.Args[0],write.Args[2],write.Args[3]&0xf0000))&&DisplayLabel(write.Text))
                {if(fields.Count>=100000)throw new InvalidDataException("WOLF 文本数量超过限制。");fields.Add(new($"{location}/command:{write.Index}:250/display-db",write.Text));}
            // Short labels may be assigned in conditional branches and then
            // copied into the temporary menu DB. Keep all possible literals
            // for a proven display destination, without rewriting variables.
            // This also covers a self-string rendered directly by this event.
            var displayedWrites=databaseVariableWrites.Where(w=>renderedColumns.Contains((w.Args[0],w.Args[2],w.Args[3]&0xf0000)))
                .GroupBy(w=>w.Args[4]).ToDictionary(g=>g.Key,g=>g.Max(w=>w.Index));
            foreach(var assignment in assignments)
            {
                bool direct=displaySelfs.Contains(assignment.Variable);
                bool stored=displayedWrites.TryGetValue(assignment.Variable,out int lastWrite)&&assignment.Index<lastWrite;
                if((!direct&&!stored)||!DisplayLabel(assignment.Text))continue;
                var field=new TextField($"{location}/command:{assignment.Index}:122/display-db",assignment.Text);
                if(assignmentFields.TryGetValue(assignment.Index,out int position))fields[position]=field;
                else {if(fields.Count>=100000)throw new InvalidDataException("WOLF 文本数量超过限制。");fields.Add(field);}
            }
        }
    }
    internal static bool DisplayLabel(string text)
    {
        if(text.Length is <1 or >6000||text.Any(c=>char.IsControl(c)&&c is not ('\r' or '\n' or '\t')))return false;
        if(System.Text.RegularExpressions.Regex.IsMatch(text,@"(?:[/\\]|\.(?:png|jpg|ogg|wav|dat|mps|exe|txt)$|[{}=;])",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return false;
        return RpgTranslationText.HasTranslatableText(text,EmbeddedTextSyntax.Wolf);
    }
    internal static bool AssignmentProse(string text)
    {
        if(text.Length is <4 or >6000||text.Contains('\0'))return false;
        // Resource names, paths, labels and opaque identifiers stay out. This
        // is not a def-use claim; cache substitution happens at draw time.
        string prose=GameTranslationLiterals.Visible(text,EmbeddedTextSyntax.Wolf);
        if(System.Text.RegularExpressions.Regex.IsMatch(prose.Trim(),@"^[A-Za-z_][A-Za-z0-9_.-]*$"))return false;
        if(System.Text.RegularExpressions.Regex.IsMatch(prose.TrimStart(),@"^(?:[A-Za-z]:[\\/]|[.]{0,2}[\\/])|\.(?:png|jpe?g|ogg|wav|mp3|mps|dat|project|wolf|txt|csv|exe|dll)(?:$|[\s\""'])",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return false;
        if(prose.Any(c=>c is '{' or '}' or '=' or ';')&&!prose.Contains('\n'))return false;
        int cjk=prose.Count(c=>c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff' or >= '\uac00' and <= '\ud7af');
        return cjk>=4||System.Text.RegularExpressions.Regex.Matches(prose,@"\p{L}{2,}").Count>=3;
    }
    private sealed class Reader
    {
        private readonly byte[] data;private readonly Encoding encoding;private readonly Action check;private int p,nodes;
        internal bool Utf8 {get;}internal int Remaining=>data.Length-p;
        internal Reader(byte[] bytes,bool utf8,Action check){data=bytes;Utf8=utf8;this.check=check;Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);encoding=utf8?new UTF8Encoding(false,true):Encoding.GetEncoding(932,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);}
        internal void Skip(int n){check();if(++nodes>2000000||n<0||n>Remaining)throw new InvalidDataException("WOLF 数据范围或读取数量无效。");p+=n;}
        internal byte Byte(){int at=p;Skip(1);return data[at];}
        internal int Int(){int at=p;Skip(4);return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at,4));}
        internal int Peek(){if(Remaining<4)throw new InvalidDataException("WOLF 数据截断。");return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p,4));}
        internal int Count(int max=1000000){int n=Int();if(n<0||n>max)throw new InvalidDataException("WOLF 数据数量超过限制。");return n;}
        internal string String(){int n=Count(1024*1024);if(n<1)throw new InvalidDataException("WOLF 字符串长度无效。");int at=p;Skip(n);if(data[p-1]!=0)throw new InvalidDataException("WOLF 字符串结尾无效。");return encoding.GetString(data,at,n-1);}
        internal void Expect(params byte[] expected){foreach(byte b in expected)if(Byte()!=b)throw new InvalidDataException("WOLF 结构标记不符。");}
    }
}
