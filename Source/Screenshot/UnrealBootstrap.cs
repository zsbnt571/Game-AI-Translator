using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class UnrealBootstrap
{
    // Unreal's bootstrap embeds its relative executable in RCDATA 201. Resolve
    // the declared target, never select an arbitrary neighboring executable.
    internal static string? Target(string exe)
    {
        try
        {
            if(new FileInfo(exe).Length>2*1024*1024)return null;
            using var input=File.OpenRead(exe);using var pe=new PEReader(input);
            var resource=pe.PEHeaders.PEHeader?.ResourceTableDirectory;
            if(resource is null||resource.Value.Size is <16 or >1024*1024)return null;
            var data=pe.GetSectionData(resource.Value.RelativeVirtualAddress).GetContent(0,resource.Value.Size).ToArray();
            uint U32(int at){if(at<0||at>data.Length-4)throw new InvalidDataException();return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));}
            int Child(int offset,int? id)
            {
                int count=(int)(U32(offset+12)&0xffff)+(int)(U32(offset+12)>>16);if(count>1024)throw new InvalidDataException();
                for(int i=0;i<count;i++){int at=offset+16+i*8;uint name=U32(at);if(id is null||name==id)return unchecked((int)U32(at+4));}
                throw new InvalidDataException();
            }
            int DirectoryChild(int offset,int id)
            {
                uint value=unchecked((uint)Child(offset,id));if((value&0x80000000)==0)throw new InvalidDataException();return checked((int)(value&0x7fffffff));
            }
            int type=DirectoryChild(0,10),nameDir=DirectoryChild(type,201);int leaf=Child(nameDir,null);
            if(leaf<0)throw new InvalidDataException();int rva=checked((int)U32(leaf)),size=checked((int)U32(leaf+4));
            if(size is <4 or >4096||size%2!=0)return null;
            string relative=new UnicodeEncoding(false,false,true).GetString(pe.GetSectionData(rva).GetContent(0,size).ToArray()).TrimEnd('\0');
            if(relative.Contains('\0')||Path.IsPathRooted(relative)||!relative.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))return null;
            string root=Path.GetDirectoryName(Path.GetFullPath(exe))!;string target=UnrealGameAdapter.SafePath(root,relative);
            if(!System.Text.RegularExpressions.Regex.IsMatch(relative,@"(^|[\\/])Binaries[\\/](Win64|Win32)[\\/][^\\/]+\.exe$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)||!File.Exists(target))return null;
            using var targetInput=File.OpenRead(target);using var targetPe=new PEReader(targetInput);return targetPe.PEHeaders.PEHeader is not null?target:null;
        }
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException or OverflowException or ArgumentException){return null;}
    }
}
