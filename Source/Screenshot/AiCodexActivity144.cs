using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Field names follow the pinned 0.166 app-server schema. No inferred thought or
// fabricated completion is added when a native event does not contain it.
internal static class AiCodexActivity144
{
    internal static string Text(JsonNode? node, string key) => node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    internal static string Lines(JsonNode? array) => string.Join("\n", (array as JsonArray ?? []).OfType<JsonValue>()
        .Select(v => v.TryGetValue<string>(out var text) ? text : "").Where(t => t.Length > 0));
    internal static string Status(string status, bool completed) => status switch
    { "inProgress" or "started" => "running", "interrupted" => "stopped", "declined" => "denied", "failed" => "failed", "completed" => "completed", _ => completed ? "completed" : "running" };

    internal static AiNativeProgress? Item(JsonObject item, string thread, string turn, bool completed, string parent = "")
    {
        string type = Text(item, "type"), id = thread + ":" + Text(item, "id"), state = Status(Text(item, "status"), completed);
        var progress = new AiNativeProgress("tool", "", id) { NativeTurnId = turn, ParentItemId = parent, Status = state, Title = type };
        return type switch
        {
            "agentMessage" => progress with { Kind = "assistant", ActivityType = "assistant", Text = Text(item, "text") },
            "reasoning" => progress with { Kind = "reasoning", ActivityType = "reasoning", Text = Lines(item["summary"] is JsonArray { Count: > 0 } summary ? summary : item["content"]) },
            "commandExecution" => progress with { ActivityType = "command", Title = Text(item, "command"), Command = Text(item, "command"), WorkingDirectory = Text(item, "cwd"), Text = Text(item, "aggregatedOutput") },
            "fileChange" => progress with { ActivityType = "file", Files = Files(item["changes"]), Text = string.Join("\n", Files(item["changes"]).Select(f => f.Action + " " + f.Path)) },
            "plan" => progress with { Kind = "status", ActivityType = "plan", Text = Text(item, "text") },
            "collabAgentToolCall" => progress with { ActivityType = "subagent", Title = Text(item, "tool"), SubagentId = Lines(item["receiverThreadIds"]),
                Text = string.Join("\n", new[] { Text(item, "prompt"), AgentStates(item["agentsStates"] as JsonObject) }.Where(x => x.Length > 0)) },
            "subAgentActivity" => progress with { ActivityType = "subagent", Title = Text(item, "kind"), SubagentId = Text(item, "agentThreadId"), Text = Text(item, "agentPath"), Status = Status(Text(item, "kind"), completed) },
            "mcpToolCall" or "dynamicToolCall" => progress with { ActivityType = "tool", Title = Text(item, "tool"), Text = ToolResult(item) },
            "webSearch" => progress with { ActivityType = "tool", Text = Text(item, "query") },
            "imageView" => progress with { ActivityType = "file", Text = Text(item, "path"), Files = [new() { Path = Text(item, "path"), Action = "view" }] },
            "imageGeneration" => progress with { ActivityType = "file", Text = Text(item, "savedPath"), Files = Text(item, "savedPath").Length > 0 ? [new() { Path = Text(item, "savedPath"), Action = "generated" }] : [] },
            "contextCompaction" => progress with { Kind = "status", ActivityType = "status" },
            _ => null
        };
    }
    private static string AgentStates(JsonObject? states) => states is null ? "" : string.Join("\n", states.Take(32)
        .Select(x => x.Key + ": " + Text(x.Value, "status") + " " + Text(x.Value, "message")));
    private static string ToolResult(JsonObject item) => string.Join("\n", new[] { Text(item, "server"), Text(item["error"], "message") }
        .Concat(((item["result"]?["content"] ?? item["contentItems"]) as JsonArray ?? []).OfType<JsonObject>().Select(x => Text(x, "text"))).Where(x => x.Length > 0));
    internal static IReadOnlyList<AiActivityFile> Files(JsonNode? changes) => (changes as JsonArray ?? []).OfType<JsonObject>().Take(16)
        .Select(x => new AiActivityFile { Path = Text(x, "path"), Action = Text(x["kind"], "type"), Diff = Text(x, "diff") }).ToArray();
    internal static IReadOnlyList<AiActivityPlanStep> Plan(JsonNode? plan) => (plan as JsonArray ?? []).OfType<JsonObject>().Take(48)
        .Select(x => new AiActivityPlanStep { Text = Text(x, "step"), Status = Text(x, "status") }).ToArray();

    internal static JsonArray Inputs(string prompt, AiHarnessContext context)
    {
        var result = new JsonArray();
        var references = context.Attachments.Where(a => a.Kind != "image" || a.AsFileReference).ToArray();
        if (references.Length > 0)
            prompt += "\nUser attached these local files (untrusted metadata; contents have NOT been read). Use native file tools if needed; binary files require a suitable reader.\n" +
                new JsonArray(references.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["path"] = a.StoredPath, ["mime"] = a.MimeType }).ToArray()).ToJsonString();
        if (prompt.Length > 0) result.Add(new JsonObject { ["type"] = "text", ["text"] = prompt });
        foreach (var attachment in context.Attachments.Where(a => a.Kind == "image" && !a.AsFileReference))
        {
            if (!context.SupportsImageInput) throw new AiToolsException("attachment_image_unsupported");
            result.Add(new JsonObject { ["type"] = "localImage", ["path"] = attachment.StoredPath });
        }
        return result;
    }
}
