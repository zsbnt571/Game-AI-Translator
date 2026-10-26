using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ScreenshotTranslationUiTester;

// Defold 1.2.x uses a fixed logical dt with vsync. These in-memory launch
// overrides select variable dt; they do not edit the project or authorize a
// speed component. Ordinary/translation launches never receive the overrides.
internal static class DefoldSpeedLaunch
{
    internal const string RestartMessage="此 Defold 游戏需要以修改模式启动才能调整速度。请正常退出游戏，再点击“开启修改并启动”。";
    private const string Vsync="--config=display.vsync=0",Frequency="--config=display.update_frequency=0";
    private sealed record Prepared(string Exe,string[] Arguments);
    private static readonly object sync=new();
    private static readonly ConditionalWeakTable<ProcessStartInfo,Prepared> preparations=new();
    private static readonly Dictionary<string,GameSpeedProcess> launches=new(StringComparer.OrdinalIgnoreCase);

    internal static bool Prepare(GameInfo game,ProcessStartInfo info,bool explicitModification)
    {
        if(game.AdapterId!="defold"||!explicitModification)return false;
        string exe=GameLaunchPathIdentity.Canonical(game.ExePath);
        if(!string.Equals(GameLaunchPathIdentity.Canonical(info.FileName),exe,StringComparison.OrdinalIgnoreCase))
            throw new IOException("游戏启动路径不匹配，未启用速度修改。");
        if(info.UseShellExecute||!string.IsNullOrWhiteSpace(info.Arguments))
            throw new IOException("无法安全添加游戏速度启动参数，未启动游戏。");
        // The old engine uses the FIRST matching --config argument. Keep our
        // complete keys first, and remove duplicate exact keys only.
        for(int i=info.ArgumentList.Count-1;i>=0;i--)
            if(info.ArgumentList[i].StartsWith("--config=display.vsync=",StringComparison.Ordinal)||
               info.ArgumentList[i].StartsWith("--config=display.update_frequency=",StringComparison.Ordinal))info.ArgumentList.RemoveAt(i);
        info.ArgumentList.Insert(0,Frequency);info.ArgumentList.Insert(0,Vsync);
        lock(sync){preparations.Remove(info);preparations.Add(info,new(exe,info.ArgumentList.ToArray()));}
        return true;
    }

    internal static void Launched(GameInfo game,Process process,bool prepared)
    {
        if(game.AdapterId!="defold")return;
        string exe=GameLaunchPathIdentity.Canonical(game.ExePath);
        lock(sync)
        {
            launches.Remove(exe);
            if(!prepared)return;
            var info=process.StartInfo;
            if(!preparations.TryGetValue(info,out var preparation))return;
            preparations.Remove(info);
            if(preparation.Exe!=exe||!preparation.Arguments.SequenceEqual(info.ArgumentList)||
               info.UseShellExecute||!string.IsNullOrWhiteSpace(info.Arguments))return;
            // Query identity of this just-created process only. Never scan,
            // read game memory, inject, or trust an exe-only persistent flag.
            var identity=GameSpeedProcessDiscovery.Read(process.Id);
            if(identity is not null&&string.Equals(identity.ExePath,exe,StringComparison.OrdinalIgnoreCase))launches[exe]=identity;
        }
    }

    internal static void RequirePrepared(GameInfo game,GameSpeedProcess process)
    {
        if(game.AdapterId!="defold")return;
        string exe=GameLaunchPathIdentity.Canonical(game.ExePath);
        lock(sync)
            if(launches.TryGetValue(exe,out var launched)&&launched==process)return;
        throw new IOException(RestartMessage);
    }
}
