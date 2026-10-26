using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

// Session credentials belong to one explicit launch. A new game must not inherit
// another engine's connection, startup cache or collection request from its host.
internal static class GameLaunchEnvironment
{
    internal static void ClearSession(ProcessStartInfo start)
    {
        foreach(string key in start.Environment.Keys.Where(key=>key.StartsWith("FUSION_",StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
    }

    internal static ProcessStartInfo Ordinary(string exe)
    {
        var start=new ProcessStartInfo(exe){WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=false};
        ClearSession(start);
        return start;
    }
}
