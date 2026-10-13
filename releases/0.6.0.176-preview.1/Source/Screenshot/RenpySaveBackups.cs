using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    // Ren'Py desktop releases can mirror saves in game/saves even when savedir
    // points elsewhere. Keep the original primary layout and record both roots.
    internal static void BackupRenpySaveLocations(string exe,string savedir,string target)
    {
        const string infoFile="fusion-backup-info.json",locationsFile="fusion-backup-locations.json";
        static string Full(string path)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        static bool Within(string child,string root)=>child.Equals(root,StringComparison.OrdinalIgnoreCase)
            ||child.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        static void NoLinks(string path)
        {
            for(string? part=path;part is not null;part=Path.GetDirectoryName(part))
                if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("存档备份路径包含链接，未执行修改。");
        }
        string primary=Full(savedir),backup=Full(target);
        string? local=RenpyGameAdapter.Detect(exe) is {} installation?Full(Path.Combine(installation.GameDirectory,"saves")):null;
        NoLinks(primary);NoLinks(backup);if(local is not null)NoLinks(local);
        if(File.Exists(backup)||Directory.Exists(backup))throw new IOException("存档备份目录已存在，未覆盖旧备份。");
        foreach(string source in local is null?new[]{primary}:new[]{primary,local})
        {
            if(Within(backup,source)||Within(source,backup))throw new IOException("存档目录与备份目录重叠，未执行修改。");
            if(File.Exists(Path.Combine(source,infoFile))||Directory.Exists(Path.Combine(source,infoFile))
                ||File.Exists(Path.Combine(source,locationsFile))||Directory.Exists(Path.Combine(source,locationsFile)))
                throw new IOException("存档中存在同名备份说明文件，未覆盖该文件或执行修改。");
        }
        BackupRenpySaves(primary,backup);
        var locations=new List<object>{new{kind="configured",source=primary,relativeDestination=".",existed=Directory.Exists(primary)}};
        string localStatus=local is null?"engine-not-detected":local.Equals(primary,StringComparison.OrdinalIgnoreCase)?"same-directory":!Directory.Exists(local)?"not-present":"backed-up";
        if(localStatus=="backed-up")
        {
            string folder="fusion-game-local-saves-"+Guid.NewGuid().ToString("N"),destination=Path.Combine(backup,folder);
            if(File.Exists(destination)||Directory.Exists(destination))throw new IOException("附加存档备份目录已存在，未覆盖。");
            BackupRenpySaves(local!,destination);
            locations.Add(new{kind="game-local",source=local!,relativeDestination=folder,existed=true});
        }
        // Written last: its existence means every advertised directory copied.
        using var output=new FileStream(Path.Combine(backup,locationsFile),FileMode.CreateNew,FileAccess.Write,FileShare.None);
        JsonSerializer.Serialize(output,new{schema=1,createdUtc=DateTime.UtcNow,executable=Path.GetFullPath(exe),gameLocalSource=local,gameLocalStatus=localStatus,locations});
        output.Flush(true);
    }
}
