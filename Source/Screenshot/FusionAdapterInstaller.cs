using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Retained MGI installer. Cloud Meadow uses the separate hash-journal implementation.
internal sealed class LegacyMgiAdapterInstaller
{
    internal const string PluginRelative = "BepInEx/plugins/MGITranslator/MGITranslator.dll";
    internal const string ConfigRelative = "BepInEx/config/local.codex.mgi.translator.cfg";
    private const string ManifestName = ".fusion-translator-install.json";
    private sealed record Entry(string RelativePath, bool Existed, string? Backup);
    private sealed class Manifest
    {
        public string Version { get; set; } = BuildIdentity.BuildVersion;
        public string BackupId { get; set; } = Guid.NewGuid().ToString("N");
        public List<Entry> Files { get; set; } = new();
    }
    internal bool IsInstalled(GameInfo game) => File.Exists(Path.Combine(Path.GetDirectoryName(game.ExePath)!, ManifestName));
    internal static bool IsRunning(GameInfo game)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game.ExePath)))
            using (process)
            {
                try { if (string.Equals(process.MainModule?.FileName, game.ExePath, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { return true; }
            }
        return false;
    }
    private static string InRoot(string root, string relative)
    {
        var full=Path.GetFullPath(Path.Combine(root, relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装路径不属于所选游戏。");
        for(var part=Path.GetDirectoryName(full);part is not null && part.Length>=root.Length;part=Path.GetDirectoryName(part))
            if(Directory.Exists(part)&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("安装路径包含链接，请选择独立游戏目录。");
        return full;
    }
    private static void RequireStopped(GameInfo game)
    {
        if (game.Engine!="Unity Mono" || game.Support != SupportLevel.Supported) throw new InvalidOperationException("当前内嵌适配仅支持已验证的 MGI 游戏。");
        if (IsRunning(game)) throw new InvalidOperationException("游戏仍在运行。请退出游戏后再安装、应用设置或恢复。");
    }
    internal void Install(GameInfo game, ApiSettings settings, string toggleKey)
    {
        RequireStopped(game);
        var root=Path.GetDirectoryName(game.ExePath)!;
        var manifestPath=Path.Combine(root,ManifestName);
        var manifest=File.Exists(manifestPath)?JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))!:new Manifest();
        var updating=File.Exists(manifestPath);
        void Track(string relative)
        {
            if(manifest.Files.Any(x=>x.RelativePath.Equals(relative,StringComparison.OrdinalIgnoreCase)))return;
            var path=InRoot(root,relative);
            var existed=File.Exists(path);
            string? backup=existed?".fusion-translator-backup/"+manifest.BackupId+"/"+relative:null;
            if(backup is not null){var dest=InRoot(root,backup);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(path,dest,false);}
            manifest.Files.Add(new(relative,existed,backup));
            File.WriteAllText(manifestPath,JsonSerializer.Serialize(manifest,new JsonSerializerOptions{WriteIndented=true}));
        }
        using var stream=RuntimePayloadProvider.Open("unity-legacy","Unity","Mono","x64",RuntimePayloadProvider.PackageVersion);
        using var zip=new ZipArchive(stream,ZipArchiveMode.Read);
        foreach(var entry in zip.Entries)
        {
            if(string.IsNullOrEmpty(entry.Name))continue;
            if(updating && !entry.FullName.Equals(PluginRelative,StringComparison.OrdinalIgnoreCase))continue;
            var target=InRoot(root,entry.FullName);
            Track(entry.FullName);Directory.CreateDirectory(Path.GetDirectoryName(target)!);entry.ExtractToFile(target,true);
        }
        Verify(game);
        Track(ConfigRelative);
        WriteConfiguration(InRoot(root,ConfigRelative),settings,toggleKey);
    }
    internal void Verify(GameInfo game)
    {
        var file=InRoot(Path.GetDirectoryName(game.ExePath)!,PluginRelative);
        using var stream=File.OpenRead(file);
        if(Convert.ToHexString(SHA256.HashData(stream))!=FusionPluginIdentity.Sha256)
            throw new InvalidDataException("游戏中的插件与本候选不一致，请重新安装；完整性检查未通过。");
    }
    internal void Restore(GameInfo game)
    {
        RequireStopped(game);
        var root=Path.GetDirectoryName(game.ExePath)!;
        var path=Path.Combine(root,ManifestName);
        if(!File.Exists(path))throw new InvalidOperationException("该游戏没有本候选的安装记录，不执行清理。");
        var manifest=JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        // Validate the complete recovery plan before changing any files.
        foreach(var entry in manifest.Files)
        {
            _=InRoot(root,entry.RelativePath);
            if(entry.Existed&&(entry.Backup is null||!File.Exists(InRoot(root,entry.Backup))))
                throw new IOException("原文件备份不完整，已停止恢复并保留现状。");
        }
        foreach(var entry in manifest.Files.AsEnumerable().Reverse())
        {
            var dest=InRoot(root,entry.RelativePath);
            if(entry.Existed)File.Copy(InRoot(root,entry.Backup!),dest,true);
            else if(File.Exists(dest))File.Delete(dest);
        }
        File.Move(path,path+".restored-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        // Keep the backup and configuration-specific translation caches; never recursively remove BepInEx.
    }
    private static string SingleLine(string value)
    {
        if(value.Contains('\r')||value.Contains('\n'))throw new ArgumentException("接口、模型和密钥不能包含换行。");
        return value.Trim();
    }
    internal static void WriteConfiguration(string path,ApiSettings settings,string toggleKey)
    {
        if(!Uri.TryCreate(settings.ApiUrl,UriKind.Absolute,out var uri)||uri.Scheme is not ("https" or "http"))throw new ArgumentException("请输入有效的 HTTP/HTTPS 接口地址。");
        if(!Enumerable.Range(1,12).Select(x=>"F"+x).Contains(toggleKey))throw new ArgumentException("请选择 F1 至 F12 的游戏内按键。");
        var prompt=Convert.ToBase64String(Encoding.UTF8.GetBytes(FusionConfiguration.PluginPrompt(settings)));
        var text=$"[General]\nEnabled = true\nToggleKey = {toggleKey}\nScanIntervalSeconds = 0.6\nConcurrentRequests = 3\nBatchSize = 8\nRequestTimeoutSeconds = {Math.Clamp(settings.RequestTimeoutSeconds,15,90)}\n\n[API]\nEndpoint = {SingleLine(settings.ApiUrl)}\nApiKey = {SingleLine(settings.ApiKey)}\nModel = {SingleLine(settings.Model)}\n\n[Translation]\nTargetLanguage = {FusionConfiguration.NormalizeLanguage(settings.TargetLanguage)}\nPromptBase64 = {prompt}\n";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text,new UTF8Encoding(false));
    }
}
