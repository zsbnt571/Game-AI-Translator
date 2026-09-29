using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class PortableDataStorage
{
    private sealed record Location(string Path,string? CopyFrom=null,string? MigrationId=null);
    private sealed record Receipt(string MigrationId,string Source);
    internal static string LocationFile=>System.IO.Path.Combine(AppContext.BaseDirectory,"data-location.json");
    internal static string Resolve(string applicationRoot)
    {
        var locationFile=System.IO.Path.Combine(applicationRoot,"data-location.json");
        if(!File.Exists(locationFile))return System.IO.Path.Combine(applicationRoot,"data");
        if(new FileInfo(locationFile).Length>16384)throw new IOException("数据位置配置无法读取，未使用其他位置。");
        var location=JsonSerializer.Deserialize<Location>(File.ReadAllText(locationFile))??throw new IOException("数据位置配置为空。");
        var target=AppDataPaths.ValidateExplicitRoot(System.IO.Path.GetFullPath(location.Path,applicationRoot));
        if(location.CopyFrom is { } source)
        {
            CopyForRelocation(source,target,location.MigrationId);
            WriteJson(locationFile,new Location(target));
        }
        return target;
    }
    internal static void RequestLocation(string target)
    {
        target=AppDataPaths.ValidateExplicitRoot(target);
        if(Same(target,AppDataPaths.Root)){WriteJson(LocationFile,new Location(target));return;}
        ValidateDestination(AppDataPaths.Root,target);
        WriteJson(LocationFile,new Location(target,AppDataPaths.Root,Guid.NewGuid().ToString("N")));
    }
    internal static bool Same(string a,string b)=>string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\'),System.IO.Path.GetFullPath(b).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
    private static void ValidateDestination(string source,string target)
    {
        source=AppDataPaths.ValidateExplicitRoot(source);target=AppDataPaths.ValidateExplicitRoot(target);
        if(Same(source,target)||target.StartsWith(source+"\\",StringComparison.OrdinalIgnoreCase)||source.StartsWith(target+"\\",StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择当前数据目录之外的独立空文件夹。");
        if(Directory.Exists(target)&&Directory.EnumerateFileSystemEntries(target).Any())throw new IOException("目标文件夹已有文件；请选择空文件夹，避免覆盖已有数据。");
    }
    internal static void CopyForRelocation(string source,string target,string? migrationId=null)
    {
        source=AppDataPaths.ValidateExplicitRoot(source);target=AppDataPaths.ValidateExplicitRoot(target);
        // A completed copy can be resumed if updating the small location file failed.
        // A random receipt binds the target to this pending operation; existing folders
        // without that receipt are never treated as a successful copy.
        const string receiptName=".fusion-migration.json";
        if(migrationId is not null&&Guid.TryParseExact(migrationId,"N",out _)&&File.Exists(System.IO.Path.Combine(target,receiptName)))
        {
            var receiptPath=System.IO.Path.Combine(target,receiptName);
            if(new FileInfo(receiptPath).Length<16384)
            {
                var receipt=JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receiptPath));
                if(receipt?.MigrationId==migrationId&&Same(receipt.Source,source))return;
            }
        }
        ValidateDestination(source,target);
        var stage=target+".fusion-copy-"+Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        // Preserve the source and retain an incomplete staging directory if any validation fails.
        // Never traverse junctions or merge into an existing collection.
        var directories=new Stack<string>();directories.Push(source);
        var files=new List<string>();
        while(directories.Count>0)
        {
            var directory=directories.Pop();AppDataPaths.ValidateExplicitRoot(directory);
            Directory.CreateDirectory(System.IO.Path.Combine(stage,System.IO.Path.GetRelativePath(source,directory)));
            files.AddRange(Directory.EnumerateFiles(directory));
            foreach(var dir in Directory.EnumerateDirectories(directory)){AppDataPaths.ValidateExplicitRoot(dir);directories.Push(dir);}
        }
        foreach(var file in files)
        {
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("数据目录中包含链接，未迁移。");
            var relative=System.IO.Path.GetRelativePath(source,file);
            var destination=System.IO.Path.GetFullPath(System.IO.Path.Combine(stage,relative));
            if(!destination.StartsWith(stage+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("迁移路径超出目标。");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);File.Copy(file,destination,false);
            using var a=File.OpenRead(file);using var b=File.OpenRead(destination);
            if(!SHA256.HashData(a).SequenceEqual(SHA256.HashData(b)))throw new IOException("数据复制校验失败，原数据已保留。");
        }
        if(migrationId is not null)WriteJson(System.IO.Path.Combine(stage,receiptName),new Receipt(migrationId,source));
        ValidateDestination(source,target);
        if(Directory.Exists(target))Directory.Delete(target,false);
        Directory.Move(stage,target);
    }
    internal static void WriteJson<T>(string path,T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".pending";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(stream,value,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}
            if(File.Exists(path))File.Replace(temp,path,path+".previous");else File.Move(temp,path);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
