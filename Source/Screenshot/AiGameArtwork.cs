using System.Windows.Media.Imaging;

namespace ScreenshotTranslationUiTester;

internal sealed record AiGameArtworkImages(BitmapSource? Cover, BitmapSource? Icon);

// UI-only metadata. It is never serialized into a task or model request.
// Consumers must check that their card is still current after awaiting a load.
internal sealed class AiGameArtwork(string key, CoverFraming? frame, Func<Task<AiGameArtworkImages>> load)
{
    internal string Key { get; } = key;
    internal CoverFraming? Frame { get; } = frame;
    internal Task<AiGameArtworkImages> LoadAsync() => load();
}

public sealed partial class MainForm
{
    // Card thumbnails share a bounded cache across page refreshes. The library's
    // full-size detail cache is left intact; no original artwork is replaced.
    private readonly WpfCoverCache _aiArtworkCache = new(32L * 1024 * 1024, 128);

    private static string AiArtworkKey(RecentGame record) => string.Join("\0", record.Id,
        record.ArtworkVersion, record.Cover, record.Icon, record.IconStamp, record.CoverFrame);

    private AiGameArtwork AiArtwork(RecentGameStore store, RecentGame record)
    {
        string key = AiArtworkKey(record);
        bool Current() => store.Find(record.ExePath) is { } live && AiArtworkKey(live) == key;
        BitmapSource? Read(bool cover)
        {
            // The existing library reader validates the current record, stored
            // image name, dimensions and quality. It performs no web lookup.
            if (!Current()) return null;
            using var bitmap = store.ReadImage(record, cover, cover ? 640 : 64);
            return bitmap is null || !Current() ? null : WpfDrawing.Image(bitmap);
        }
        async Task<AiGameArtworkImages> Load()
        {
            try
            {
                var cover = _aiArtworkCache.GetAsync(record.Cover is null ? null : key + "\0cover", () => Read(true));
                var icon = _aiArtworkCache.GetAsync(record.Icon is null ? null : key + "\0icon", () => Read(false));
                await Task.WhenAll(cover, icon).ConfigureAwait(false);
                return Current() ? new(cover.Result, icon.Result) : new(null, null);
            }
            catch (Exception) { return new(null, null); }
        }
        return new(key, record.CoverFrame?.Normalize(), Load);
    }
}
