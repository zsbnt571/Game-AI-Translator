using System.Text.Json.Serialization;

namespace ScreenshotTranslationUiTester;

// One entry per attempted provider request, retained even when canceled or failed.
// Missing counters and money stay null; reasoning/cached tokens are subsets, never added twice.
internal sealed class AiRequestUsage
{
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "pending";
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? ReasoningTokens { get; set; }
    public decimal? Cost { get; set; }
    public string Currency { get; set; } = "";
    public string CostKind { get; set; } = "unavailable";
    public string PriceSource { get; set; } = "";
    public string EstimateScope { get; set; } = "";
    public bool CacheDiscountUnknown { get; set; }
    internal void Apply(AiUsage? usage, AiModelPrice? price, ApiSettings settings)
    {
        // Native sessions report cumulative snapshots. Never leave an older subtotal
        // looking like the completed cost when the next checkpoint is unknown.
        InputTokens = OutputTokens = CachedInputTokens = ReasoningTokens = null;
        Cost = null; Currency = ""; CostKind = "unavailable"; PriceSource = "";
        EstimateScope = ""; CacheDiscountUnknown = false;
        if (usage is null) return;
        InputTokens = usage.InputTokens; OutputTokens = usage.OutputTokens;
        CachedInputTokens = usage.CachedInputTokens; ReasoningTokens = usage.ReasoningTokens;
        if (usage.Cost is >= 0 and <= 1000000 && ValidCurrency(usage.Currency) && usage.CostKind is "reported" or "estimated")
        { Cost = usage.Cost; Currency = usage.Currency; CostKind = usage.CostKind; PriceSource = usage.Source;
          EstimateScope = usage.CostKind == "estimated" ? "native_estimate" : ""; return; }
        if (price is null || price.CredentialFingerprint.Length == 0 || price.CredentialFingerprint != AiModelPrice.Fingerprint(settings) || price.ModelId != settings.Model ||
            !string.Equals(price.Endpoint.TrimEnd('/'), settings.ApiUrl.TrimEnd('/'), StringComparison.Ordinal) ||
            price.Source.Length == 0 || InputTokens is null || OutputTokens is null ||
            price.InputPerMillion is null || price.OutputPerMillion is null) return;
        if (InputTokens < 0 || OutputTokens < 0 || CachedInputTokens < 0 || CachedInputTokens > InputTokens) return;
        // Some native harnesses cannot distinguish missing cache counters from zero.
        // Keep that usage field unknown. An official standard-text rate can still
        // give a useful base estimate without applying an unreported cache discount.
        // It is explicitly scoped, never presented as the provider's actual charge.
        if (CachedInputTokens is null && price.ScopeNote != "standard_text_base") return;
        if (CachedInputTokens > 0 && price.CachedInputPerMillion is null) return;
        try
        {
            price.Validate();
            long pricedCachedInput = CachedInputTokens ?? 0;
            Cost = ((InputTokens.Value - pricedCachedInput) * price.InputPerMillion.Value +
                pricedCachedInput * (price.CachedInputPerMillion ?? price.InputPerMillion.Value) +
                OutputTokens.Value * price.OutputPerMillion.Value) / 1000000m;
            if (Cost is < 0 or > 1000000) { Cost = null; return; }
            Currency = price.Currency; CostKind = "estimated"; PriceSource = price.Source;
            EstimateScope = price.ScopeNote; CacheDiscountUnknown = CachedInputTokens is null;
        }
        catch (Exception ex) when (ex is OverflowException or AiToolsException)
        { Cost = null; Currency = ""; CostKind = "unavailable"; }
    }
    internal static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(c => c is >= 'A' and <= 'Z');
}

internal sealed class AiUsage
{
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? ReasoningTokens { get; set; }
    public decimal? Cost { get; set; }
    public string Currency { get; set; } = "";
    public string CostKind { get; set; } = "reported";
    public string Source { get; set; } = "";
}

internal sealed class AiTurnUsage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string Model { get; set; } = "";
    public string Backend { get; set; } = "direct";
    public string Effort { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "pending";
    public List<AiRequestUsage> Requests { get; set; } = [];
    [JsonIgnore] public int RequestCount => Requests.Count;
    [JsonIgnore] public int? ApiRequestCount => Backend == "direct" ? Requests.Count : null;
    [JsonIgnore] public long? InputTokens => Requests.Count > 0 && Requests.All(x => x.InputTokens.HasValue) ? Requests.Sum(x => x.InputTokens!.Value) : null;
    [JsonIgnore] public long? OutputTokens => Requests.Count > 0 && Requests.All(x => x.OutputTokens.HasValue) ? Requests.Sum(x => x.OutputTokens!.Value) : null;
    [JsonIgnore] public string Currency => Requests.Count > 0 && Requests.All(x => x.Currency == Requests[0].Currency) ? Requests[0].Currency : "";
    [JsonIgnore] public decimal? Cost => Requests.Count > 0 && Currency.Length > 0 && Requests.All(x => x.Cost.HasValue) ? Requests.Sum(x => x.Cost!.Value) : null;
    [JsonIgnore] public string CostKind => !Cost.HasValue ? "unavailable" : Requests.All(x => x.CostKind == "reported") ? "reported" : "estimated";

    internal bool Valid(string taskId) => Guid.TryParseExact(Id, "N", out _) && TaskId == taskId &&
        AiAgentOptions.Clean(ProfileId, 256) && AiAgentOptions.Clean(ProfileName, 300) && AiAgentOptions.Clean(Model, 256) &&
        Backend is "direct" or "codex" or "opencode" && AiAgentOptions.Clean(Effort, 80) && AiAgentOptions.Clean(Status, 100) &&
        Requests is not null && Requests.Count <= 20 && Requests.All(x => x is not null &&
        x.InputTokens is null or >= 0 and <= 100000000 && x.OutputTokens is null or >= 0 and <= 100000000 &&
        x.CachedInputTokens is null or >= 0 and <= 100000000 && x.ReasoningTokens is null or >= 0 and <= 100000000 &&
        !(x.CachedInputTokens > x.InputTokens) && !(x.ReasoningTokens > x.OutputTokens) &&
        x.Cost is null or >= 0 and <= 1000000 &&
        (x.Cost.HasValue ? AiRequestUsage.ValidCurrency(x.Currency) && x.CostKind is "reported" or "estimated" :
            x.CostKind == "unavailable" && (x.Currency is { Length: 0 } || AiRequestUsage.ValidCurrency(x.Currency))) &&
        AiAgentOptions.Clean(x.Status, 100) && AiAgentOptions.Clean(x.PriceSource, 2048) && AiAgentOptions.Clean(x.EstimateScope, 120));
}
