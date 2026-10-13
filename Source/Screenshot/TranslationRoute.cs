using System.Text.Json;
namespace ScreenshotTranslationUiTester;

internal static class TranslationRoute
{
    private sealed record Choice(bool GenericTest);
    private static string FileFor(string exe)=>Path.Combine(RpgMakerDataAdapter.Storage(exe),"translation-route.json");
    internal static bool GenericTest(string exe)
    {
        string file=FileFor(exe);if(!File.Exists(file))return false;
        try{return JsonSerializer.Deserialize<Choice>(File.ReadAllText(file))?.GenericTest==true;}
        catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {throw new IOException("翻译接入方式无法读取，请先检查游戏资料目录；未自动切换接入方式。",ex);}
    }
    internal static string DataRoot(string exe)=>GenericTest(exe)?Path.Combine(RpgMakerDataAdapter.Storage(exe),"generic-test"):RpgMakerDataAdapter.Storage(exe);
    internal static void Set(GameInfo game,bool generic)
    {
        if(UnityEmbeddedAdapter.Detect(game.ExePath) is null)throw new IOException("此选择只适用于能够识别运行环境的 Unity 游戏。");
        if(LegacyMgiAdapterInstaller.IsRunning(game))throw new IOException("请退出游戏后更改接入方式。");
        string root=Path.GetDirectoryName(game.ExePath)!;
        // Never orphan the previous install ledger by selecting another adapter.
        if(Directory.EnumerateFiles(root,".fusion*install*.json",SearchOption.TopDirectoryOnly).Any())
            throw new IOException("此目录已有翻译安装记录。请先在游戏菜单中恢复安装前状态，再切换；也可以使用新的独立游戏目录。");
        PortableDataStorage.WriteJson(FileFor(game.ExePath),new Choice(generic));
    }
}
