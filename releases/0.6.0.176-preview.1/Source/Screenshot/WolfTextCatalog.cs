using System.Buffers.Binary;
using System.Text;

namespace ScreenshotTranslationUiTester;

// Read-only implementation of the WOLF command layout documented by WolfTL
// (Sinflower, MIT). Only display commands grant text eligibility. Variable,
// condition, file-name and script parameters are never translation candidates.
internal static class WolfTextCatalog
{
    internal sealed record TextField(string Location,string Text);
    internal static TextField[] Read(byte[] bytes,bool map,Action check)
    {
        if(bytes.Length>32*1024*1024)throw new InvalidDataException("WOLF 文件超过读取限制。");
        static uint U32(byte[] b,int p)=>p>=0&&p<=b.Length-4?BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p,4)):throw new InvalidDataException("WOLF 文件截断。");
        byte[] Unpack(int offset)
        {
            uint size=U32(bytes,offset),stored=U32(bytes,offset+4);
            if(size>16*1024*1024||stored!=bytes.Length-offset-8L)throw new InvalidDataException("WOLF 压缩范围无效。");
            return DefoldTextCatalog.Decompress(bytes.AsSpan(offset+8,(int)stored).ToArray(),(int)size);
        }
        var fields=new List<TextField>();Reader r;bool modern;
        if(map)
        {
            if(bytes.Length<26||!bytes.AsSpan(10,5).SequenceEqual("WOLFM"u8)||bytes.Take(10).Any(b=>b!=0)||bytes[16] is not (0 or 0x55))throw new InvalidDataException("WOLF 地图头或加密格式尚未支持。");
            uint version=U32(bytes,20);if(version<100||version>103)throw new InvalidDataException("WOLF 地图版本尚未支持。");
            modern=version>=103;r=new(version>=101?Unpack(25):bytes[25..],bytes[16]==0x55,check);
            r.String();r.Int();int width=r.Count(8192),height=r.Count(8192),events=r.Count(50000),layers=3;
            if(modern){r.Int();layers=r.Count(16);}
            if(r.Remaining>1)
            {
                if(r.Utf8&&r.Peek()==-1)r.Int();else r.Skip(checked(width*height*layers*4));
                for(int i=0;i<events;i++)
                {
                    r.Expect(0x6f);r.Expect(0x39,0x30,0,0);int id=r.Int();r.String();r.Skip(8);int pages=r.Count(4096);r.Expect(0,0,0,0);
                    for(int page=0;page<pages;page++)
                    {
                        r.Expect(0x79);r.Int();r.String();r.Skip(4+37+4+2);Routes();Commands($"event:{id}/page:{page}");
                        int features=r.Int();r.Skip(3);if(features>3)r.Skip(1);r.Expect(0x7a);
                    }
                    r.Expect(0x70);
                }
            }
            else if(events!=0)throw new InvalidDataException("WOLF 地图事件数量不符。");
            r.Expect(0x66);
        }
        else
        {
            if(bytes.Length<12||!bytes.AsSpan(0,6).SequenceEqual(new byte[]{0,0x57,0,0,0x4f,0x4c})||bytes[6] is not (0 or 0x55)||!bytes.AsSpan(7,3).SequenceEqual(new byte[]{0x46,0x43,0}))throw new InvalidDataException("WOLF 公共事件头或加密格式尚未支持。");
            byte version=bytes[10];modern=version is 0x93 or 0xcc;
            if(!modern&&version!=0x8f)throw new InvalidDataException("WOLF 公共事件版本尚未支持。");
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
        return fields.ToArray();

        void Strings(){int count=r.Count(100000);for(int i=0;i<count;i++)r.String();}
        void Routes(){int count=r.Count(100000);for(int i=0;i<count;i++){r.Byte();r.Skip(r.Byte()*4);r.Expect(1,0);}}
        void Commands(string location)
        {
            int count=r.Count(100000);
            for(int i=0;i<count;i++)
            {
                int n=r.Byte()-1;if(n<0)throw new InvalidDataException("WOLF 指令参数数量无效。");int command=r.Int();int[] args=new int[n];for(int a=0;a<n;a++)args[a]=r.Int();r.Byte();
                n=r.Byte();string[] strings=new string[n];for(int a=0;a<n;a++)strings[a]=r.String();
                byte end=r.Byte();if(end>1)throw new InvalidDataException("WOLF 指令结尾无效。");
                if(end==1||command==201){r.Skip(6);Routes();}
                if(modern)r.Skip(r.Byte());
                bool display=command is 101 or 102||command==150&&args.Length>0&&((args[0]>>4)&7)==2;
                if(!display)continue;
                // Choices expose every string; message/picture commands use
                // only their first string as display text.
                for(int a=0;a<(command==102?strings.Length:Math.Min(1,strings.Length));a++)
                {
                    string text=strings[a];if(text.Length is >0 and <=6000&&RpgTranslationText.HasTranslatableText(text,EmbeddedTextSyntax.Rpg))
                    {if(fields.Count>=100000)throw new InvalidDataException("WOLF 文本数量超过限制。");fields.Add(new($"{location}/command:{i}:{command}/text:{a}",text));}
                }
            }
        }
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
