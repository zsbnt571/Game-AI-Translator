using System.Diagnostics;
namespace ScreenshotTranslationUiTester;

internal static class GameTestCopyEnvironment
{
    internal const string Marker=".fusion-test-copy";
    internal static void Apply(ProcessStartInfo info,bool renpy=false)
    {
        string root=Path.GetDirectoryName(Path.GetFullPath(info.FileName))!,marker=Path.Combine(root,Marker);
        if(!File.Exists(marker))return;
        if(new FileInfo(marker).Length>128||File.ReadAllText(marker).Trim()!="FusionTranslationTestCopy/1")throw new IOException("测试副本标记无效。");
        string data=Path.Combine(root,".fusion-test-data");
        for(string? part=data;part is not null;part=Path.GetDirectoryName(part))
            if((Directory.Exists(part)||File.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("测试数据目录包含链接，未启动。");
        info.UseShellExecute=false;
        foreach(string key in new[]{"APPDATA","LOCALAPPDATA","TEMP","TMP","RENPY_MULTIPERSISTENT"})
        {string path=Path.Combine(data,key);Directory.CreateDirectory(path);info.Environment[key]=path;}
        if(renpy)
        {
            string saves=Path.Combine(data,"saves");Directory.CreateDirectory(saves);
            info.ArgumentList.Add("--savedir");info.ArgumentList.Add(saves);
        }
    }
}
