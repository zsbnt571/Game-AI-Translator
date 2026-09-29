using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace ScreenshotTranslationUiTester;

// Best-effort, read-only evidence collection. A recently written log is not proof
// of a crash cause, and its older lines are not automatically session evidence.
internal static class GameDiagnosticLogs
{
    internal sealed record Entry(string Kind, string Name, string Text, bool Truncated, string? Note);
    internal const int MaximumFileBytes = 128 * 1024;
    internal const int MaximumTotalBytes = 512 * 1024;
    internal const int MaximumContentFiles = 8;
    private const int MaximumEntries = 24, MaximumDirectoryEntries = 32;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, false);
    private static readonly HashSet<string> XmlFields = new(StringComparer.Ordinal)
    {
        "ErrorMessage", "CrashType", "EngineVersion", "BuildVersion", "PlatformName",
        "IsAssert", "IsEnsure", "IsStall", "SecondsSinceStart", "EngineMode",
        "ExceptionCode", "ExceptionDescription", "CallStack", "PortableCallStack"
    };
    private static readonly Regex[] SensitiveLines =
    [
        Pattern(@"(?i)(?:command[ _-]*line|cmdline|\b(?:process|launch)?arguments\b|\bargs\s*[:=]|api[ _-]*key|access[ _-]*token|refresh[ _-]*token|authorization|authentication|\bauth\b)"),
        Pattern(@"(?i)(?:password|passwd|secret|credential|cookie|\btoken\b|loginid|accountid|userid|username|computername|machinename|命令行|启动参数|密码|密钥|令牌|凭据)")
    ];
    private static readonly Regex HomePath = Pattern(@"(?i)(?:[a-z]:[\\/]Users[\\/][^\\/\s\""'<>]+|/home/[^/\s\""'<>]+|/Users/[^/\s\""'<>]+)");
    private static readonly Regex Url = Pattern(@"(?i)\b(?:https?|wss?)://[^\s\""'<>]+");
    private static readonly Regex Token = Pattern(@"\b(?:sk-[A-Za-z0-9_-]{8,}|eyJ[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]+){1,2})\b");
    private static readonly Regex NewRecord = Pattern(@"(?i)^\s*(?:\[\d{4}[-./]\d{2}[-./]\d{2}|\d{4}[-./]\d{2}[-./]\d{2}[ T]|\[(?:Info|Message|Warning|Error|Fatal|Debug|Trace)\s*[:\]])");
    private static readonly Regex JsonKey = Pattern(@"^\s*""[A-Za-z_][A-Za-z0-9_.-]{0,80}""\s*:");
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(150));

    internal static IReadOnlyList<Entry> Collect(GameInfo game, DateTime startedUtc, DateTime endedUtc)
    {
        var result = new List<Entry>();
        try
        {
            startedUtc = AsUtc(startedUtc); endedUtc = AsUtc(endedUtc);
            if (endedUtc < startedUtc || startedUtc < DateTime.UnixEpoch || endedUtc > DateTime.UtcNow.AddMinutes(5))
                return [Note("collection", "logs", "会话时间无效，未读取游戏日志。")];
            string root = Path.GetDirectoryName(Path.GetFullPath(game.ExePath))!;
            bool unreal = game.Engine.Contains("Unreal", StringComparison.OrdinalIgnoreCase);
            if (unreal && (Path.GetFileName(root).Equals("Win64", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(root).Equals("Win32", StringComparison.OrdinalIgnoreCase))
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(root)), "Binaries", StringComparison.OrdinalIgnoreCase))
                root = Path.GetDirectoryName(Path.GetDirectoryName(root))!;
            if (!LocalPath(root) || !SafePath(root, root, directory: true))
                return [Note("collection", "logs", "游戏日志根目录不可读或包含链接，未读取。")];

            var candidates = new List<(string Kind, string Path, bool Xml)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Candidate(string kind, string path, bool xml = false)
            {
                if (candidates.Count < MaximumEntries && seen.Add(path)) candidates.Add((kind, path, xml));
            }
            string? data = null;
            if (!string.IsNullOrWhiteSpace(game.DataDirectory))
            {
                var proposed = Path.GetFullPath(game.DataDirectory);
                if (Inside(root, proposed) && SafePath(root, proposed, true)) data = proposed;
                else result.Add(Note("collection", "data", "数据目录位于游戏范围外、包含链接或不可读；未搜索该目录。"));
            }
            if (game.Engine.Contains("Unity", StringComparison.OrdinalIgnoreCase))
            {
                Candidate("unity", Path.Combine(root, "BepInEx", "LogOutput.log"));
                if (data is not null) Candidate("unity", Path.Combine(data, "output_log.txt"));
                Candidate("unity", Path.Combine(root, "output_log.txt"));
            }
            else if (game.Engine.Replace('’', '\'').Contains("Ren'Py", StringComparison.OrdinalIgnoreCase)
                || string.Equals(game.AdapterId, "renpy", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string name in new[] { "traceback.txt", "errors.txt", "log.txt" }) Candidate("renpy", Path.Combine(root, name));
            }
            else if (unreal)
            {
                var project = data ?? root;
                string logs = Path.Combine(project, "Saved", "Logs");
                string crashes = Path.Combine(project, "Saved", "Crashes");
                Candidate("unreal-crash", Path.Combine(crashes, "CrashContext.runtime-xml"), true);
                foreach (string folder in Enumerate(root, crashes, directories: true, result).Take(8))
                    Candidate("unreal-crash", Path.Combine(folder, "CrashContext.runtime-xml"), true);
                foreach (string file in Enumerate(root, logs, directories: false, result).Where(p => Path.GetExtension(p).Equals(".log", StringComparison.OrdinalIgnoreCase)).Take(8)) Candidate("unreal", file);
                Candidate("ue4ss", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(game.ExePath))!, "ue4ss", "UE4SS.log"));
                Candidate("ue4ss", Path.Combine(project, "Binaries", "Win64", "ue4ss", "UE4SS.log"));
            }
            else return [Note("collection", "logs", "此引擎暂无限定日志位置，未搜索其他目录。")];

            int readBytes = 0, outputBytes = 0, contentFiles = 0;
            foreach (var candidate in candidates)
            {
                if (result.Count >= MaximumEntries) break;
                string name = Sanitize(Path.GetRelativePath(root, candidate.Path));
                if (!File.Exists(candidate.Path) && !Directory.Exists(candidate.Path)) continue;
                if (!SafePath(root, candidate.Path, false))
                { result.Add(Note(candidate.Kind, name, "日志路径包含链接或不在游戏范围内，未读取。")); continue; }
                try
                {
                    var info = new FileInfo(candidate.Path);
                    var stamp = info.LastWriteTimeUtc;
                    if (stamp < startedUtc.AddSeconds(-2) || stamp > endedUtc.AddSeconds(2))
                    { result.Add(Note(candidate.Kind, name, "日志修改时间不属于本次会话，未读取正文；不能作为本次异常证据。")); continue; }
                    if (contentFiles >= MaximumContentFiles || readBytes >= MaximumTotalBytes || outputBytes >= MaximumTotalBytes)
                    { result.Add(Note(candidate.Kind, name, "已达到本次日志数量或大小上限，未读取。")); break; }
                    using var stream = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    // Check again after open; never intentionally follow a reparse point.
                    if (!SafePath(root, candidate.Path, false) || !OpenedPathMatches(stream.SafeFileHandle, candidate.Path))
                    { result.Add(Note(candidate.Kind, name, "打开的日志路径或链接状态不符合安全范围，已跳过。")); continue; }
                    long length = stream.Length;
                    int limit = Math.Min(MaximumFileBytes, MaximumTotalBytes - readBytes);
                    if (limit < 8)
                    { result.Add(Note(candidate.Kind, name, "已达到本次日志读取大小上限，未读取。")); break; }
                    if (candidate.Xml && length > limit)
                    { result.Add(new(candidate.Kind, name, "", true, Sanitize("崩溃上下文超过读取上限，未读取或截断解析。"))); continue; }
                    long offset = Math.Max(0, length - limit);
                    Encoding encoding = Utf8;
                    if (offset > 0)
                    {
                        // Include this small BOM probe in the per-file and total read
                        // budgets. A UTF-16 tail must not evade token redaction via NULs.
                        byte[] prefix = new byte[4]; int probed = stream.Read(prefix, 0, prefix.Length);
                        readBytes += probed; limit -= probed;
                        if (probed >= 2 && prefix[0] == 0xff && prefix[1] == 0xfe) encoding = Encoding.Unicode;
                        else if (probed >= 2 && prefix[0] == 0xfe && prefix[1] == 0xff) encoding = Encoding.BigEndianUnicode;
                        offset = Math.Max(0, length - limit);
                        if (encoding != Utf8 && offset % 2 != 0) offset++;
                    }
                    stream.Position = offset;
                    byte[] bytes = new byte[(int)Math.Min(length - offset, limit)];
                    int got = 0;
                    while (got < bytes.Length)
                    {
                        int count = stream.Read(bytes, got, bytes.Length - got); if (count == 0) break; got += count;
                    }
                    readBytes += got; contentFiles++;
                    bool truncated = offset > 0 || got != length;
                    string raw;
                    if (candidate.Xml) raw = ReadCrashXml(bytes.AsSpan(0, got).ToArray());
                    else
                    {
                        // Partial first lines are dropped before redaction to avoid exposing
                        // credential suffixes that straddled the byte boundary.
                        using var reader = new StreamReader(new MemoryStream(bytes, 0, got), encoding, detectEncodingFromByteOrderMarks: offset == 0);
                        raw = reader.ReadToEnd();
                        if (offset > 0) { int newline = raw.IndexOf('\n'); raw = newline < 0 ? "" : raw[(newline + 1)..]; }
                    }
                    string text = Sanitize(raw);
                    int outputLimit = Math.Min(MaximumFileBytes, MaximumTotalBytes - outputBytes);
                    if (Utf8.GetByteCount(text) > outputLimit) { text = LimitUtf8(text, outputLimit); truncated = true; }
                    outputBytes += Utf8.GetByteCount(text);
                    string note = candidate.Xml
                        ? "仅保留异常和引擎白名单字段；修改时间接近本次会话，但不据此推断闪退原因。"
                        : "仅收集本次会话附近更新的日志尾部；其中可能含早先记录，不代表每行都由本次异常产生。";
                    if (string.IsNullOrWhiteSpace(text)) note += " 没有可安全收集的正文。";
                    result.Add(new(candidate.Kind, name, text, truncated, Sanitize(note)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException)
                { result.Add(Note(candidate.Kind, name, "日志不可读取或格式不受支持，已跳过（" + ex.GetType().Name + "）。")); }
            }
            if (result.Count == 0) result.Add(Note("collection", "logs", "未找到本次会话的限定日志；这不表示游戏没有发生异常。"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { result.Add(Note("collection", "logs", "未能完成限定日志收集（" + ex.GetType().Name + "）。")); }
        return result.Take(MaximumEntries).ToArray();
    }

    private static DateTime AsUtc(DateTime date) => date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : DateTime.SpecifyKind(date, DateTimeKind.Utc);
    private static Entry Note(string kind, string name, string note) => new(Sanitize(kind), Sanitize(name), "", false, Sanitize(note));
    private static bool LocalPath(string path) => Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) && path.IndexOf(':', 2) < 0;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInformation information);
    private static bool OpenedPathMatches(SafeFileHandle file, string expected)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!GetFileInformationByHandle(file, out var information) || information.NumberOfLinks != 1
            || (information.Attributes & (uint)FileAttributes.ReparsePoint) != 0) return false;
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return false;
        string actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        return LocalPath(actual) && string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }
    private static bool Inside(string root, string path) => string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), path.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool SafePath(string root, string path, bool directory)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (!LocalPath(path) || !Inside(root, path)) return false;
            for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            {
                var attributes = File.GetAttributes(part);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (part == path && ((attributes & FileAttributes.Directory) != 0) != directory) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
    private static string[] Enumerate(string root, string directory, bool directories, List<Entry> result)
    {
        if (!Directory.Exists(directory)) return [];
        if (!SafePath(root, directory, true))
        { result.Add(Note("collection", Path.GetRelativePath(root, directory), "日志目录包含链接或不可读，未搜索。")); return []; }
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var items = (directories ? Directory.EnumerateDirectories(directory, "*", options) : Directory.EnumerateFiles(directory, "*", options)).Take(MaximumDirectoryEntries + 1).ToArray();
            if (items.Length > MaximumDirectoryEntries) result.Add(Note("collection", Path.GetRelativePath(root, directory), "仅检查限定数量的目录项，可能遗漏其他日志。"));
            return items.Take(MaximumDirectoryEntries).OrderByDescending(p => File.GetLastWriteTimeUtc(p)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Add(Note("collection", "logs", "限定日志目录不可读取，已跳过。")); return []; }
    }
    private static string ReadCrashXml(byte[] bytes)
    {
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumFileBytes, MaxCharactersFromEntities = 0 });
        var document = XDocument.Load(reader, LoadOptions.None);
        if (document.Root?.Name.LocalName != "FGenericCrashContext") return "";
        var properties = document.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "RuntimeProperties");
        if (properties is null) return "";
        return string.Join(Environment.NewLine, properties.Elements().Where(e => XmlFields.Contains(e.Name.LocalName) && !e.HasElements).Select(e => e.Name.LocalName + ": " + e.Value));
    }
    private static string Sanitize(string? value)
    {
        try
        {
            string text = SafeDiagnosticOutput.Redact(value);
            text = new string(text.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray());
            text = SafeDiagnosticOutput.Redact(text);
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var safeLines = new List<string>(); bool hideContinuation = false;
            foreach (string line in lines)
            {
                if (SensitiveLines.Any(pattern => pattern.IsMatch(line)))
                { safeLines.Add("[已移除敏感字段及未分隔续行]"); hideContinuation = true; continue; }
                if (hideContinuation)
                {
                    // Unknown multiline values are not safe merely because their
                    // field label was on the preceding line. Resume only at an
                    // explicit record/field/paragraph boundary, otherwise omit.
                    if (string.IsNullOrWhiteSpace(line) || NewRecord.IsMatch(line) || JsonKey.IsMatch(line)) hideContinuation = false;
                    else continue;
                }
                safeLines.Add(line);
            }
            text = string.Join('\n', safeLines);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home)) text = text.Replace(home, "[USER_HOME]", StringComparison.OrdinalIgnoreCase).Replace(home.Replace('\\', '/'), "[USER_HOME]", StringComparison.OrdinalIgnoreCase);
            text = HomePath.Replace(text, "[USER_HOME]"); text = Url.Replace(text, "[URL_REMOVED]"); text = Token.Replace(text, "[REDACTED]");
            text = new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
            return SafeDiagnosticOutput.Redact(text);
        }
        catch { return "[诊断内容无法安全脱敏，已省略]"; }
    }
    private static string LimitUtf8(string text, int byteLimit)
    {
        int low = 0, high = Math.Min(text.Length, byteLimit);
        while (low < high)
        {
            int middle = low + (high - low + 1) / 2;
            if (Utf8.GetByteCount(text.AsSpan(0, middle)) <= byteLimit) low = middle; else high = middle - 1;
        }
        int end = low;
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end];
    }
}
