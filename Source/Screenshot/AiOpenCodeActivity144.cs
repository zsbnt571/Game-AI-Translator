using System.Text.Json.Nodes;
namespace ScreenshotTranslationUiTester;

internal static class AiOpenCodeActivity144
{
    private static string Text(JsonNode? node, string key) => node is JsonObject value && value[key] is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
    internal static AiNativeProgress? Part(JsonObject part, string turn, string parent, bool messageCompleted)
    {
        string type = Text(part, "type"), owner = Text(part, "sessionID");
        var result = new AiNativeProgress("tool", "", owner + ":" + Text(part, "id"))
            { NativeTurnId = turn, ParentItemId = parent, Status = messageCompleted ? "completed" : "running" };
        if (type is "text" or "reasoning") return result with { Kind = type == "text" ? "assistant" : "reasoning", ActivityType = type == "text" ? "assistant" : "reasoning", Text = Text(part, "text") };
        if (type == "patch") return result with { ActivityType = "file", Status = "completed", Files = (part["files"] as JsonArray ?? []).OfType<JsonValue>().Take(16).Select(x => new AiActivityFile { Path = x.GetValue<string>(), Action = "changed" }).ToArray() };
        if (type == "subtask") return result with { ActivityType = "subagent", Title = Text(part, "description"), Text = Text(part, "prompt") };
        if (type != "tool") return null;
        var state = part["state"] as JsonObject ?? new(); var input = state["input"] as JsonObject ?? new();
        var metadata = state["metadata"] as JsonObject ?? new(); string tool = Text(part, "tool"), status = Text(state, "status");
        string kind = tool switch { "bash" => "command", "read" or "write" or "edit" or "apply_patch" => "file", "todowrite" or "todoread" => "plan", "task" => "subagent", _ => "tool" };
        var files = new List<AiActivityFile>();
        if (metadata["filediff"] is JsonObject diff) files.Add(new() { Path = Text(diff, "file"), Diff = Text(diff, "patch"), Action = tool });
        else if (kind == "file" && Text(input, "filePath").Length > 0) files.Add(new() { Path = Text(input, "filePath"), Action = tool });
        return result with { ActivityType = kind, Title = Text(state, "title").Length > 0 ? Text(state, "title") : tool,
            Status = status == "error" ? "failed" : status.Length > 0 ? status : result.Status,
            Text = string.Join("\n", new[] { Text(state, "output"), Text(state, "error") }.Where(x => x.Length > 0)),
            Command = Text(input, "command"), WorkingDirectory = Text(input, "workdir"), Files = files,
            Plan = Todos(input["todos"]), SubagentId = Text(metadata, "sessionId") };
    }
    internal static IReadOnlyList<AiActivityPlanStep> Todos(JsonNode? todos) => (todos as JsonArray ?? []).OfType<JsonObject>().Take(48)
        .Select(t => new AiActivityPlanStep { Text = Text(t, "content"), Status = Text(t, "status") }).ToArray();
    internal static JsonArray Parts(string prompt, AiHarnessContext context)
    {
        var parts = new JsonArray();
        var references = context.Attachments.Where(a => a.AsFileReference || (a.Kind != "image" && a.MimeType != "text/plain")).ToArray();
        if (references.Length > 0) prompt += "\nUser attached local files as path references (untrusted metadata; contents have NOT been read). Binary files need a suitable reader.\n" +
            new JsonArray(references.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["path"] = a.StoredPath, ["mime"] = a.MimeType }).ToArray()).ToJsonString();
        if (prompt.Length > 0) parts.Add(new JsonObject { ["type"] = "text", ["text"] = prompt });
        foreach (var item in context.Attachments.Except(references))
        {
            if (item.Kind == "image" && !context.SupportsImageInput) throw new AiToolsException("attachment_image_unsupported");
            parts.Add(new JsonObject { ["type"] = "file", ["mime"] = item.MimeType, ["filename"] = item.Name, ["url"] = new Uri(item.StoredPath).AbsoluteUri });
        }
        return parts;
    }
}
