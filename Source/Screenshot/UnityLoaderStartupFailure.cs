namespace ScreenshotTranslationUiTester;

// Only new/changed preloader reports from this launch can end its wait. Historical
// reports and unrelated Unity exceptions never count as current loader failures.
internal sealed class UnityLoaderStartupFailure
{
    private readonly string root;
    private readonly DateTime started=DateTime.UtcNow;
    private readonly Dictionary<string,(long Length,long Written)> before=[];
    internal UnityLoaderStartupFailure(string exe)
    {
        root=Path.GetDirectoryName(Path.GetFullPath(exe))!;
        foreach(var file in Files())before[file.FullName]=(file.Length,file.LastWriteTimeUtc.Ticks);
    }
    private IEnumerable<FileInfo> Files()
    {
        try
        {
            UnityMonoLaunchPath.RequirePlainParents(root);
            return new DirectoryInfo(root).EnumerateFiles("preloader_*.log",new EnumerationOptions{RecurseSubdirectories=false,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint})
                .Take(1024).OrderByDescending(f=>f.LastWriteTimeUtc).ToArray();
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return [];}
    }
    internal string? Read()
    {
        foreach(var file in Files().Take(8))
        {
            try
            {
                if(file.Length is <=0 or >65536)continue;
                bool existed=before.TryGetValue(file.FullName,out var old);
                // A changed pre-existing report is current even on filesystems
                // whose write timestamps have coarser precision than UtcNow.
                if(existed&&old==(file.Length,file.LastWriteTimeUtc.Ticks)||!existed&&file.LastWriteTimeUtc<started.AddSeconds(-2))continue;
                using var stream=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var reader=new StreamReader(stream);char[] buffer=new char[65537];int length=reader.ReadBlock(buffer,0,buffer.Length);
                if(length>65536)continue;string text=new(buffer,0,length);
                if(!text.Contains("Exception",StringComparison.Ordinal))continue;
                if(!text.Contains("Doorstop.Entrypoint.Start",StringComparison.Ordinal)
                    &&!(text.Contains("Could not run preloader!",StringComparison.Ordinal)&&text.Contains("BepInEx.Unity.Mono.Preloader",StringComparison.Ordinal)))continue;
                if(!text.Contains("BepInEx",StringComparison.Ordinal))continue;
                string cause=text.Contains("Illegal byte sequence",StringComparison.Ordinal)||text.Contains("StackTraceFixes.OnILChainRefresh",StringComparison.Ordinal)
                    ?"加载器在初始化运行时补丁时失败，可能与目录编码兼容性有关":"现有加载器初始化失败";
                return cause+"；翻译组件尚未启动，继续等待不会得到译文。日志："+file.Name;
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        }
        return null;
    }
}
