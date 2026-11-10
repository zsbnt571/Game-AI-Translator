using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Process-local metadata only. No credential, chat content or auth file is stored in this cache.
internal sealed class AiModelCatalogCache144
{
    internal static AiModelCatalogCache144 Shared { get; } = new();
    private static readonly byte[] salt = RandomNumberGenerator.GetBytes(32);
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TimeSpan ttl;
    private readonly Func<DateTimeOffset> clock;
    private long sequence;
    private sealed class Entry
    {
        internal long Generation;
        internal DateTimeOffset Expires;
        internal AiModelCatalogResult? Result;
        internal Task<AiModelCatalogResult>? Pending;
    }
    internal AiModelCatalogCache144(TimeSpan? lifetime = null, Func<DateTimeOffset>? now = null)
    { ttl = lifetime ?? TimeSpan.FromMinutes(10); clock = now ?? (() => DateTimeOffset.UtcNow); }

    internal static string Identity(ApiSettings settings, AiAgentOptions options)
    {
        string endpoint = settings.ApiUrl.Trim().TrimEnd('/');
        // Paths can be case sensitive; do not fold an entire custom URL to lower case.
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            endpoint = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + uri.PathAndQuery.TrimEnd('/');
        string material = string.Join('\0', options.Backend, options.AuthMode,
            options.ProviderId.Trim().ToLowerInvariant(), options.HarnessPath.Trim(), endpoint,
            settings.AllowEmptyApiKey ? "keyless" : "key", settings.ApiKey ?? "");
        return Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(material)));
    }
    internal bool TryGet(ApiSettings settings, AiAgentOptions options, out AiModelCatalogResult result)
    {
        lock (gate)
        {
            if (entries.TryGetValue(Identity(settings, options), out var entry) && entry.Result is not null && entry.Expires > clock())
            { result = Copy(entry.Result); return true; }
        }
        result = new(); return false;
    }
    internal void Invalidate(ApiSettings settings, AiAgentOptions options)
    {
        lock (gate) entries.Remove(Identity(settings, options));
    }
    internal void Clear() { lock (gate) entries.Clear(); }

    internal async Task<AiModelCatalogResult> GetOrFetchAsync(ApiSettings settings, AiAgentOptions options,
        Func<CancellationToken, Task<AiModelCatalogResult>> fetch, bool forceRefresh = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string identity = Identity(settings, options);
        Task<AiModelCatalogResult> work;
        lock (gate)
        {
            if (!forceRefresh && entries.TryGetValue(identity, out var existing))
            {
                if (existing.Result is not null && existing.Expires > clock()) return Copy(existing.Result);
                if (existing.Pending is not null) { work = existing.Pending; goto Wait; }
            }
            // Bounded metadata cache; in-flight generations still check reference identity before publishing.
            if (entries.Count >= 128)
            {
                var oldest = entries.OrderBy(x => x.Value.Expires).First();
                entries.Remove(oldest.Key);
            }
            var entry = new Entry { Generation = ++sequence };
            entries[identity] = entry;
            entry.Pending = work = Fetch(identity, entry, fetch);
        Wait:;
        }
        // One reader cancelling must not cancel another reader's shared catalog request.
        return Copy(await work.WaitAsync(token).ConfigureAwait(false));
    }
    private async Task<AiModelCatalogResult> Fetch(string identity, Entry entry, Func<CancellationToken, Task<AiModelCatalogResult>> fetch)
    {
        await Task.Yield(); // Publish Pending before a synchronous fake or cached transport can complete.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var result = await fetch(timeout.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (!entries.TryGetValue(identity, out var current) || !ReferenceEquals(current, entry))
                    return new() { Status = "superseded", Code = "catalog_superseded", CheckedAt = clock() };
                entry.Pending = null;
                if (result.Status == "available") { entry.Result = Copy(result); entry.Expires = clock() + ttl; }
                else entries.Remove(identity);
            }
            return result;
        }
        catch
        {
            lock (gate) if (entries.TryGetValue(identity, out var current) && ReferenceEquals(current, entry)) entries.Remove(identity);
            throw;
        }
    }
    private static AiModelCatalogResult Copy(AiModelCatalogResult result) =>
        JsonSerializer.Deserialize<AiModelCatalogResult>(JsonSerializer.Serialize(result))!;
}
