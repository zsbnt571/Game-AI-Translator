using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace ScreenshotTranslationUiTester;

// New packages can carry a bootstrap descriptor, not an aging copy of private
// data. Explicit --data-root and existing portable data always take precedence.
internal static class DataContinuity
{
    private sealed record Location(string Path,bool RequireExisting=true);
    private sealed record Bootstrap(int Version,string Source,string Target,string MigrationId);
    internal static string RegistrationPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FusionR1","data-location-v1.json");
    internal static string? Resolve(string applicationRoot,string? registrationOverride=null)
    {
        string local=Path.Combine(applicationRoot,"data");
        if(Directory.Exists(local)&&Directory.EnumerateFileSystemEntries(local).Any())return null;
        string registration=registrationOverride??RegistrationPath;
        string descriptor=Path.Combine(applicationRoot,"data-upgrade.json");
        // Existing standalone packages without a descriptor stay standalone.
        // Only an upgrade-aware distribution follows the stable registration.
        if(!File.Exists(descriptor))return null;
        AppDataPaths.ValidateExplicitRoot(Path.GetDirectoryName(Path.GetFullPath(registration))!);
        string id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(registration).ToUpperInvariant())));
        using var mutex=new Mutex(false,"Local\\FusionR1.DataContinuity."+id);bool held=false;
        try
        {
            try{held=mutex.WaitOne(TimeSpan.FromSeconds(30));}catch(AbandonedMutexException){held=true;}
            if(!held)throw new IOException("另一窗口正在继承数据，请稍后重新打开。");
            var bootstrap=Read<Bootstrap>(descriptor);
            if(bootstrap.Version!=1||!Guid.TryParseExact(bootstrap.MigrationId,"N",out _))throw new IOException("升级数据配置无效，原数据未改动。");
            string target;
            if(File.Exists(registration))
            {
                target=AppDataPaths.ValidateExplicitRoot(Read<Location>(registration).Path);
                if(!Directory.Exists(target))throw new IOException("已有数据目录不可用，请检查数据位置；未创建空库代替。");
            }
            else
            {
                string source=AppDataPaths.ValidateExplicitRoot(bootstrap.Source);
                target=AppDataPaths.ValidateExplicitRoot(bootstrap.Target);
                if(!Directory.Exists(source))throw new IOException("旧版数据目录不存在，未创建空库代替。");
                PortableDataStorage.CopyForRelocation(source,target,bootstrap.MigrationId);
                PortableDataStorage.WriteJson(registration,new Location(target));
            }
            // Pin this package too. A later registry change cannot silently move
            // a package that the user has already opened and configured.
            PortableDataStorage.WriteJson(Path.Combine(applicationRoot,"data-location.json"),new Location(target));
            return target;
        }
        finally{if(held)mutex.ReleaseMutex();}
    }
    private static T Read<T>(string path)
    {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0||new FileInfo(path).Length>16384)throw new IOException("数据位置配置无法安全读取。");
        try{return JsonSerializer.Deserialize<T>(File.ReadAllText(path))??throw new IOException("数据位置配置为空。");}
        catch(JsonException ex){throw new IOException("数据位置配置已损坏，原数据保留。",ex);}
    }
}
