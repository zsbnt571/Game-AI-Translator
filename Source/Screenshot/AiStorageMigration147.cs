using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Copy into a private sibling stage, verify every source byte, then publish the
// directory. Originals, native databases and historical activity paths stay intact.
internal static class AiStorageMigration147
{
    internal const string LeaseName = ".fusion-ai-use.lock";
    private const string MarkerName = ".fusion-ai-storage.json";
    private const string ReceiptDirectory = "migration-receipts";
    internal sealed record FileStamp(string Path, long Size, string Sha256);
    internal sealed record WorkspaceChange(string TaskId, string From, string To);
    internal sealed record Receipt(int Schema, string MigrationId, string Source, string Target, DateTimeOffset CompletedAt,
        IReadOnlyList<FileStamp> SourceFiles, IReadOnlyList<FileStamp> TargetFiles, IReadOnlyList<WorkspaceChange> WorkspaceChanges);
    private sealed record Marker(int Schema, string RootId, IReadOnlyList<string>? PreviousRoots = null);
    internal static IReadOnlyList<string> PreviousRoots(string root)
    {
        string path = Path.Combine(root, MarkerName); AppDataPaths.RejectReparsePoints(path);
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 16384) throw new AiToolsException("ai_storage_invalid");
        try
        {
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path));
            if (marker?.Schema != 1 || marker.PreviousRoots?.Count > 32) throw new AiToolsException("ai_storage_invalid");
            return marker.PreviousRoots?.Select(AiStoragePaths147.ValidateRoot).ToArray() ?? [];
        }
        catch (JsonException) { throw new AiToolsException("ai_storage_invalid"); }
    }
    internal static bool IsEstablished(string root)
    {
        AiStoragePaths147.ValidateRoot(root);
        if (!Directory.Exists(root)) return false;
        foreach (string path in new[] { MarkerName, "connections.json", "tasks", "conversations", "harness", "mods", "value-changes" })
        {
            string full = Path.Combine(root, path); AppDataPaths.RejectReparsePoints(full);
            if (File.Exists(full) || Directory.Exists(full)) return true;
        }
        return false;
    }
    internal static void InitializeEmpty(string target)
    {
        target = AiStoragePaths147.ValidateRoot(target);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new AiToolsException("ai_storage_conflict");
        Directory.CreateDirectory(target);
        PortableDataStorage.WriteJson(Path.Combine(target, MarkerName), new Marker(1, Guid.NewGuid().ToString("N")));
    }
    internal static void ValidateDestination(string source, string target)
    {
        source = AiStoragePaths147.ValidateRoot(source); target = AiStoragePaths147.ValidateRoot(target);
        if (AiStoragePaths147.Same(source, target) || Within(source, target) || Within(target, source)) throw new AiToolsException("ai_storage_invalid");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new AiToolsException("ai_storage_conflict");
    }
    internal static Receipt Copy(string source, string target, string migrationId)
    {
        source = AiStoragePaths147.ValidateRoot(source); target = AiStoragePaths147.ValidateRoot(target);
        if (!Guid.TryParseExact(migrationId, "N", out _) || AiStoragePaths147.Same(source, target) || Within(source, target) || Within(target, source))
            throw new AiToolsException("ai_storage_invalid");
        string receiptPath = Path.Combine(target, ReceiptDirectory, migrationId + ".json");
        AppDataPaths.RejectReparsePoints(receiptPath);
        if (File.Exists(receiptPath))
        {
            if (new FileInfo(receiptPath).Length > 64L * 1024 * 1024) throw new AiToolsException("ai_storage_invalid");
            var completed = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receiptPath));
            if (completed?.Schema == 1 && completed.MigrationId == migrationId && AiStoragePaths147.Same(completed.Source, source) && AiStoragePaths147.Same(completed.Target, target))
            {
                string ownReceipt = Path.GetRelativePath(target, receiptPath);
                if (completed.TargetFiles is null || !completed.TargetFiles.SequenceEqual(Inventory(target).Where(x => x.Path != ownReceipt)))
                    throw new AiToolsException("ai_storage_changed");
                return completed;
            }
            throw new AiToolsException("ai_storage_conflict");
        }
        if (!Directory.Exists(source)) throw new AiToolsException("ai_storage_unavailable");
        ValidateDestination(source, target);
        using var sourceLease = CheckSourceClosed(source);
        var before = Inventory(source);
        string stage = target + ".fusion-ai-copy-" + migrationId + "-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        // A failed stage is retained for inspection, never merged or mistaken for
        // a committed target on a subsequent startup.
        CopyDirectories(source, stage);
        foreach (var item in before)
        {
            string from = Path.Combine(source, item.Path), to = Path.Combine(stage, item.Path);
            AppDataPaths.RejectReparsePoints(from); AppDataPaths.RejectReparsePoints(to);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            if (new FileInfo(to).Length != item.Size || Hash(to) != item.Sha256) throw new AiToolsException("ai_storage_changed");
        }
        if (!before.SequenceEqual(Inventory(source))) throw new AiToolsException("ai_storage_changed");
        var changes = RebindManagedWorkspaces(stage, source, target);
        var copied = Inventory(stage);
        var receipt = new Receipt(1, migrationId, source, target, DateTimeOffset.UtcNow, before, copied, changes);
        PortableDataStorage.WriteJson(Path.Combine(stage, MarkerName), new Marker(1, migrationId,
            new[] { source }.Concat(PreviousRoots(source)).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray()));
        PortableDataStorage.WriteJson(Path.Combine(stage, ReceiptDirectory, migrationId + ".json"), receipt);
        // Recheck immediately before publication. This is a consistency check,
        // not a claim that a legacy process without a lease can never write later.
        if (!before.SequenceEqual(Inventory(source))) throw new AiToolsException("ai_storage_changed");
        ValidateDestination(source, target);
        if (Directory.Exists(target)) Directory.Delete(target, false); // Verified empty target only.
        Directory.Move(stage, target);
        return receipt;
    }
    internal static IReadOnlyList<FileStamp> Inventory(string root)
    {
        var files = new List<FileStamp>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            string folder = pending.Pop(); AppDataPaths.RejectReparsePoints(folder);
            foreach (string directory in Directory.EnumerateDirectories(folder)) { AppDataPaths.RejectReparsePoints(directory); pending.Push(directory); }
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                AppDataPaths.RejectReparsePoints(file); string relative = Path.GetRelativePath(root, file);
                if (relative is LeaseName or MarkerName) continue;
                if (files.Count >= 200000 || relative.Length > 4096) throw new AiToolsException("ai_storage_invalid");
                files.Add(new(relative, new FileInfo(file).Length, Hash(file)));
            }
        }
        return files.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
    }
    private static void CopyDirectories(string source, string stage)
    {
        var pending = new Stack<string>(); pending.Push(source);
        while (pending.Count > 0)
        {
            string directory = pending.Pop(); AppDataPaths.RejectReparsePoints(directory);
            Directory.CreateDirectory(Path.Combine(stage, Path.GetRelativePath(source, directory)));
            foreach (string child in Directory.EnumerateDirectories(directory)) { AppDataPaths.RejectReparsePoints(child); pending.Push(child); }
        }
    }
    // Only the explicit default workspace for this exact task may be rebound.
    // Empty Workspace already derives from the new root. Attachments persist
    // RelativePath and are rebound by AiAttachmentStore; no JSON rewrite needed.
    private static IReadOnlyList<WorkspaceChange> RebindManagedWorkspaces(string stage, string source, string target)
    {
        var changes = new List<WorkspaceChange>(); string tasks = Path.Combine(stage, "tasks");
        if (!Directory.Exists(tasks)) return changes;
        foreach (string path in Directory.EnumerateFiles(tasks, "*.json"))
        {
            if (new FileInfo(path).Length > 8L * 1024 * 1024) throw new AiToolsException("ai_storage_invalid");
            JsonObject task;
            try { task = JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { MaxDepth = 20 })!.AsObject(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException) { throw new AiToolsException("ai_storage_invalid"); }
            string id = task["Id"]?.GetValue<string>() ?? "";
            if (!Guid.TryParseExact(id, "N", out _) || Path.GetFileNameWithoutExtension(path) != id) continue;
            string workspace = task["Workspace"]?.GetValue<string>() ?? "";
            string previous = Path.Combine(source, "harness", "projects", id);
            if (workspace.Length == 0 || !Path.IsPathFullyQualified(workspace) || !AiStoragePaths147.Same(workspace, previous)) continue;
            string next = Path.Combine(target, "harness", "projects", id);
            task["Workspace"] = next;
            // Atomic replacement inside our private stage only; original metadata remains intact.
            string temporary = path + ".rebound"; File.WriteAllText(temporary, task.ToJsonString(new() { WriteIndented = true })); File.Move(temporary, path, true);
            changes.Add(new(id, workspace, next));
        }
        return changes;
    }
    private static bool Within(string path, string parent) => path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Hash(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static FileStream? CheckSourceClosed(string source)
    {
        string path = Path.Combine(source, LeaseName); AppDataPaths.RejectReparsePoints(path);
        if (!File.Exists(path)) return null;
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException) { throw new AiToolsException("ai_storage_busy"); }
    }
}
