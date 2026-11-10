using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenshotTranslationUiTester;

internal sealed class AiActivityFile
{
    public string Path { get; set; } = "";
    public string Action { get; set; } = "";
    public string Diff { get; set; } = "";
}
internal sealed class AiActivityPlanStep
{
    public string Text { get; set; } = "";
    public string Status { get; set; } = "pending";
}
internal sealed class AiExecutionActivity
{
    public string ItemId { get; set; } = "";
    public string Kind { get; set; } = "status";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "running";
    public string Text { get; set; } = "";
    public string Command { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string SubagentId { get; set; } = "";
    public string ParentItemId { get; set; } = "";
    public List<AiActivityFile> Files { get; set; } = [];
    public List<AiActivityPlanStep> Plan { get; set; } = [];
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Truncated { get; set; }
}
internal sealed class AiExecutionTurn
{
    public string Id { get; set; } = "";
    public string NativeTurnId { get; set; } = "";
    public string Backend { get; set; } = "";
    public string Status { get; set; } = "running";
    public int MessageStartIndex { get; set; }
    public int MessageEndIndex { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<AiExecutionActivity> Activities { get; set; } = [];
    public List<AiAttachmentRecord> Attachments { get; set; } = [];
    public bool Truncated { get; set; }
}
internal sealed class AiAttachmentRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "file";
    public string MimeType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public bool AsFileReference { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    // References remain portable when the application's data directory is copied.
    [JsonIgnore] public string StoredPath { get; internal set; } = "";
    internal AiAttachmentRecord Copy() => new() { Id = Id, Name = Name, Kind = Kind, MimeType = MimeType, Size = Size, Sha256 = Sha256,
        RelativePath = RelativePath, AsFileReference = AsFileReference, AddedAt = AddedAt, StoredPath = StoredPath };
}

// Activity has a separate bounded file: a verbose command must not make the
// conversation, credentials configuration, or accounting record unsaveable.
internal sealed class AiExecutionStore(string root)
{
    internal const int MaxTurns = 2000, MaxActivitiesPerTurn = 128, MaxActivityText = 12000;
    private const int MaxBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16 };
    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new AiToolsException("session_expired");
        return Path.Combine(root, id, "execution.json");
    }
    internal List<AiExecutionTurn> Load(string taskId) => Load(taskId, out _);
    internal List<AiExecutionTurn> Load(string taskId, out bool recovered)
    {
        recovered = false;
        string path = PathFor(taskId); AppDataPaths.RejectReparsePoints(path);
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > MaxBytes) throw new AiToolsException("storage_error");
        try
        {
            var turns = JsonSerializer.Deserialize<List<AiExecutionTurn>>(File.ReadAllText(path), Json) ?? [];
            if (turns.Count > MaxTurns || turns.Any(t => t is null || !Guid.TryParseExact(t.Id, "N", out _) ||
                t.Activities is null || t.Activities.Count > MaxActivitiesPerTurn || t.Attachments is null || t.Attachments.Count > AiAttachmentStore.MaxCount ||
                t.Attachments.Any(a => !AiAttachmentStore.Valid(a)) ||
                t.Activities.Any(a => a is null || a.Files is null || a.Plan is null || a.Files.Any(f => f is null) || a.Plan.Any(s => s is null)))) throw new AiToolsException("storage_error");
            foreach (var turn in turns)
            {
                Normalize(turn);
                if (turn.CompletedAt is null)
                {
                    recovered = true;
                    turn.Status = "interrupted"; turn.CompletedAt = DateTimeOffset.UtcNow;
                    foreach (var activity in turn.Activities.Where(x => x.Status is "running" or "pending")) activity.Status = "interrupted";
                }
            }
            return turns;
        }
        catch (JsonException) { throw new AiToolsException("storage_error"); }
    }
    internal void Save(string taskId, List<AiExecutionTurn> turns)
    {
        if (turns.Count > MaxTurns) throw new AiToolsException("context_limit");
        foreach (var turn in turns) Normalize(turn);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(turns, Json);
        // Preserve turn identity, timings and attachments while shrinking old detail.
        // Truncation is visible; never replace missing output with invented results.
        foreach (var turn in turns)
        {
            if (bytes.Length <= MaxBytes) break;
            if (turn.Activities.Count == 0) continue;
            turn.Truncated = true;
            foreach (var item in turn.Activities) { item.Text = ""; item.Command = Limit(item.Command, 1024); item.Files.Clear(); item.Plan.Clear(); item.Truncated = true; }
            if (turn.Activities.Count > 8) turn.Activities.RemoveRange(0, turn.Activities.Count - 8);
            bytes = JsonSerializer.SerializeToUtf8Bytes(turns, Json);
        }
        if (bytes.Length > MaxBytes) throw new AiToolsException("context_limit");
        string destination = PathFor(taskId); AppDataPaths.RejectReparsePoints(destination); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
        File.Move(temporary, destination, true);
    }
    internal static string Limit(string? value, int length) => value is null ? "" : value.Length <= length ? value : value[..length];
    internal static string ActivityText(string? value, int length)
    {
        if (value is null) return "";
        if (value.Length <= length) return value;
        int head = (length - 3) * 2 / 3, tail = length - head - 3;
        return value[..head] + "\n…\n" + value[^tail..];
    }
    private static void Normalize(AiExecutionTurn turn)
    {
        turn.NativeTurnId = Limit(turn.NativeTurnId, 256); turn.Backend = Limit(turn.Backend, 32); turn.Status = Limit(turn.Status, 32);
        if (turn.Activities.Count > MaxActivitiesPerTurn) { turn.Activities.RemoveRange(MaxActivitiesPerTurn, turn.Activities.Count - MaxActivitiesPerTurn); turn.Truncated = true; }
        foreach (var activity in turn.Activities)
        {
            activity.ItemId = Limit(activity.ItemId, 512); activity.Kind = Limit(activity.Kind, 32); activity.Status = Limit(activity.Status, 32);
            activity.Title = Limit(activity.Title, 256); activity.WorkingDirectory = Limit(activity.WorkingDirectory, 2048);
            activity.SubagentId = Limit(activity.SubagentId, 256); activity.ParentItemId = Limit(activity.ParentItemId, 512);
            activity.Truncated |= activity.Text?.Length > MaxActivityText || activity.Command?.Length > 8192;
            activity.Text = ActivityText(activity.Text, MaxActivityText); activity.Command = Limit(activity.Command, 8192);
            if (activity.Files.Count > 16) { activity.Files.RemoveRange(16, activity.Files.Count - 16); activity.Truncated = true; }
            foreach (var file in activity.Files) { file.Path = Limit(file.Path, 2048); file.Action = Limit(file.Action, 40); activity.Truncated |= file.Diff?.Length > 6000; file.Diff = Limit(file.Diff, 6000); }
            if (activity.Plan.Count > 48) { activity.Plan.RemoveRange(48, activity.Plan.Count - 48); activity.Truncated = true; }
            foreach (var step in activity.Plan) { step.Text = Limit(step.Text, 1024); step.Status = Limit(step.Status, 32); }
        }
    }
}

internal sealed class AiAttachmentStore(string root)
{
    internal const int MaxCount = 8;
    internal const long MaxFileBytes = 16 * 1024 * 1024, MaxTotalBytes = 48 * 1024 * 1024;
    internal static bool Valid(AiAttachmentRecord? item) => item is not null && Guid.TryParseExact(item.Id, "N", out _) &&
        AiAgentOptions.Clean(item.Name, 256) && item.Name.Length > 0 && item.Kind is "file" or "image" &&
        AiAgentOptions.Clean(item.MimeType, 80) && item.Size is >= 0 and <= MaxFileBytes &&
        item.Sha256 is { Length: 64 } && item.Sha256.All(char.IsAsciiHexDigit) && AiAgentOptions.Clean(item.RelativePath, 256);
    internal void Bind(string taskId, AiAttachmentRecord item)
    {
        if (!Valid(item)) throw new AiToolsException("attachment_invalid");
        string folder = Path.GetFullPath(Path.Combine(root, taskId, "attachments"));
        string path = Path.GetFullPath(Path.Combine(root, item.RelativePath));
        if (!Guid.TryParseExact(taskId, "N", out _) || Path.IsPathRooted(item.RelativePath) ||
            !string.Equals(Path.GetDirectoryName(path), folder, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            Path.GetFileNameWithoutExtension(path) != item.Id) throw new AiToolsException("attachment_invalid");
        AppDataPaths.RejectReparsePoints(path); item.StoredPath = path;
    }
    internal async Task<AiAttachmentRecord> ImportAsync(string taskId, string source, CancellationToken token)
    {
        if (!Guid.TryParseExact(taskId, "N", out _) || !Path.IsPathFullyQualified(source)) throw new AiToolsException("attachment_invalid");
        string name = Path.GetFileName(source); if (!AiAgentOptions.Clean(name, 256) || name.Length == 0) throw new AiToolsException("attachment_invalid");
        if (!File.Exists(source)) throw new AiToolsException("attachment_missing");
        AppDataPaths.RejectReparsePoints(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > MaxFileBytes) throw new AiToolsException("attachment_size");
        byte[] header = new byte[16]; int read = await input.ReadAsync(header, token); input.Position = 0;
        string extension = Path.GetExtension(name).ToLowerInvariant();
        string mime = ImageMime(header.AsSpan(0, read));
        bool imageExtension = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
        if (imageExtension && mime.Length == 0) throw new AiToolsException("attachment_invalid");
        bool image = mime.Length > 0;
        if (!image) mime = extension is ".txt" or ".md" or ".json" or ".ini" or ".cfg" or ".csv" or ".xml" or ".cs" or ".js" or ".ts" or ".py" or ".gd" or ".yml" or ".yaml" or ".log" ? "text/plain" : "application/octet-stream";
        if (mime == "text/plain")
        {
            try
            {
                using var reader = new StreamReader(input, new UTF8Encoding(false, true), false, 4096, true);
                char[] buffer = new char[4096]; int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
                    if (buffer.AsSpan(0, count).Contains('\0')) { mime = "application/octet-stream"; break; }
            }
            catch (DecoderFallbackException) { mime = "application/octet-stream"; }
            input.Position = 0;
        }
        if (image) extension = mime switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", _ => ".webp" };
        if (extension.Length > 16 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = ".bin";
        string id = Guid.NewGuid().ToString("N"), relative = Path.Combine(taskId, "attachments", id + extension), destination = Path.Combine(root, relative);
        AppDataPaths.RejectReparsePoints(destination); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            { await input.CopyToAsync(output, token); await output.FlushAsync(token); }
            using var copied = File.OpenRead(destination); string hash = Convert.ToHexString(await SHA256.HashDataAsync(copied, token));
            if (copied.Length != input.Length) throw new AiToolsException("attachment_changed");
            return new() { Id = id, Name = name, Kind = image ? "image" : "file", MimeType = mime, Size = copied.Length, Sha256 = hash, RelativePath = relative, StoredPath = destination };
        }
        catch { try { File.Delete(destination); } catch { } throw; }
    }
    internal async Task ValidateAsync(string taskId, AiAttachmentRecord item, CancellationToken token)
    {
        Bind(taskId, item);
        if (!File.Exists(item.StoredPath)) throw new AiToolsException("attachment_missing");
        await using var input = new FileStream(item.StoredPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length != item.Size || Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != item.Sha256) throw new AiToolsException("attachment_changed");
    }
    internal void RemoveOwned(string taskId, AiAttachmentRecord item)
    {
        Bind(taskId, item); // Only a generated file in this conversation's attachment folder.
        File.Delete(item.StoredPath);
    }
    private static string ImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return "";
    }
}
