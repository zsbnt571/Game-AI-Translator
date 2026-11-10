using System.Security.Cryptography;

namespace ScreenshotTranslationUiTester;

// Runtime-only recovery state. Failed ciphertext is kept verbatim in its existing
// file until that individual key is replaced; it is never exposed as an API key.
internal static class UnavailableCredentials
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, string>> Stores = new(StringComparer.OrdinalIgnoreCase);
    internal const string Notice = "部分 API 密钥无法在当前 Windows 账户读取。原有方案、连接和记录已保留；请在设置中重新填写翻译方案及 AI 连接的 API Key。";
    internal static bool Any { get { lock (Gate) return Stores.Values.Any(v => v.Count != 0); } }
    internal static string Field(string name) => name.Replace("~", "~0").Replace("/", "~1");
    private static string Store(string path) => Path.GetFullPath(path);

    internal static string Read(string text, string field, Dictionary<string, string> pending)
    {
        try { return FusionSecrets.Unprotect(text); }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            pending[field] = text;
            return "";
        }
    }
    internal static void Loaded(string path, Dictionary<string, string> pending)
    {
        lock (Gate) Stores[Store(path)] = new(pending, StringComparer.Ordinal);
    }
    internal static string LoadSingle(string path, string field, string text)
    {
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        var plain = Read(text, field, pending);
        Loaded(path, pending);
        return plain;
    }
    internal static bool IsUnavailable(string path, string field)
    {
        lock (Gate) return Stores.TryGetValue(Store(path), out var fields) && fields.ContainsKey(field);
    }
    internal static string ForSave(string path, string field, string value)
    {
        if (value.Length == 0)
            lock (Gate)
                if (Stores.TryGetValue(Store(path), out var fields) && fields.TryGetValue(field, out var original)) return original;
        return FusionSecrets.Protect(value);
    }
    // Call only AFTER the atomic persistence succeeds, so a failed save cannot
    // forget another still-unavailable secret or replace it with an empty value.
    internal static void Saved(string path, string field, string value)
    {
        if (value.Length == 0) return;
        lock (Gate) if (Stores.TryGetValue(Store(path), out var fields)) fields.Remove(field);
    }
    internal static void RetainFields(string path, IEnumerable<string> present)
    {
        var keep = present.ToHashSet(StringComparer.Ordinal);
        lock (Gate)
            if (Stores.TryGetValue(Store(path), out var fields))
                foreach (var field in fields.Keys.Where(k => !keep.Contains(k)).ToArray()) fields.Remove(field);
    }
    internal static void CopyField(string path, string from, string to)
    {
        lock (Gate)
            if (Stores.TryGetValue(Store(path), out var fields) && fields.TryGetValue(from, out var value)) fields[to] = value;
    }
    internal static void CopyStore(string from, string to)
    {
        lock (Gate)
            if (Stores.TryGetValue(Store(from), out var fields)) Stores[Store(to)] = new(fields, StringComparer.Ordinal);
            else Stores.Remove(Store(to));
    }
    internal static void Forget(string path) { lock (Gate) Stores.Remove(Store(path)); }
}
