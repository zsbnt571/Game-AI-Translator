using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Explicit account catalog discovery. This connection never creates a thread or turn,
// invokes inference, or reads the desktop application's credentials.
internal static class AiHarnessCatalog
{
    internal static async Task<AiModelCatalogResult> DiscoverCodexAsync(string dataDirectory, AiAgentOptions options, CancellationToken token = default)
    {
        var result = new AiModelCatalogResult { Source = "Codex app-server " + AiHarnessEnvironment.CodexVersion + " model/list (account)" };
        Process? child = null;
        try
        {
            options = options.Validated();
            if (options.Backend != "codex") throw new AiToolsException("harness_backend");
            if (options.AuthMode != "account") throw new AiToolsException("harness_auth_mode");
            string executable = AiHarnessEnvironment.ResolveExecutable(options);
            string home = AiHarnessEnvironment.AccountHome(dataDirectory, options);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await AiHarnessEnvironment.VerifyVersionAsync(executable, "codex", home, timeout.Token).ConfigureAwait(false);
            var info = AiHarnessEnvironment.StartInfo(executable, home, home);
            info.ArgumentList.Add("app-server"); info.ArgumentList.Add("--listen"); info.ArgumentList.Add("stdio://");
            var config = AiCodexHarnessSession.RestrictedConfig();
            config["forced_login_method"] = "chatgpt"; config["model_provider"] = "openai";
            foreach (var pair in config) { info.ArgumentList.Add("-c"); info.ArgumentList.Add(pair.Key + "=" + pair.Value!.ToJsonString()); }
            child = Process.Start(info) ?? throw new AiToolsException("harness_start");
            using var stop = timeout.Token.Register(() => AiHarnessEnvironment.Kill(child));
            _ = DrainAsync(child.StandardError, timeout.Token);
            var connection = new CatalogConnection(child, timeout.Token);
            await connection.CallAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "fusion_ai_catalog", ["title"] = "Fusion model catalog", ["version"] = "1.1.144" },
                ["capabilities"] = new JsonObject { ["experimentalApi"] = true }
            }).ConfigureAwait(false);
            await connection.SendAsync(new JsonObject { ["method"] = "initialized" }).ConfigureAwait(false);
            var account = await connection.CallAsync("account/read", new JsonObject { ["refreshToken"] = false }).ConfigureAwait(false);
            if (account["account"]?["type"]?.GetValue<string>() != "chatgpt") throw new AiToolsException("harness_login_required");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string cursor = "";
            for (int page = 0; page < 20; page++)
            {
                var parameters = new JsonObject { ["limit"] = 200, ["includeHidden"] = false };
                if (cursor.Length > 0) parameters["cursor"] = cursor;
                var response = await connection.CallAsync("model/list", parameters).ConfigureAwait(false);
                AppendPage(result, response, ids);
                cursor = Text(response, "nextCursor");
                if (cursor.Length == 0) { result.Status = "available"; return result; }
                if (cursor.Length > 2048 || !cursors.Add(cursor)) throw new AiToolsException("provider_pagination");
            }
            throw new AiToolsException("provider_catalog_limit");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            result.Models.Clear();
            result.Code = ex is AiToolsException known ? known.Code : "harness_error";
            result.Status = result.Code == "harness_login_required" ? "needs_credential" : "error";
            return result;
        }
        finally { AiHarnessEnvironment.Kill(child); child?.Dispose(); }
    }

    internal static void AppendPage(AiModelCatalogResult result, JsonObject response, HashSet<string> ids)
    {
        if (response["data"] is not JsonArray rows) throw new AiToolsException("provider_invalid_response");
        foreach (var node in rows)
        {
            if (node is not JsonObject row) throw new AiToolsException("provider_invalid_response");
            // `model` is the wire model ID; `id` is the catalog record identity.
            string id = Text(row, "model"), name = Text(row, "displayName");
            if (!AiAgentOptions.Clean(id, 256) || id.Length == 0) throw new AiToolsException("provider_invalid_response");
            if (!ids.Add(id)) continue;
            if (ids.Count > 4000) throw new AiToolsException("provider_catalog_limit");
            var model = new AiModelDescriptor { Id = id, Name = AiAgentOptions.Clean(name, 256) && name.Length > 0 ? name : id };
            if (row["inputModalities"] is JsonArray inputs)
            {
                var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var input in inputs)
                {
                    if(input is not JsonValue value || !value.TryGetValue<string>(out var modality) || !AiAgentOptions.Clean(modality,80))
                        throw new AiToolsException("provider_invalid_response");
                    values.Add(modality);
                }
                model.SupportsImageInput=values.Contains("image");model.SupportsPdfInput=values.Contains("pdf");model.InputCapabilitySource=result.Source;
            }
            if (row["supportedReasoningEfforts"] is not JsonArray efforts) throw new AiToolsException("provider_invalid_response");
            foreach (var entry in efforts)
            {
                if (entry is not JsonObject effort) throw new AiToolsException("provider_invalid_response");
                string value = Text(effort, "reasoningEffort");
                if (!AiAgentOptions.Clean(value, 80) || value.Length == 0 || model.SupportedEfforts.Count >= 32) throw new AiToolsException("provider_invalid_response");
                if (!model.SupportedEfforts.Contains(value, StringComparer.Ordinal)) model.SupportedEfforts.Add(value);
            }
            string preferred = Text(row, "defaultReasoningEffort");
            if (model.SupportedEfforts.Contains(preferred, StringComparer.Ordinal)) model.DefaultEffort = preferred;
            model.EffortSource = result.Source; model.EffortDiscoveredAt = result.CheckedAt;
            result.Models.Add(model);
        }
    }

    private static string Text(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    { try { var buffer = new char[4096]; while (await reader.ReadAsync(buffer, token).ConfigureAwait(false) > 0) { } } catch { } }

    private sealed class CatalogConnection(Process process, CancellationToken token)
    {
        private long sequence;
        internal async Task SendAsync(JsonObject value)
        {
            await process.StandardInput.WriteLineAsync(value.ToJsonString().AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        }
        internal async Task<JsonObject> CallAsync(string method, JsonObject parameters)
        {
            long id = ++sequence;
            await SendAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }).ConfigureAwait(false);
            while (true)
            {
                string? line = await AiHarnessWire.ReadLineAsync(process.StandardOutput, token).ConfigureAwait(false);
                if (line is null) throw new AiToolsException("harness_exited");
                var message = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { MaxDepth = 40 })?.AsObject() ?? throw new AiToolsException("harness_protocol");
                if (message["method"] is not null)
                {
                    if (message["id"] is JsonNode serverId)
                        await SendAsync(new JsonObject { ["id"] = serverId.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "This connection only reads the model catalog." } }).ConfigureAwait(false);
                    continue;
                }
                if (message["id"] is not JsonValue responseId || !responseId.TryGetValue<long>(out long actual) || actual != id) continue;
                if (message["error"] is not null) throw new AiToolsException("harness_protocol");
                return message["result"] as JsonObject ?? throw new AiToolsException("provider_invalid_response");
            }
        }
    }
}
