using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal enum EngineTestEvidenceKind { Unavailable, FontFile, ControlledAdapter, ActualEngine }
internal enum EngineTestRunStatus { Completed, Failed, Cancelled, NotReady }
internal sealed record EngineTestCatalogItem(string EngineId, string Name, bool Ready, EngineTestEvidenceKind EvidenceKind, string Message);
internal sealed record EngineTestCheck(string Name, bool Passed, string Detail, EngineTestEvidenceKind EvidenceKind);
internal sealed record EngineTestRequest
{
    public string EngineId { get; init; } = "godot4";
    public string FontPath { get; init; } = "";
    public string FontFamily { get; init; } = "";
    public string FontMode { get; init; } = "default";
    public string SampleText { get; init; } = "你好 Hello Привет 日本語 한국어";
    public string RuntimePath { get; init; } = "";
    public string OutputRoot { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 45;
}
internal sealed record EngineTestResult
{
    public string EngineId { get; init; } = "";
    public EngineTestRunStatus Status { get; init; }
    public EngineTestEvidenceKind EvidenceKind { get; init; }
    public string Summary { get; init; } = "";
    public EngineTestCheck[] Checks { get; init; } = [];
    public string[] Screenshots { get; init; } = [];
    public string ReportPath { get; init; } = "";
    public string RuntimeVersion { get; init; } = "";
    public string RuntimeSha256 { get; init; } = "";
    public string FontSha256 { get; init; } = "";
    public string BridgeSha256 { get; init; } = "";
    public string FixtureSha256 { get; init; } = "";
    public bool OriginalGamesLaunched { get; init; }
    public int ApiRequests { get; init; }
    public bool CharacterBindingVerified { get; init; }
    public bool GameSaveFilesWritten { get; init; }
}

// Only owned neutral projects are executed. A runtime is copied into each new
// session; no original game path, translation provider, or preference store enters
// this API. A file-coverage check is never promoted to an engine-rendering result.
internal static class EngineTestWorkspaceRunner
{
    // Only the two isolated, tested official runtimes may be executed here.
    // An arbitrary selected EXE must not run even for a --version probe.
    internal static bool VerifiedRuntime(string engineId,long length,string sha256) => (engineId,length,sha256) switch
    {
        ("godot3",66296952,"AA19ACAE5BBC67BD7B79E20328AE2D7E2C2BB8589A2934C43C9126D742F60C97") => true,
        ("godot4",132918392,"B554BA80FB2AD8F1817BC21242AC8ADBB9E41F806C49AD5CC73603DB71FEE32F") => true,
        _ => false
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly (string Id, string Name)[] Engines = FontTestEngines.Items.Select(x => (x.Id, x.Name)).ToArray();

    internal static IReadOnlyList<EngineTestCatalogItem> Catalog => GetCatalog();
    internal static IReadOnlyList<EngineTestCatalogItem> GetCatalog(IReadOnlyDictionary<string, string>? runtimePaths = null)
        => Engines.Select(engine =>
        {
            bool implemented = engine.Id is "godot3" or "godot4";
            string runtime = runtimePaths != null && runtimePaths.TryGetValue(engine.Id, out var value) ? value : FindDefaultRuntime(engine.Id);
            bool ready = implemented && File.Exists(runtime);
            return new EngineTestCatalogItem(engine.Id, engine.Name, ready,
                ready ? EngineTestEvidenceKind.ActualEngine : EngineTestEvidenceKind.Unavailable,
                implemented ? ready ? "可运行独立引擎样例。" : "需要准备引擎运行环境。" : "此引擎的独立样例尚未准备。");
        }).ToArray();

    internal static string FindDefaultRuntime(string engineId)
    {
        if (engineId is not ("godot3" or "godot4")) return "";
        foreach (string name in new[] { "runtime.exe", "godot.exe" })
        {
            string path = Path.Combine(AppContext.BaseDirectory, "test-runtimes", engineId, name);
            if (File.Exists(path)) return path;
        }
        return "";
    }

    internal static Task<EngineTestResult> RunCharacterCheckAsync(string outputRoot = "", CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new EngineTestResult { EngineId = "shared-character", Status = EngineTestRunStatus.Cancelled, Summary = "测试已取消。" });
        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(outputRoot) ? Path.Combine(AppDataPaths.Root, "test-area") : outputRoot);
        RejectLinks(root);
        if (Path.GetPathRoot(root)!.TrimEnd('\\', '/').Equals(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(NotReady("shared-character", "测试输出位置无效。"));
        Directory.CreateDirectory(root);
        string session = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-characters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        var result = new EngineTestResult { EngineId = "shared-character", EvidenceKind = EngineTestEvidenceKind.ControlledAdapter, ReportPath = Path.Combine(session, "report.json") };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checks = EngineTestCharacterFixture.Run(session);
            result = result with { Status = checks.All(x => x.Passed) ? EngineTestRunStatus.Completed : EngineTestRunStatus.Failed,
                Checks = checks, Summary = "离线角色检查完成；实际引擎角色识别需单独验证。" };
        }
        catch (OperationCanceledException) { result = result with { Status = EngineTestRunStatus.Cancelled, Summary = "测试已取消。" }; }
        catch (Exception error) { result = result with { Status = EngineTestRunStatus.Failed, Summary = "测试未完成。", Checks = [new("测试未完成", false, error.Message, EngineTestEvidenceKind.ControlledAdapter)] }; }
        File.WriteAllText(result.ReportPath, JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));
        return Task.FromResult(result);
    }

    internal static async Task<EngineTestResult> RunAsync(EngineTestRequest request, CancellationToken cancellationToken = default)
    {
        string engineId = request.EngineId;
        if (!Engines.Any(x => x.Id == engineId)) return NotReady(engineId, "此引擎的独立样例尚未准备。");
        if (engineId is not ("godot3" or "godot4")) return NotReady(engineId, "此引擎的独立样例尚未准备。");
        string runtime = string.IsNullOrWhiteSpace(request.RuntimePath) ? FindDefaultRuntime(engineId) : Path.GetFullPath(request.RuntimePath);
        if (!File.Exists(runtime)) return NotReady(engineId, "需要准备引擎运行环境。");
        if (!string.Equals(Path.GetExtension(runtime), ".exe", StringComparison.OrdinalIgnoreCase)) return NotReady(engineId, "请选择引擎运行程序。");
        // Godot's small *_console.exe is a name-bound launcher, not the engine.
        // Resolve its sibling explicitly; only the actual engine is copied/run.
        if (runtime.EndsWith("_console.exe", StringComparison.OrdinalIgnoreCase))
        {
            string main = runtime[..^"_console.exe".Length] + ".exe";
            if (File.Exists(main)) runtime = main;
        }
        if (request.SampleText.Length > 4096 || request.SampleText.Contains('\0')) return NotReady(engineId, "测试文字无效或过长。");
        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(request.OutputRoot) ? Path.Combine(AppDataPaths.Root, "test-area") : request.OutputRoot);
        if (Path.GetPathRoot(root)!.TrimEnd('\\', '/').Equals(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return NotReady(engineId, "测试输出位置无效。");
        RejectLinks(root);
        Directory.CreateDirectory(root);
        string session = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + engineId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        string reportPath = Path.Combine(session, "report.json");
        var checks = new List<EngineTestCheck>();
        string runtimeHash = "", version = "", fontHash = "", bridgeHash = "", fixtureHash = "";
        var result = new EngineTestResult { EngineId = engineId, ReportPath = reportPath };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(runtime);
            if (new FileInfo(runtime).Length > 512L * 1024 * 1024) throw new InvalidDataException("引擎运行程序过大。");
            string copiedRuntime = Path.Combine(session, "neutral-engine.exe");
            runtimeHash = Hash(runtime);
            if (!VerifiedRuntime(engineId,new FileInfo(runtime).Length,runtimeHash))
                throw new InvalidDataException("此运行程序尚未验证。测试区目前支持官方 Godot 3.6 或 4.3 Windows 64 位独立运行程序。");
            File.Copy(runtime, copiedRuntime);
            if (Hash(copiedRuntime) != runtimeHash) throw new IOException("引擎运行程序复制校验失败。");
            var versionRun = await RunProcess(copiedRuntime, ["--version"], session, 10, cancellationToken);
            version = versionRun.Stdout.Trim();
            if (versionRun.ExitCode != 0 || !version.StartsWith(engineId == "godot3" ? "3." : "4.", StringComparison.Ordinal))
                throw new InvalidDataException("引擎版本与所选类型不符。");
            string font = string.IsNullOrWhiteSpace(request.FontPath) && request.FontMode == "default" ? WpfTestWorkspace.DefaultFontPath(0) : request.FontPath;
            if (!File.Exists(font)) throw new FileNotFoundException("测试字体文件不存在。");
            RejectLinks(font);
            if (new FileInfo(font).Length > 32L * 1024 * 1024) throw new InvalidDataException("测试字体文件过大。");
            string copiedFont = Path.Combine(session, "selected" + Path.GetExtension(font).ToLowerInvariant());
            File.Copy(font, copiedFont);
            fontHash = Hash(font);
            var coverage = GameFontCoverage.Check(copiedFont, request.SampleText);
            checks.Add(new("字体文件字符覆盖", coverage.Missing.Length == 0, coverage.Missing.Length == 0 ? "样文无缺字" : "缺少 " + coverage.Missing.Length + " 个字符", EngineTestEvidenceKind.FontFile));
            // The fixture's authored face is its own neutral baseline, not a
            // replacement or a claim about an arbitrary game's original face.
            string authored = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "consola.ttf");
            if (!File.Exists(authored)) throw new FileNotFoundException("独立样例基准字体未准备。");
            File.Copy(authored, Path.Combine(session, "authored.ttf"));
            Directory.CreateDirectory(Path.Combine(session, "session"));
            Write(session, "project.godot", "[application]\nconfig/name=\"Fusion neutral font test\"\n[display]\nwindow/size/width=900\nwindow/size/height=420\nwindow/size/viewport_width=900\nwindow/size/viewport_height=420\n");
            string bridgeName = engineId == "godot3" ? "GodotTranslationBridge.gd" : "Godot4TranslationBridge.gd";
            using (var stream = typeof(EngineTestWorkspaceRunner).Assembly.GetManifestResourceStream("Fusion." + bridgeName) ?? throw new InvalidDataException("引擎测试组件未准备。"))
            using (var file = File.Create(Path.Combine(session, "bridge.gd"))) stream.CopyTo(file);
            bridgeHash = Hash(Path.Combine(session, "bridge.gd"));
            string script = EngineTestWorkspaceFixtures.Godot(engineId == "godot4", request.SampleText, Path.GetFileName(copiedFont));
            Write(session, "fixture.gd", script);fixtureHash = Hash(Path.Combine(session, "fixture.gd"));
            string[] rendering = engineId == "godot3" ? ["--video-driver", "GLES2"] : ["--rendering-method", "gl_compatibility"];
            var process = await RunProcess(copiedRuntime, ["--audio-driver", "Dummy", .. rendering, "--position", "-12000,-12000", "--path", session, "--script", "res://fixture.gd"], session, Math.Clamp(request.TimeoutSeconds, 5, 90), cancellationToken);
            Write(session, "stdout.log", process.Stdout);Write(session, "stderr.log", process.Stderr);
            string engineReport = Path.Combine(session, "engine-result.json");
            if (!File.Exists(engineReport)) throw new InvalidDataException("独立引擎样例未返回结果。");
            using var doc = JsonDocument.Parse(File.ReadAllText(engineReport));
            foreach (var row in doc.RootElement.GetProperty("checks").EnumerateArray())
                checks.Add(new(row.GetProperty("name").GetString() ?? "", row.GetProperty("passed").GetBoolean(), "", EngineTestEvidenceKind.ActualEngine));
            bool unchanged = runtimeHash == Hash(runtime) && runtimeHash == Hash(copiedRuntime) && fontHash == Hash(font) && fontHash == Hash(copiedFont);
            checks.Add(new("运行程序与字体原件保持不变", unchanged, "", EngineTestEvidenceKind.ActualEngine));
            var screenshots = new[] { "original.png", "custom.png", "restored.png" }.Select(x => Path.Combine(session, x)).Where(File.Exists).ToArray();
            bool enginePassed = process.ExitCode == 0 && screenshots.Length == 3 && unchanged && checks.Where(x => x.EvidenceKind == EngineTestEvidenceKind.ActualEngine).All(x => x.Passed);
            result = result with { Status = enginePassed ? EngineTestRunStatus.Completed : EngineTestRunStatus.Failed, EvidenceKind = EngineTestEvidenceKind.ActualEngine, Summary = enginePassed ? "独立引擎样例完成；原游戏效果仍需单独验证。" : "独立引擎样例未通过，请查看结果。", Screenshots = screenshots };
        }
        catch (OperationCanceledException) { result = result with { Status = EngineTestRunStatus.Cancelled, Summary = "测试已取消。" }; }
        catch (Exception error) { result = result with { Status = EngineTestRunStatus.Failed, Summary = "测试未完成。", Checks = [new("测试未完成", false, error.Message, EngineTestEvidenceKind.Unavailable)] }; }
        result = result with { Checks = [.. checks, .. result.Checks], RuntimeVersion = version, RuntimeSha256 = runtimeHash, FontSha256 = fontHash, BridgeSha256 = bridgeHash, FixtureSha256 = fixtureHash };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));
        return result;
    }

    private static EngineTestResult NotReady(string id, string message) => new() { EngineId = id, Status = EngineTestRunStatus.NotReady, EvidenceKind = EngineTestEvidenceKind.Unavailable, Summary = message };
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Write(string folder, string name, string text) => File.WriteAllText(Path.Combine(folder, name), text, new UTF8Encoding(false));
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("测试路径不能使用链接目录。");
    }
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcess(string executable, string[] arguments, string directory, int seconds, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (string variable in new[] { "APPDATA", "LOCALAPPDATA", "TEMP", "TMP" }) { string value = Path.Combine(directory, "profile", variable);Directory.CreateDirectory(value);start.Environment[variable] = value; }
        using var process = Process.Start(start) ?? throw new IOException("无法启动独立引擎样例。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var stdout = ReadBounded(process.StandardOutput);var stderr = ReadBounded(process.StandardError);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } await process.WaitForExitAsync().ConfigureAwait(false);if (cancel.IsCancellationRequested) throw;throw new TimeoutException("独立引擎样例运行超时。"); }
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
    private static async Task<string> ReadBounded(StreamReader reader)
    {
        var result = new StringBuilder();char[] chunk = new char[4096];int count;
        while ((count = await reader.ReadAsync(chunk).ConfigureAwait(false)) != 0) if (result.Length < 1024 * 1024) result.Append(chunk, 0, Math.Min(count, 1024 * 1024 - result.Length));
        return result.ToString();
    }
}
