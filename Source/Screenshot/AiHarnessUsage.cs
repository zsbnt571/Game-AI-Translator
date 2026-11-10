using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Native harnesses normalize missing usage subfields to zero. Positive values are evidence;
// a normalized zero alone is not evidence of a free request or zero cached/reasoning tokens.
internal static class AiHarnessUsage
{
    internal static long? Count(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (!value.TryGetValue<long>(out long number)) { if (!value.TryGetValue<int>(out int small)) return null; number = small; }
        return number is >= 0 and <= 10000000 ? number : null;
    }
    private static long? Positive(JsonNode? node) => Count(node) is > 0 and var count ? count : null;
    internal static AiUsage? Codex(JsonNode? node)
    {
        if (node is not JsonObject) return null;
        var value = new AiUsage { InputTokens = Positive(node["inputTokens"]), OutputTokens = Positive(node["outputTokens"]),
            CachedInputTokens = Positive(node["cachedInputTokens"]), ReasoningTokens = Positive(node["reasoningOutputTokens"]),
            CostKind = "unavailable", Source = "Codex app-server tokenUsage.total" };
        if (value.CachedInputTokens > value.InputTokens) value.CachedInputTokens = null;
        if (value.ReasoningTokens > value.OutputTokens) value.ReasoningTokens = null;
        return value.InputTokens is null && value.OutputTokens is null ? null : value;
    }

    internal static AiUsage? OpenCode(JsonNode? history, bool account)
    {
        long? input = 0, output = 0, cached = 0, reasoning = 0;
        decimal? cost = 0; bool any = false;
        foreach (var entry in (history as JsonArray ?? []).OfType<JsonObject>())
        {
            if (entry["info"]?["role"]?.GetValue<string>() != "assistant") continue;
            any = true; var node = entry["info"]?["tokens"];
            long? i = Count(node?["input"]), o = Count(node?["output"]), r = Count(node?["reasoning"]),
                read = Count(node?["cache"]?["read"]), write = Count(node?["cache"]?["write"]);
            // v1.19.5 session.getUsage subtracts cached input and reasoning output. Restore
            // the common inclusive counters before handing them to Fusion's cost/history UI.
            long? allInput = i + read + write, allOutput = o + r;
            input = allInput is > 0 ? input + allInput : null;
            output = allOutput is > 0 ? output + allOutput : null;
            cached = read is > 0 ? cached + read : null;
            reasoning = r is > 0 ? reasoning + r : null;
            if (!account && entry["info"]?["cost"] is JsonValue c && c.TryGetValue<decimal>(out decimal amount) && amount is > 0 and <= 1000000)
                cost += amount;
            else cost = null;
        }
        if (!any) return null;
        return new AiUsage { InputTokens = input, OutputTokens = output, CachedInputTokens = cached, ReasoningTokens = reasoning,
            Cost = cost, Currency = cost is null ? "" : "USD", CostKind = cost is null ? "unavailable" : "estimated",
            Source = "OpenCode 1.19.5 native usage; model-rate cost estimate" };
    }

}
