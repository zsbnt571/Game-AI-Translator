using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// AI-only metadata. ApiSettings.Model remains the exact, authoritative wire model ID.
internal sealed class AiAgentOptions
{
    public string Backend { get; set; } = "direct";
    public string AuthMode { get; set; } = "api";
    public string HarnessPath { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ReasoningEffort { get; set; } = "";
    public List<string> SupportedEfforts { get; set; } = [];
    public string EffortSource { get; set; } = "";
    public string EffortBackend { get; set; } = "";
    public string EffortModelId { get; set; } = "";
    public string EffortEndpoint { get; set; } = "";
    public DateTimeOffset? EffortDiscoveredAt { get; set; }
    public AiModelPrice? Price { get; set; }
    public string XaiManagementApiKey { get; set; } = "";
    public string XaiTeamId { get; set; } = "";

    internal AiAgentOptions Clone() => JsonSerializer.Deserialize<AiAgentOptions>(JsonSerializer.Serialize(this))!;
    internal AiAgentOptions Validated()
    {
        var value = Clone();
        if (value.Backend is not ("direct" or "codex" or "opencode") || value.AuthMode is not ("api" or "account") ||
            !Clean(value.HarnessPath, 2048) || !Clean(value.ProviderId, 120) || !Clean(value.ReasoningEffort, 80) ||
            !Clean(value.EffortSource, 2048) || value.EffortBackend is not ("" or "codex" or "opencode") || !Clean(value.EffortModelId, 256) || !Clean(value.EffortEndpoint, 2048) ||
            !Clean(value.XaiManagementApiKey, 8192) || !Clean(value.XaiTeamId, 120) || value.SupportedEfforts is null ||
            value.SupportedEfforts.Count > 32 || value.SupportedEfforts.Any(x => !Clean(x, 80) || x.Length == 0) ||
            value.SupportedEfforts.Distinct(StringComparer.Ordinal).Count() != value.SupportedEfforts.Count)
            throw new AiToolsException("ai_agent_options");
        if (value.AuthMode == "account" &&
            (value.Backend is not ("codex" or "opencode") || !value.ProviderId.Equals("openai", StringComparison.OrdinalIgnoreCase)))
            throw new AiToolsException("ai_account_unsupported");
        value.Price?.Validate();
        return value;
    }

    // A changed model or endpoint must not inherit another model's reasoning knobs.
    internal IReadOnlyList<string> EffortsFor(ApiSettings settings) =>
        !EffortProtocolUnsupported(settings) &&
        (EffortBackend.Length == 0 || EffortBackend == Backend) &&
        string.Equals(EffortModelId, settings.Model, StringComparison.Ordinal) &&
        SameEndpoint(EffortEndpoint, settings.ApiUrl) &&
        EffortSource.Length > 0 ? SupportedEfforts.ToArray() : [];

    internal string EffectiveEffort(ApiSettings settings) => EffortsFor(settings).Contains(ReasoningEffort, StringComparer.Ordinal) ? ReasoningEffort : "";
    private static bool SameEndpoint(string left, string right) =>
        Uri.TryCreate(left.Trim(), UriKind.Absolute, out var first) && Uri.TryCreate(right.Trim(), UriKind.Absolute, out var second) &&
        string.Equals(first.AbsoluteUri.TrimEnd('/'), second.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);
    internal bool EffortProtocolUnsupported(ApiSettings settings) => Backend == "opencode" && AuthMode == "api" &&
        NativeProtocol(settings.ApiUrl, ProviderId) is "anthropic" or "gemini";

    // Shared wire-protocol classification, independent of SDK or persisted model metadata.
    internal static string NativeProtocol(string apiUrl, string providerId)
    {
        if (!Uri.TryCreate(apiUrl.Trim(), UriKind.Absolute, out var url)) return "compatible";
        string path = url.AbsolutePath.TrimEnd('/'), provider = providerId.ToLowerInvariant();
        bool compatible = path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) || path.Contains("/openai", StringComparison.OrdinalIgnoreCase);
        if (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) || !compatible &&
            (provider is "anthropic" or "claude" || url.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase))) return "anthropic";
        if (!compatible && (provider is "gemini" or "google" || url.Host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))) return "gemini";
        return "compatible";
    }
    internal static bool Clean(string? value, int maximum) => value is not null && value.Length <= maximum && !value.Any(char.IsControl);
}

internal sealed class AiModelPrice
{
    public string Currency { get; set; } = "USD";
    public decimal? InputPerMillion { get; set; }
    public decimal? CachedInputPerMillion { get; set; }
    public decimal? OutputPerMillion { get; set; }
    public string ModelId { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Source { get; set; } = "";
    public string ScopeNote { get; set; } = "";
    public string CredentialFingerprint { get; set; } = "";
    public DateTimeOffset? AsOf { get; set; }
    public bool IsEstimate { get; set; } = true;
    internal static string Fingerprint(ApiSettings settings)
    {
        if (!Uri.TryCreate(settings.ApiUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")) return "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri + "\0" + (settings.ApiKey ?? ""))));
    }
    internal void BindTo(ApiSettings settings)
    {
        ModelId = settings.Model; Endpoint = settings.ApiUrl;
        CredentialFingerprint = Fingerprint(settings);
    }
    internal void Validate()
    {
        if (Currency is not { Length: 3 } || Currency.Any(c => c is < 'A' or > 'Z') || !AiAgentOptions.Clean(ModelId, 256) ||
            !AiAgentOptions.Clean(Endpoint, 2048) || !AiAgentOptions.Clean(Source, 2048) || !AiAgentOptions.Clean(ScopeNote, 120) || !IsEstimate ||
            CredentialFingerprint is null || (CredentialFingerprint.Length != 0 &&
                (CredentialFingerprint.Length != 64 || CredentialFingerprint.Any(c => !char.IsAsciiHexDigit(c)))) ||
            InputPerMillion is < 0 or > 1000000 || CachedInputPerMillion is < 0 or > 1000000 || OutputPerMillion is < 0 or > 1000000)
            throw new AiToolsException("ai_agent_options");
    }
}

internal sealed class AiModelDescriptor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> SupportedEfforts { get; set; } = [];
    public string DefaultEffort { get; set; } = "";
    public string EffortSource { get; set; } = "";
    public DateTimeOffset? EffortDiscoveredAt { get; set; }
    public AiModelPrice? Price { get; set; }
    // Null means the catalog did not establish this capability. Do not infer vision from a name.
    public bool? SupportsImageInput { get; set; }
    public bool? SupportsPdfInput { get; set; }
    public bool? SupportsTextOutput { get; set; }
    public string InputCapabilitySource { get; set; } = "";
    public string OutputCapabilitySource { get; set; } = "";
    public string CatalogCategory { get; set; } = "unknown";
}

internal sealed class AiModelCatalogResult
{
    public string Status { get; set; } = "error";
    public string Code { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AiModelDescriptor> Models { get; set; } = [];
}

internal sealed class AiBalanceItem
{
    public string Currency { get; set; } = "";
    public decimal Total { get; set; }
    public decimal? Granted { get; set; }
    public decimal? ToppedUp { get; set; }
    // xAI exposes signed prepaid ledger cents. Keep its sign, do not invent a credit conversion.
    public string Kind { get; set; } = "available_balance";
}

internal sealed class AiBalanceChange
{
    public string Currency { get; set; } = "";
    public decimal Amount { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset? At { get; set; }
}

internal sealed class AiBalanceResult
{
    public string Status { get; set; } = "unsupported";
    public string Code { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Source { get; set; } = "";
    public string OfficialUrl { get; set; } = "";
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool? IsAvailable { get; set; }
    public List<AiBalanceItem> Items { get; set; } = [];
    public List<AiBalanceChange> Changes { get; set; } = [];
}
