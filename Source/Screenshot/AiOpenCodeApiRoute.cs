namespace ScreenshotTranslationUiTester;

// Select the native SDK/wire protocol. Nothing here converts model requests or
// translates one provider's responses into another provider's format.
internal sealed record AiOpenCodeApiRoute(string Protocol, string Sdk, Uri BaseUri, string ModelId)
{
    internal string ProviderId => Protocol switch { "anthropic" => "anthropic", "gemini" => "google", _ => "fusion_api" };
    internal string AuthHeader => Protocol switch { "anthropic" => "x-api-key", "gemini" => "x-goog-api-key", _ => "Authorization" };
    internal string Credential(string key) => AuthHeader == "Authorization" ? "Bearer " + key : key;
    internal static AiOpenCodeApiRoute Resolve(ApiSettings settings, AiAgentOptions options)
    {
        if (!Uri.TryCreate(settings.ApiUrl.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            !AiAgentOptions.Clean(settings.Model, 256) || settings.Model.Length == 0 || !AiAgentOptions.Clean(settings.ApiKey, 8192) ||
            (string.IsNullOrWhiteSpace(settings.ApiKey) && !settings.AllowEmptyApiKey)) throw new AiToolsException("harness_endpoint");
        string path = url.AbsolutePath.TrimEnd('/');
        string protocol = AiAgentOptions.NativeProtocol(settings.ApiUrl, options.ProviderId);
        if (protocol == "anthropic")
        {
            if (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)) path = path[..^9];
            if (path.Length == 0) path = "/v1";
            return new("anthropic", "@ai-sdk/anthropic", new Uri(url.GetLeftPart(UriPartial.Authority) + path), settings.Model);
        }
        if (protocol == "gemini")
        {
            string model = settings.Model.StartsWith("models/", StringComparison.Ordinal) ? settings.Model[7..] : settings.Model;
            if (model.Length == 0 || model.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) throw new AiToolsException("harness_endpoint");
            int resource = path.IndexOf("/models/", StringComparison.Ordinal);
            if (resource >= 0)
            {
                if (!path.EndsWith(":generateContent", StringComparison.Ordinal) && !path.EndsWith(":streamGenerateContent", StringComparison.Ordinal)) throw new AiToolsException("harness_endpoint");
                path = path[..resource];
            }
            if (path.Length == 0) path = "/v1beta";
            return new("gemini", "@ai-sdk/google", new Uri(url.GetLeftPart(UriPartial.Authority) + path), model);
        }
        return Legacy(settings);
    }

    internal static AiOpenCodeApiRoute Legacy(ApiSettings settings)
    {
        var endpoint = AiHarnessEnvironment.ApiEndpoint(settings, "opencode");
        string suffix = endpoint.Responses ? "/responses" : "/chat/completions";
        return new(endpoint.Responses ? "responses" : "chat", endpoint.Responses ? "@ai-sdk/openai" : "@ai-sdk/openai-compatible",
            new Uri(endpoint.Url.AbsoluteUri[..^suffix.Length]), settings.Model);
    }

    internal string? Resource(string target)
    {
        string expected = Protocol switch { "anthropic" => "/messages", "responses" => "/responses", "chat" => "/chat/completions", _ => "" };
        if (expected.Length > 0) return target == "/v1" + expected ? expected : null;
        string model = "/models/" + Uri.EscapeDataString(ModelId);
        foreach (string method in new[] { ":generateContent", ":streamGenerateContent?alt=sse" })
            if (target == "/v1" + model + method) return model + method;
        return null;
    }
}
