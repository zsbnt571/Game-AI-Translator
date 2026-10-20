using System.Buffers.Binary;
using System.Text;

namespace ScreenshotTranslationUiTester;

// Read-only DXArchive v8 reader. Adapted from giveptr/anylang (MIT), with
// strict allocation, traversal, cancellation and output-size bounds. No game
// loader, script, native archive library or archive write-back is executed.
internal sealed class WolfArchive : IDisposable
{
    internal const int MaxFile=32*1024*1024,MaxIndex=16*1024*1024;
    internal sealed record Entry(string Path,ulong Offset,ulong Size,ulong LzSize,ulong HuffSize,byte[] Pad);
    private readonly FileStream stream;private readonly Action check;private readonly WolfArchiveCipher cipher;
    private readonly ulong dataAt,namesAt;private readonly uint filesAt,dirsAt;private readonly byte huffKb;private readonly byte[] index;
    internal Entry[] Entries {get;}
    internal int Seal {get;}
    internal string FileName {get;}
    internal WolfArchive(string path,Action check)
    {
        this.check=check;FileName=path;AppDataPaths.RejectReparsePoints(path);
        stream=new(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.RandomAccess);
        try
        {
            byte[] header=ReadAt(0,64);if(U16(header,0)!=0x5844)throw new NotSupportedException("不是 WOLF DXArchive 封包。");
            if(U16(header,2)!=8)throw new NotSupportedException("此 WOLF 封包的 DXArchive 版本尚未支持。");
            uint flags=U32(header,44);Seal=(int)(flags>>16);
            cipher=new(header,(ulong)stream.Length);cipher.Unmask(header);
            uint size=U32(header,4);dataAt=U64(header,8);namesAt=U64(header,16);
            ulong f=U64(header,24),d=U64(header,32);uint page=U32(header,40);huffKb=header[48];
            if(size>MaxIndex||size<32||f>size||d>size-32||namesAt<64||dataAt<64||namesAt>(ulong)stream.Length||page is not (932 or 65001))
                throw new InvalidDataException("WOLF 封包索引范围或编码无效。");
            filesAt=(uint)f;dirsAt=(uint)d;
            bool compressed=(flags&2)==0;
            ulong stored=compressed?(ulong)stream.Length-namesAt:size;
            if(stored>MaxIndex*2)throw new InvalidDataException("WOLF 封包压缩索引过大。");
            byte[] raw=ReadAt(namesAt,(int)stored);cipher.Turn(raw,0,namesAt,null);
            index=compressed?UnLz(UnHuff(raw,MaxIndex,check),MaxIndex,check):raw;
            if(index.Length!=size)throw new InvalidDataException("WOLF 封包索引解码长度不符。");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding encoding=page==65001?new UTF8Encoding(false,true):Encoding.GetEncoding(932,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
            var entries=new List<Entry>();var visited=new HashSet<ulong>();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Walk(0,"",[],0);Entries=entries.ToArray();
            void Walk(ulong directory,string parent,byte[] chain,int depth)
            {
                check();if(depth>32||!visited.Add(directory))throw new InvalidDataException("WOLF 封包目录循环或层级过深。");
                int at=Ix((ulong)dirsAt+directory,32);ulong count=U64(index,at+16),heads=U64(index,at+24);
                if(count>100000||(ulong)entries.Count+count>100000)throw new InvalidDataException("WOLF 封包条目超过限制。");
                for(ulong i=0;i<count;i++)
                {
                    check();int p=Ix((ulong)filesAt+heads+i*72,72);int n=Ix(U64(index,p),4);int chars=U16(index,n);
                    int spelling=checked(n+4+chars*4);Ix((ulong)spelling,1);int end=Array.IndexOf(index,(byte)0,spelling);
                    if(end<spelling||end-spelling>1024)throw new InvalidDataException("WOLF 封包文件名无效。");
                    string name=encoding.GetString(index,spelling,end-spelling);
                    if(name is "" or "." or "..")continue;
                    if(name.IndexOfAny(['/', '\\', ':', '\0'])>=0||name.Any(char.IsControl)||name.EndsWith('.')||name.EndsWith(' '))throw new InvalidDataException("WOLF 封包文件名包含不安全路径。");
                    int keyEnd=Array.IndexOf(index,(byte)0,n+4);if(keyEnd<0||keyEnd-n>2048)throw new InvalidDataException("WOLF 封包文件名键无效。");
                    byte[] keyName=index[(n+4)..keyEnd];string rel=parent+name;
                    if((U64(index,p+8)&0x10)!=0)
                    {Walk(U64(index,p+40),rel+"/",keyName.Concat(chain).Take(2040).ToArray(),depth+1);continue;}
                    if(!names.Add(rel))throw new InvalidDataException("WOLF 封包存在重名资源，未猜测加载顺序。");
                    ulong offset=U64(index,p+40),length=U64(index,p+48),lz=U64(index,p+56),huff=U64(index,p+64);
                    if(offset>ulong.MaxValue-dataAt)throw new InvalidDataException("WOLF 封包资源位置溢出。");
                    entries.Add(new(rel,offset+dataAt,length,lz,huff,cipher.FilePad(keyName,chain)));
                }
            }
            int Ix(ulong offset,int n){if(offset>(ulong)index.Length||(ulong)n>(ulong)index.Length-offset)throw new InvalidDataException("WOLF 封包索引字段越界。");return (int)offset;}
        }
        catch{stream.Dispose();throw;}
    }
    internal byte[] Read(Entry entry)
    {
        check();if(entry.Size>MaxFile)throw new InvalidDataException("WOLF 文本资源超过 32 MB。");
        if(entry.Size==0)return [];
        ulong target=entry.LzSize==ulong.MaxValue?entry.Size:entry.LzSize;
        if(target>MaxFile*2)throw new InvalidDataException("WOLF 资源压缩范围过大。");
        byte[] body;
        if(entry.HuffSize==ulong.MaxValue)body=Lift(entry.Offset,(int)target,entry.Size);
        else
        {
            if(entry.HuffSize>MaxFile*2)throw new InvalidDataException("WOLF Huffman 资源范围过大。");
            body=UnHuff(Lift(entry.Offset,(int)entry.HuffSize,entry.Size),MaxFile*2,check);
            int span=huffKb*1024;
            if(huffKb!=255&&target>(ulong)span*2)
            {
                if(body.Length!=span*2)throw new InvalidDataException("WOLF 分段 Huffman 长度不符。");
                byte[] joined=new byte[(int)target];body.AsSpan(0,span).CopyTo(joined);body.AsSpan(span,span).CopyTo(joined.AsSpan(joined.Length-span));
                Lift(checked(entry.Offset+entry.HuffSize),joined.Length-span*2,checked(entry.Size+entry.HuffSize)).CopyTo(joined,span);body=joined;
            }
        }
        if(body.Length!=(int)target)throw new InvalidDataException("WOLF 资源解码中间长度不符。");
        if(entry.LzSize!=ulong.MaxValue)body=UnLz(body,MaxFile,check);
        if(body.Length!=(int)entry.Size)throw new InvalidDataException("WOLF 资源解码长度不符。");
        return body;
        byte[] Lift(ulong at,int count,ulong seek){byte[] b=ReadAt(at,count);cipher.Turn(b,seek,at,entry.Pad);return b;}
    }
    private byte[] ReadAt(ulong at,int size)
    {
        check();if(size<0||at>(ulong)stream.Length||(ulong)size>(ulong)stream.Length-at)throw new InvalidDataException("WOLF 封包数据截断。");
        stream.Position=(long)at;byte[] b=new byte[size];stream.ReadExactly(b);return b;
    }
    internal static ushort U16(byte[] b,int p)=>p>=0&&p<=b.Length-2?BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p,2)):throw new InvalidDataException("WOLF 字段截断。");
    internal static uint U32(byte[] b,int p)=>p>=0&&p<=b.Length-4?BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p,4)):throw new InvalidDataException("WOLF 字段截断。");
    internal static ulong U64(byte[] b,int p)=>p>=0&&p<=b.Length-8?BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p,8)):throw new InvalidDataException("WOLF 字段截断。");
    public void Dispose()=>stream.Dispose();
    private sealed class Bits(byte[] data)
    {
        private int p;internal int Bytes=>(p+7)/8;
        internal ulong Read(int count){if(count<1||count>64||p+(long)count>data.Length*8L)throw new InvalidDataException("WOLF Huffman 表截断。");ulong n=0;for(int i=0;i<count;i++,p++)n=(n<<1)|(uint)((data[p/8]>>(7-p%8))&1);return n;}
        internal ulong Sized()=>Read((int)Read(6)+1);
    }
    internal static byte[] UnHuff(byte[] raw,int max,Action check)
    {
        var bits=new Bits(raw);ulong length=bits.Sized();bits.Sized();if(length>(ulong)max)throw new InvalidDataException("WOLF Huffman 展开量超过限制。");
        var weights=new ushort[256];for(int i=0;i<256;i++){int width=((int)bits.Read(3)+1)*2;bool minus=bits.Read(1)!=0;ushort step=(ushort)bits.Read(width);weights[i]=i==0?step:unchecked((ushort)(minus?weights[i-1]-step:weights[i-1]+step));}
        int start=bits.Bytes;if(length>(ulong)(raw.Length-start)*8)throw new InvalidDataException("WOLF Huffman 输出长度不可达。");
        var q=new PriorityQueue<int,(ulong,int)>();var loads=new ulong[511];var kids=new int[511,2];
        for(int i=0;i<256;i++){loads[i]=weights[i];q.Enqueue(i,(loads[i],i));}
        for(int i=256;i<511;i++){int a=q.Dequeue(),b=q.Dequeue();kids[i,0]=a;kids[i,1]=b;loads[i]=loads[a]+loads[b];q.Enqueue(i,(loads[i],i));}
        byte[] result=new byte[(int)length];long p=start*8L;
        for(int i=0;i<result.Length;i++)
        {if((i&4095)==0)check();int node=510;while(node>255){if(p>=raw.Length*8L)throw new InvalidDataException("WOLF Huffman 正文截断。");int bit=(raw[p/8]>>(int)(p%8))&1;p++;node=kids[node,bit];}result[i]=(byte)node;}
        return result;
    }
    internal static byte[] UnLz(byte[] raw,int max,Action check)
    {
        uint length=U32(raw,0),stored=U32(raw,4);if(length>max||stored<9||stored!=raw.Length)throw new InvalidDataException("WOLF LZ 范围无效。");
        byte mark=raw[8];byte[] result=new byte[(int)length];int p=9,o=0;
        byte Take(){if(p>=raw.Length)throw new InvalidDataException("WOLF LZ 数据截断。");return raw[p++];}
        void Put(byte b){if(o>=result.Length)throw new InvalidDataException("WOLF LZ 输出越界。");result[o++]=b;}
        while(p<raw.Length)
        {
            if((p&4095)==0)check();byte lead=Take();if(lead!=mark){Put(lead);continue;}byte n=Take();if(n==mark){Put(mark);continue;}
            int code=n>mark?n-1:n;int run=code>>3;if((code&4)!=0)run|=Take()<<5;run+=4;int back=Take();if((code&3)!=0)back|=Take()<<8;if((code&3)>=2)back|=Take()<<16;back++;
            if(back>o||run>result.Length-o)throw new InvalidDataException("WOLF LZ 回溯或长度无效。");for(int i=0;i<run;i++)Put(result[o-back]);
        }
        if(o!=result.Length)throw new InvalidDataException("WOLF LZ 解码长度不符。");return result;
    }
}
