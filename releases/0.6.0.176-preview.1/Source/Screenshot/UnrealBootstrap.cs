using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

internal static class UnrealBootstrap
{
    internal sealed record Launch(string Runtime,string? Project,string[] Arguments);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr CommandLineToArgvW(string command,out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    // Unreal's bootstrap embeds its relative executable in RCDATA 201. Resolve
    // the declared target, never select an arbitrary neighboring executable.
    internal static string? Target(string exe)=>Read(exe)?.Runtime;
    internal static Launch? Read(string exe)
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
            int type=DirectoryChild(0,10);
            string Resource(int id)
            {
                int nameDir=DirectoryChild(type,id),leaf=Child(nameDir,null);
                if(leaf<0)throw new InvalidDataException();int rva=checked((int)U32(leaf)),size=checked((int)U32(leaf+4));
                if(size is <2 or >4096||size%2!=0)throw new InvalidDataException();
                string value=new UnicodeEncoding(false,false,true).GetString(pe.GetSectionData(rva).GetContent(0,size).ToArray()).TrimEnd('\0');
                if(value.Contains('\0'))throw new InvalidDataException();return value;
            }
            string relative=Resource(201);
            if(relative.Contains('\0')||Path.IsPathRooted(relative)||!relative.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))return null;
            string root=Path.GetDirectoryName(Path.GetFullPath(exe))!;string target=UnrealGameAdapter.SafePath(root,relative);
            if(!System.Text.RegularExpressions.Regex.IsMatch(relative,@"(^|[\\/])Binaries[\\/](Win64|Win32)[\\/][^\\/]+\.exe$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)||!File.Exists(target))return null;
            using var targetInput=File.OpenRead(target);using var targetPe=new PEReader(targetInput);if(targetPe.PEHeaders.PEHeader is null)return null;
            string raw="";try{raw=Resource(202);}catch(InvalidDataException){}
            string[] arguments=SplitArguments(raw);string? project=null;
            if(arguments.Length>0&&arguments[0].EndsWith(".uproject",StringComparison.OrdinalIgnoreCase))
            {
                // Blueprint-only builds use Engine/Binaries/UnrealGame plus a
                // project argument. The .uproject itself can reside in the PAK.
                if(Path.IsPathRooted(arguments[0]))return null;
                string file=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target)!,arguments[0]));
                file=UnrealGameAdapter.SafePath(root,Path.GetRelativePath(root,file));
                project=Path.GetDirectoryName(file);
                if(project is null||!Directory.Exists(Path.Combine(project,"Content","Paks")))return null;
            }
            return new(target,project,arguments);
        }
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException or OverflowException or ArgumentException){return null;}
    }
    internal static string[] SplitArguments(string value)
    {
        if(value.Length==0)return [];if(value.Length>4096||value.Contains('\0'))throw new InvalidDataException();
        IntPtr argv=CommandLineToArgvW("bootstrap.exe "+value,out int count);
        if(argv==IntPtr.Zero)throw new InvalidDataException();
        try{if(count is <1 or >33)throw new InvalidDataException();return Enumerable.Range(1,count-1).Select(i=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv,i*IntPtr.Size))??"").ToArray();}
        finally{LocalFree(argv);}
    }
}
