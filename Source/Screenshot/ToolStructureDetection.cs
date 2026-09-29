using System.Text.Json;
namespace ScreenshotTranslationUiTester;
internal static class ToolStructureDetection
{
    // Helpers shipped next to an engine must be rejected before directory-based
    // engine detection. A chromedriver next to renpy is not a Ren'Py game.
    internal static bool IsKnownHelper(string exe)=>System.Text.RegularExpressions.Regex.IsMatch(
        Path.GetFileNameWithoutExtension(exe),@"^(chromedriver|geckodriver|msedgedriver|iedriverserver|selenium-manager|crashpad_handler|notification_helper|createdump|cefsubprocess|ffmpeg|ffprobe|pythonw?|nwjc|steamwebhelper)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase|System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal static GameInfo? Detect(string exe,string architecture)
    {
        string root=Path.GetDirectoryName(exe)!,name=Path.GetFileNameWithoutExtension(exe);
        if(IsKnownHelper(exe))return new(exe,name,"辅助工具",architecture,"这是游戏附带的辅助程序，请选择游戏主程序。",SupportLevel.Unknown,null);
        if(name.Equals("ReiPatcher",StringComparison.OrdinalIgnoreCase)
            &&File.Exists(Path.Combine(root,"Mono.Cecil.dll"))
            &&(Directory.Exists(Path.Combine(root,"Patches"))||File.Exists(Path.Combine(root,"ExIni.dll"))))
            return new(exe,name,"辅助工具",architecture,"这是 ReiPatcher 工具程序。请从其上级游戏目录选择游戏主程序。",SupportLevel.Unknown,null);
        string unpacked=Path.Combine(root,"resources","app");
        string manifest=Path.Combine(unpacked,"package.json"),runtime=Path.Combine(unpacked,"game","cocos2d-js-min.js");
        if(File.Exists(manifest)&&File.Exists(runtime)&&new FileInfo(manifest).Length<131072)
            try
            {
                using var json=JsonDocument.Parse(File.ReadAllText(manifest));
                if(json.RootElement.TryGetProperty("main",out var main)&&main.ValueKind==JsonValueKind.String
                    &&File.Exists(Path.Combine(unpacked,main.GetString()!)))
                    return new(exe,name,"Cocos / Electron",architecture,"已识别网页游戏文本资源；此引擎的内嵌替换尚未接入。",SupportLevel.Planned,unpacked);
            }
            catch(Exception ex)when(ex is IOException or JsonException or ArgumentException){}
        return null;
    }
}
