using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal static class GameFontLive
{
    internal static async Task<string> ReconcileIndependentAsync(GameInfo game, IGameDataConnection connection)
    {
        var capability = GameFontCapabilities.ForGame(game);
        if (!capability.Custom || !capability.Independent || capability.RestartRequired) return "";
        try
        {
            var result = await ApplyAsync(game, connection);
            return result.TryGetProperty("independent", out var separate) && separate.ValueKind == JsonValueKind.True
                ? "" : "字体选择已保存，但当前游戏组件未确认实时应用。";
        }
        catch (OperationCanceledException) when (connection.Connected)
        {
            // Reconciliation has no caller cancellation token. A connected
            // bridge can time out one font command without losing its session.
            return "字体应用超时；翻译连接和已保存的字体选择保持不变，可稍后重试。";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or GameFontPreferenceRejectedException or GameDataOperationException)
        {
            // Font preferences remain recoverable. Do not discard the connected
            // session, translation cache or a valid currently displayed font.
            return SafeDiagnosticOutput.Redact(ex.Message);
        }
    }
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.OrdinalIgnoreCase);
    private static SemaphoreSlim Gate(GameInfo game) => gates.GetOrAdd(RecentGameStore.Normalize(game.ExePath), _ => new(1, 1));
    internal static JsonObject Request(GameInfo game)
    {
        // Reuse the startup validation, including ownership, size and exact hash.
        // Constructing start info does not launch or change the game.
        var start = new ProcessStartInfo();
        if(game.AdapterId=="gamemaker")GameFontSettings.ApplyGameMakerLaunch(start,game.ExePath,true);
        else if(game.AdapterId=="wolf")
        {
            GameFontSettings.ApplyWolfLaunch(start,game.ExePath);
            if(start.Environment.ContainsKey("FUSION_WOLF_FONT_FAILED"))
                throw new GameFontPreferenceRejectedException("自定义字体文件缺失或已改变，请重新选择或恢复默认。");
        }
        else GameFontSettings.ApplyLaunch(start, game.ExePath, true);
        bool custom = start.Environment.ContainsKey(GameFontSettings.FamilyVariable);
        start.Environment.TryGetValue(GameFontSettings.PathVariable, out var path);
        start.Environment.TryGetValue(GameFontSettings.FamilyVariable, out var family);
        start.Environment.TryGetValue("FUSION_WOLF_FONT_HASH", out var nativeHash);
        if(game.AdapterId=="unreal"&&custom)nativeHash=GameFontSettings.Read(game.ExePath).Hash;
        var request = new JsonObject
        {
            ["op"] = "fontPreferences", ["mode"] = custom ? "custom" : "default",
            ["path"] = path ?? "", ["family"] = family ?? "",
            ["independent"] = GameFontCapabilities.ForGame(game).Independent
        };
        if(custom&&game.AdapterId is "wolf" or "unreal")request["sha256"]=nativeHash??"";
        return request;
    }

    internal static async Task<JsonElement> ApplyAsync(GameInfo game, IGameDataConnection connection, CancellationToken cancellation = default)
    {
        var gate = Gate(game);
        await gate.WaitAsync(cancellation);
        try { return await ApplyCoreAsync(game, connection, null, cancellation); }
        finally { gate.Release(); }
    }

    internal static async Task<JsonElement?> ApplyWhenAsync(GameInfo game, IGameDataConnection connection, Func<bool> allowed, CancellationToken cancellation = default)
    {
        var gate = Gate(game);
        await gate.WaitAsync(cancellation);
        try
        {
            // A previous font command may have held the gate while translation
            // was disabled. Recheck before reading/staging/sending a new choice.
            if (!allowed()) return null;
            return await ApplyCoreAsync(game, connection, null, cancellation);
        }
        finally { gate.Release(); }
    }

    private static async Task<JsonElement> ApplyCoreAsync(GameInfo game, IGameDataConnection connection, JsonObject? request, CancellationToken cancellation = default)
    {
        if (!GameFontSettings.Supports(game)) throw new InvalidOperationException("此游戏暂不支持自定义内嵌字体。");
        if (GameFontCapabilities.ForAdapter(game.AdapterId).RestartRequired)
            throw new InvalidOperationException(GameFontCapabilities.NextStart);
        if (!connection.Connected) throw new IOException("字体选择已保存；游戏连接尚未就绪，尚未实时应用。");
        if(game.AdapterId is "wolf" or "defold")
        {
            JsonElement state;
            try {state=await connection.RequestAsync(new JsonObject{["op"]="fontState"},cancellation);}
            catch(IOException ex) when(ex.Message=="游戏未接受此操作。") {throw new InvalidOperationException(GameFontCapabilities.NextStart);}
            if(!state.TryGetProperty("restartRequired",out var restart)||restart.ValueKind!=JsonValueKind.False)
                throw new InvalidOperationException(GameFontCapabilities.NextStart);
        }
        if(game.AdapterId=="unreal")
        {
            request??=Request(game);
            var state=await connection.RequestAsync(new JsonObject{["op"]="fontState"},cancellation);
            bool supported=state.TryGetProperty("customFontSupported",out var available)&&available.ValueKind==JsonValueKind.True;
            bool unchangedDefault=request["mode"]?.GetValue<string>()=="default"&&state.TryGetProperty("customFontActive",out var active)&&active.ValueKind==JsonValueKind.False;
            if(!supported&&!unchangedDefault)
                throw new InvalidOperationException("当前引擎版本不支持此字体接口，已保留原有字体和译文。");
        }
        JsonElement result;
        try { result = await connection.RequestAsync(request ?? Request(game), cancellation); }
        catch (GameDataOperationException ex) when (GameFontRuntimeMessages.Canonical(ex.Message) is { } canonical)
        {
            // Keep the operation-error category and original protocol reason.
            // The UI translates the canonical diagnostic using its own language.
            Exception localized = GameFontRuntimeMessages.PreferenceWasRetained(ex.Message)
                ? new GameFontPreferenceRejectedException(canonical)
                : new GameDataOperationException(canonical);
            localized.Data["RuntimeReason"] = ex.Message;
            throw localized;
        }
        if (!result.TryGetProperty("accepted", out var accepted) || accepted.ValueKind != JsonValueKind.True)
            throw new IOException("字体选择已保存，但当前游戏组件未确认实时应用。");
        return result;
    }

    internal static async Task<string> ApplyIfSupportedAsync(GameInfo game, IGameDataConnection connection)
    {
        if (!GameFontSettings.Supports(game)) return "";
        if (GameFontCapabilities.ForAdapter(game.AdapterId).RestartRequired)
        {
            try
            {
                var status=await connection.RequestAsync(new JsonObject{["op"]="fontState"},default);
                return status.TryGetProperty("customFontFailed",out var failed)&&failed.ValueKind==JsonValueKind.True
                    ?GameFontCapabilities.StartupFallback:"";
            }
            catch(IOException ex) when(ex.Message=="游戏未接受此操作。")
            {
                // A still-running older WOLF bridge has no fontState command.
                // Font settings must not become a requirement for translation.
                return GameFontCapabilities.NextStart;
            }
        }
        // Reconcile default too: a reset saved while disconnected must replace
        // the previous custom preference in a reconnecting, still-live bridge.
        // Unsupported older bridges report their operation error; do not swallow
        // transport, loader or validation failures as a successful reset.
        if (GameFontCapabilities.ForGame(game).Independent) return await ReconcileIndependentAsync(game, connection);
        var gate = Gate(game);
        await gate.WaitAsync();
        try
        {
            var stamp = GameFontSettings.Capture(game.ExePath);
            try { await ApplyCoreAsync(game, connection, null); return ""; }
            catch (GameFontPreferenceRejectedException) when (stamp.Value.Custom && GameFontSettings.IsCurrent(game.ExePath, stamp))
            {
                // An explicit default request must be acknowledged before the
                // saved rejected custom choice can be retired. Unknown failures
                // and a newer saved selection must never be overwritten.
                await ApplyCoreAsync(game, connection, new JsonObject
                {
                    ["op"] = "fontPreferences", ["mode"] = "default", ["path"] = "", ["family"] = ""
                });
                if (!GameFontSettings.TryRestore(game.ExePath, stamp, new GameFontPreference())) throw;
                return "所选字体无法用于此游戏，已恢复默认字体；翻译可继续使用当前适配。";
            }
        }
        finally { gate.Release(); }
    }
}
