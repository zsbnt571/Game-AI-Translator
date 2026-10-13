using System.Buffers.Binary;
using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class DefoldWire
{
    internal sealed record Item(int Number,int Kind,ulong Value,byte[] Data);
    internal static List<Item> Read(byte[] data)
    {
        int p=0;var result=new List<Item>();
        ulong Var(){ulong v=0;for(int i=0;i<10;i++){if(p>=data.Length)throw new InvalidDataException("资源字段不完整。");byte b=data[p++];if(i==9&&b>1)throw new InvalidDataException("资源字段过大。");v|=(ulong)(b&127)<<(i*7);if(b<128)return v;}throw new InvalidDataException("资源字段无效。");}
        while(p<data.Length){if(result.Count>=200000)throw new InvalidDataException("资源字段过多。");ulong t=Var();int n=checked((int)(t>>3)),k=(int)(t&7);if(n==0)throw new InvalidDataException("资源字段无效。");if(k==0){result.Add(new(n,k,Var(),[]));continue;}int size=k switch{1=>8,5=>4,2=>checked((int)Var()),_=>throw new InvalidDataException("资源字段格式未支持。")};if(size>data.Length-p)throw new InvalidDataException("资源字段越界。");result.Add(new(n,k,0,data.AsSpan(p,size).ToArray()));p+=size;}return result;
    }
    internal static byte[] One(byte[] b,int n)=>Read(b).Single(x=>x.Number==n&&x.Kind==2).Data;
    internal static byte[] Write(IEnumerable<Item> items)
    {
        using var s=new MemoryStream();void Var(ulong v){while(v>127){s.WriteByte((byte)((v&127)|128));v>>=7;}s.WriteByte((byte)v);}
        foreach(var x in items){Var((ulong)((x.Number<<3)|x.Kind));if(x.Kind==0)Var(x.Value);else{if(x.Kind==2)Var((ulong)x.Data.Length);s.Write(x.Data);}}return s.ToArray();
    }
    internal static Item Bytes(int n,byte[] v)=>new(n,2,0,v);
    internal static Item UInt(int n,int v)=>new(n,0,checked((ulong)v),[]);
    internal static Item Float(int n,float v)=>new(n,5,0,BitConverter.GetBytes(v));
    internal static byte[] Replace(byte[] b,int n,byte[] v)=>Write(Read(b).Where(x=>x.Number!=n).Append(Bytes(n,v)));
}

// Standard bundled v4 resources only. The alternate project keeps the game's
// original archive, scripts, identifiers and files untouched.
internal sealed class DefoldArchive : IDisposable
{
    private readonly byte[] index;private readonly FileStream archive;private readonly int count,entryOffset,hashOffset,hashSize;
    private readonly Dictionary<string,int> names=new(StringComparer.Ordinal);
    internal IEnumerable<string> Names=>names.Keys;
    private static uint U32(byte[] b,int p)=>BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p,4));
    internal DefoldArchive(string exe)
    {
        string root=Path.GetDirectoryName(exe)!;
        byte[] Bounded(string name,int limit){string p=GameMakerGameAdapter.InGame(exe,name);if(new FileInfo(p).Length>limit)throw new InvalidDataException("资源索引超过上限。");return File.ReadAllBytes(p);}
        index=Bounded("game.arci",16*1024*1024);if(index.Length<32||U32(index,0)!=4)throw new InvalidDataException("当前内嵌接入支持 Defold v4 资源包。");
        count=checked((int)U32(index,16));entryOffset=checked((int)U32(index,20));hashOffset=checked((int)U32(index,24));hashSize=checked((int)U32(index,28));
        if(count<1||count>100000||entryOffset<32||hashOffset<32||hashSize is <16 or >64||entryOffset+count*16L>index.Length||hashOffset+count*64L>index.Length)throw new InvalidDataException("资源索引越界。");
        var hashes=new Dictionary<string,int>(StringComparer.Ordinal);for(int i=0;i<count;i++)hashes.TryAdd(Convert.ToHexString(index.AsSpan(hashOffset+i*64,hashSize)),i);
        foreach(var item in DefoldWire.Read(DefoldWire.One(Bounded("game.dmanifest",32*1024*1024),1)).Where(x=>x.Number==3))
        {
            string name=new UTF8Encoding(false,true).GetString(DefoldWire.One(item.Data,1));string digest=Convert.ToHexString(DefoldWire.One(DefoldWire.One(item.Data,3),1));
            if(name.Length>512||!name.StartsWith('/')||name.Contains('\\')||name.Split('/').Any(x=>x is "." or "..")||!hashes.TryGetValue(digest,out int i))throw new InvalidDataException("资源引用无效。");names.TryAdd(name,i);
        }
        archive=File.OpenRead(GameMakerGameAdapter.InGame(exe,"game.arcd"));if(archive.Length>2L*1024*1024*1024){archive.Dispose();throw new InvalidDataException("资源包超过本次接入上限。");}
    }
    internal byte[] Read(string name)
    {
        int i=names[name],p=entryOffset+i*16;uint off=U32(index,p),size=U32(index,p+4),compressed=U32(index,p+8),flags=U32(index,p+12);uint stored=compressed==uint.MaxValue?size:compressed;
        if(size>64*1024*1024||stored>64*1024*1024||off+stored>(ulong)archive.Length||(flags&~1u)!=0)throw new InvalidDataException("资源范围或格式未支持。");archive.Position=off;byte[] b=new byte[stored];archive.ReadExactly(b);if((flags&1)!=0)b=DefoldTextCatalog.DecodeScript(b,()=>{});if(compressed!=uint.MaxValue)b=DefoldTextCatalog.Decompress(b,checked((int)size));return b;
    }
    internal void Write(string folder,IReadOnlyDictionary<string,byte[]> changes)
    {
        byte[] idx=(byte[])index.Clone();using(var output=new FileStream(Path.Combine(folder,"game.arcd"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            archive.Position=0;archive.CopyTo(output);
            foreach(var pair in changes){int original=names[pair.Key];uint off=checked((uint)output.Position);if(pair.Value.Length>64*1024*1024)throw new InvalidDataException("翻译资源过大。");output.Write(pair.Value);
                for(int i=0;i<count;i++)if(index.AsSpan(hashOffset+i*64,hashSize).SequenceEqual(index.AsSpan(hashOffset+original*64,hashSize))){int p=entryOffset+i*16;BinaryPrimitives.WriteUInt32BigEndian(idx.AsSpan(p,4),off);BinaryPrimitives.WriteUInt32BigEndian(idx.AsSpan(p+4,4),(uint)pair.Value.Length);BinaryPrimitives.WriteUInt32BigEndian(idx.AsSpan(p+8,4),uint.MaxValue);BinaryPrimitives.WriteUInt32BigEndian(idx.AsSpan(p+12,4),0);}}
            output.Flush(true);
        }
        File.WriteAllBytes(Path.Combine(folder,"game.arci"),idx);
    }
    public void Dispose()=>archive.Dispose();
}
