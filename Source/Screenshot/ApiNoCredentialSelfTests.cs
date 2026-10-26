using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Explicit pre-startup entry: synthetic profiles, one fresh data directory, loopback HTTP only.
// No game, provider process, capture, private settings, or model is started/read.
internal static class ApiNoCredentialSelfTests
{
    private sealed record Check(string Name, bool Passed, string Detail);
    private static readonly List<Check> Checks = [];
    private const string SyntheticKey = "fusion-noauth-test-key-not-a-secret";
    internal static async Task<int> RunLocalAsync(string output, string endpoint, string model)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http" || string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Real local verification requires a loopback HTTP endpoint and explicit model name.");
        if (AppDataPaths.IsInitialized) throw new InvalidOperationException("Run API verification before normal application initialization.");
        output = AppDataPaths.ValidateExplicitRoot(output);
        var dataRoot = Path.Combine(output, "local-model-data-" + Guid.NewGuid().ToString("N"));
        AppDataPaths.Initialize(["--data-root=" + dataRoot, "--disable-global-input"]);
        Directory.CreateDirectory(output);
        var settings = Settings(endpoint); settings.Model = model;
        var fixture = new FusionState { SchemaVersion = 3, CommonProfile = "local-test", TranslationContent = TranslationContentSettings.From(settings) };
        fixture.Profiles["local-test"] = settings; fixture.ProfileInfo["local-test"] = new() { Name = "Synthetic local model test" };
        File.WriteAllText(Path.Combine(AppDataPaths.Root, "fusion-settings.json"), FusionSecrets.Serialize(fixture));
        var config = new FusionConfiguration(); config.SaveProfile("local-test", "Synthetic local model test", settings);
        config = new FusionConfiguration(); var game = new GameInfo(Path.Combine(dataRoot, "synthetic", "Game.exe"), "Synthetic menu/dialogue", "RenPy", "x64", "Never executed", SupportLevel.Supported, null, "renpy");
        config.SelectGameProfile(game.ExePath, "local-test"); config.RequireReady(false); config.RequireReady(true); config.RequireGameReady(game.ExePath);
        bool ready = MainForm.GameTranslationReady(config, settings, game);
        var effective = config.GameSettings(settings, game.ExePath);
        var rows = new List<object>(); var authorizationSent = new List<bool>(); int failures = ready ? 0 : 1;
        TranslationService.AuthorizationHeaderForDiagnostics = authorizationSent.Add;
        try
        {
            foreach (string source in new[] { "New Game", "Open the door.", "Welcome back. The village is safe now." })
            {
                try
                {
                    var response = await new TranslationService().TranslatePlainAsync(source, effective);
                    rows.Add(new { Source = source, Translation = response.Text, Success = true, response.RequestCount, response.HttpStatusCode });
                }
                catch (Exception ex) { failures++; rows.Add(new { Source = source, Success = false, Error = SafeDiagnosticOutput.ExceptionSummary(ex) }); }
            }
        }
        finally { TranslationService.AuthorizationHeaderForDiagnostics = null; }
        if (authorizationSent.Count != 3 || authorizationSent.Any(x => x)) failures++;
        File.WriteAllText(Path.Combine(output, "real-local-model-verification.json"), JsonSerializer.Serialize(new {
            ProductAssembly = BuildIdentity.MainAssemblyPath, ProductSha256 = AssemblyHash(),
            Endpoint = endpoint, Model = model, DataRoot = dataRoot, SavedProfileReloaded = true, GameTranslationReady = ready,
            AllowEmptyApiKey = effective.AllowEmptyApiKey, ApiKeyEmpty = !ApiConnectionPolicy.HasKey(effective), AuthorizationHeadersSent = authorizationSent.Count(x => x),
            ActualRequests = authorizationSent.Count, RealModelTested = true, GameStarted = false, PrivateSettingsRead = false,
            TranslationQualityRequiresReview = true, Failures = failures, Results = rows
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
    internal static async Task<int> RunAsync(string output)
    {
        if (AppDataPaths.IsInitialized) throw new InvalidOperationException("Run API verification before normal application initialization.");
        output = AppDataPaths.ValidateExplicitRoot(output);
        var dataRoot = Path.Combine(output, "isolated-data-" + Guid.NewGuid().ToString("N"));
        AppDataPaths.Initialize(["--data-root=" + dataRoot, "--disable-global-input"]);
        Directory.CreateDirectory(output);
        await Run("saved profiles and launch entry validation", () => { Profiles(); return Task.CompletedTask; });
        foreach (var (name, key, allow) in new[] {
            ("empty key with explicit no-auth", "", true),
            ("whitespace key with explicit no-auth", "   ", true),
            ("configured key with explicit no-auth", SyntheticKey, true),
            ("configured key with required auth", SyntheticKey, false) })
            await Run("HTTP: " + name, () => RequestSuccess(key, allow));
        foreach (string key in new[] { "", "   " })
            await Run("required key rejected before HTTP: " + (key.Length == 0 ? "empty" : "whitespace"), () => RejectBeforeRequest(key));
        foreach (var (status, key, hint) in new[] {
            (401, "", "免鉴权"), (403, "", "免鉴权"), (401, SyntheticKey, "权限"),
            (404, "", "接口或模型"), (429, "", "额度"), (503, "", "服务发生错误") })
            await Run("HTTP diagnosis " + status + (key.Length > 0 ? " with key" : " no key"), () => RequestFailure(status, key, hint));
        await Run("successful HTTP with incompatible protocol is explained", async () =>
        {
            using var server = new LoopbackReply(200, "{\"unexpected\":true}");
            Exception? error = null;
            try { await new TranslationService().TestAsync(Settings(server.Url)); } catch (Exception ex) { error = ex; }
            await server.Completion;
            Assert(error is InvalidDataException && error.Message.Contains("响应格式"), "Protocol failure was not diagnosed.");
        });
        await Run("connection refusal is distinguished from missing key", async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            Exception? error = null;
            try { await new TranslationService().TestAsync(Settings($"http://127.0.0.1:{port}")); } catch (Exception ex) { error = ex; }
            Assert(error is HttpRequestException && error.Message.Contains("服务已启动"), "Transport failure was not diagnosed.");
        });
        var result = new { ProductAssembly = BuildIdentity.MainAssemblyPath, ProductSha256 = AssemblyHash(), Scope = "Production configuration and request paths against synthetic profiles and loopback HTTP", DataRoot = dataRoot,
            RealModelTested = false, GameStarted = false, PrivateSettingsRead = false,
            Passed = Checks.Count(c => c.Passed), Failed = Checks.Count(c => !c.Passed), Checks };
        File.WriteAllText(Path.Combine(output, "api-no-credential-verification.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return Checks.All(c => c.Passed) ? 0 : 1;
    }
    private static ApiSettings Settings(string url = "http://127.0.0.1:1/v1", string key = "", bool allow = true) => new()
    {
        ApiUrl = url, ApiKey = key, AllowEmptyApiKey = allow, Model = "synthetic-local-model",
        RequestKind = TranslationRequestKind.GameText, SourceLanguage = SourceLanguageMode.Auto, TargetLanguage = "zh-CN"
    };
    private static string AssemblyHash() { using var input = File.OpenRead(BuildIdentity.MainAssemblyPath); return Convert.ToHexString(SHA256.HashData(input)); }
    private static void Profiles()
    {
        var settings = Settings();
        // Schema 3 avoids even the historical template-evidence migration path.
        var fixture = new FusionState { SchemaVersion = 3, CommonProfile = "test", TranslationContent = TranslationContentSettings.From(settings) };
        fixture.Profiles["test"] = settings; fixture.ProfileInfo["test"] = new() { Name = "Synthetic local profile" };
        File.WriteAllText(Path.Combine(AppDataPaths.Root, "fusion-settings.json"), FusionSecrets.Serialize(fixture));
        var config = new FusionConfiguration();
        var draft = new ApiProfileEditorState(); draft.Load(settings, "Synthetic local profile");
        Assert(draft.AllowEmptyKey && draft.Snapshot().AllowEmptyApiKey, "Editor lost explicit no-auth selection.");
        config.SaveProfile("test", "Synthetic local profile", draft.Snapshot());
        config = new FusionConfiguration();
        Assert(!config.State.ProfileInfo["test"].Draft, "No-auth profile was marked incomplete after save/reload.");
        config.RequireReady(false); config.RequireReady(true);
        var game = Path.Combine(AppDataPaths.Root, "synthetic-game", "Game.exe");
        config.SelectGameProfile(game, "test");
        config.RequireGameReady(game); // shared by launch, runtime activation, and game-page screen translation
        foreach (string adapter in new[] { "rpg-maker-mv", "rpg-maker-mz", "renpy", "godot3", "godot4", "unity-mono", "unity-il2cpp", "unreal", "gamemaker", "web-rpg", "web-construct", "web-tyrano", "web-vnmaker", "web-choicescript", "defold", "wolf", "yukar-bakin", "rgss" })
            Assert(MainForm.GameTranslationReady(config, settings, new GameInfo(game, "Synthetic", "Unity Mono", "x64", "Never executed", SupportLevel.Candidate, null, adapter)), "Visible readiness rejected no-auth for " + adapter);
        Assert(config.Effective(settings, false).AllowEmptyApiKey && config.Effective(settings, true).AllowEmptyApiKey && config.GameSettings(settings, game).AllowEmptyApiKey,
            "An effective screenshot/embedded/game profile lost the flag.");
        var required = Settings(allow: false); config.SaveProfile("test", "Synthetic local profile", required);
        Assert(config.State.ProfileInfo["test"].Draft, "Required-key draft was considered complete.");
        Assert(!MainForm.GameTranslationReady(config, settings, new GameInfo(game, "Synthetic", "RenPy", "x64", "Never executed", SupportLevel.Supported, null, "renpy")), "Visible readiness accepted a missing required key.");
        foreach (Action entry in new Action[] { () => config.RequireReady(false), () => config.RequireReady(true), () => config.RequireGameReady(game) })
        {
            bool rejected = false; try { entry(); } catch (InvalidOperationException ex) { rejected = ex.Message.Contains("API 密钥"); }
            Assert(rejected, "An entry allowed a missing required key.");
        }
        config.SaveProfile("test", "Synthetic local profile", Settings(key: SyntheticKey, allow: false));
        config.RequireReady(false); config.RequireReady(true); config.RequireGameReady(game);
        foreach (var invalid in new[] { Settings("not-a-url"), Settings("file:///tmp/model"), new ApiSettings { ApiUrl = "http://127.0.0.1:1", AllowEmptyApiKey = true, Model = "" } })
            Assert(!ApiConnectionPolicy.IsConfigured(invalid), "No-auth bypassed URL/model requirements.");
    }
    private static async Task RequestSuccess(string key, bool allow)
    {
        using var server = new LoopbackReply(200, "{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}");
        await new TranslationService().TestAsync(Settings(server.Url, key, allow));
        var request = await server.Completion;
        Assert(request.Path == "/v1/chat/completions", "Request did not use OpenAI-compatible endpoint.");
        Assert(request.Authorization == (string.IsNullOrWhiteSpace(key) ? null : "Bearer " + key), "Authorization contract mismatch.");
        using var body = JsonDocument.Parse(request.Body);
        Assert(body.RootElement.GetProperty("model").GetString() == "synthetic-local-model" && !body.RootElement.GetProperty("stream").GetBoolean(), "Model/stream request contract changed.");
    }
    private static async Task RejectBeforeRequest(string key)
    {
        int starts = 0; TranslationService.RequestStartingForDiagnostics = () => starts++;
        try
        {
            Exception? error = null;
            try { await new TranslationService().TestAsync(Settings(key: key, allow: false)); } catch (Exception ex) { error = ex; }
            Assert(error is InvalidOperationException && error.Message.Contains("API 密钥") && starts == 0, "Missing key reached HTTP or lacked an accurate diagnosis.");
        }
        finally { TranslationService.RequestStartingForDiagnostics = null; }
    }
    private static async Task RequestFailure(int status, string key, string hint)
    {
        using var server = new LoopbackReply(status, "{\"error\":{\"message\":\"synthetic rejection\"}}");
        Exception? error = null;
        try { await new TranslationService().TestAsync(Settings(server.Url, key)); } catch (Exception ex) { error = ex; }
        var request = await server.Completion;
        Assert(error is TranslationHttpException && error.Message.Contains("HTTP " + status) && error.Message.Contains(hint), "HTTP diagnosis did not preserve status and actionable cause.");
        Assert(request.Authorization == (key.Length == 0 ? null : "Bearer " + key), "Failure request used wrong authentication.");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Run(string name, Func<Task> action)
    {
        try { await action(); Checks.Add(new(name, true, "")); }
        catch (Exception ex) { Checks.Add(new(name, false, SafeDiagnosticOutput.ExceptionSummary(ex))); }
    }
    private sealed record Received(string Path, string? Authorization, string Body);
    private sealed class LoopbackReply : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(12));
        internal string Url { get; }
        internal Task<Received> Completion { get; }
        internal LoopbackReply(int status, string body)
        {
            listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            Completion = Reply(status, body);
        }
        private async Task<Received> Reply(int status, string body)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
            using var stream = client.GetStream(); using var received = new MemoryStream();
            byte[] buffer = new byte[4096]; int headerEnd = -1, length = 0; string headers = "";
            while (headerEnd < 0 || received.Length < headerEnd + 4 + length)
            {
                int count = await stream.ReadAsync(buffer, cancellation.Token); if (count == 0) throw new IOException("HTTP request ended early.");
                received.Write(buffer, 0, count); if (received.Length > 1024 * 1024) throw new IOException("Synthetic request exceeded test limit.");
                if (headerEnd < 0)
                {
                    string current = Encoding.UTF8.GetString(received.ToArray()); headerEnd = current.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0)
                    {
                        headers = current[..headerEnd];
                        var size = headers.Split("\r\n").FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                        length = size is null ? 0 : int.Parse(size[(size.IndexOf(':') + 1)..].Trim());
                    }
                }
            }
            var auth = headers.Split("\r\n").FirstOrDefault(x => x.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
            var result = new Received(headers.Split("\r\n")[0].Split(' ')[1], auth is null ? null : auth[(auth.IndexOf(':') + 1)..].Trim(),
                Encoding.UTF8.GetString(received.ToArray(), headerEnd + 4, length));
            byte[] response = Encoding.UTF8.GetBytes(body);
            byte[] responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeader, cancellation.Token); await stream.WriteAsync(response, cancellation.Token);
            return result;
        }
        public void Dispose() { cancellation.Cancel(); listener.Stop(); cancellation.Dispose(); }
    }
}
