using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal static class FusionSecrets
{
    private const string Prefix = "dpapi:v1:";
    internal static string Protect(string text) => string.IsNullOrEmpty(text) || text.StartsWith(Prefix) ? text :
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));
    internal static string Unprotect(string text) => text.StartsWith(Prefix) ?
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text[Prefix.Length..]), null, DataProtectionScope.CurrentUser)) : text;
    internal static string Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value)!;
        Visit(node, true);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    internal static T Deserialize<T>(string json)
    {
        var node = JsonNode.Parse(json)!;
        Visit(node, false);
        return node.Deserialize<T>()!;
    }
    private static void Visit(JsonNode node, bool protect)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
                if (pair.Key == "ApiKey" && pair.Value is JsonValue v && v.TryGetValue<string>(out var key))
                    obj[pair.Key] = protect ? Protect(key) : Unprotect(key);
                else if (pair.Value is JsonObject or JsonArray) Visit(pair.Value, protect);
        if (node is JsonArray array) foreach (var value in array) if (value is not null) Visit(value, protect);
    }
}

