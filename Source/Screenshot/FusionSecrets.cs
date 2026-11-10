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
    internal static string Serialize<T>(T value, string? storagePath = null)
    {
        var node = JsonSerializer.SerializeToNode(value)!;
        Visit(node, true, storagePath, "", null);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    internal static T Deserialize<T>(string json, string? storagePath = null)
    {
        var node = JsonNode.Parse(json)!;
        var pending = storagePath is null ? null : new Dictionary<string, string>(StringComparer.Ordinal);
        Visit(node, false, storagePath, "", pending);
        var value = node.Deserialize<T>()!;
        if (storagePath is not null) UnavailableCredentials.Loaded(storagePath, pending!);
        return value;
    }
    private static void Visit(JsonNode node, bool protect, string? storagePath, string pointer, Dictionary<string, string>? pending)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
            {
                var field = pointer + "/" + UnavailableCredentials.Field(pair.Key);
                if (pair.Key == "ApiKey" && pair.Value is JsonValue v && v.TryGetValue<string>(out var key))
                    obj[pair.Key] = storagePath is null ? (protect ? Protect(key) : Unprotect(key))
                        : protect ? UnavailableCredentials.ForSave(storagePath, field, key)
                        : UnavailableCredentials.Read(key, field, pending!);
                else if (pair.Value is JsonObject or JsonArray) Visit(pair.Value, protect, storagePath, field, pending);
            }
        if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++)
                if (array[i] is { } value) Visit(value, protect, storagePath, pointer + "/" + i, pending);
    }
}

