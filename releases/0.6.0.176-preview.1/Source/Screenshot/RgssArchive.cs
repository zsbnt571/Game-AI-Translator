using System.Buffers.Binary;
using System.Text;

namespace ScreenshotTranslationUiTester;

// A bounded, read-only implementation of the RGSSAD v1/v3 container format.
// Format reference: https://github.com/Ancurio/mkxp/blob/master/src/rgssad.cpp
// Only requested members are decoded in memory. No archive paths are extracted.
internal sealed class RgssArchive : IDisposable
{
    internal sealed record Entry(string Name,long Offset,int Length,uint Key);
    private readonly FileStream file;
    internal IReadOnlyDictionary<string,Entry> Entries {get;}
    private static uint Next(uint key)=>unchecked(key*7+3);
    private uint Word(){Span<byte> b=stackalloc byte[4];file.ReadExactly(b);return BinaryPrimitives.ReadUInt32LittleEndian(b);}
    internal RgssArchive(string path,CancellationToken token=default)
    {
        file=new(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        try
        {
            Span<byte> header=stackalloc byte[8];file.ReadExactly(header);
            if(!header[..7].SequenceEqual("RGSSAD\0"u8)||header[7] is not (1 or 3))throw new NotSupportedException("RGSS 封包头或版本未支持。");
            var entries=new Dictionary<string,Entry>(StringComparer.OrdinalIgnoreCase);uint key=header[7]==1?0xDEADCAFE:unchecked(Word()*9+3);
            long tableEnd=0;
            while(file.Position<file.Length)
            {
                token.ThrowIfCancellationRequested();if(entries.Count>=100000)throw new IOException("RGSS 封包文件数量超过限制。");
                uint size,nameLength,entryKey;long offset;
                if(header[7]==1){nameLength=Word()^key;key=Next(key);offset=0;size=0;entryKey=0;}
                else
                {
                    offset=Word()^key;
                    if(offset==0){tableEnd=file.Position;break;}
                    size=Word()^key;entryKey=Word()^key;nameLength=Word()^key;
                }
                if(nameLength is <1 or >4096)throw new InvalidDataException("RGSS 封包文件名长度无效。");
                byte[] name=new byte[nameLength];file.ReadExactly(name);
                for(int i=0;i<name.Length;i++)
                    if(header[7]==1){name[i]^=(byte)key;key=Next(key);}else name[i]^=(byte)(key>>(8*(i%4)));
                if(header[7]==1){size=Word()^key;key=Next(key);entryKey=key;offset=file.Position;}
                if(size>int.MaxValue||offset<8||offset>file.Length-size)throw new InvalidDataException("RGSS 封包成员范围无效。");
                string member=Name(name);
                if(!entries.TryAdd(member,new(member,offset,(int)size,entryKey)))throw new InvalidDataException("RGSS 封包包含重复路径。");
                if(header[7]==1)file.Position=offset+size;
            }
            if(header[7]==3&&(tableEnd==0||entries.Values.Any(e=>e.Offset<tableEnd)))throw new InvalidDataException("RGSS 封包索引不完整或与数据重叠。");
            Entries=entries;
        }
        catch{file.Dispose();throw;}
    }
    private static string Name(byte[] bytes)
    {
        string value;
        try{value=new UTF8Encoding(false,true).GetString(bytes);}
        catch(DecoderFallbackException){Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);value=Encoding.GetEncoding(932,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(bytes);}
        value=value.Replace('\\','/');
        if(value.StartsWith('/')||value.Contains(':')||value.Any(char.IsControl)||value.Split('/').Any(x=>x is "" or "." or ".."))throw new InvalidDataException("RGSS 封包路径无效。");
        return value;
    }
    internal byte[] Read(string name,int limit=32*1024*1024,CancellationToken token=default)
    {
        if(!Entries.TryGetValue(name.Replace('\\','/'),out var entry))throw new FileNotFoundException("RGSS 封包中未找到所需数据。",name);
        if(entry.Length>limit)throw new IOException("RGSS 数据超过读取限制。");
        byte[] result=new byte[entry.Length];file.Position=entry.Offset;file.ReadExactly(result);uint key=entry.Key;
        for(int i=0;i<result.Length;i++){if((i&0xffff)==0)token.ThrowIfCancellationRequested();if(i>0&&i%4==0)key=Next(key);result[i]^=(byte)(key>>(8*(i%4)));}
        return result;
    }
    public void Dispose()=>file.Dispose();
}
