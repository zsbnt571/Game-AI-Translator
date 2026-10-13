namespace ScreenshotTranslationUiTester;

// One identity list for library process state, returning to a game, and cover
// discovery. Engine layout rules contribute exact paths, never game titles.
internal static class GameRuntimeExecutables
{
    internal static string[] Resolve(string selected)
    {
        string exe=Path.GetFullPath(selected);
        var paths=new List<string>{exe};
        try
        {
            if(YukarGameAdapter.Detect(exe) is {} yukar){paths.Add(yukar.Player);paths.Add(Path.Combine(yukar.Directory,"FusionYukarBridge.exe"));}
            else if(RenpyGameAdapter.Detect(exe) is {} renpy)paths.AddRange(renpy.RuntimeExecutables);
            else paths.AddRange(EngineStructureDetection.UnrealRuntimeExecutables(exe));
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){ }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
