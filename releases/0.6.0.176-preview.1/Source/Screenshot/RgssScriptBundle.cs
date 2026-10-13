using System.IO.Compression;
using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class RgssScriptBundle
{
    internal sealed record Script(int Id,byte[] Name,string NameEncoding,byte[] Packed);
    internal static Script[] Read(byte[] bytes)
    {
        var root=new RubyMarshalData(bytes,()=>{}).Read();
        if(root.Kind!='['||root.Items.Count is <1 or >10000)throw new IOException("RGSS 脚本目录格式未支持。");
        return root.Items.Select(n=>
        {
            if(n.Kind!='['||n.Items.Count!=3||n.Items[0].Kind!='i'||n.Items[1].Kind!='"'||n.Items[2].Kind!='"'||n.Items[1].Bytes.Length>4096)
                throw new IOException("RGSS 脚本项目格式未支持，原文件未改动。");
            return new Script(n.Items[0].Number,n.Items[1].Bytes,n.Items[1].Encoding,n.Items[2].Bytes);
        }).ToArray();
    }
    internal static byte[] Inflate(byte[] packed,int limit=8*1024*1024)
    {
        using var input=new MemoryStream(packed);using var zip=new ZLibStream(input,CompressionMode.Decompress);using var output=new MemoryStream();byte[] buffer=new byte[16384];
        int count;while((count=zip.Read(buffer))>0){if(output.Length+count>limit)throw new IOException("RGSS 脚本解压后超过限制。");output.Write(buffer,0,count);}return output.ToArray();
    }
    internal static byte[] Pack(byte[] value)
    {using var output=new MemoryStream();using(var zip=new ZLibStream(output,CompressionLevel.Optimal,true))zip.Write(value);return output.ToArray();}
    internal static byte[] Prepare(byte[] original,string bridge)
    {
        var scripts=Read(original);byte[] first=Inflate(scripts[0].Packed);int start=0;
        // Preserve BOM, encoding and frozen-string headers at their original beginning.
        if(first.AsSpan().StartsWith(new byte[]{239,187,191}))start=3;
        int position=start;
        for(int i=0;i<2&&position<first.Length;i++)
        {
            int end=Array.IndexOf(first,(byte)'\n',position);if(end<0)end=first.Length-1;
            string line=Encoding.ASCII.GetString(first,position,end-position);
            if(!line.TrimStart().StartsWith('#'))break;start=end+1;position=start;
        }
        byte[] prefix=Encoding.ASCII.GetBytes("\n"+bridge+"\n");
        using var patched=new MemoryStream();patched.Write(first,0,start);patched.Write(prefix);patched.Write(first,start,first.Length-start);
        scripts[0]=scripts[0] with {Packed=Pack(patched.ToArray())};
        return Write(scripts);
    }
    internal static byte[] Write(IEnumerable<Script> entries)
    {
        var scripts=entries.ToArray();using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);
        writer.Write((byte)4);writer.Write((byte)8);writer.Write((byte)'[');Int(scripts.Length);
        foreach(var s in scripts){writer.Write((byte)'[');Int(3);writer.Write((byte)'i');Int(s.Id);String(s.Name,s.NameEncoding);String(s.Packed,"");}
        return output.ToArray();
        void Int(int value)
        {
            if(value==0){writer.Write((byte)0);return;}if(value is >0 and <123){writer.Write((byte)(value+5));return;}if(value is <0 and >-124){writer.Write(unchecked((byte)(value-5)));return;}
            int n=1;while(n<4&&(value>=0?(uint)value>>(8*n)!=0:value>>(8*n)!=-1))n++;
            writer.Write(unchecked((byte)(value<0?-n:n)));for(int i=0;i<n;i++)writer.Write((byte)(value>>(i*8)));
        }
        void Bytes(byte[] b){Int(b.Length);writer.Write(b);}
        void String(byte[] b,string encoding)
        {
            if(encoding.Length>0)writer.Write((byte)'I');writer.Write((byte)'"');Bytes(b);
            if(encoding.Length>0)
            {
                Int(1);writer.Write((byte)':');
                if(encoding is "utf-8" or "us-ascii"){Bytes("E"u8.ToArray());writer.Write((byte)(encoding=="utf-8"?'T':'F'));}
                else{Bytes("encoding"u8.ToArray());writer.Write((byte)'"');Bytes(Encoding.ASCII.GetBytes(encoding));}
            }
        }
    }
}
