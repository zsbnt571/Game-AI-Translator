namespace ScreenshotTranslationUiTester;

internal enum GameLaunchMode { Unknown, Ordinary, Translation, Modification }

// UI-thread owned. Viewing a different game never ends or resets a session.
internal sealed class GameWorkspaceSession
{
    internal GameInfo Game { get; set; }
    internal int Section { get; set; } = 1;
    internal bool TranslationRequested { get; set; }
    internal bool Busy { get; set; }
    internal string Feedback { get; set; } = "";
    internal string TranslationBlockedReason { get; set; } = "";
    internal bool LaunchedWithoutBridge { get; set; }
    internal GameLaunchMode LaunchMode { get; private set; }
    internal FusionGameProcessState Process { get; private set; } = FusionGameProcessState.Unknown;
    internal DateTime LaunchPendingUntil { get; private set; }
    internal bool LaunchPending => DateTime.UtcNow < LaunchPendingUntil;
    internal int Revision { get; private set; }
    internal string? AppliedProfileId { get; private set; }
    internal string? AppliedProfileName { get; private set; }
    internal string? AppliedSettingsIdentity { get; private set; }
    internal GameWorkspaceSession(GameInfo game) => Game = game;

    internal void Launched(GameLaunchMode mode, string? profileId,string? profileName=null,string? settingsIdentity=null)
    {
        Revision++;
        LaunchMode = mode;
        AppliedProfileId = profileId;
        AppliedProfileName=profileName;AppliedSettingsIdentity=settingsIdentity;
        TranslationRequested = false;
        TranslationBlockedReason = "";
        LaunchedWithoutBridge = false;
        LaunchPendingUntil = DateTime.UtcNow.AddSeconds(15);
        Process = FusionGameProcessState.Unknown;
        Feedback = "已发起启动，正在等待运行检测。";
    }

    internal void Observe(FusionGameProcessState process)
    {
        var previous=Process;
        Process = process;
        if(previous==FusionGameProcessState.Running&&process==FusionGameProcessState.Stopped&&!Busy)Feedback="";
        if (process == FusionGameProcessState.Running) LaunchPendingUntil = default;
        if (process == FusionGameProcessState.Stopped && !LaunchPending)
        {
            LaunchMode = GameLaunchMode.Unknown;
            AppliedProfileId = null;
            AppliedProfileName=null;AppliedSettingsIdentity=null;
            if (Feedback == "已发起启动，正在等待运行检测。") Feedback = "未检测到游戏运行，可以重试启动。";
        }
        else if (process == FusionGameProcessState.Running && Feedback == "已发起启动，正在等待运行检测。") Feedback = "";
    }
    internal void LaunchExited(){LaunchPendingUntil=default;Observe(FusionGameProcessState.Stopped);}
}

internal sealed class GameWorkspaceSessions
{
    private readonly Dictionary<string, GameWorkspaceSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    internal GameWorkspaceSession Get(GameInfo game)
    {
        var path = RecentGameStore.Normalize(game.ExePath);
        if (!sessions.TryGetValue(path, out var session)) sessions[path] = session = new(game);
        session.Game = game;
        return session;
    }
    internal GameWorkspaceSession? Find(string path) => sessions.GetValueOrDefault(RecentGameStore.Normalize(path));
    internal GameWorkspaceSession[] Snapshot() => sessions.Values.ToArray();
}

internal sealed partial class FusionConfiguration
{
    internal static string GameProfileKey(string path) => RecentGameStore.Normalize(path).ToUpperInvariant();
    internal string GameProfileId(string path) => State.GameProfiles.GetValueOrDefault(GameProfileKey(path)) ?? SelectedId(true);
    internal void PinGameProfile(string path)
    {
        var key = GameProfileKey(path);
        if (!State.GameProfiles.ContainsKey(key)) Change(next => next.GameProfiles[key] = SelectedId(true));
    }
    internal void SelectGameProfile(string path, string id)
    {
        if (!State.Profiles.ContainsKey(id)) throw new InvalidOperationException("方案已不存在，请重新选择。");
        Change(next => next.GameProfiles[GameProfileKey(path)] = id);
    }
    internal ApiSettings GameSettings(ApiSettings basis, string path)
    {
        if (!State.Profiles.TryGetValue(GameProfileId(path), out var profile)) throw new InvalidOperationException("此游戏的翻译方案已不存在，请重新选择。");
        var result = ApiSettingsSnapshot.Copy(basis);
        ApplyTranslation(result, profile);
        return result;
    }
    internal void RequireGameReady(string path)
    {
        var profile = State.Profiles[GameProfileId(path)];
        var missing = Missing(profile);
        if (missing.Count > 0) throw new InvalidOperationException("翻译方案尚缺：" + string.Join("、", missing) + "。请先编辑方案。");
        if (string.IsNullOrEmpty(profile.ApiKey)) throw new InvalidOperationException("当前游戏适配仍需要 API 密钥，请选择已配置密钥的方案。");
    }
}
