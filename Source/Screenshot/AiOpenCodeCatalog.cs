using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Reads the pinned native provider catalog for Fusion's explicit OpenCode account.
// No session, prompt, tool execution or direct reading of native credential files.
internal static class AiOpenCodeCatalog
{
    internal static async Task<AiModelCatalogResult> DiscoverAsync(string dataDirectory, AiAgentOptions options, CancellationToken token = default)
    {
        var result = new AiModelCatalogResult { Source = "OpenCode " + AiHarnessEnvironment.OpenCodeVersion + " native provider catalog (account)" };
        Process? child = null;
        try
        {
            options = options.Validated();
            if (options.Backend != "opencode") throw new AiToolsException("harness_backend");
            if (options.AuthMode != "account" || options.ProviderId != "openai") throw new AiToolsException("harness_account_provider");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            string executable = AiHarnessEnvironment.ResolveExecutable(options), home = AiHarnessEnvironment.AccountHome(dataDirectory, options);
            string run = Path.Combine(dataDirectory, "ai-harness", "catalog", Guid.NewGuid().ToString("N"));
            AppDataPaths.RejectReparsePoints(run); Directory.CreateDirectory(run);
            await AiHarnessEnvironment.VerifyVersionAsync(executable, "opencode", home, timeout.Token).ConfigureAwait(false);
            await AiOpenCodeHarnessSession.RequireOAuthAsync(executable, home, run, timeout.Token).ConfigureAwait(false);
            var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            var info = AiHarnessEnvironment.StartInfo(executable, home, run);
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            info.Environment["OPENCODE_SERVER_PASSWORD"] = password; info.Environment["OPENCODE_SERVER_USERNAME"] = "fusion";
            info.Environment["OPENCODE_CONFIG_CONTENT"] = new JsonObject
            {
                ["autoupdate"] = false, ["share"] = "disabled", ["enabled_providers"] = new JsonArray(options.ProviderId),
                ["permission"] = new JsonObject { ["*"] = "deny" }, ["tools"] = new JsonObject { ["*"] = false }, ["mcp"] = new JsonObject()
            }.ToJsonString();
            foreach (string value in new[] { "serve", "--pure", "--hostname", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) }) info.ArgumentList.Add(value);
            child = Process.Start(info) ?? throw new AiToolsException("harness_start");
            using var stop = timeout.Token.Register(() => AiHarnessEnvironment.Kill(child));
            _ = DrainAsync(child.StandardError, timeout.Token);
            await AiOpenCodeHarnessSession.WaitForReadyAsync(child.StandardOutput, port, child.WaitForExitAsync(), timeout.Token).ConfigureAwait(false);
            _ = DrainAsync(child.StandardOutput, timeout.Token);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false });
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("fusion:" + password)));
            using var response = await http.GetAsync("http://127.0.0.1:" + port + "/provider", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AiToolsException("harness_protocol");
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream(); var buffer = new byte[16384];
            while (true)
            {
                int count = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); if (count == 0) break;
                if (memory.Length + count > 8 * 1024 * 1024) throw new AiToolsException("provider_catalog_limit");
                memory.Write(buffer, 0, count);
            }
            var catalog = JsonNode.Parse(memory.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 64 })?.AsObject() ?? throw new AiToolsException("provider_invalid_response");
            AppendCatalog(result, catalog, options.ProviderId);
            result.Status = "available";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            result.Models.Clear(); result.Code = ex is AiToolsException known ? known.Code : "harness_error";
            result.Status = result.Code == "harness_login_required" ? "needs_credential" : "error";
        }
        finally { AiHarnessEnvironment.Kill(child); child?.Dispose(); }
        return result;
    }

    internal static void AppendCatalog(AiModelCatalogResult result, JsonObject catalog, string provider)
    {
        if (catalog["connected"] is not JsonArray connected || !connected.Any(n => n is JsonValue v && v.TryGetValue<string>(out var id) && id == provider))
            throw new AiToolsException("harness_login_required");
        var entry = (catalog["all"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(p => p["id"]?.GetValue<string>() == provider);
        if (entry?["models"] is not JsonObject models) throw new AiToolsException("provider_invalid_response");
        foreach (var pair in models)
        {
            if (!AiAgentOptions.Clean(pair.Key, 256) || pair.Key.Length == 0 || pair.Value is not JsonObject model) throw new AiToolsException("provider_invalid_response");
            if (result.Models.Count >= 4000) throw new AiToolsException("provider_catalog_limit");
            string name = model["name"]?.GetValue<string>() ?? pair.Key;
            var descriptor = new AiModelDescriptor { Id = pair.Key, Name = AiAgentOptions.Clean(name, 256) ? name : pair.Key };
            if(model["capabilities"] is JsonObject capabilities)
            {
                if(capabilities["input"] is JsonObject input)
                {
                    descriptor.SupportsImageInput=Boolean(input,"image");descriptor.SupportsPdfInput=Boolean(input,"pdf");
                    if(descriptor.SupportsImageInput is not null||descriptor.SupportsPdfInput is not null)descriptor.InputCapabilitySource=result.Source;
                }
                if(capabilities["output"] is JsonObject output)
                {descriptor.SupportsTextOutput=Boolean(output,"text");if(descriptor.SupportsTextOutput is not null)descriptor.OutputCapabilitySource=result.Source;}
            }
            if (model["variants"] is JsonObject variants)
                foreach (var variant in variants)
                {
                    if (variant.Value?["disabled"]?.GetValue<bool>() == true) continue;
                    if (!AiAgentOptions.Clean(variant.Key, 80) || variant.Key.Length == 0 || descriptor.SupportedEfforts.Count >= 32) throw new AiToolsException("provider_invalid_response");
                    descriptor.SupportedEfforts.Add(variant.Key);
                }
            if (model["variants"] is JsonObject) { descriptor.EffortSource = result.Source; descriptor.EffortDiscoveredAt = result.CheckedAt; }
            result.Models.Add(descriptor);
        }
    }

    private static bool? Boolean(JsonObject value,string name) => value[name] is JsonValue leaf && leaf.TryGetValue<bool>(out bool supported)?supported:null;

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    { try { var buffer = new char[4096]; while (await reader.ReadAsync(buffer, token).ConfigureAwait(false) > 0) { } } catch { } }
}
