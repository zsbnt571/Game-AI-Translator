using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace ScreenshotTranslationUiTester;

// All paths and original content stay local. Only a deliberately selected excerpt
// may be put in a model request. None of these mutation methods is an AI tool.
internal sealed class AiModException : IOException
{
    internal string Code { get; }
    internal AiModException(string code, Exception? inner = null) : base(code, inner) => Code = code;
}

internal sealed record AiModSource(string Token, string RelativePath, string Sha256, string Text, long Bytes);
internal sealed record AiModEntry(string Id, string Name, string GameId, string Status, long Bytes, DateTimeOffset CreatedUtc);
internal sealed record AiModPreview(string Id, string Name, string GameId, string RelativePath, string Before, string After, string Status);

internal sealed class AiModLibrary
{
    internal const int MaximumFileBytes = 256 * 1024;
    internal const int MaximumArchiveBytes = MaximumFileBytes + 32 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = false };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".json", ".ini", ".cfg", ".csv", ".xml" };
    private static readonly HashSet<string> ForbiddenParts = new(StringComparer.OrdinalIgnoreCase)
    { "save", "saves", "savegames", "saved", "persistent", "scripts", "script", "autorun", "appdata", ".git", ".fusion", "logs", "backups" };
    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase)
    { "desktop.ini", "autorun.inf", "settings.json", "credentials.json", "secrets.json", "data-location.json", "manifest.json", "package.json", "project.godot", "engine.cfg" };
    private readonly string root;
    private readonly object gate = new();
    private readonly Dictionary<string, SourceState> sources = new(StringComparer.Ordinal);
    private readonly Action<string>? fault;

    private sealed record SourceState(string GameId, string GameRoot, string RelativePath, byte[] Bytes, string Sha256, DateTimeOffset Expires);
    private sealed class Manifest
    {
        public int Format { get; set; } = 1;
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string GameId { get; set; } = "";
        public string GameRoot { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string OriginalSha256 { get; set; } = "";
        public string ReplacementSha256 { get; set; } = "";
        public long Bytes { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public string Status { get; set; } = "draft";
    }
    private sealed class Journal
    {
        public string Operation { get; set; } = "";
        public string State { get; set; } = "prepared";
        public string Error { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    // The fault hook is only used by isolated transaction tests.
    internal AiModLibrary(string libraryRoot, Action<string>? fault = null)
    {
        root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length < 4 || Path.GetPathRoot(root)?.TrimEnd('\\', '/') == root) throw Error("mod_invalid_root");
        CheckLinks(root);
        this.fault = fault;
        // Opening the page is read-only. The library is created only by a user action.
    }

    internal AiModSource ReadSource(string gameId, string gameRoot, string relativePath)
    {
        lock (gate)
        {
            ValidateGameId(gameId);
            string game = GameRoot(gameRoot), rel = Relative(relativePath), target = Target(game, rel);
            byte[] bytes = ReadBounded(target, MaximumFileBytes);
            string content = Decode(bytes);
            ValidateDocument(rel, content);
            foreach (var old in sources.Where(p => p.Value.Expires < DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) sources.Remove(old);
            if (sources.Count >= 16) sources.Remove(sources.Keys.First());
            string token = Guid.NewGuid().ToString("N"), sha = Hash(bytes);
            sources[token] = new(gameId, game, rel, bytes, sha, DateTimeOffset.UtcNow.AddMinutes(15));
            return new(token, rel, sha, content, bytes.Length);
        }
    }

    internal void ForgetSources() { lock (gate) sources.Clear(); }

    // The UI checks the current library entry immediately before confirming an
    // operation. A moved/replaced game must not silently reuse an old binding.
    internal void ValidateGameBinding(string id, string gameId, string gameRoot)
    {
        lock (gate)
        {
            Manifest m = Load(id);
            if (!string.Equals(m.GameId, gameId, StringComparison.Ordinal) ||
                !string.Equals(m.GameRoot, GameRoot(gameRoot), StringComparison.OrdinalIgnoreCase)) throw Error("mod_game_binding_changed");
        }
    }

    internal AiModEntry CreateDraft(string sourceToken, string name, string findText, string replacementText)
    {
        lock (gate)
        {
            if (!sources.TryGetValue(sourceToken, out var source) || source.Expires < DateTimeOffset.UtcNow) throw Error("mod_source_expired");
            ValidateName(name);
            if (string.IsNullOrEmpty(findText) || findText.Length > 16 * 1024 || replacementText.Length > 32 * 1024 || findText == replacementText)
                throw Error("mod_invalid_replacement");
            string target = Target(source.GameRoot, source.RelativePath);
            if (Hash(ReadBounded(target, MaximumFileBytes)) != source.Sha256) throw Error("mod_source_changed");
            string before = Decode(source.Bytes);
            int at = before.IndexOf(findText, StringComparison.Ordinal);
            if (at < 0 || before.IndexOf(findText, at + 1, StringComparison.Ordinal) >= 0) throw Error("mod_match_not_unique");
            string after = before[..at] + replacementText + before[(at + findText.Length)..];
            ValidateDocument(source.RelativePath, after);
            byte[] payload = Encode(after, HasBom(source.Bytes));
            if (payload.Length > MaximumFileBytes) throw Error("mod_file_too_large");
            var manifest = new Manifest
            {
                Id = Guid.NewGuid().ToString("N"), Name = name.Trim(), GameId = source.GameId, GameRoot = source.GameRoot,
                RelativePath = source.RelativePath, OriginalSha256 = source.Sha256, ReplacementSha256 = Hash(payload),
                Bytes = payload.Length, CreatedUtc = DateTimeOffset.UtcNow
            };
            Store(manifest, payload);
            return Entry(manifest);
        }
    }

    internal IReadOnlyList<AiModEntry> List()
    {
        lock (gate)
        {
            CheckLinks(root);
            if (!Directory.Exists(root)) return Array.Empty<AiModEntry>();
            var list = new List<AiModEntry>();
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                string id = Path.GetFileName(dir);
                if (!ValidId(id)) continue;
                Manifest manifest = Load(id);
                string status = HasOpenJournal(id) ? "recovery_required" : manifest.Status;
                list.Add(Entry(manifest) with { Status = status });
            }
            return list.OrderByDescending(m => m.CreatedUtc).ToArray();
        }
    }

    internal AiModPreview Preview(string id)
    {
        lock (gate)
        {
            Manifest m = Load(id);
            string target = Target(GameRoot(m.GameRoot), m.RelativePath);
            byte[] current = ReadBounded(target, MaximumFileBytes);
            byte[] before = m.Status == "installed" || File.Exists(Local(id, "original.bin"))
                ? ReadVerified(Local(id, "original.bin"), m.OriginalSha256) : current;
            if (Hash(before) != m.OriginalSha256) throw Error("mod_source_changed");
            return new(m.Id, m.Name, m.GameId, m.RelativePath, Decode(before), Decode(ReadPayload(m)), HasOpenJournal(id) ? "recovery_required" : m.Status);
        }
    }

    internal void Export(string id, string zipPath)
    {
        lock (gate)
        {
            Manifest m = Load(id); byte[] payload = ReadPayload(m);
            string destination = Path.GetFullPath(zipPath); CheckLinks(destination);
            if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase)) throw Error("mod_invalid_package");
            if (Within(destination, root) || Within(destination, m.GameRoot)) throw Error("mod_invalid_destination");
            if (File.Exists(destination) || Directory.Exists(destination)) throw Error("mod_destination_exists");
            var export = new Manifest
            {
                Id = m.Id, Name = m.Name, RelativePath = m.RelativePath, OriginalSha256 = m.OriginalSha256,
                ReplacementSha256 = m.ReplacementSha256, Bytes = m.Bytes, CreatedUtc = m.CreatedUtc
            };
            using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            WriteEntry(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions));
            WriteEntry(archive, "replacement.bin", payload);
        }
    }

    internal AiModEntry Import(string zipPath, string gameId, string gameRoot)
    {
        lock (gate)
        {
            ValidateGameId(gameId); string game = GameRoot(gameRoot);
            CheckLinks(zipPath);
            using var file = new FileStream(Path.GetFullPath(zipPath), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaximumArchiveBytes) throw Error("mod_file_too_large");
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            if (archive.Entries.Count != 2) throw Error("mod_invalid_package");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (!names.Add(entry.FullName) || entry.FullName is not ("manifest.json" or "replacement.bin") ||
                    (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000 || (entry.ExternalAttributes & 0x400) != 0)
                    throw Error("mod_invalid_package");
                if (entry.Length < 0 || entry.Length > (entry.FullName == "manifest.json" ? 16 * 1024 : MaximumFileBytes)) throw Error("mod_file_too_large");
            }
            Manifest m;
            try { m = JsonSerializer.Deserialize<Manifest>(ReadZip(archive.GetEntry("manifest.json")!, 16 * 1024), JsonOptions) ?? throw Error("mod_invalid_package"); }
            catch (JsonException e) { throw new AiModException("mod_invalid_package", e); }
            ValidateManifest(m, false);
            byte[] payload = ReadZip(archive.GetEntry("replacement.bin")!, MaximumFileBytes);
            if (Hash(payload) != m.ReplacementSha256 || payload.Length != m.Bytes) throw Error("mod_package_changed");
            ValidateDocument(m.RelativePath, Decode(payload));
            string target = Target(game, m.RelativePath);
            if (Hash(ReadBounded(target, MaximumFileBytes)) != m.OriginalSha256) throw Error("mod_source_changed");
            m.Id = Guid.NewGuid().ToString("N"); m.GameId = gameId; m.GameRoot = game; m.Status = "draft";
            Store(m, payload);
            return Entry(m);
        }
    }

    // Must only be called after the local UI has shown the exact preview and
    // received confirmation. The mandatory callback checks the selected game's
    // live binding; a second OS-level check catches processes outside Fusion.
    internal AiModEntry Install(string id, Func<bool> isGameRunning)
    {
        ArgumentNullException.ThrowIfNull(isGameRunning);
        lock (gate)
        {
            Manifest m = Load(id);
            if (HasOpenJournal(id)) throw Error("mod_recovery_required");
            if (m.Status == "installed") throw Error("mod_already_installed");
            EnsureStopped(m.GameRoot, isGameRunning);
            foreach (var other in List().Where(x => x.Id != id && x.Status is "installed" or "recovery_required"))
            {
                var conflict = Load(other.Id);
                if (string.Equals(conflict.GameRoot, m.GameRoot, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(conflict.RelativePath, m.RelativePath, StringComparison.OrdinalIgnoreCase)) throw Error("mod_conflict");
            }
            string target = Target(GameRoot(m.GameRoot), m.RelativePath);
            byte[] before = ReadVerified(target, m.OriginalSha256), after = ReadPayload(m);
            string backup = Local(id, "original.bin");
            if (File.Exists(backup)) ReadVerified(backup, m.OriginalSha256);
            else AtomicNew(backup, before);
            if (Hash(ReadBounded(backup, MaximumFileBytes)) != m.OriginalSha256) throw Error("mod_backup_failed");
            Apply(m, target, before, after, "install", "installed", isGameRunning);
            return Entry(m);
        }
    }

    internal AiModEntry Disable(string id, Func<bool> isGameRunning)
    {
        ArgumentNullException.ThrowIfNull(isGameRunning);
        lock (gate)
        {
            Manifest m = Load(id);
            if (HasOpenJournal(id)) throw Error("mod_recovery_required");
            if (m.Status != "installed") throw Error("mod_not_installed");
            EnsureStopped(m.GameRoot, isGameRunning);
            string target = Target(GameRoot(m.GameRoot), m.RelativePath);
            byte[] current = ReadVerified(target, m.ReplacementSha256), original = ReadVerified(Local(id, "original.bin"), m.OriginalSha256);
            Apply(m, target, current, original, "disable", "disabled", isGameRunning);
            return Entry(m);
        }
    }

    private void Apply(Manifest m, string target, byte[] before, byte[] after, string operation, string status, Func<bool> isGameRunning)
    {
        string oldStatus = m.Status, beforeHash = Hash(before), afterHash = Hash(after);
        var journal = new Journal { Operation = operation };
        SaveJournal(m.Id, journal);
        bool writeAttempted = false;
        try
        {
            fault?.Invoke("before_replace");
            EnsureStopped(m.GameRoot, isGameRunning);
            if (Hash(ReadBounded(target, MaximumFileBytes)) != beforeHash) throw Error("mod_source_changed");
            writeAttempted = true;
            ReplaceFile(target, after);
            fault?.Invoke("after_replace");
            if (Hash(ReadBounded(target, MaximumFileBytes)) != afterHash) throw Error("mod_readback_failed");
            m.Status = status;
            fault?.Invoke("before_manifest");
            SaveManifest(m);
            journal.State = "committed"; SaveJournal(m.Id, journal);
        }
        catch (Exception error)
        {
            bool restored = !writeAttempted;
            try
            {
                if (writeAttempted)
                {
                    string observedHash = Hash(ReadBounded(target, MaximumFileBytes));
                    if (observedHash == beforeHash) restored = true;
                    else
                    {
                        fault?.Invoke("before_rollback");
                        // Never overwrite another writer's intervening changes.
                        // Also verify the actual file if ReplaceFile threw after
                        // attempting a commit rather than assuming it did nothing.
                        if (observedHash != afterHash) throw Error("mod_source_changed");
                        ReplaceFile(target, before);
                        restored = Hash(ReadBounded(target, MaximumFileBytes)) == beforeHash;
                    }
                }
                m.Status = restored ? oldStatus : "recovery_required";
                SaveManifest(m);
            }
            catch { restored = false; }
            journal.State = restored ? "rolled_back" : "recovery_required";
            journal.Error = error is AiModException e ? e.Code : "mod_io_error";
            // A previously persisted prepared journal remains if disk failure
            // prevents this write. List() still reports recovery_required.
            try { SaveJournal(m.Id, journal); } catch { }
            throw new AiModException(restored ? "mod_install_rolled_back" : "mod_recovery_required", error);
        }
    }

    private void Store(Manifest m, byte[] payload)
    {
        CheckLinks(root); Directory.CreateDirectory(root); CheckLinks(root);
        string dir = Local(m.Id, "");
        if (Directory.Exists(dir)) throw Error("mod_destination_exists");
        Directory.CreateDirectory(dir);
        AtomicNew(Local(m.Id, "replacement.bin"), payload);
        SaveManifest(m);
    }

    private Manifest Load(string id)
    {
        ValidateId(id);
        Manifest manifest;
        try { manifest = JsonSerializer.Deserialize<Manifest>(ReadBounded(Local(id, "manifest.json"), 16 * 1024), JsonOptions) ?? throw Error("mod_invalid_package"); }
        catch (JsonException e) { throw new AiModException("mod_invalid_package", e); }
        ValidateManifest(manifest, true);
        if (manifest.Id != id) throw Error("mod_invalid_package");
        return manifest;
    }

    private static void ValidateManifest(Manifest m, bool local)
    {
        if (m.Id is null || m.Name is null || m.GameId is null || m.GameRoot is null || m.RelativePath is null ||
            m.OriginalSha256 is null || m.ReplacementSha256 is null ||
            m.Format != 1 || !ValidId(m.Id) || !ValidHash(m.OriginalSha256) || !ValidHash(m.ReplacementSha256) ||
            m.Bytes < 0 || m.Bytes > MaximumFileBytes || m.OriginalSha256 == m.ReplacementSha256 ||
            m.Status is not ("draft" or "installed" or "disabled" or "recovery_required")) throw Error("mod_invalid_package");
        ValidateName(m.Name); Relative(m.RelativePath);
        if (local) ValidateGameId(m.GameId);
        else if (m.GameRoot.Length != 0 || m.GameId.Length != 0 || m.Status != "draft") throw Error("mod_invalid_package");
    }

    private bool HasOpenJournal(string id)
    {
        string path = Local(id, "transaction.json");
        if (!File.Exists(path)) return false;
        try
        {
            var journal = JsonSerializer.Deserialize<Journal>(ReadBounded(path, 4096), JsonOptions);
            return journal is null || journal.State is not ("committed" or "rolled_back");
        }
        catch { return true; }
    }

    private void SaveManifest(Manifest m) => WriteLocal(Local(m.Id, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(m, JsonOptions));
    private void SaveJournal(string id, Journal journal)
    {
        WriteLocal(Local(id, "transaction.json"), JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions));
        // Keep failure/transaction history; later success does not erase it.
        if (journal.State != "prepared")
            AtomicNew(Local(id, "transaction-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions));
    }
    private byte[] ReadPayload(Manifest m)
    {
        byte[] payload = ReadVerified(Local(m.Id, "replacement.bin"), m.ReplacementSha256);
        if (payload.Length != m.Bytes) throw Error("mod_package_changed");
        ValidateDocument(m.RelativePath, Decode(payload));
        return payload;
    }
    private string Local(string id, string name)
    {
        ValidateId(id);
        string result = Path.Combine(root, id, name); CheckLinks(result); return result;
    }
    private string GameRoot(string value)
    {
        string game;
        try { game = Path.GetFullPath(value).TrimEnd('\\', '/'); }
        catch (Exception e) { throw new AiModException("mod_invalid_root", e); }
        if (game.Length < 4 || !Directory.Exists(game) || Within(game, root) || Within(root, game)) throw Error("mod_invalid_root");
        CheckLinks(game); return game;
    }
    private static string Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || Path.IsPathRooted(value) || value.Contains(':') || value.Contains('\0')) throw Error("mod_invalid_path");
        string path = value.Replace('\\', '/');
        foreach (string part in path.Split('/'))
        {
            string stem = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
                part.Any(char.IsControl) || ForbiddenParts.Contains(part) ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3])))
                throw Error("mod_invalid_path");
        }
        string name = Path.GetFileName(path);
        if (!Extensions.Contains(Path.GetExtension(name)) || ForbiddenNames.Contains(name) || name.StartsWith(".", StringComparison.Ordinal) ||
            name.Contains("save", StringComparison.OrdinalIgnoreCase) || name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) || name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)) throw Error("mod_unsupported_file");
        return path;
    }
    private static string Target(string game, string relative)
    {
        string target = Path.GetFullPath(Path.Combine(game, Relative(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(target, game) || target.Equals(game, StringComparison.OrdinalIgnoreCase)) throw Error("mod_invalid_path");
        CheckLinks(target);
        if (!File.Exists(target)) throw Error("mod_source_missing");
        return target;
    }
    private static bool Within(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void CheckLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Error("mod_link_not_allowed");
            }
            current = Path.GetDirectoryName(current);
        }
    }
    private static void EnsureStopped(string gameRoot, Func<bool> isGameRunning)
    {
        bool running;
        try { running = isGameRunning(); }
        catch (Exception e) { throw new AiModException("mod_game_state_unknown", e); }
        if (running) throw Error("mod_game_running");
        // Independent check: Fusion may not own a game launched externally.
        var executableNames = Directory.EnumerateFiles(gameRoot, "*.exe", SearchOption.TopDirectoryOnly)
            .Take(256).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                bool plausibleGame = false;
                try
                {
                    plausibleGame = executableNames.Contains(process.ProcessName);
                    string? executable = process.MainModule?.FileName;
                    if (executable is not null && Within(Path.GetFullPath(executable), gameRoot)) throw Error("mod_game_running");
                }
                catch (AiModException) { throw; }
                catch (System.ComponentModel.Win32Exception e) { if (plausibleGame) throw new AiModException("mod_game_state_unknown", e); }
                catch (InvalidOperationException) { }
                catch (NotSupportedException) { }
            }
        }
    }
    private static byte[] ReadVerified(string path, string expected)
    {
        byte[] value = ReadBounded(path, MaximumFileBytes);
        if (Hash(value) != expected) throw Error("mod_source_changed");
        return value;
    }
    private static byte[] ReadBounded(string path, int maximum)
    {
        CheckLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw Error("mod_file_too_large");
        using var result = new MemoryStream();
        byte[] buffer = new byte[8192]; int count;
        while ((count = file.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (result.Length + count > maximum) throw Error("mod_file_too_large");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
    private static byte[] ReadZip(ZipArchiveEntry entry, int maximum)
    {
        using var stream = entry.Open(); using var result = new MemoryStream();
        byte[] buffer = new byte[8192]; int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (result.Length + count > maximum) throw Error("mod_file_too_large");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); stream.Write(bytes);
    }
    private static void AtomicNew(string path, byte[] bytes)
    {
        CheckLinks(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }
    private static void WriteLocal(string target, byte[] bytes)
    {
        CheckLinks(target);
        if (!File.Exists(target)) { AtomicNew(target, bytes); return; }
        ReplaceFile(target, bytes);
    }
    private static void ReplaceFile(string target, byte[] bytes)
    {
        CheckLinks(target);
        string temp = Path.Combine(Path.GetDirectoryName(target)!, ".fusion-mod-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            AtomicNew(temp, bytes); CheckLinks(target); File.Replace(temp, target, null);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static bool HasBom(byte[] value) => value.Length >= 3 && value[0] == 0xef && value[1] == 0xbb && value[2] == 0xbf;
    private static byte[] Encode(string value, bool bom)
    {
        byte[] bytes = Utf8.GetBytes(value);
        return bom ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes).ToArray() : bytes;
    }
    private static string Decode(byte[] value)
    {
        try
        {
            string text = Utf8.GetString(value, HasBom(value) ? 3 : 0, value.Length - (HasBom(value) ? 3 : 0));
            if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw Error("mod_unsupported_encoding");
            return text;
        }
        catch (DecoderFallbackException e) { throw new AiModException("mod_unsupported_encoding", e); }
    }
    private static void ValidateDocument(string path, string text)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) using (JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 })) { }
            if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumFileBytes });
                while (reader.Read()) { }
            }
        }
        catch (Exception e) when (e is JsonException or XmlException) { throw new AiModException("mod_invalid_document", e); }
    }
    private static bool ValidId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateId(string id) { if (!ValidId(id)) throw Error("mod_invalid_package"); }
    private static bool ValidHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static void ValidateName(string name) { if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)) throw Error("mod_invalid_name"); }
    private static void ValidateGameId(string id) { if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl)) throw Error("mod_invalid_game"); }
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static AiModEntry Entry(Manifest m) => new(m.Id, m.Name, m.GameId, m.Status, m.Bytes, m.CreatedUtc);
    private static AiModException Error(string code) => new(code);
}
