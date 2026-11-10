using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Discovery and balance GETs only. Never probes an inference endpoint or reads another app's auth.
internal sealed class AiProviderServices : IDisposable
{
    private readonly HttpClient client;
    internal AiProviderServices(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }
    public void Dispose() => client.Dispose();

    internal static string InferProvider(ApiSettings settings)
    {
        if (!Uri.TryCreate(settings.ApiUrl, UriKind.Absolute, out var uri)) return "custom";
        return uri.IdnHost.ToLowerInvariant() switch
        {
            "api.deepseek.com" => "deepseek", "api.x.ai" => "xai", "api.openai.com" => "openai",
            "api.anthropic.com" => "anthropic", "generativelanguage.googleapis.com" => "gemini", _ => "custom"
        };
    }

    internal async Task<AiModelCatalogResult> DiscoverModelsAsync(ApiSettings settings, AiAgentOptions options, CancellationToken token = default)
    {
        var result = new AiModelCatalogResult();
        try
        {
            options = options.Validated();
            if (options.AuthMode == "account") { result.Status = "unsupported"; result.Code = "account_catalog_in_harness"; return result; }
            var provider = Provider(settings, options);
            var endpoint = ModelsEndpoint(settings, provider);
            bool nativeGemini = provider == "gemini" && !endpoint.AbsolutePath.Contains("/openai/", StringComparison.OrdinalIgnoreCase);
            result.Source = endpoint.GetLeftPart(UriPartial.Path);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            for (int page = 0; page < 20; page++)
            {
                using var response = await GetJsonAsync(endpoint, settings.ApiKey, provider == "gemini" && !nativeGemini ? "gemini-openai" : provider, token);
                JsonElement root = response.RootElement;
                string listName = nativeGemini ? "models" : "data";
                if (!root.TryGetProperty(listName, out var rows) || rows.ValueKind != JsonValueKind.Array)
                    throw new AiToolsException("provider_invalid_response");
                foreach (var row in rows.EnumerateArray())
                {
                    string id = String(row, nativeGemini ? "name" : "id");
                    if (!AiAgentOptions.Clean(id, 256) || id.Length == 0) throw new AiToolsException("provider_invalid_response");
                    if (!ids.Add(id)) continue; // Only identical IDs are duplicates; dated versions remain separate.
                    if (ids.Count > 4000) throw new AiToolsException("provider_catalog_limit");
                    string name = String(row, "name");
                    if (nativeGemini) name = String(row, "displayName");
                    if (name.Length == 0) name = String(row, "display_name");
                    var model = new AiModelDescriptor { Id = id, Name = AiAgentOptions.Clean(name, 256) && name.Length > 0 ? name : id };
                    bool hasEffortMetadata = ReadEffortMetadata(row, model, result.Source, result.CheckedAt);
                    ReadModalities(row, model, result.Source);
                    ApplyVerifiedMetadata(model, provider, !hasEffortMetadata);
                    result.Models.Add(model);
                }
                string next = nativeGemini ? String(root, "nextPageToken") : "";
                if (provider == "anthropic" && root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True)
                {
                    next = String(root, "last_id");
                    if (next.Length == 0) throw new AiToolsException("provider_invalid_response");
                }
                if (next.Length == 0) { result.Status = "available"; return result; }
                if (next.Length > 2048 || !cursors.Add(next)) throw new AiToolsException("provider_pagination");
                endpoint = new Uri(result.Source + (nativeGemini ? "?pageToken=" : "?after_id=") + Uri.EscapeDataString(next));
            }
            throw new AiToolsException("provider_catalog_limit");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (Handled(ex)) { result.Status = "error"; result.Code = Code(ex); result.Models.Clear(); return result; }
    }

    internal async Task<AiBalanceResult> GetBalanceAsync(ApiSettings settings, AiAgentOptions options, CancellationToken token = default)
    {
        var result = new AiBalanceResult();
        try
        {
            options = options.Validated();
            string provider = Provider(settings, options);
            result.ProviderId = provider;
            result.OfficialUrl = BalanceHelp(provider);
            if (options.AuthMode == "account") { result.Code = "account_balance_in_harness"; result.OfficialUrl = ""; return result; }
            if (provider is not ("deepseek" or "xai")) { result.Code = "balance_not_supported"; return result; }
            ValidateOfficialRoute(settings, provider);
            Uri endpoint;
            string key;
            if (provider == "deepseek")
            {
                endpoint = new Uri("https://api.deepseek.com/user/balance");
                key = settings.ApiKey;
                if (string.IsNullOrWhiteSpace(key)) { result.Status = "needs_credential"; result.Code = "api_key_required"; return result; }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(options.XaiManagementApiKey) || string.IsNullOrWhiteSpace(options.XaiTeamId))
                { result.Status = "needs_credential"; result.Code = "xai_management_key_team_required"; return result; }
                if (!options.XaiTeamId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new AiToolsException("xai_team_invalid");
                endpoint = new Uri("https://management-api.x.ai/v1/billing/teams/" + Uri.EscapeDataString(options.XaiTeamId) + "/prepaid/balance");
                key = options.XaiManagementApiKey;
            }
            result.Source = endpoint.AbsoluteUri;
            using var response = await GetJsonAsync(endpoint, key, provider, token);
            var root = response.RootElement;
            if (provider == "deepseek")
            {
                if (!root.TryGetProperty("is_available", out var available) || available.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !root.TryGetProperty("balance_infos", out var rows) || rows.ValueKind != JsonValueKind.Array) throw new AiToolsException("provider_invalid_response");
                result.IsAvailable = available.GetBoolean();
                foreach (var row in rows.EnumerateArray())
                {
                    string currency = String(row, "currency");
                    if (currency is not ("CNY" or "USD") || result.Items.Any(x => x.Currency == currency)) throw new AiToolsException("provider_invalid_response");
                    result.Items.Add(new AiBalanceItem { Currency = currency, Total = Number(row, "total_balance"), Granted = Number(row, "granted_balance"), ToppedUp = Number(row, "topped_up_balance") });
                }
                if (result.Items.Count == 0) throw new AiToolsException("provider_invalid_response");
            }
            else
            {
                if (!root.TryGetProperty("total", out var total)) throw new AiToolsException("provider_invalid_response");
                result.Items.Add(new AiBalanceItem { Currency = "USD", Total = Number(total, "val") / 100m, Kind = "prepaid_ledger" });
                if (root.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var change in changes.EnumerateArray())
                    {
                        if (result.Changes.Count >= 2000 || !change.TryGetProperty("amount", out var amount)) throw new AiToolsException("provider_invalid_response");
                        string kind = String(change, "changeOrigin");
                        result.Changes.Add(new AiBalanceChange { Currency = "USD", Amount = Number(amount, "val") / 100m,
                            Kind = AiAgentOptions.Clean(kind, 100) ? kind : "", At = DateTimeOffset.TryParse(String(change, "createTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null });
                    }
                }
            }
            result.Status = "available"; return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (Handled(ex)) { result.Status = "error"; result.Code = Code(ex); result.Items.Clear(); result.Changes.Clear(); return result; }
    }

    private async Task<JsonDocument> GetJsonAsync(Uri endpoint, string? key, string provider, CancellationToken token)
    {
        if (!AiAgentOptions.Clean(key, 8192)) throw new AiToolsException("api_key_invalid");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (provider == "anthropic") { request.Headers.Add("x-api-key", key); request.Headers.Add("anthropic-version", "2023-06-01"); }
            else if (provider == "gemini") request.Headers.Add("x-goog-api-key", key);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new AiToolsException((int)response.StatusCode is >= 300 and < 400 ? "provider_redirect_refused" : "provider_http_" + (int)response.StatusCode);
        const int maxBytes = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes) throw new AiToolsException("provider_response_limit");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + read > maxBytes) throw new AiToolsException("provider_response_limit");
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 20 });
    }

    internal static string Provider(ApiSettings settings, AiAgentOptions options)
    {
        string provider = options.ProviderId.Trim().ToLowerInvariant();
        return provider.Length == 0 ? InferProvider(settings) : provider switch { "claude" => "anthropic", "grok" => "xai", "google" => "gemini", _ => provider };
    }
    // Reviewed primary documentation, 2026-11-01. This enriches only exact IDs returned by the
    // authenticated official catalog; no aliases, snapshots or catalog entries are invented.
    internal static void ApplyVerifiedMetadata(AiModelDescriptor model, string provider, bool fillEfforts = true)
    {
        string source = "";
        List<string> efforts = [];
        string defaultEffort = "";
        if (provider == "openai")
        {
            if (model.Id is "gpt-6.1-sol" or "gpt-6-astra")
            {
                efforts = ["low", "medium", "high", "xhigh", "max"];
                source = "https://developers.openai.com/api/docs/models/" + model.Id;
                if (model.Id == "gpt-6.1-sol") defaultEffort = "medium";
            }
            else if (model.Id is "gpt-6-sol" or "gpt-6-luna" or "gpt-6.1-terra")
            {
                efforts = ["none", "low", "medium", "high", "xhigh", "max"];
                defaultEffort = "medium";
                source = "https://developers.openai.com/api/docs/models/" + model.Id;
            }
            else if (model.Id is "gpt-5.5" or "gpt-5.5-2026-04-23")
            {
                efforts = ["none", "low", "medium", "high", "xhigh"];
                defaultEffort = "medium";
                source = "https://developers.openai.com/api/docs/models/gpt-5.5";
            }
        }
        else if (provider == "deepseek" && model.Id is "deepseek-flash" or "deepseek-v4-pro" or "deepseek-v4-flash" or "deepseek-v4-flash-vision-exp")
        {
            // Official /models and Codex integration expose these levels. DSH labels are not copied.
            efforts = ["low", "high", "max"];
            defaultEffort = "high";
            source = "https://api-docs.deepseek.com/guides/thinking_mode/";
        }
        else if (provider == "xai" && model.Id is "grok-4.7" or "grok-4.6" or "grok-4.5")
        {
            efforts = model.Id == "grok-4.5" ? ["low", "medium", "high"] : ["low", "medium", "high", "xhigh"];
            defaultEffort = "high";
            source = "https://docs.x.ai/developers/model-capabilities/text/reasoning";
        }
        var verifiedAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        if (source.Length > 0 && fillEfforts)
        { model.SupportedEfforts = efforts; model.DefaultEffort = defaultEffort; model.EffortSource = source; model.EffortDiscoveredAt = verifiedAt; }
        if (provider == "openai" && model.Id is "gpt-6-sol" or "gpt-6-luna" or "gpt-6.1-terra" or "gpt-6.1-sol" or "gpt-6-astra")
        {
            model.SupportsImageInput ??= true;
            model.SupportsTextOutput ??= true;
            if (model.InputCapabilitySource.Length == 0) model.InputCapabilitySource = source;
            if (model.OutputCapabilitySource.Length == 0) model.OutputCapabilitySource = source;
            model.CatalogCategory = "text";
            (decimal input, decimal cached, decimal output) = model.Id switch
            {
                "gpt-6-luna" => (0.10m, 0.01m, 0.50m), "gpt-6.1-terra" => (1m, 0.05m, 5m),
                "gpt-6-astra" => (10m, 1m, 50m), "gpt-6.1-sol" => (2m, 0.10m, 10m), _ => (2m, 0.20m, 10m)
            };
            model.Price ??= new() { Currency = "USD", InputPerMillion = input, CachedInputPerMillion = cached,
                OutputPerMillion = output, ModelId = model.Id, Source = source, AsOf = verifiedAt,
                ScopeNote = "standard_text_base", IsEstimate = true };
        }
        if (provider == "deepseek" && model.Id is "deepseek-flash" or "deepseek-v4-pro" or "deepseek-v4-flash" or "deepseek-v4-flash-vision-exp")
        {
            model.SupportsImageInput ??= model.Id != "deepseek-v4-pro";
            model.SupportsTextOutput ??= true;
            if (model.InputCapabilitySource.Length == 0) model.InputCapabilitySource = "https://api-docs.deepseek.com/api/list-models/";
            if (model.OutputCapabilitySource.Length == 0) model.OutputCapabilitySource = "https://api-docs.deepseek.com/api/list-models/";
            model.CatalogCategory = "text";
        }
        // Known non-chat official families. Unknown/custom IDs are retained, never guessed incompatible.
        if (provider == "openai" && model.CatalogCategory == "unknown")
        {
            string id = model.Id;
            model.CatalogCategory = id.StartsWith("text-embedding-", StringComparison.Ordinal) ? "embedding" :
                id.StartsWith("gpt-image-", StringComparison.Ordinal) || id.StartsWith("dall-e-", StringComparison.Ordinal) ? "image" :
                id.StartsWith("sora-", StringComparison.Ordinal) ? "video" :
                id.StartsWith("omni-moderation-", StringComparison.Ordinal) || id.StartsWith("text-moderation-", StringComparison.Ordinal) ? "moderation" :
                id.StartsWith("whisper-", StringComparison.Ordinal) || id.StartsWith("tts-", StringComparison.Ordinal) ||
                id.StartsWith("gpt-audio", StringComparison.Ordinal) || id.StartsWith("gpt-realtime", StringComparison.Ordinal) ||
                id.Contains("-transcribe", StringComparison.Ordinal) || id.Contains("-tts", StringComparison.Ordinal) ||
                id.Contains("-realtime-", StringComparison.Ordinal) || id.Contains("-audio-preview", StringComparison.Ordinal) ? "audio" : "unknown";
        }
    }

    private static bool ReadEffortMetadata(JsonElement row, AiModelDescriptor model, string source, DateTimeOffset at)
    {
        JsonElement metadata = default, levels = default;
        // Claude Models API advertises individual supported leaves; it is not an OpenAI effort enum.
        if(row.TryGetProperty("capabilities",out var capabilities)&&capabilities.ValueKind==JsonValueKind.Object&&
            capabilities.TryGetProperty("effort",out var capability)&&capability.ValueKind==JsonValueKind.Object&&
            capability.TryGetProperty("supported",out var supported)&&supported.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if(supported.GetBoolean())foreach(string level in new[]{"low","medium","high","xhigh","max"})
                if(Support(capability,level)==true)model.SupportedEfforts.Add(level);
            model.EffortSource=source;model.EffortDiscoveredAt=at;return true;
        }
        if (row.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.Object && effort.TryGetProperty("supported_levels", out levels)) metadata = effort;
        else if (row.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object && reasoning.TryGetProperty("supported_efforts", out levels)) metadata = reasoning;
        else if (row.TryGetProperty("supported_reasoning_efforts", out levels)) metadata = row;
        else return false;
        if (levels.ValueKind != JsonValueKind.Array) throw new AiToolsException("provider_invalid_response");
        foreach (var level in levels.EnumerateArray())
        {
            string value = level.ValueKind == JsonValueKind.String ? level.GetString()! : String(level, "reasoning_effort");
            if (!AiAgentOptions.Clean(value, 80) || value.Length == 0) throw new AiToolsException("provider_invalid_response");
            if (!model.SupportedEfforts.Contains(value, StringComparer.Ordinal)) model.SupportedEfforts.Add(value);
            if (model.SupportedEfforts.Count > 32) throw new AiToolsException("provider_invalid_response");
        }
        string defaultValue = String(metadata, "default_level");
        if (defaultValue.Length == 0) defaultValue = String(metadata, "default_effort");
        if (defaultValue.Length == 0) defaultValue = String(metadata, "default_reasoning_effort");
        if (model.SupportedEfforts.Contains(defaultValue, StringComparer.Ordinal)) model.DefaultEffort = defaultValue;
        model.EffortSource = source; model.EffortDiscoveredAt = at;
        return true;
    }
    private static void ReadModalities(JsonElement row, AiModelDescriptor model, string source)
    {
        if(row.TryGetProperty("capabilities",out var capabilities)&&capabilities.ValueKind==JsonValueKind.Object)
        {
            model.SupportsImageInput=Support(capabilities,"image_input");model.SupportsPdfInput=Support(capabilities,"pdf_input");
            if(model.SupportsImageInput is not null||model.SupportsPdfInput is not null)model.InputCapabilitySource=source;
        }
        JsonElement inputs = default, outputs = default;
        if (row.TryGetProperty("modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Object)
        { modalities.TryGetProperty("input", out inputs); modalities.TryGetProperty("output", out outputs); }
        if (inputs.ValueKind == JsonValueKind.Undefined) row.TryGetProperty("input_modalities", out inputs);
        if (outputs.ValueKind == JsonValueKind.Undefined) row.TryGetProperty("output_modalities", out outputs);
        if (inputs.ValueKind == JsonValueKind.Array)
        {
            var values = inputs.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
            model.SupportsImageInput ??= values.Contains("image", StringComparer.OrdinalIgnoreCase);
            model.SupportsPdfInput ??= values.Contains("pdf", StringComparer.OrdinalIgnoreCase) || values.Contains("application/pdf", StringComparer.OrdinalIgnoreCase);
            model.InputCapabilitySource = source;
        }
        if (outputs.ValueKind == JsonValueKind.Array)
        { model.SupportsTextOutput = outputs.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString()!.Equals("text", StringComparison.OrdinalIgnoreCase)); model.OutputCapabilitySource = source; }
        string category = String(row, "type").ToLowerInvariant();
        if (category is "embedding" or "image" or "audio" or "video" or "moderation") model.CatalogCategory = category;
        if(row.TryGetProperty("supportedGenerationMethods",out var methods)&&methods.ValueKind==JsonValueKind.Array)
        {
            var names=methods.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Select(x=>x.GetString()).ToArray();
            if(names.Contains("embedContent")&&!names.Contains("generateContent"))model.CatalogCategory="embedding";
        }
    }
    private static bool? Support(JsonElement parent,string name) => parent.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Object&&
        value.TryGetProperty("supported",out var flag)&&flag.ValueKind is JsonValueKind.True or JsonValueKind.False?flag.GetBoolean():null;

    internal static string OfficialAddress(string provider) => provider switch
    {
        "openai" => "https://api.openai.com/v1", "deepseek" => "https://api.deepseek.com/v1",
        "anthropic" => "https://api.anthropic.com/v1", "xai" => "https://api.x.ai/v1",
        "gemini" => "https://generativelanguage.googleapis.com/v1beta/openai", _ => ""
    };
    private static Uri ModelsEndpoint(ApiSettings settings, string provider)
    {
        if (!Uri.TryCreate(settings.ApiUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))) throw new AiToolsException("provider_route_untrusted");
        if (provider is "deepseek" or "openai" or "anthropic" or "xai" or "gemini") ValidateOfficialRoute(settings, provider);
        bool geminiCompatible = uri.AbsolutePath.TrimEnd('/').Length == 0 || uri.AbsolutePath.TrimEnd('/').Equals("/v1beta", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.TrimEnd('/').Equals("/v1beta/openai", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.StartsWith("/v1beta/openai/", StringComparison.OrdinalIgnoreCase);
        string suffix = provider switch { "deepseek" => "/models", "openai" or "anthropic" or "xai" => "/v1/models", "gemini" => geminiCompatible ? "/v1beta/openai/models" : "/v1beta/models", _ => "" };
        if (suffix.Length > 0) return new Uri(uri.GetLeftPart(UriPartial.Authority) + suffix);
        string path = uri.AbsolutePath.TrimEnd('/');
        foreach (string ending in new[] { "/chat/completions", "/responses", "/messages", "/models" })
            if (path.EndsWith(ending, StringComparison.OrdinalIgnoreCase)) { path = path[..^ending.Length]; break; }
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + path + "/models");
    }
    private static void ValidateOfficialRoute(ApiSettings settings, string provider)
    {
        string host = provider switch { "deepseek" => "api.deepseek.com", "xai" => "api.x.ai", "openai" => "api.openai.com", "anthropic" => "api.anthropic.com", "gemini" => "generativelanguage.googleapis.com", _ => "" };
        if (!Uri.TryCreate(settings.ApiUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            !uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new AiToolsException("provider_route_untrusted");
    }
    internal static string BalanceHelp(string provider) => provider switch
    {
        "deepseek" => "https://api-docs.deepseek.com/api/get-user-balance/",
        "xai" => "https://docs.x.ai/developers/rest-api-reference/management/billing",
        "openai" => "https://platform.openai.com/settings/organization/billing/overview",
        "anthropic" => "https://platform.claude.com/settings/billing",
        "gemini" => "https://ai.google.dev/gemini-api/docs/billing", _ => ""
    };
    private static string String(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static decimal Number(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var value)) throw new AiToolsException("provider_invalid_response");
        string text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) throw new AiToolsException("provider_invalid_response");
        return number;
    }
    private static bool Handled(Exception ex) => ex is AiToolsException or HttpRequestException or IOException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException or OverflowException;
    private static string Code(Exception ex) => ex is AiToolsException ae ? ae.Code : ex is OperationCanceledException ? "provider_timeout" : ex is HttpRequestException or IOException ? "provider_network" : "provider_invalid_response";
}
