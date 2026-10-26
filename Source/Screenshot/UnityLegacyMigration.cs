using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Retire only the exact old Fusion assemblies. Configuration, translation TSVs,
// saves, the shared loader, and every other mod remain in their original paths.
internal static class UnityLegacyMigration
{
    internal const string MgiPlugin="BepInEx/plugins/MGITranslator/MGITranslator.dll";
    internal const string CloudPlugin="BepInEx/plugins/CloudMeadowTranslator/CloudMeadowTranslator.dll";
    internal const string JournalName=".fusion-unity-legacy-migration.json";
    private static readonly Dictionary<string,string[]> Known=new(StringComparer.OrdinalIgnoreCase)
    {
        [MgiPlugin]=[
            "FFB6EA283ED84FE4C38275D423A4246EC78B86F808BE670A27B50EAD6831F4D5", // 119/120 payload
            "80AC603E1B177E6E4E6A605B45826BEC21496A1C5E94BE184F363CAA7CD47CD1", // 121 payload
            "A115B2411472039E42C3F3535438743B5C8A73A06B6F4CE4A149A4890C3BCC88"], // historical installer identity
        [CloudPlugin]=[
            "61B9E2BB9BA8E51E34E6CF69CDE51B04E58B28A49FD2569430C2E0A2D5960141", // 119/120 payload
            "6CB00141695A934CCE53D2AABA8EBC3E497F2068700F1E44FB055394F0518F6F"] // 121 payload
    };
    internal sealed record Entry(string Path,string Hash);
    internal sealed record Record(int Version,string Exe,string BackupId,bool Complete,Entry[] Files);
    private static string Root(string exe)=>Path.GetDirectoryName(Path.GetFullPath(exe))!;
    private static string Safe(string root,string relative)
    {
        if(Path.IsPathRooted(relative)||relative.Contains(':'))throw new IOException("旧组件迁移路径无效，未改动游戏。");
        string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("旧组件迁移路径越界，未改动游戏。");
        for(string? item=full;item is not null;item=Path.GetDirectoryName(item))
            if((File.Exists(item)||Directory.Exists(item))&&(File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("旧组件迁移路径包含链接，未改动游戏。");
        return full;
    }
    private static string Hash(string file){using var input=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(input));}
    private static bool Recognized(string path,string hash)=>Known.TryGetValue(path,out var hashes)&&hashes.Contains(hash,StringComparer.OrdinalIgnoreCase);
    private static string Backup(Record record,Entry item)=>".fusion-unity-backups/legacy-"+record.BackupId+"/"+item.Path;
    private static void Write(string root,Record record)
    {
        string file=Safe(root,JournalName),temporary=Safe(root,JournalName+"."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(output,record,new JsonSerializerOptions{WriteIndented=true});output.Flush(true);}
            File.Move(temporary,file,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static Record Read(string exe)
    {
        string file=Safe(Root(exe),JournalName);
        if(new FileInfo(file).Length>65536)throw new IOException("旧组件迁移记录超过限制，未改动游戏。");
        var record=JsonSerializer.Deserialize<Record>(File.ReadAllText(file));
        if(record is null||record.Version!=1||!string.Equals(record.Exe,Path.GetFullPath(exe),StringComparison.OrdinalIgnoreCase)
            ||!Guid.TryParseExact(record.BackupId,"N",out _)||record.Files is null||record.Files.Length is <1 or >2
            ||record.Files.Any(x=>x is null||!Recognized(x.Path,x.Hash))||record.Files.Select(x=>x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=record.Files.Length)
            throw new IOException("旧组件迁移记录无效，已保留现状。");
        return record;
    }
    internal static void Recover(string exe,bool newerInstalled)
    {
        string root=Root(exe);if(!File.Exists(Safe(root,JournalName)))return;
        var record=Read(exe);if(record.Complete)return;
        // A crash after the new install completed must not reactivate the old DLL
        // beside it. The caller first recovers and verifies the new install ledger.
        if(newerInstalled)
        {
            if(!record.Files.All(x=>!File.Exists(Safe(root,x.Path))&&File.Exists(Safe(root,Backup(record,x)))&&Hash(Safe(root,Backup(record,x)))==x.Hash))
                throw new IOException("新版组件已安装，但旧组件迁移状态有冲突；已保留现状，请核对备份。");
            Write(root,record with{Complete=true});return;
        }
        Rollback(root,record);
    }
    private static void Rollback(string root,Record record)
    {
        // Validate the complete plan before restoring either assembly.
        foreach(var item in record.Files)
        {
            string file=Safe(root,item.Path),backup=Safe(root,Backup(record,item));
            if(File.Exists(file)){if(Hash(file)!=item.Hash)throw new IOException("旧组件迁移期间文件发生改变，已保留备份："+item.Path);}
            else if(!File.Exists(backup)||Hash(backup)!=item.Hash)throw new IOException("旧组件备份缺失或改变，已保留现状："+item.Path);
        }
        foreach(var item in record.Files)
        {
            string file=Safe(root,item.Path);if(File.Exists(file))continue;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);string temporary=Safe(root,item.Path+".restore-"+Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                using(var input=File.OpenRead(Safe(root,Backup(record,item))))using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {input.CopyTo(output);output.Flush(true);}
                if(Hash(temporary)!=item.Hash)throw new IOException("旧组件恢复校验失败，已保留备份。");
                File.Move(temporary,file,false);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        File.Move(Safe(root,JournalName),Safe(root,JournalName+".rolled-back-"+Guid.NewGuid().ToString("N")));
    }
    internal static Transition Begin(string exe)
    {
        string root=Root(exe);var files=new List<Entry>();
        foreach(string relative in Known.Keys)
        {
            string file=Safe(root,relative);if(!File.Exists(file))continue;
            string hash=Hash(file);if(!Recognized(relative,hash))throw new EmbeddedCompatibilityException("旧翻译组件不是已核实的 Fusion 版本，未覆盖或停用："+relative);
            files.Add(new(relative,hash));
        }
        if(files.Count==0)return new(root,null);
        var record=new Record(1,Path.GetFullPath(exe),Guid.NewGuid().ToString("N"),false,files.ToArray());
        // Resolve every absolute target before the first write or move.
        foreach(var file in files){_=Safe(root,file.Path);_=Safe(root,Backup(record,file));}
        if(File.Exists(Safe(root,JournalName)))
        {
            if(!Read(exe).Complete)throw new IOException("旧组件迁移尚未恢复，已保留现状。");
            File.Copy(Safe(root,JournalName),Safe(root,JournalName+".previous-"+Guid.NewGuid().ToString("N")),false);
        }
        Write(root,record);var transition=new Transition(root,record);
        try
        {
            foreach(var item in files)
            {
                string source=Safe(root,item.Path),backup=Safe(root,Backup(record,item));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                {
                    if(Convert.ToHexString(SHA256.HashData(input))!=item.Hash)throw new IOException("旧组件在迁移前发生改变，未替换。");
                    File.Move(source,backup,false);
                    if(Hash(backup)!=item.Hash)throw new IOException("旧组件备份校验失败，已停止安装。");
                }
            }
            return transition;
        }
        catch{transition.Dispose();throw;}
    }
    internal sealed class Transition(string root,Record? record):IDisposable
    {
        private bool retain;
        internal void Commit(){retain=true;if(record is not null)Write(root,record with{Complete=true});}
        public void Dispose(){if(!retain&&record is not null){retain=true;Rollback(root,record);}}
    }
}
