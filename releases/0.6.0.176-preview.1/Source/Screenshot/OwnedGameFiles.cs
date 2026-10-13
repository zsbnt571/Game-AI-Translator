using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace ScreenshotTranslationUiTester;

// Durable journal before mutation, all-file preflight before restore. Used by
// script adapters; foreign changes are never silently overwritten.
internal static class OwnedGameFiles
{
    internal sealed record Item(string Path,string? Before,string After,string Backup);
    internal sealed record Ledger(int Version,string Exe,string Payload,Item[] Files);
    internal static string Digest(string path){using var s=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(s));}
    private static string Target(string exe,string name)=>GameMakerGameAdapter.InGame(exe,name);
    private static string BackupName(string path,string? before,int version)
        =>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))+(version==1?"":"-"+(before??"new"))+".bin";
    private static void RecordPath(string record)
    {
        for(string? p=Path.GetFullPath(record);p is not null;p=Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("组件记录目录包含链接，未修改文件。");
    }
    private static Ledger Read(string exe,string record)
    {
        RecordPath(record);
        if(new FileInfo(record).Length>65536)throw new IOException("组件记录过大。");
        var r=JsonSerializer.Deserialize<Ledger>(File.ReadAllText(record))??throw new IOException("组件记录无效。");
        bool Hash(string? s)=>s is {Length:64}&&s.All(Uri.IsHexDigit);
        if(r.Version is not (1 or 2)||r.Exe!=Path.GetFullPath(exe)||r.Files is null||r.Files.Length is <1 or >32||r.Files.Any(f=>f is null)||r.Files.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=r.Files.Length)throw new IOException("组件记录不属于当前游戏。");
        foreach(var f in r.Files){Target(exe,f.Path);if(!Hash(f.After)||f.Before is not null&&!Hash(f.Before)||f.Backup!=BackupName(f.Path,f.Before,r.Version))throw new IOException("组件备份记录无效。");}
        return r;
    }
    internal static bool Installed(string exe,string record,string payload)
    {
        try{var r=Read(exe,record);return r.Payload==payload&&r.Files.All(f=>File.Exists(Target(exe,f.Path))&&Digest(Target(exe,f.Path))==f.After);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException){return false;}
    }
    internal static void Install(string exe,string record,string payload,IReadOnlyDictionary<string,byte[]> changes,ISet<string> created,Action stopped)
    {
        stopped();RecordPath(record);if(File.Exists(record))throw new IOException("已有组件记录，请先恢复后再更新。");
        if(changes.Count is <1 or >32||changes.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=changes.Count||created.Any(n=>!changes.ContainsKey(n)))throw new IOException("组件文件范围无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);var files=new List<Item>();
        using var gate=new FileStream(record+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        if(File.Exists(record))throw new IOException("已有组件记录，请先恢复后再更新。");
        foreach(var p in changes)
        {
            string path=Target(exe,p.Key);bool exists=File.Exists(path);
            if(created.Contains(p.Key)&&exists)throw new IOException("游戏已有同名文件，未覆盖："+p.Key);
            if(!created.Contains(p.Key)&&!exists)throw new IOException("待接入的原文件不存在："+p.Key);
            string? before=exists?Digest(path):null;
            string backup=BackupName(p.Key,before,2);
            string backupPath=Path.Combine(Path.GetDirectoryName(record)!,backup);
            RecordPath(backupPath);
            if(exists){if(!File.Exists(backupPath))File.Copy(path,backupPath);if(Digest(backupPath)!=before)throw new IOException("已有不同版本的备份，未覆盖。");}
            files.Add(new(p.Key,before,Convert.ToHexString(SHA256.HashData(p.Value)),backup));
        }
        stopped();
        foreach(var f in files)if(f.Before is null?File.Exists(Target(exe,f.Path)):!File.Exists(Target(exe,f.Path))||Digest(Target(exe,f.Path))!=f.Before)throw new IOException("准备期间游戏文件发生变化。");
        PortableDataStorage.WriteJson(record,new Ledger(2,Path.GetFullPath(exe),payload,files.ToArray()));
        foreach(var f in files)
        {
            string path=Target(exe,f.Path),temp=path+".fusion-"+Guid.NewGuid().ToString("N")+".tmp";
            try{
                if(f.Before is null?File.Exists(path):!File.Exists(path)||Digest(path)!=f.Before)throw new IOException("游戏文件发生变化，已保留组件记录以供恢复。");
                File.WriteAllBytes(temp,changes[f.Path]);if(Digest(temp)!=f.After)throw new IOException("组件写入校验失败。");File.Move(temp,path,f.Before is not null);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
    internal static void Restore(string exe,string record,Action stopped)
    {
        stopped();RecordPath(record);if(!File.Exists(record))return;
        using var gate=new FileStream(record+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var r=Read(exe,record);
        foreach(var f in r.Files)
        {
            string path=Target(exe,f.Path);string? now=File.Exists(path)?Digest(path):null;
            if(now!=f.Before&&now!=f.After)throw new IOException("组件文件已被其他程序修改，未覆盖："+f.Path);
            string backupPath=Path.Combine(Path.GetDirectoryName(record)!,f.Backup);RecordPath(backupPath);
            if(f.Before is not null&&Digest(backupPath)!=f.Before)throw new IOException("原始文件备份校验失败。");
        }
        stopped();
        foreach(var f in r.Files)
        {
            string path=Target(exe,f.Path);string? now=File.Exists(path)?Digest(path):null;if(now==f.Before)continue;
            if(now!=f.After)throw new IOException("恢复期间文件发生变化，停止恢复。");
            if(f.Before is null){File.Delete(path);continue;}
            string temp=path+".fusion-"+Guid.NewGuid().ToString("N")+".tmp";
            try{File.Copy(Path.Combine(Path.GetDirectoryName(record)!,f.Backup),temp);if(Digest(path)!=f.After||Digest(temp)!=f.Before)throw new IOException("恢复校验失败。");File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
        }
        File.Move(record,record+".restored-"+DateTime.UtcNow.Ticks);
    }
}
