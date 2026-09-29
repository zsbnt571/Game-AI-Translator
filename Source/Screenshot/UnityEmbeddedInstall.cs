using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static partial class UnityEmbeddedAdapter
{
    internal static void CheckCompatibility(UnityEmbeddedGame game)
    {
        if(game.Backend!="IL2CPP")return;
        if(game.Architecture!="x64")throw new EmbeddedCompatibilityException("此 IL2CPP 游戏的架构尚不支持内嵌翻译，可普通启动。");
        string metadata=Path.Combine(game.DataRoot,"il2cpp_data","Metadata","global-metadata.dat");
        if(!File.Exists(metadata))throw new EmbeddedCompatibilityException("未找到标准 IL2CPP 文本组件元数据，本次不安装翻译组件。可普通启动。");
        using var input=File.OpenRead(metadata);using var reader=new BinaryReader(input);
        if(input.Length<64||reader.ReadUInt32()!=0xFAB11BAF)
            throw new EmbeddedCompatibilityException("此游戏使用非标准 IL2CPP 元数据，当前加载组件无法安全接入。可普通启动；反复重启不会解决此兼容问题。");
        int version=reader.ReadInt32();
        // BepInEx 788 pins Cpp2IL development.1452, whose known metadata range is 23-106.
        if(version is <23 or >106)throw new EmbeddedCompatibilityException($"此游戏的 IL2CPP 元数据版本 {version} 不在当前组件的支持范围（23–106）。可普通启动。");
    }

    private sealed record UpdateRecord(InstallLedger? Before,InstallLedger After,string Backup);
    private static string TransactionPath(UnityEmbeddedGame game)=>Path.Combine(game.Root,".fusion-unity-embedded-update.json");
    private static void WriteJsonAtomic<T>(string path,T value)
    {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,value,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static void RecoverUpdate(UnityEmbeddedGame game)
    {
        string pending=TransactionPath(game);if(!File.Exists(pending))return;
        if(new FileInfo(pending).Length>2*1024*1024)throw new IOException("组件更新记录异常，未改动游戏文件。");
        var record=JsonSerializer.Deserialize<UpdateRecord>(File.ReadAllText(pending))??throw new IOException("组件更新记录不可读。");
        if(!string.Equals(record.After.ExePath,game.ExePath,StringComparison.OrdinalIgnoreCase)||record.After.Files.Length>1024||record.Before?.Files.Length>1024)
            throw new IOException("组件更新记录与游戏不一致。");
        var before=record.Before?.Files.ToDictionary(x=>x.Path,StringComparer.OrdinalIgnoreCase)??new();
        var after=record.After.Files.ToDictionary(x=>x.Path,StringComparer.OrdinalIgnoreCase);
        foreach(var item in before.Values.Concat(after.Values).GroupBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()))
        {
            string target=SafePath(game.Root,item.Path);
            if(File.Exists(target))
            {
                string hash=Hash(target);
                if(!(before.TryGetValue(item.Path,out var old)&&hash==old.Hash)&&!(after.TryGetValue(item.Path,out var next)&&hash==next.Hash))
                    throw new IOException("组件更新中断后文件又有改变，请保留备份并检查："+item.Path);
            }
            if(before.TryGetValue(item.Path,out var previous)&&previous.Owned)
            {
                string backup=SafePath(game.Root,record.Backup+"/"+item.Path);
                if(!File.Exists(backup)||Hash(backup)!=previous.Hash)throw new IOException("组件更新备份校验失败："+item.Path);
            }
        }
        foreach(var next in after.Values.Where(x=>x.Owned&&!before.ContainsKey(x.Path)))
        {string path=SafePath(game.Root,next.Path);if(File.Exists(path))File.Delete(path);}
        foreach(var old in before.Values.Where(x=>x.Owned))
        {string path=SafePath(game.Root,old.Path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.Copy(SafePath(game.Root,record.Backup+"/"+old.Path),path,true);}
        string ledger=Path.Combine(game.Root,LedgerName);
        if(record.Before is not null)WriteJsonAtomic(ledger,record.Before);else if(File.Exists(ledger))File.Delete(ledger);
        File.Delete(pending);
    }

    private static void InstallPayload(UnityEmbeddedGame game)
    {
        var previous=ReadLedger(game);
        if(previous is not null&&!IsInstalled(game.ExePath))throw new IOException("Unity 翻译组件已改变，请先恢复或检查该副本。");
        var old=previous?.Files.ToDictionary(x=>x.Path,StringComparer.OrdinalIgnoreCase)??new();
        using var payload=UnityEmbeddedPayload.Open(game.Backend,game.Architecture);using var zip=new ZipArchive(payload,ZipArchiveMode.Read);
        var plan=new List<(ZipArchiveEntry Entry,InstalledFile File)>();var unique=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries.Where(e=>!e.FullName.EndsWith('/')))
        {
            if(!unique.Add(entry.FullName)||entry.Length>96L*1024*1024)throw new IOException("Unity 组件清单无效。");
            string target=SafePath(game.Root,entry.FullName);using var input=entry.Open();string hash=Convert.ToHexString(SHA256.HashData(input));
            bool own=old.TryGetValue(entry.FullName,out var prior)&&prior.Owned;
            if(File.Exists(target)&&Hash(target)!=hash&&!own)throw new EmbeddedCompatibilityException("现有文件与翻译组件冲突："+entry.FullName+"。已保留现有安装；请使用独立干净副本接入翻译。");
            plan.Add((entry,new(entry.FullName,hash,own||!File.Exists(target))));
        }
        var next=new InstallLedger(1,game.ExePath,game.Backend,game.Architecture,plan.Select(x=>x.File).ToArray());
        if(previous is not null&&old.Count==plan.Count&&plan.All(x=>old.TryGetValue(x.File.Path,out var f)&&f==x.File))return;
        string backup=".fusion-unity-backups/"+Guid.NewGuid().ToString("N");
        foreach(var item in old.Values.Where(x=>x.Owned))
        {string dest=SafePath(game.Root,backup+"/"+item.Path);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(SafePath(game.Root,item.Path),dest);if(Hash(dest)!=item.Hash)throw new IOException("组件备份失败，未安装。");}
        WriteJsonAtomic(TransactionPath(game),new UpdateRecord(previous,next,backup));
        try
        {
            foreach(var item in plan.Where(x=>x.File.Owned))
            {
                string target=SafePath(game.Root,item.File.Path);if(File.Exists(target)&&Hash(target)==item.File.Hash)continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);string temp=target+".fusion-"+Guid.NewGuid().ToString("N")+".tmp";
                try{using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var input=item.Entry.Open()){input.CopyTo(output);output.Flush(true);}if(Hash(temp)!=item.File.Hash)throw new IOException("组件写入校验失败。");File.Move(temp,target,true);}
                finally{if(File.Exists(temp))File.Delete(temp);}
            }
            foreach(var retired in old.Values.Where(x=>x.Owned&&!unique.Contains(x.Path)))File.Delete(SafePath(game.Root,retired.Path));
            WriteJsonAtomic(Path.Combine(game.Root,LedgerName),next);
            File.Delete(TransactionPath(game));
        }
        catch{RecoverUpdate(game);throw;}
    }
}
