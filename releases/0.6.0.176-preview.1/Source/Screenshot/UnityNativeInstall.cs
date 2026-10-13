using System.Security.Cryptography;
namespace ScreenshotTranslationUiTester;
internal static partial class UnityEmbeddedAdapter
{
    internal static bool IsNativeRuntimeInstalled(string exe)=>Detect(exe)is{} game&&ReadLedger(game)?.Files.Any(f=>f.Path==UnityNativeRuntime.Dll)==true;
    private static void InstallNativeRuntime(UnityEmbeddedGame game)
    {
        var previous=ReadLedger(game);
        if(previous is not null&&previous.Files.Any(f=>!f.Path.StartsWith(UnityNativeRuntime.Folder+"/",StringComparison.Ordinal)))throw new EmbeddedCompatibilityException("此副本已有另一种 Fusion 接入，请先恢复原组件或使用干净副本。");
        var payload=UnityNativeRuntime.Payload(game);var old=previous?.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase)??new();var missing=new List<string>();
        foreach(var file in old.Values){string path=SafePath(game.Root,file.Path);if(!File.Exists(path)){if(!file.Owned)throw new IOException("借用组件缺失。");missing.Add(file.Path);}else if(Hash(path)!=file.Hash)throw new IOException("组件已被其他程序改变，未覆盖："+file.Path);}
        var files=new List<InstalledFile>();foreach(var pair in payload)
        {string path=SafePath(game.Root,pair.Key),hash=Convert.ToHexString(SHA256.HashData(pair.Value));bool owned=old.TryGetValue(pair.Key,out var prior)&&prior.Owned;if(File.Exists(path)&&Hash(path)!=hash&&!owned)throw new IOException("原生组件路径已有其他文件，未覆盖："+pair.Key);files.Add(new(pair.Key,hash,owned||!File.Exists(path)));}
        if(old.Keys.Any(p=>!payload.ContainsKey(p)))throw new IOException("原生组件记录包含未知文件。");
        var next=new InstallLedger(1,game.ExePath,game.Backend,game.Architecture,files.ToArray());
        if(missing.Count==0&&old.Count==files.Count&&files.All(f=>old.TryGetValue(f.Path,out var before)&&before==f))return;
        string backup=".fusion-unity-backups/"+Guid.NewGuid().ToString("N");
        foreach(var file in old.Values.Where(f=>f.Owned&&!missing.Contains(f.Path))){string dest=SafePath(game.Root,backup+"/"+file.Path);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(SafePath(game.Root,file.Path),dest);if(Hash(dest)!=file.Hash)throw new IOException("原生组件备份失败。");}
        WriteJsonAtomic(TransactionPath(game),new UpdateRecord(previous,next,backup,missing.ToArray()));
        try
        {
            foreach(var file in files.Where(f=>f.Owned)){string path=SafePath(game.Root,file.Path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllBytes(temp,payload[file.Path]);if(Hash(temp)!=file.Hash)throw new IOException("原生组件写入校验失败。");File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}}
            WriteJsonAtomic(Path.Combine(game.Root,LedgerName),next);File.Delete(TransactionPath(game));
        }
        catch{RecoverUpdate(game);throw;}
    }
}
