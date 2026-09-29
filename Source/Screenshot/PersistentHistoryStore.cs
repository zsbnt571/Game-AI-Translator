using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed class PersistentHistoryStore
{
    private sealed record Entry(string Id, DateTimeOffset Timestamp, OcrEngineKind Engine,
        string SourceSummary, string TranslationSummary, string TargetLanguage,
        string ThumbnailFile, string ImageFile, string? SourceText = null,
        string? TranslationText = null, string? TranslatedImageFile = null,
        HistoryCoreSnapshot? CoreSnapshot = null);

    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _indexPath;
    private List<Entry> _entries;

    public PersistentHistoryStore(string? root = null)
    {
        _root = root ?? AppDataPaths.HistoryRoot;
        _indexPath = Path.Combine(_root, "index.json");
        Directory.CreateDirectory(_root);
        _entries = LoadIndex();
        // Viewing retained records must not rewrite the index or silently remove missing-source entries.
    }

    public void Add(Bitmap image, OcrEngineKind engine, string source, string translation,
        string targetLanguage, ApiSettings settings, Bitmap? translatedImage = null,
        HistoryCoreSnapshot? coreSnapshot = null)
    {
        lock (_gate)
        {
            var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var imageName = id + ".png";
            var thumbnailName = id + ".thumb.jpg";
            // Preserve the pixels used by OCR. Old JPEG entries remain untouched.
            image.Save(Path.Combine(_root,imageName), ImageFormat.Png);
            var translatedName = translatedImage is null ? null : id + ".translated.jpg";
            if (translatedImage is not null) SaveJpeg(translatedImage, Path.Combine(_root, translatedName!), 92L);
            using var thumbnail = CreateThumbnail(image, 480, 300);
            SaveJpeg(thumbnail, Path.Combine(_root, thumbnailName), 84L);
            _entries.Insert(0, new Entry(id, DateTimeOffset.Now, engine, Ellipsis(source, 320),
                Ellipsis(translation, 320), targetLanguage, thumbnailName, imageName,
                source, translation, translatedName, coreSnapshot));
            CleanupCore(settings);
            SaveIndex();
        }
    }

    public IReadOnlyList<SessionHistoryItem> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Select(x => new SessionHistoryItem(x.Timestamp, x.Engine,
                x.SourceSummary, x.TranslationSummary, x.TargetLanguage,
                ReadOptional(x.ThumbnailFile) ?? ReadOptional(x.TranslatedImageFile) ?? ReadOptional(x.ImageFile) ?? [],
                ReadOptional(x.ImageFile) ?? [],
                x.SourceText ?? x.SourceSummary, x.TranslationText ?? x.TranslationSummary,
                ReadOptional(x.TranslatedImageFile), Path.Combine(_root, x.ImageFile),
                string.IsNullOrWhiteSpace(x.TranslatedImageFile) ? null : Path.Combine(_root, x.TranslatedImageFile),
                x.CoreSnapshot)).ToList();
        }
    }

    public long UsageBytes
    {
        get { lock (_gate) return _entries.Sum(SizeOf); }
    }

    public void ApplyCleanup(ApiSettings settings)
    {
        lock (_gate) { CleanupCore(settings); SaveIndex(); }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _entries) DeleteFiles(entry);
            _entries.Clear();
            SaveIndex();
        }
    }

    private void CleanupCore(ApiSettings settings)
    {
        if (settings.HistoryCleanupByAge)
        {
            var cutoff = DateTimeOffset.Now.AddDays(-settings.HistoryRetentionDays);
            RemoveWhere(x => x.Timestamp < cutoff);
        }
        if (settings.HistoryCleanupByCount)
            while (_entries.Count > settings.HistoryLimit) RemoveLast();
        if (settings.HistoryCleanupBySpace)
        {
            var budget = settings.HistoryMaximumMegabytes * 1024L * 1024L;
            while (_entries.Count > 0 && _entries.Sum(SizeOf) > budget) RemoveLast();
        }
    }

    private void RemoveBrokenEntries()
    {
        var changed = _entries.RemoveAll(x => !File.Exists(Path.Combine(_root, x.ImageFile)) ||
            !File.Exists(Path.Combine(_root, x.ThumbnailFile))) > 0;
        if (changed) SaveIndex();
    }

    private void RemoveWhere(Func<Entry, bool> predicate)
    {
        foreach (var entry in _entries.Where(predicate).ToList()) { DeleteFiles(entry); _entries.Remove(entry); }
    }

    private void RemoveLast()
    {
        var entry = _entries[^1]; DeleteFiles(entry); _entries.RemoveAt(_entries.Count - 1);
    }

    private void DeleteFiles(Entry entry)
    {
        TryDelete(Path.Combine(_root, entry.ImageFile));
        TryDelete(Path.Combine(_root, entry.ThumbnailFile));
        if (!string.IsNullOrWhiteSpace(entry.TranslatedImageFile)) TryDelete(Path.Combine(_root, entry.TranslatedImageFile));
    }

    private long SizeOf(Entry entry) => FileSize(Path.Combine(_root, entry.ImageFile)) +
        FileSize(Path.Combine(_root, entry.ThumbnailFile)) +
        (string.IsNullOrWhiteSpace(entry.TranslatedImageFile) ? 0 : FileSize(Path.Combine(_root, entry.TranslatedImageFile)));
    private byte[]? ReadOptional(string? file) => string.IsNullOrWhiteSpace(file) ? null :
        File.Exists(Path.Combine(_root, file)) ? File.ReadAllBytes(Path.Combine(_root, file)) : null;
    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }

    private List<Entry> LoadIndex()
    {
        try { return File.Exists(_indexPath) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_indexPath)) ?? [] : []; }
        catch (Exception ex) { AppLog.Write("history", "History index could not be read; starting with an empty index.", ex); return []; }
    }

    private void SaveIndex()
    {
        var temporary = _indexPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _indexPath, true);
    }

    private static void SaveJpeg(Bitmap bitmap, string path, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(path, codec, parameters);
    }

    private static Bitmap CreateThumbnail(Bitmap source, int maxWidth, int maxHeight)
    {
        var scale = Math.Min(1d, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        var bitmap = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }

    private static string Ellipsis(string value, int length) => value.Length <= length ? value : value[..length] + "…";
}
