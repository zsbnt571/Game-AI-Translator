using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ScreenshotTranslationUiTester;

// This exception to the foreign-loader rule recognizes the signed Microsoft
// redistributable. It never loads the inspected DLL or trusts metadata alone.
internal static class OfficialXInput
{
    internal const long MaximumBytes=8*1024*1024;
    private static readonly string[] Exports={"XInputEnable","XInputGetBatteryInformation","XInputGetCapabilities",
        "XInputGetDSoundAudioDeviceGuids","XInputGetKeystroke","XInputGetState","XInputSetState"};

    internal static bool IsOfficial(string path)
    {
        if(!OperatingSystem.IsWindows()||!Path.GetFileName(path).Equals("xinput1_3.dll",StringComparison.OrdinalIgnoreCase))return false;
        try
        {
            if((File.GetAttributes(path)&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)return false;
            // Deny simultaneous replacement/write for the complete inspection;
            // no size/mtime classification cache can outlive these bytes.
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(input.Length<1024||input.Length>MaximumBytes)return false;
            using var pe=new PEReader(input,PEStreamOptions.LeaveOpen);
            if(pe.PEHeaders.CoffHeader.Machine!=Machine.Amd64||pe.PEHeaders.PEHeader?.Magic!=PEMagic.PE32Plus
                ||(pe.PEHeaders.CoffHeader.Characteristics&Characteristics.Dll)==0||!HasStandardExports(pe))return false;
            var version=FileVersionInfo.GetVersionInfo(path);
            if(!string.Equals(version.OriginalFilename,"XInput1_3.dll",StringComparison.OrdinalIgnoreCase)
                ||version.InternalName!="XInput"||version.FileDescription!="Microsoft Common Controller API"
                ||version.CompanyName!="Microsoft Corporation"||version.ProductName?.Contains("DirectX",StringComparison.Ordinal)!=true)return false;
            if(!VerifyOffline(path,input.SafeFileHandle.DangerousGetHandle()))return false;
            using var signed=X509Certificate.CreateFromSignedFile(path);
            using var certificate=new X509Certificate2(signed);
            // A valid signature by another publisher is not an official XInput.
            var names=certificate.SubjectName.EnumerateRelativeDistinguishedNames().ToArray();
            return names.Any(n=>!n.HasMultipleElements&&n.GetSingleElementType().Value=="2.5.4.3"&&n.GetSingleElementValue()=="Microsoft Corporation")
                &&names.Any(n=>!n.HasMultipleElements&&n.GetSingleElementType().Value=="2.5.4.10"&&n.GetSingleElementValue()=="Microsoft Corporation");
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or BadImageFormatException or CryptographicException
            or ArgumentException or OverflowException or InvalidOperationException or System.Security.SecurityException)
        {return false;}
    }

    private static bool HasStandardExports(PEReader pe)
    {
        int rva=pe.PEHeaders.PEHeader!.ExportTableDirectory.RelativeVirtualAddress;
        if(rva==0)return false;
        var reader=pe.GetSectionData(rva).GetReader(0,40);
        reader.Offset=12;int nameRva=reader.ReadInt32();
        reader.Offset=24;int count=reader.ReadInt32();reader.Offset=32;int namesRva=reader.ReadInt32();
        if(count<Exports.Length||count>Exports.Length+1||!ReadName(pe,nameRva).Equals("xinput1_3.dll",StringComparison.OrdinalIgnoreCase))return false;
        var names=pe.GetSectionData(namesRva).GetReader(0,checked(count*4));
        var found=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<count;i++)found.Add(ReadName(pe,names.ReadInt32()));
        return Exports.All(found.Contains)&&found.All(n=>n=="DllMain"||Exports.Contains(n,StringComparer.Ordinal));
    }

    private static string ReadName(PEReader pe,int rva)
    {
        var block=pe.GetSectionData(rva);var reader=block.GetReader(0,Math.Min(block.Length,256));
        var bytes=new List<byte>();
        while(reader.RemainingBytes>0){byte b=reader.ReadByte();if(b==0)return Encoding.ASCII.GetString(bytes.ToArray());if(b>127)break;bytes.Add(b);}
        throw new BadImageFormatException("Invalid XInput export name");
    }

    [DllImport("wintrust.dll",ExactSpelling=true,CharSet=CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window,ref Guid action,ref TrustData data);

    private static bool VerifyOffline(string path,IntPtr handle)
    {
        var file=new TrustFile {Size=(uint)Marshal.SizeOf<TrustFile>(),Path=Marshal.StringToCoTaskMemUni(Path.GetFullPath(path)),Handle=handle};
        IntPtr pointer=IntPtr.Zero;
        try
        {
            pointer=Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());Marshal.StructureToPtr(file,pointer,false);
            var data=new TrustData {Size=(uint)Marshal.SizeOf<TrustData>(),UiChoice=2,UnionChoice=1,File=pointer,StateAction=1,
                // WTD_CACHE_ONLY_URL_RETRIEVAL forbids certificate network I/O;
                // no HASH_ONLY or NO_POLICY_USAGE shortcuts are used.
                ProviderFlags=0x1000|0x10|0x2000};
            var action=new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            try{return WinVerifyTrust(new IntPtr(-1),ref action,ref data)==0;}
            finally{data.StateAction=2;WinVerifyTrust(new IntPtr(-1),ref action,ref data);}
        }
        finally{if(pointer!=IntPtr.Zero)Marshal.FreeHGlobal(pointer);Marshal.FreeCoTaskMem(file.Path);}
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile {internal uint Size;internal IntPtr Path,Handle,KnownSubject;}
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        internal uint Size;internal IntPtr PolicyCallback,SipClient;internal uint UiChoice,RevocationChecks,UnionChoice;
        internal IntPtr File;internal uint StateAction;internal IntPtr StateData,UrlReference;
        internal uint ProviderFlags,UiContext;internal IntPtr SignatureSettings;
    }
}

internal static class UnrealComponentCompatibility
{
    internal static readonly string[] Components={"dwmapi.dll","UE4SS.dll","ue4ss","Mods","xinput1_3.dll","version.dll","winmm.dll","dinput8.dll"};
    internal static string? FindConflict(string root,bool ownedPayloadInstalled)
    {
        foreach(string name in Components)
        {
            // Reuse is allowed only after UnrealGameAdapter validates its ledger.
            if(ownedPayloadInstalled&&(name=="dwmapi.dll"||name=="ue4ss"))continue;
            string path=Path.Combine(root,name);
            if(!File.Exists(path)&&!Directory.Exists(path))continue;
            if(name=="xinput1_3.dll"&&OfficialXInput.IsOfficial(path))continue;
            return name;
        }
        return null;
    }

    internal static string InputStamp(string path)
    {
        try
        {
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(file.Length>OfficialXInput.MaximumBytes)return "oversized-input";
            return Convert.ToHexString(SHA256.HashData(file));
        }
        catch(IOException){return "unreadable-input";}catch(UnauthorizedAccessException){return "unreadable-input";}
    }
}
