using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Separate transport: never enters TranslationService, its queue, cache or retry state.
internal sealed record AiFunctionCall(string Id, string Name, JsonObject Arguments);
internal sealed record AiModelReply(string Text, IReadOnlyList<AiFunctionCall> Calls, long InputTokens, long OutputTokens,
    string? ReasoningContent = null, JsonArray? ResponseItems = null, AiUsage? Usage = null);
internal sealed class AiToolsException(string code) : Exception(code)
{ internal string Code => code; internal AiUsage? Usage { get; set; } }

internal sealed class AiConversationClient : IDisposable
{
    internal const int MaxResponseBytes = 256 * 1024;
    internal const int MaxOutputTokens = 1200;
    internal const int MaxRequestBytes = 96 * 1024;
    private readonly HttpClient http;
    internal AiConversationClient(HttpMessageHandler? handler = null)
    {
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    }
    internal static (Uri Url, bool Responses) Endpoint(ApiSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiUrl) || string.IsNullOrWhiteSpace(settings.Model) ||
            (string.IsNullOrWhiteSpace(settings.ApiKey) && !settings.AllowEmptyApiKey))
            throw new AiToolsException("profile_incomplete");
        if (!Uri.TryCreate(settings.ApiUrl.Trim(), UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0 || url.Fragment.Length != 0)
            throw new AiToolsException("profile_incomplete");
        var builder = new UriBuilder(url);
        string path = builder.Path.TrimEnd('/');
        bool explicitChat = path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase);
        bool responses = path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) ||
            (!explicitChat && (url.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) || url.Host.Equals("api.x.ai", StringComparison.OrdinalIgnoreCase)));
        if (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) || path.Contains(":generateContent", StringComparison.OrdinalIgnoreCase))
            throw new AiToolsException("ai_protocol_unsupported");
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/chat/completions".Length];
        else if (path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/responses".Length];
        else if (url.Host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Length == 0 || path == "/v1beta") path = "/v1beta/openai";
            else if (path != "/v1beta/openai") throw new AiToolsException("ai_protocol_unsupported");
        }
        else if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path += "/v1";
        builder.Path = path + (responses ? "/responses" : "/chat/completions");
        return (builder.Uri, responses);
    }
    internal static JsonObject Request(ApiSettings settings, IReadOnlyList<JsonObject> messages, JsonArray tools, bool responses)
        => Request(settings, messages, tools, responses, null);
    internal static int OutputLimit(AiAgentOptions? options) => options is null ? MaxOutputTokens : 8192;
    internal static JsonObject Request(ApiSettings settings, IReadOnlyList<JsonObject> messages, JsonArray tools, bool responses, AiAgentOptions? options)
    {
        if (!responses)
        {
            var request = new JsonObject { ["model"] = settings.Model, ["messages"] = new JsonArray(messages.Select(x => x.DeepClone()).ToArray()),
                ["tools"] = tools.DeepClone(), ["tool_choice"] = "auto", ["parallel_tool_calls"] = false,
                ["max_tokens"] = OutputLimit(options), ["stream"] = false };
            if (options is null && settings.Model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase))
                request["thinking"] = new JsonObject { ["type"] = "disabled" };
            ApplyEffort(request, settings, options, false);
            return request;
        }
        var input = new JsonArray();
        foreach (var message in messages)
        {
            var role = message["role"]?.GetValue<string>() ?? "user";
            if (role == "assistant" && message["_response_output"] is JsonArray rawOutput)
            {
                foreach (var item in rawOutput) input.Add(item?.DeepClone());
                continue;
            }
            if (role == "tool")
            {
                input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = message["tool_call_id"]?.DeepClone(), ["output"] = message["content"]?.DeepClone() });
                continue;
            }
            var content = message["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(content)) input.Add(new JsonObject { ["role"] = role == "system" &&
                !Endpoint(settings).Url.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) ? "developer" : role, ["content"] = content });
            if (message["tool_calls"] is JsonArray calls) foreach (var call in calls.OfType<JsonObject>())
                input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = call["id"]?.DeepClone(),
                    ["name"] = call["function"]?["name"]?.DeepClone(), ["arguments"] = call["function"]?["arguments"]?.DeepClone() });
        }
        var functions = new JsonArray();
        foreach (var tool in tools.OfType<JsonObject>())
        {
            var function = tool["function"]!.DeepClone().AsObject(); function["type"] = "function";
            function["strict"] = false; functions.Add(function);
        }
        var responseRequest = new JsonObject { ["model"] = settings.Model, ["input"] = input, ["tools"] = functions,
            ["tool_choice"] = "auto", ["parallel_tool_calls"] = false, ["max_output_tokens"] = OutputLimit(options), ["store"] = false };
        if (Endpoint(settings).Url.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase))
            responseRequest["include"] = new JsonArray("reasoning.encrypted_content");
        if (options is null && settings.Model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase))
            responseRequest["thinking"] = new JsonObject { ["type"] = "disabled" };
        ApplyEffort(responseRequest, settings, options, true);
        return responseRequest;
    }
    private static void ApplyEffort(JsonObject request, ApiSettings settings, AiAgentOptions? options, bool responses)
    {
        if (options is null) return;
        string effort = options.EffectiveEffort(settings);
        if (options.ReasoningEffort.Length > 0 && effort.Length == 0) throw new AiToolsException("ai_effort_stale");
        if (effort.Length == 0) return; // Model default is not an invented low/high setting.
        string provider = AiProviderServices.InferProvider(settings);
        if (provider == "anthropic" || provider == "custom") throw new AiToolsException("ai_effort_unsupported");
        if (responses) request["reasoning"] = new JsonObject { ["effort"] = effort };
        else request["reasoning_effort"] = effort;
    }
    internal async Task<AiModelReply> CompleteAsync(ApiSettings settings, IReadOnlyList<JsonObject> messages,
        JsonArray tools, CancellationToken cancellation)
        => await CompleteAsync(settings, messages, tools, cancellation, null);
    internal async Task<AiModelReply> CompleteAsync(ApiSettings settings, IReadOnlyList<JsonObject> messages,
        JsonArray tools, CancellationToken cancellation, AiAgentOptions? options)
    {
        var endpoint = Endpoint(settings);
        var body = JsonSerializer.SerializeToUtf8Bytes(Request(settings, messages, tools, endpoint.Responses, options));
        if (body.Length > MaxRequestBytes) throw new AiToolsException("context_limit");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 15, 90)));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url);
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AiToolsException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "api_auth",
                HttpStatusCode.TooManyRequests => "api_limit", _ => "api_error"
            });
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new AiToolsException("response_limit");
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var bytes = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > MaxResponseBytes) throw new AiToolsException("response_limit");
            bytes.Write(buffer, 0, count);
        }
        cancellation.ThrowIfCancellationRequested();
        try
        {
            var parsed = JsonNode.Parse(bytes.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 32 })?.AsObject()
                ?? throw new AiToolsException("api_format");
            var allowed = tools.OfType<JsonObject>().Select(x => x["function"]?["name"]?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var usage = ReadUsage(parsed, endpoint.Responses, endpoint.Url.Host);
            try { return Parse(parsed, endpoint.Responses, allowed) with { Usage = usage }; }
            catch (AiToolsException ex) { ex.Usage = usage; throw; }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
            { throw new AiToolsException("api_format") { Usage = usage }; }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        { throw new AiToolsException("api_format"); }
    }
    internal static AiUsage ReadUsage(JsonObject response, bool responses, string host)
    {
        var result = new AiUsage { Source = host };
        if (response["usage"] is not JsonObject usage) return result;
        long? Count(JsonNode? value)
        {
            if (value is JsonValue node && node.TryGetValue<long>(out var count) && count is >= 0 and <= 100000000) return count;
            return null;
        }
        result.InputTokens = Count(usage[responses ? "input_tokens" : "prompt_tokens"]);
        result.OutputTokens = Count(usage[responses ? "output_tokens" : "completion_tokens"]);
        result.CachedInputTokens = Count((usage[responses ? "input_tokens_details" : "prompt_tokens_details"] as JsonObject)?["cached_tokens"] ?? usage["prompt_cache_hit_tokens"]);
        result.ReasoningTokens = Count((usage[responses ? "output_tokens_details" : "completion_tokens_details"] as JsonObject)?["reasoning_tokens"]);
        if (result.CachedInputTokens > result.InputTokens) result.CachedInputTokens = null;
        if (result.ReasoningTokens > result.OutputTokens) result.ReasoningTokens = null;
        // Only xAI documents this field as official monetary ticks. A compatible server's arbitrary cost is not a bill.
        if (host.Equals("api.x.ai", StringComparison.OrdinalIgnoreCase) && usage["cost_in_usd_ticks"] is JsonValue cost &&
            cost.TryGetValue<decimal>(out var ticks) && ticks is >= 0 and <= 10000000000000000m)
        { result.Cost = ticks / 10000000000m; result.Currency = "USD"; result.CostKind = "reported"; }
        return result;
    }
    internal static AiModelReply Parse(JsonObject response, bool responses, ISet<string?> allowed)
    {
        var calls = new List<AiFunctionCall>(); var text = new StringBuilder(); string? reasoning = null;
        void Call(string? id, string? name, string? arguments)
        {
            if (calls.Count >= 4 || string.IsNullOrWhiteSpace(id) || id.Length > 160 ||
                calls.Any(x => x.Id == id) || name is null || !allowed.Contains(name) || arguments is null || arguments.Length > 18000)
                throw new AiToolsException("tool_invalid");
            var args = JsonNode.Parse(arguments, documentOptions: new JsonDocumentOptions { MaxDepth = 8 }) as JsonObject
                ?? throw new AiToolsException("tool_invalid");
            calls.Add(new(id, name, args));
        }
        if (responses)
        {
            if (response["status"]?.GetValue<string>() is "incomplete" or "failed" or "cancelled" or "queued" or "in_progress") throw new AiToolsException("api_incomplete");
            if (response["output"] is not JsonArray output) throw new AiToolsException("api_format");
            foreach (var item in output.OfType<JsonObject>())
            {
                if (item["type"]?.GetValue<string>() == "function_call")
                    Call(item["call_id"]?.GetValue<string>(), item["name"]?.GetValue<string>(), item["arguments"]?.GetValue<string>());
                else if (item["type"]?.GetValue<string>() == "message" && item["content"] is JsonArray content)
                    foreach (var part in content.OfType<JsonObject>()) if (part["type"]?.GetValue<string>() == "output_text") text.Append(part["text"]?.GetValue<string>());
            }
        }
        else
        {
            if (response["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
                throw new AiToolsException("api_format");
            if (choice["finish_reason"]?.GetValue<string>() is "length" or "content_filter") throw new AiToolsException("api_incomplete");
            var message = choice["message"] as JsonObject ?? throw new AiToolsException("api_format");
            reasoning = message["reasoning_content"]?.GetValue<string>();
            if (message["content"] is JsonValue content && content.TryGetValue<string>(out var value)) text.Append(value);
            if (message["tool_calls"] is JsonArray toolCalls) foreach (var call in toolCalls.OfType<JsonObject>())
                Call(call["id"]?.GetValue<string>(), call["function"]?["name"]?.GetValue<string>(), call["function"]?["arguments"]?.GetValue<string>());
        }
        if (text.Length > 16000 || (text.Length == 0 && calls.Count == 0)) throw new AiToolsException("api_format");
        long Count(string key)
        {
            long count = response["usage"]?[key]?.GetValue<long>() ?? 0;
            if (count < 0 || count > 100000) throw new AiToolsException("api_format");
            return count;
        }
        return new(text.ToString(), calls, Count(responses ? "input_tokens" : "prompt_tokens"), Count(responses ? "output_tokens" : "completion_tokens"),
            reasoning, responses ? (JsonArray?)response["output"]?.DeepClone() : null);
    }
    public void Dispose() => http.Dispose();
}
