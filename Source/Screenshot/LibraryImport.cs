using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;
internal sealed record LibraryImportScan(string[] Games,int Skipped,bool Limited);
internal static class LibraryImport
{
    internal static bool IsCandidate(string path)=>path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)&&!ToolStructureDetection.IsKnownHelper(path)&&
        !Regex.IsMatch(Path.GetFileNameWithoutExtension(path),@"^(unins|uninstall|setup|install|unitycrash|crashreport|crashhandler|dxsetup|vcredist|vc_redist|dotnet|redist|reipatcher|patcher|updater|helper|notification_helper|createdump|cefsubprocess|ffmpeg|pythonw?$|nwjc$|steamwebhelper|UE4Prereq|UEPrereq)",RegexOptions.IgnoreCase)&&
        !Regex.IsMatch(path,@"[\\/](ReiPatcher|MonoBleedingEdge|_CommonRedist|redist|redistributables|Engine|renpy|lib|BepInEx|MelonLoader)[\\/]|_Data[\\/](Managed|Plugins)[\\/]",RegexOptions.IgnoreCase);
    internal static string[] FindCandidates(IEnumerable<string> paths)=>Scan(paths).Games;
    internal static LibraryImportScan Scan(IEnumerable<string> paths)
    {
        var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int skipped=0;bool limited=false;
        foreach(var path in paths)
        {
            if(File.Exists(path)){if(IsCandidate(path))found.Add(RecentGameStore.Normalize(path));else skipped++;}
            else if(Directory.Exists(path))
            {
                var options=new EnumerationOptions{RecurseSubdirectories=true,MaxRecursionDepth=8,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint};
                foreach(var file in Directory.EnumerateFiles(path,"*.exe",options))
                {if(IsCandidate(file))found.Add(RecentGameStore.Normalize(file));else skipped++;if(found.Count>=500){limited=true;break;}}
            }
            if(limited)break;
        }
        // Match Unreal payloads by game name, never by their shared library parent.
        var remove=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var outer in found)
            if(UnrealBootstrap.Target(outer) is {} target&&found.Contains(target))remove.Add(target);
        foreach(var inner in found)
        {
            var match=Regex.Match(inner,@"^(.*)[\\/]Binaries[\\/](Win64|Win32)[\\/]([^\\/]+)\.exe$",RegexOptions.IgnoreCase);
            if(!match.Success)continue;
            var project=match.Groups[1].Value;
            string Name(string f)=>Regex.Replace(Path.GetFileNameWithoutExtension(f),@"(-Win64|-Win32)?-(Shipping|Development)$","",RegexOptions.IgnoreCase);
            var candidates=new[]{project,Path.GetDirectoryName(project)}.Where(d=>d is not null).ToArray();
            var outer=found.FirstOrDefault(f=>f!=inner&&candidates.Contains(Path.GetDirectoryName(f),StringComparer.OrdinalIgnoreCase)&&Name(f).Equals(Name(inner),StringComparison.OrdinalIgnoreCase));
            if(outer is not null)remove.Add(inner);
        }
        foreach(var group in found.Where(f=>!remove.Contains(f)).GroupBy(Path.GetDirectoryName,StringComparer.OrdinalIgnoreCase))
        {
            var dir=group.Key!;var entries=group.ToArray();if(entries.Length<2)continue;
            var named=entries.Where(f=>!Path.GetFileName(f).Equals("nw.exe",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(named.Length==1&&File.Exists(Path.Combine(dir,"package.json")))foreach(var f in entries.Except(named))remove.Add(f);
            foreach(var f in entries.Where(f=>Path.GetFileNameWithoutExtension(f).EndsWith("-32",StringComparison.OrdinalIgnoreCase)))
                if(Directory.Exists(Path.Combine(dir,"renpy"))&&entries.Any(other=>Path.GetFileNameWithoutExtension(other).Equals(Path.GetFileNameWithoutExtension(f)[..^3],StringComparison.OrdinalIgnoreCase)))remove.Add(f);
        }
        return new(found.Except(remove,StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray(),skipped+remove.Count,limited);
    }
}
