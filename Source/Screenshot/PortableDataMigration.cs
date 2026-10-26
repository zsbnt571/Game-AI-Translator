using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

/// <summary>Copy a portable data collection into a new folder, then select it for the next launch.</summary>
internal static partial class PortableDataMigration
{
    internal sealed class MigrationException(string code, Exception? inner = null) : IOException(code, inner)
    {
        internal string Code { get; } = code;
    }

    internal sealed record MigrationProgress(int CopiedFiles, int TotalFiles, long CopiedBytes, long TotalBytes, string RelativePath);
    internal sealed record PendingMigration(string Id, string SourceRoot, string PreviousRoot, string DestinationRoot, DateTime CreatedUtc);
    internal sealed record FileSnapshot(string RelativePath, long Length, long LastWriteTicks);
    private sealed record Snapshot(FileSnapshot[] Files, string[] Directories);
    private sealed record Location(string Path, bool RequireExisting = true);
    private sealed record PendingRecord(int Version, PendingMigration Migration, string? PreviousDescriptor,
        string PreviousDescriptorHash, string SelectedDescriptorHash, string? ApplicationRoot = null);

    internal sealed class MigrationPlan
    {
        internal MigrationPlan(string id, string source, string current, string app, string destination,
            FileSnapshot[] files, string[] directories, string descriptorHash)
        {
            Id = id; SourceRoot = source; CurrentRoot = current; ApplicationRoot = app; DestinationRoot = destination;
            Files = files; Directories = directories; DescriptorHash = descriptorHash;
            TotalBytes = files.Aggregate(0L, (sum, file) => checked(sum + file.Length));
            Categories = files.GroupBy(file => Category(file.RelativePath))
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        }
        internal string Id { get; }
        internal string SourceRoot { get; }
        internal string CurrentRoot { get; }
        internal string ApplicationRoot { get; }
        internal string DestinationRoot { get; }
        internal int FileCount => Files.Length;
        internal long TotalBytes { get; }
        internal IReadOnlyDictionary<string, int> Categories { get; }
        internal FileSnapshot[] Files { get; }
        internal string[] Directories { get; }
        internal string DescriptorHash { get; }
    }

    private const string PendingName = "data-migration-pending.json";
    private const int MaxFiles = 250000;
    private const int BufferSize = 1024 * 1024;

    internal static MigrationPlan Inspect(string selection, string currentRoot, string applicationRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string app = ValidateRoot(applicationRoot), current = ValidateRoot(currentRoot);
        if (!Directory.Exists(current)) throw Error("CurrentRootUnavailable");
        using var operation = AcquireLocationLock(app);
        EnsureNoPendingLocationChange(app, current);
        string source = ResolveSource(selection);
        ValidateCredentials(source);
        PortableDataImportValidation.Validate(source);
        string id = Guid.NewGuid().ToString("N");
        string destination = Path.Combine(app, "data-imports", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + id, "data");
        ValidateSeparation(source, current, destination);
        var snapshot = Enumerate(source, cancellationToken);
        if (snapshot.Files.Length == 0) throw Error("UnrecognizedSource");
        var plan = new MigrationPlan(id, source, current, app, destination, snapshot.Files, snapshot.Directories,
            DescriptorHash(ReadDescriptor(app)));
        CheckAvailableSpace(plan);
        return plan;
    }

    internal static Task<PendingMigration> ImportAsync(MigrationPlan plan, IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(() => Import(plan, progress, cancellationToken), cancellationToken);

    internal static PendingMigration Import(MigrationPlan plan, IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = AcquireLocationLock(plan.ApplicationRoot);
        EnsureNoPendingLocationChange(plan.ApplicationRoot, plan.CurrentRoot);
        if (!Directory.Exists(plan.CurrentRoot)) throw Error("CurrentRootUnavailable");
        ValidateSeparation(plan.SourceRoot, plan.CurrentRoot, plan.DestinationRoot);
        string container = Path.GetDirectoryName(plan.DestinationRoot)!;
        string imports = Path.GetDirectoryName(container)!;
        string stage = container + ".partial";
        string stageData = Path.Combine(stage, "data");
        ValidateRoot(imports); ValidateRoot(stage);
        if (Directory.Exists(container) || File.Exists(container) || Directory.Exists(stage) || File.Exists(stage))
            throw Error("DestinationExists");
        byte[]? previous = ReadDescriptor(plan.ApplicationRoot);
        if (DescriptorHash(previous) != plan.DescriptorHash) throw Error("LocationChanged");
        CheckSnapshot(plan, cancellationToken);
        CheckAvailableSpace(plan);
        Directory.CreateDirectory(stageData);
        foreach (string directory in plan.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string targetDirectory = Contained(stageData, directory);
            ValidateRoot(targetDirectory);
            Directory.CreateDirectory(targetDirectory);
        }
        var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int copiedFiles = 0; long copiedBytes = 0;
        byte[] buffer = new byte[BufferSize];
        long lastProgress = 0;
        void Report(string relative, bool force = false)
        {
            long now = Stopwatch.GetTimestamp();
            if (progress is null || (!force && Stopwatch.GetElapsedTime(lastProgress, now).TotalMilliseconds < 100)) return;
            lastProgress = now;
            progress.Report(new(copiedFiles, plan.FileCount, copiedBytes, plan.TotalBytes, relative));
        }
        Report("", true);
        // A failed or cancelled copy stays in its unique .partial folder. Neither the old
        // source nor the current live collection is edited, merged, renamed or deleted.
        foreach (var file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = Contained(plan.SourceRoot, file.RelativePath);
            string destination = Contained(stageData, file.RelativePath);
            RejectLinks(source); RejectLinks(destination);
            CheckFile(source, file);
            string hash;
            {
                using var input = OpenSource(source);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count); digest.AppendData(buffer, 0, count);
                    copiedBytes += count;
                    Report(file.RelativePath);
                }
                output.Flush(true);
                hash = Convert.ToHexString(digest.GetHashAndReset());
            }
            CheckFile(source, file);
            if (new FileInfo(destination).Length != file.Length || HashFile(destination, cancellationToken) != hash)
                throw Error("VerificationFailed");
            File.SetLastWriteTimeUtc(destination, new DateTime(file.LastWriteTicks, DateTimeKind.Utc));
            sourceHashes.Add(file.RelativePath, hash);
            copiedFiles++;
            Report(file.RelativePath);
        }
        Report("", true);
        cancellationToken.ThrowIfCancellationRequested();
        // Recheck all files, including ones copied early, so a running old instance
        // cannot silently produce a collection containing different moments in time.
        CheckSnapshot(plan, cancellationToken);
        foreach (var file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HashFile(Contained(plan.SourceRoot, file.RelativePath), cancellationToken) != sourceHashes[file.RelativePath])
                throw Error("SourceChanged");
        }
        CheckSnapshot(plan, cancellationToken);
        ValidateCredentials(stageData);
        PortableDataImportValidation.Validate(stageData);
        CheckSnapshot(plan, cancellationToken);
        if (DescriptorHash(ReadDescriptor(plan.ApplicationRoot)) != plan.DescriptorHash) throw Error("LocationChanged");
        var pending = new PendingMigration(plan.Id, plan.SourceRoot, plan.CurrentRoot, plan.DestinationRoot, DateTime.UtcNow);
        byte[] selected = JsonSerializer.SerializeToUtf8Bytes(new Location(PortablePath(plan.DestinationRoot, plan.ApplicationRoot)), JsonOptions);
        var receipt = new PendingRecord(2, pending, previous is null ? null : Convert.ToBase64String(previous),
            plan.DescriptorHash, DescriptorHash(selected), plan.ApplicationRoot);
        WriteBytesAtomic(Path.Combine(stage, "migration-receipt.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions));
        cancellationToken.ThrowIfCancellationRequested();
        RejectLinks(container); RejectLinks(stage);
        if (Directory.Exists(container) || File.Exists(container)) throw Error("DestinationExists");
        Directory.Move(stage, container);
        // The receipt is durable before the atomic pointer replacement. A crash before
        // the replacement leaves the current data selected and can be cancelled safely.
        WriteBytesAtomic(Path.Combine(plan.ApplicationRoot, PendingName), JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions));
        if (DescriptorHash(ReadDescriptor(plan.ApplicationRoot)) != plan.DescriptorHash) throw Error("LocationChanged");
        WriteBytesAtomic(Path.Combine(plan.ApplicationRoot, "data-location.json"), selected);
        return pending;
    }

    internal static bool HasPending(string applicationRoot, string currentRoot) => GetPending(applicationRoot, currentRoot) is not null;

    internal static PendingMigration? GetPending(string applicationRoot, string currentRoot)
    {
        string app = ValidateRoot(applicationRoot), current = ValidateRoot(currentRoot);
        using var operation = AcquireLocationLock(app);
        var record = ReadPending(app);
        if (record is null) return null;
        string hash = DescriptorHash(ReadDescriptor(app));
        if (Same(current, record.Migration.DestinationRoot) && hash == record.SelectedDescriptorHash) return null;
        if (!Same(current, record.Migration.PreviousRoot) ||
            (hash != record.PreviousDescriptorHash && hash != record.SelectedDescriptorHash)) throw Error("LocationChanged");
        return record.Migration;
    }

    internal static void CancelPending(string applicationRoot, string currentRoot)
    {
        string app = ValidateRoot(applicationRoot), current = ValidateRoot(currentRoot);
        using var operation = AcquireLocationLock(app);
        var record = ReadPending(app) ?? throw Error("NoPending");
        if (Same(current, record.Migration.DestinationRoot)) throw Error("AlreadyApplied");
        if (!Same(current, record.Migration.PreviousRoot)) throw Error("LocationChanged");
        string hash = DescriptorHash(ReadDescriptor(app));
        if (hash != record.PreviousDescriptorHash && hash != record.SelectedDescriptorHash) throw Error("LocationChanged");
        if (hash == record.SelectedDescriptorHash || (record.PreviousDescriptor is not null && !Same(record.ApplicationRoot!, app)))
        {
            string path = Path.Combine(app, "data-location.json");
            if (record.PreviousDescriptor is null)
            {
                RejectLinks(path);
                File.Delete(path);
            }
            else WriteBytesAtomic(path, PreviousDescriptorForRestore(record, app));
        }
        // The copied data and its receipt remain available. Only the pending selection
        // is cancelled; the still-running process has always used PreviousRoot.
        File.Delete(Path.Combine(app, PendingName));
    }

    internal static void EnsureNoPendingLocationChange(string applicationRoot, string currentRoot)
    {
        var pending = ReadPending(applicationRoot);
        byte[]? bytes = ReadDescriptor(applicationRoot);
        if (pending is not null)
        {
            if (!Same(currentRoot, pending.Migration.DestinationRoot)) throw Error("PendingExists");
            if (DescriptorHash(bytes) != pending.SelectedDescriptorHash) throw Error("LocationChanged");
            // This process started with the imported collection. Its durable receipt
            // remains beside the copy; the pending marker must not block a later move.
            File.Delete(Path.Combine(applicationRoot, PendingName));
        }
        if (bytes is null) return;
        using var document = Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Error("InvalidConfiguration");
        if (document.RootElement.TryGetProperty("CopyFrom", out var copy) && copy.ValueKind != JsonValueKind.Null)
            throw Error("PendingExists");
        string target = DescriptorTarget(document.RootElement, applicationRoot);
        if (!Same(currentRoot, target)) throw Error("LocationChanged");
    }

    internal static IDisposable AcquireLocationLock(string applicationRoot)
    {
        string app = ValidateRoot(applicationRoot);
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(app.ToUpperInvariant())));
        var mutex = new Mutex(false, "Local\\FusionR1.PortableMigration." + id);
        bool entered;
        try { entered = mutex.WaitOne(0); } catch (AbandonedMutexException) { entered = true; }
        if (!entered) { mutex.Dispose(); throw Error("Busy"); }
        return new MutexLease(mutex);
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    private static string ResolveSource(string selection)
    {
        string path = ValidateRoot(selection);
        if (!Directory.Exists(path)) throw Error("SourceMissing");
        string descriptor = Path.Combine(path, "data-location.json");
        if (File.Exists(descriptor))
        {
            using var document = Parse(ReadBounded(descriptor, 16384));
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Error("InvalidConfiguration");
            if (document.RootElement.TryGetProperty("CopyFrom", out var copy) && copy.ValueKind != JsonValueKind.Null)
                throw Error("SourcePending");
            path = DescriptorTarget(document.RootElement, path);
        }
        else if (!Recognized(path) && Directory.Exists(Path.Combine(path, "data"))) path = ValidateRoot(Path.Combine(path, "data"));
        if (!Directory.Exists(path)) throw Error("SourceMissing");
        if (!Recognized(path)) throw Error("UnrecognizedSource");
        return path;
    }

    private static bool Recognized(string root)
    {
        foreach (string marker in new[] { "settings.json", "fusion-settings.json", "library-preferences.json", "recent-games-v1\\games.json" })
        {
            string path = Path.Combine(root, marker);
            if (!File.Exists(path)) continue;
            using var document = Parse(ReadBounded(path, 16 * 1024 * 1024));
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Error("InvalidConfiguration");
            if (marker != "settings.json") return true;
            // A generic settings.json, especially {}, is not evidence of Fusion data.
            // Accept older Fusion settings without relying on a single version field.
            string[] distinctive = ["OcrEngine", "OcrLanguage", "TranslationProviderKind", "TranslationPromptProfile", "PreviewChromeMode", "CaptureToolbarPlacement"];
            string[] companions = ["SettingsSchemaVersion", "ApiUrl", "TargetLanguage", "TranslationProvider", "TranslationMode", "PreviewWindowWidth", "FirstByteTimeoutSeconds"];
            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (distinctive.Any(names.Contains) && companions.Any(names.Contains)) return true;
        }
        return false;
    }

    private static void ValidateCredentials(string root)
    {
        foreach (string name in new[] { "settings.json", "fusion-settings.json" })
        {
            string path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            using var document = Parse(ReadBounded(path, 16 * 1024 * 1024));
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Error("InvalidConfiguration");
            Visit(document.RootElement);
        }
        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind != JsonValueKind.String) throw Error("InvalidConfiguration");
                        try { _ = FusionSecrets.Unprotect(property.Value.GetString()!); }
                        catch (Exception ex) when (ex is CryptographicException or FormatException)
                        { throw Error("CredentialsUnavailable"); }
                    }
                    else Visit(property.Value);
                }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Visit(child);
        }
    }

    private static Snapshot Enumerate(string root, CancellationToken cancellationToken)
    {
        var files = new List<FileSnapshot>(); var folders = new List<string>(); var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folder = pending.Pop(); RejectLinks(folder);
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
            {
                cancellationToken.ThrowIfCancellationRequested(); RejectLinks(entry);
                string relative = Path.GetRelativePath(root, entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                { folders.Add(relative); pending.Push(entry); }
                else
                {
                    var info = new FileInfo(entry);
                    files.Add(new(relative, info.Length, info.LastWriteTimeUtc.Ticks));
                    if (files.Count > MaxFiles) throw Error("TooManyFiles");
                }
                if (files.Count + folders.Count > MaxFiles) throw Error("TooManyFiles");
            }
        }
        return new(files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            folders.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void CheckSnapshot(MigrationPlan plan, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(plan.SourceRoot)) throw Error("SourceChanged");
        var snapshot = Enumerate(plan.SourceRoot, cancellationToken);
        if (!plan.Files.SequenceEqual(snapshot.Files) || !plan.Directories.SequenceEqual(snapshot.Directories, StringComparer.OrdinalIgnoreCase))
            throw Error("SourceChanged");
    }

    private static void CheckFile(string path, FileSnapshot snapshot)
    {
        RejectLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != snapshot.Length || info.LastWriteTimeUtc.Ticks != snapshot.LastWriteTicks) throw Error("SourceChanged");
    }

    private static FileStream OpenSource(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan); }
        catch (IOException ex) { throw Error("SourceBusy", ex); }
    }

    private static void CheckAvailableSpace(MigrationPlan plan)
    {
        var drive = new DriveInfo(Path.GetPathRoot(plan.DestinationRoot)!);
        if (drive.AvailableFreeSpace < plan.TotalBytes + Math.Min(plan.TotalBytes / 100, 64L * 1024 * 1024))
            throw Error("NotEnoughSpace");
    }

    private static string HashFile(string path, CancellationToken cancellationToken)
    {
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize]; int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static PendingRecord? ReadPending(string app)
    {
        string path = Path.Combine(app, PendingName);
        if (!File.Exists(path)) return null;
        var record = ReadMigrationRecord(path);
        string origin = RecordOrigin(record);
        string destination = RebaseInternal(record.Migration.DestinationRoot, origin, app);
        // Both receipts were committed before selecting the imported collection. Do
        // not trust a stale or mismatched pending marker after moving the package.
        var copyReceipt = ReadMigrationRecord(Path.Combine(Path.GetDirectoryName(destination)!, "migration-receipt.json"));
        if (copyReceipt != record) throw Error("InvalidConfiguration");
        return record with { ApplicationRoot = origin, Migration = record.Migration with
        {
            SourceRoot = RebaseInternal(record.Migration.SourceRoot, origin, app),
            PreviousRoot = RebaseInternal(record.Migration.PreviousRoot, origin, app),
            DestinationRoot = destination
        } };
    }

    private static byte[]? ReadDescriptor(string app)
    {
        string path = Path.Combine(app, "data-location.json");
        return File.Exists(path) ? ReadBounded(path, 16384) : null;
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw Error("InvalidConfiguration");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        try
        {
            // Existing portable JSON may have a UTF-8 BOM.
            ReadOnlyMemory<byte> json = bytes;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) json = json[3..];
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex) { throw Error("InvalidConfiguration", ex); }
    }

    private static string DescriptorTarget(JsonElement document, string app, byte[]? descriptorBytes = null)
    {
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("Path", out var property) ||
            property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString())) throw Error("InvalidConfiguration");
        try
        {
            string target = ValidateRoot(Path.GetFullPath(property.GetString()!, app));
            if (document.TryGetProperty("CopyFrom", out var copy) && copy.ValueKind != JsonValueKind.Null) return target;
            return ResolveRecordedLocationTarget(target, app, descriptorBytes ?? ReadDescriptor(app));
        }
        catch (ArgumentException ex) { throw Error("InvalidConfiguration", ex); }
    }

    private static void ValidateSeparation(string source, string current, string destination)
    {
        source = ValidateRoot(source); current = ValidateRoot(current); destination = ValidateRoot(destination);
        if (Overlaps(source, current) || Overlaps(source, destination) || Overlaps(current, destination)) throw Error("Overlap");
    }
    private static bool Overlaps(string a, string b) => Same(a, b) || Within(a, b) || Within(b, a);
    private static bool Within(string path, string root) => path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static string Contained(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(path, root)) throw Error("InvalidPath");
        return path;
    }
    private static string ValidateRoot(string path)
    {
        RejectLinks(path);
        try { return AppDataPaths.ValidateExplicitRoot(path); }
        catch (ArgumentException ex) { throw Error("InvalidPath", ex); }
    }
    private static void RejectLinks(string path)
    {
        try { AppDataPaths.RejectReparsePoints(path); }
        catch (ArgumentException ex) { throw Error("ReparsePoint", ex); }
    }
    private static string DescriptorHash(byte[]? bytes) => bytes is null ? "absent" : Convert.ToHexString(SHA256.HashData(bytes));
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static void WriteBytesAtomic(string path, byte[] bytes)
    {
        RejectLinks(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        RejectLinks(path);
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
    }
    private static MigrationException Error(string code, Exception? inner = null) => new(code, inner);
    private static string Category(string relative)
    {
        string normalized = relative.Replace('\\', '/').ToLowerInvariant();
        if (normalized is "settings.json" or "fusion-settings.json") return "Settings";
        if (normalized.StartsWith("recent-games-v1/") || normalized is "library-preferences.json") return "Library";
        if (normalized.Contains("translation") || normalized.Contains("glossary") || normalized.Contains("terminology")) return "Translations";
        if (normalized.Contains("cover") || normalized.Contains("image")) return "Images";
        if (normalized.StartsWith("history/") || normalized.StartsWith("output/")) return "History";
        if (normalized.StartsWith("cache/")) return "Cache";
        if (normalized.StartsWith("logs/")) return "Logs";
        return "Other";
    }
}
