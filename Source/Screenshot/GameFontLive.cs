using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal static class GameFontLive
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.OrdinalIgnoreCase);
    private static SemaphoreSlim Gate(GameInfo game) => gates.GetOrAdd(RecentGameStore.Normalize(game.ExePath), _ => new(1, 1));
    internal static JsonObject Request(GameInfo game)
    {
        // Reuse the startup validation, including ownership, size and exact hash.
        // Constructing start info does not launch or change the game.
        var start = new ProcessStartInfo();
        GameFontSettings.ApplyLaunch(start, game.ExePath, true);
        bool custom = start.Environment.ContainsKey(GameFontSettings.FamilyVariable);
        start.Environment.TryGetValue(GameFontSettings.PathVariable, out var path);
        start.Environment.TryGetValue(GameFontSettings.FamilyVariable, out var family);
        return new JsonObject
        {
            ["op"] = "fontPreferences", ["mode"] = custom ? "custom" : "default",
            ["path"] = path ?? "", ["family"] = family ?? ""
        };
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
        if (!connection.Connected) throw new IOException("字体选择已保存；游戏连接尚未就绪，尚未实时应用。");
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
        // Reconcile default too: a reset saved while disconnected must replace
        // the previous custom preference in a reconnecting, still-live bridge.
        // Unsupported older bridges report their operation error; do not swallow
        // transport, loader or validation failures as a successful reset.
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
