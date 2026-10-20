using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenshotTranslationUiTester;

// Translation documents only. UI settings and game resources retain their own
// writers. A successful commit means exact bytes were read back, not that an
// engine loaded the document or rendered a translated glyph.
internal static class TranslationStorage
{
    internal sealed record Receipt(int Bytes,string Sha256);
    private static readonly UTF8Encoding StrictUtf8=new(false,true);
    private sealed class ExactStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>reader.GetString();
        public override void Write(Utf8JsonWriter writer,string value,JsonSerializerOptions options)
        {StrictUtf8.GetByteCount(value);writer.WriteStringValue(value);}
        public override void WriteAsPropertyName(Utf8JsonWriter writer,string value,JsonSerializerOptions options)
        {StrictUtf8.GetByteCount(value);writer.WritePropertyName(value);}
    }
    private static readonly JsonSerializerOptions Options=CreateOptions();
    private sealed class BoundedBuffer(int maximum) : MemoryStream
    {
        private void Check(int count)
        {if(Position+count>maximum)throw new IOException("译文文件超过保存上限，原文件保留。");}
        public override void Write(byte[] buffer,int offset,int count){Check(count);base.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){Check(buffer.Length);base.Write(buffer);}
        public override void WriteByte(byte value){Check(1);base.WriteByte(value);}
    }
    private static JsonSerializerOptions CreateOptions()
    {var result=new JsonSerializerOptions{WriteIndented=true};result.Converters.Add(new ExactStringConverter());return result;}
    internal static Receipt WriteJson<T>(string path,T value,int maximumBytes=64*1024*1024)
    {
        if(maximumBytes<1)throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        byte[] data;
        try{using var buffer=new BoundedBuffer(maximumBytes);JsonSerializer.Serialize(buffer,value,Options);data=buffer.ToArray();}
        catch(Exception ex)when(ex is EncoderFallbackException or JsonException or NotSupportedException)
        {throw new IOException("译文文件包含不能无损保存的内容，原文件保留。",ex);}
        if(data.Length>maximumBytes)throw new IOException("译文文件超过保存上限，原文件保留。");
        string full=Path.GetFullPath(path),id=Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(full.ToUpperInvariant())));
        using var mutex=new Mutex(false,"Local\\FusionR1.TranslationStorage."+id);bool held=false;
        string temp=full+"."+Guid.NewGuid().ToString("N")+".pending";
        byte[] expected=SHA256.HashData(data);
        try
        {
            try{held=mutex.WaitOne(1000);}catch(AbandonedMutexException){held=true;}
            if(!held)throw new IOException("另一进程正在保存该译文文件，请稍后重试。");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {output.Write(data);output.Flush(true);}
            Verify(temp,data.Length,expected);
            // Reparse the immutable staging bytes before replacing a valid file.
            using(var json=JsonDocument.Parse(data)){}
            if(File.Exists(full))File.Replace(temp,full,full+".previous");else File.Move(temp,full);
            Verify(full,data.Length,expected);
            return new(data.Length,Convert.ToHexString(expected).ToLowerInvariant());
        }
        finally
        {
            if(File.Exists(temp))File.Delete(temp);
            if(held)mutex.ReleaseMutex();
        }
    }
    private static void Verify(string path,int length,byte[] hash)
    {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length!=length||!CryptographicOperations.FixedTimeEquals(SHA256.HashData(input),hash))
            throw new IOException("译文保存后回读校验不符，已有备份保留。");
    }
}
