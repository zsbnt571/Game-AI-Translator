using System.Buffers.Binary;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace ScreenshotTranslationUiTester;

// Standard uGUI Text has a serialized schema independent of game IL2CPP
// metadata. Recover only that exact schema, never strings at guessed offsets.
// Field order: Unity-Technologies/uGUI Text, Graphic, MaskableGraphic, FontData:
// https://github.com/Unity-Technologies/uGUI/tree/main/com.unity.ugui/Runtime/UGUI/UI/Core
// Version admission is narrower than upstream; a complete exact schema match
// is required even on admitted versions. Unknown variants use runtime capture.
// Runtime replacement still requires the normal observed display binding.
internal static class UnityBuiltinTextCatalog
{
    internal static bool Supported(string version,string assembly,string space,string name)
        =>(version.StartsWith("2021.3.",StringComparison.Ordinal)||version.StartsWith("2022.3.",StringComparison.Ordinal))
        &&(assembly is "UnityEngine.UI" or "UnityEngine.UI.dll")&&space=="UnityEngine.UI"&&name=="Text";

    internal static bool TryRead(AssetsManager manager,AssetsFileInstance file,AssetFileInfo asset,AssetTypeValueField basic,out string text)
    {
        text="";
        if(basic.IsDummy||asset.TypeId!=114||file.file.Header.Version!=22||file.file.Header.Endianness||asset.ByteSize is <148 or >1024*1024)return false;
        try
        {
            var script=manager.GetExtAsset(file,basic["m_Script"]).baseField;
            if(script.IsDummy||!Supported(file.file.Metadata.UnityVersion,script["m_AssemblyName"].AsString,script["m_Namespace"].AsString,script["m_ClassName"].AsString))return false;
            byte[] prefix=basic.WriteToByteArray(false);
            if(prefix.Length>8192||prefix.Length>=asset.ByteSize)return false;
            file.file.Reader.Position=asset.GetAbsoluteByteOffset(file.file);
            byte[] raw=file.file.Reader.ReadBytes((int)asset.ByteSize);
            if(!raw.AsSpan(0,prefix.Length).SequenceEqual(prefix))return false;
            return Decode(raw,prefix.Length,out text);
        }
        catch(Exception ex)when(ex is IOException or ArgumentException or IndexOutOfRangeException or NullReferenceException){return false;}
    }

    internal static bool Decode(byte[] raw,int prefixLength,out string text)
    {
        text="";
        if(raw.Length>1024*1024||prefixLength is <32 or >8192||prefixLength%4!=0||prefixLength>raw.Length)return false;
        try
        {
            var r=new Reader(raw,prefixLength);
            r.Pointer();                           // Graphic material
            for(int i=0;i<4;i++)r.Float();          // Graphic color
            r.Boolean();                           // raycast target
            for(int i=0;i<4;i++)r.Float();          // raycast padding
            r.Boolean();                           // MaskableGraphic maskable
            int calls=r.Range(0,128);               // onCullStateChanged persistent calls
            for(int i=0;i<calls;i++)
            {
                r.Pointer();r.String(8192);r.String(8192);r.Range(0,6);
                r.Pointer();r.String(8192);r.Int();r.Float();r.String(24000);r.Boolean();r.Range(0,2);
            }
            r.Pointer();                           // FontData font
            r.Range(0,4096);r.Range(0,3);r.Boolean();
            r.Range(0,4096);r.Range(0,4096);r.Range(0,8);
            r.Boolean();r.Boolean();r.Range(0,1);r.Range(0,1);r.Float();
            string candidate=r.String(24000);       // Text.m_Text, the only admitted string
            if(!r.End||candidate.Length>6000||candidate.Contains('\0'))return false;
            text=candidate;return true;
        }
        catch(Exception ex)when(ex is InvalidDataException or DecoderFallbackException or ArgumentException or OverflowException){return false;}
    }
    private sealed class Reader(byte[] data,int offset)
    {
        private int position=offset;
        private static readonly UTF8Encoding Utf8=new(false,true);
        internal bool End=>position==data.Length;
        private ReadOnlySpan<byte> Take(int count)
        {if(count<0||count>data.Length-position)throw new InvalidDataException();var value=data.AsSpan(position,count);position+=count;return value;}
        internal int Int()=>BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        internal int Range(int min,int max){int value=Int();if(value<min||value>max)throw new InvalidDataException();return value;}
        internal void Float(){if(!float.IsFinite(BitConverter.Int32BitsToSingle(Int())))throw new InvalidDataException();}
        internal void Pointer(){Range(0,1000000);Take(8);}
        private void Align(){int n=(-position)&3;foreach(byte b in Take(n))if(b!=0)throw new InvalidDataException();}
        internal void Boolean(){if(Take(1)[0]>1)throw new InvalidDataException();Align();}
        internal string String(int max){int n=Range(0,max);string value=Utf8.GetString(Take(n));Align();return value;}
    }
}
