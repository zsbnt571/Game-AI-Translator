namespace ScreenshotTranslationUiTester;

// Saved API connections own credentials, not an execution assistant. Project a
// disposable runtime view; never rewrite credentials, URLs or saved defaults.
internal static class AiExecutionConnection146
{
    internal static AiAgentOptions Resolve(AiAgentOptions saved, string backend)
    {
        if (backend is not ("codex" or "opencode")) throw new AiToolsException("harness_backend");
        if (saved.Backend is not ("codex" or "opencode")) throw new AiToolsException("ai_backend_retired");
        if (saved.AuthMode == "account" && saved.Backend != backend) throw new AiToolsException("ai_account_backend_mismatch");
        var value = saved.Clone();
        // Legacy metadata belongs to the assistant with which it was saved.
        if (value.EffortBackend.Length == 0) value.EffortBackend = saved.Backend;
        if (value.Backend != backend) value.HarnessPath = "";
        value.Backend = backend;
        if (value.EffortBackend != backend)
        {
            value.ReasoningEffort = ""; value.SupportedEfforts = []; value.EffortSource = "";
            value.EffortModelId = ""; value.EffortEndpoint = ""; value.EffortDiscoveredAt = null;
        }
        return value;
    }

    // This checks the supported route shape, not the remote provider's promises.
    // A connection may be saved before selecting a model; no fabricated model or
    // paid request is used to determine whether an assistant can use its protocol.
    internal static string CompatibilityReason(ApiSettings settings, AiAgentOptions saved, string backend)
    {
        AiAgentOptions options;
        try { options = Resolve(saved, backend).Validated(); }
        catch (AiToolsException ex) { return ex.Code; }
        if (options.AuthMode == "account") return "";
        if (string.IsNullOrWhiteSpace(settings.ApiUrl) || string.IsNullOrWhiteSpace(settings.ApiKey) && !settings.AllowEmptyApiKey)
            return "profile_incomplete";
        if (!Uri.TryCreate(settings.ApiUrl.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0) return "harness_endpoint";
        string protocol = AiAgentOptions.NativeProtocol(settings.ApiUrl, options.ProviderId);
        string path = url.AbsolutePath.TrimEnd('/');
        if (backend == "opencode")
        {
            // Match native route shape validation without inventing a model ID.
            if (protocol == "gemini" && path.Contains("/models/", StringComparison.Ordinal) &&
                !path.EndsWith(":generateContent", StringComparison.Ordinal) && !path.EndsWith(":streamGenerateContent", StringComparison.Ordinal)) return "harness_endpoint";
            if (protocol == "compatible" && path.Contains(":generateContent", StringComparison.OrdinalIgnoreCase)) return "ai_protocol_unsupported";
            if (protocol == "compatible" && url.Host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) &&
                path is not ("" or "/v1beta" or "/v1beta/openai")) return "ai_protocol_unsupported";
            return "";
        }
        if (protocol is "anthropic" or "gemini") return "harness_responses_required";
        bool explicitChat = path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase);
        bool responses = path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) ||
            !explicitChat && (url.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) || url.Host.Equals("api.x.ai", StringComparison.OrdinalIgnoreCase)) ||
            url.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase);
        return responses ? "" : "harness_responses_required";
    }
}
