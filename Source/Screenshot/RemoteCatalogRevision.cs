using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// A resource-language selection can change without replacing the live bridge.
// Its generation invalidates pagination and unseen work, never ordinary caches.
internal sealed record RemoteCatalogRevision(int Value, bool Rebuilding)
{
    internal static RemoteCatalogRevision? Read(JsonElement message)
    {
        if (!message.TryGetProperty("catalogRevision", out var revision)) return null;
        if (revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt32(out int value) || value < 0)
            return null;
        bool rebuilding = false;
        if (message.TryGetProperty("catalogRebuilding", out var pending))
        {
            if (pending.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;
            rebuilding = pending.GetBoolean();
        }
        return new(value, rebuilding);
    }
}
