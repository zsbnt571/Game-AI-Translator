using System.Text.Json;
namespace ScreenshotTranslationUiTester;

// Read-only compatibility for existing profile/cache namespaces. This no longer selects an adapter.
internal static class TranslationRoute
{
    private sealed record Choice(bool GenericTest);
    private static string FileFor(string exe)=>Path.Combine(RpgMakerDataAdapter.Storage(exe),"translation-route.json");
    internal static bool GenericTest(string exe)
    {
        string file=FileFor(exe);if(!File.Exists(file))return false;
        try{return JsonSerializer.Deserialize<Choice>(File.ReadAllText(file))?.GenericTest==true;}
        catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {throw new IOException("旧版译文目录记录无法读取，请检查游戏资料目录；原数据已保留。",ex);}
    }
    internal static string DataRoot(string exe)=>GenericTest(exe)?Path.Combine(RpgMakerDataAdapter.Storage(exe),"generic-test"):RpgMakerDataAdapter.Storage(exe);
}
