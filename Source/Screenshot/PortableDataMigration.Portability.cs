using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal static partial class PortableDataMigration
{
    // Only paths inside this portable application follow the application. External
    // user-selected locations retain their identity and never get guessed anew.
    internal static string PortablePath(string path, string applicationRoot)
    {
        string root = ValidateRoot(applicationRoot), full = ValidateRoot(path);
        return Within(full, root) ? Path.GetRelativePath(root, full) : full;
    }

    internal static string ResolveLocationTarget(string applicationRoot, byte[] descriptor)
    {
        using var document = Parse(descriptor);
        return DescriptorTarget(document.RootElement, ValidateRoot(applicationRoot), descriptor);
    }

    private static PendingRecord ReadMigrationRecord(string path)
    {
        try
        {
            var record = JsonSerializer.Deserialize<PendingRecord>(ReadBounded(path, 65536)) ?? throw Error("InvalidConfiguration");
            if (record.Version is not (1 or 2) || !Guid.TryParseExact(record.Migration.Id, "N", out _) ||
                !Regex.IsMatch(record.SelectedDescriptorHash, "\\A[0-9A-F]{64}\\z")) throw Error("InvalidConfiguration");
            ValidateRoot(record.Migration.SourceRoot); ValidateRoot(record.Migration.PreviousRoot);
            _ = RecordOrigin(record);
            byte[]? previous = record.PreviousDescriptor is null ? null : Convert.FromBase64String(record.PreviousDescriptor);
            if (DescriptorHash(previous) != record.PreviousDescriptorHash || (previous?.Length ?? 0) > 16384) throw Error("InvalidConfiguration");
            return record;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or NullReferenceException or ArgumentException)
        { throw Error("InvalidConfiguration", ex); }
    }

    private static string RecordOrigin(PendingRecord record)
    {
        var (origin, id) = ImportedLocation(record.Migration.DestinationRoot) ?? throw Error("InvalidConfiguration");
        if (!string.Equals(id, record.Migration.Id, StringComparison.Ordinal)) throw Error("InvalidConfiguration");
        if (record.Version == 2 && (record.ApplicationRoot is null || !Same(ValidateRoot(record.ApplicationRoot), origin)))
            throw Error("InvalidConfiguration");
        return origin;
    }

    private static (string Origin, string Id)? ImportedLocation(string target)
    {
        target = ValidateRoot(target);
        if (!string.Equals(Path.GetFileName(target), "data", StringComparison.OrdinalIgnoreCase)) return null;
        string container = Path.GetDirectoryName(target)!;
        string imports = Path.GetDirectoryName(container)!;
        if (!string.Equals(Path.GetFileName(imports), "data-imports", StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(Path.GetFileName(container), "\\A[0-9]{8}-[0-9]{6}-(?<id>[0-9a-f]{32})\\z");
        if (!match.Success) return null;
        return (ValidateRoot(Path.GetDirectoryName(imports)!), match.Groups["id"].Value);
    }

    private static string RebaseInternal(string path, string origin, string app)
    {
        path = ValidateRoot(path); origin = ValidateRoot(origin); app = ValidateRoot(app);
        if (Same(path, origin)) return app;
        return Within(path, origin) ? ValidateRoot(Path.Combine(app, Path.GetRelativePath(origin, path))) : path;
    }

    private static string ResolveRecordedLocationTarget(string target, string app, byte[]? descriptor)
    {
        string pendingPath = Path.Combine(app, PendingName);
        if (descriptor is not null && File.Exists(pendingPath))
        {
            var original = ReadMigrationRecord(pendingPath);
            var mapped = ReadPending(app) ?? throw Error("InvalidConfiguration");
            string hash = DescriptorHash(descriptor);
            string expected, result;
            if (hash == original.SelectedDescriptorHash)
            { expected = original.Migration.DestinationRoot; result = mapped.Migration.DestinationRoot; }
            else if (hash == original.PreviousDescriptorHash)
            { expected = original.Migration.PreviousRoot; result = mapped.Migration.PreviousRoot; }
            else throw Error("LocationChanged");
            if (!Same(target, expected) && !Same(target, result)) throw Error("InvalidConfiguration");
            if (!Directory.Exists(result)) throw Error("SourceMissing");
            return result;
        }
        return ResolveLegacyImportedTarget(target, app, descriptor);
    }

    private static string ResolveLegacyImportedTarget(string target, string app, byte[]? descriptor)
    {
        var location = ImportedLocation(target);
        if (descriptor is null || location is null || Same(location.Value.Origin, app)) return target;
        string candidate = RebaseInternal(target, location.Value.Origin, app);
        string receiptPath = Path.Combine(Path.GetDirectoryName(candidate)!, "migration-receipt.json");
        if (!File.Exists(receiptPath)) return target;
        var record = ReadMigrationRecord(receiptPath);
        if (!Same(record.Migration.DestinationRoot, target) || record.Migration.Id != location.Value.Id ||
            DescriptorHash(descriptor) != record.SelectedDescriptorHash) throw Error("InvalidConfiguration");
        string pendingPath = Path.Combine(app, PendingName);
        if (File.Exists(pendingPath) && ReadMigrationRecord(pendingPath) != record) throw Error("InvalidConfiguration");
        // Recovery requires the exact receipt-bound internal copy. Missing or invalid
        // copies fail; no empty directory or unrelated library is substituted.
        if (!Directory.Exists(candidate)) throw Error("SourceMissing");
        if (!Recognized(candidate)) throw Error("UnrecognizedSource");
        PortableDataImportValidation.Validate(candidate);
        return candidate;
    }

    private static byte[] PreviousDescriptorForRestore(PendingRecord record, string app)
    {
        byte[] bytes = Convert.FromBase64String(record.PreviousDescriptor!);
        string origin = record.ApplicationRoot ?? throw Error("InvalidConfiguration");
        if (Same(origin, app)) return bytes; // Preserve exact bytes when no move occurred.
        using var document = Parse(bytes);
        var node = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject ?? throw Error("InvalidConfiguration");
        bool changed = false;
        foreach (string name in new[] { "Path", "CopyFrom" })
        {
            if (node[name] is not JsonValue value || !value.TryGetValue<string>(out var path) || string.IsNullOrWhiteSpace(path)) continue;
            if (!Path.IsPathFullyQualified(path)) continue;
            string rebased = RebaseInternal(path, origin, app);
            if (Same(rebased, path)) continue;
            node[name] = PortablePath(rebased, app); changed = true;
        }
        return changed ? JsonSerializer.SerializeToUtf8Bytes(node, JsonOptions) : bytes;
    }
}
