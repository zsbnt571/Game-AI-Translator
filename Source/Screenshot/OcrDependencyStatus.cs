using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public enum OcrDependencyState { Ready, Missing, InvalidVersion, WrongArchitecture, HashMismatch, NotChecked, CheckFailed }
public sealed record OcrDependencyItem(string Component, OcrDependencyState State, string Detail);
public sealed record OcrDependencyReport(OcrDependencyState State, string Detail, string RuntimeRoot,
    IReadOnlyList<OcrDependencyItem> Items)
{
    public bool Ready => State == OcrDependencyState.Ready;
}

/// <summary>Checks only explicitly located, user-provided OCR files. Never installs or downloads anything.</summary>
public static class OcrDependencyChecker
{
    public const string PythonVersion = "3.11.9";
    public const string RapidOcrVersion = "3.9.2";
    public const string OnnxRuntimeVersion = "1.28.0";
    internal static readonly (string Name, string Sha256)[] Models =
    [
        ("PP-OCRv6_det_small.onnx", "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f"),
        ("ch_ppocr_mobile_v2.0_cls_mobile.onnx", "e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c"),
        ("PP-OCRv6_rec_small.onnx", "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884")
    ];

    public static string ResolveRuntimeRoot(string applicationRoot) => Path.Combine(Path.GetFullPath(applicationRoot), "runtime");
    public static string ResolvePython(string runtimeRoot) => Path.Combine(runtimeRoot, "python", "python.exe");
    public static string ResolveSitePackages(string runtimeRoot) => Path.Combine(runtimeRoot, "venv", "Lib", "site-packages");

    public static OcrDependencyReport InspectFiles(string applicationRoot, string? runtimeRoot = null)
    {
        var root = runtimeRoot ?? "";
        try
        {
            root = runtimeRoot ?? ResolveRuntimeRoot(applicationRoot);
            root = Path.GetFullPath(root);
            if (!IsWithin(root, applicationRoot))
                return Report(root, [new("OCR runtime", OcrDependencyState.CheckFailed, "OCR 目录必须位于程序目录内。")]);
            var files = new List<(string Component, string Path)>
            {
                ($"Python {PythonVersion} x64", ResolvePython(root)),
                ("OCR dependency probe", Path.Combine(applicationRoot, "workers", "ocr_dependency_probe.py")),
                ("RapidOCR worker", Path.Combine(applicationRoot, "workers", "rapid_worker.py")),
                ("RapidOCR health check", Path.Combine(applicationRoot, "workers", "rapid_health.py"))
            };
            files.AddRange(Models.Select(model => (model.Name, Path.Combine(ResolveSitePackages(root), "rapidocr", "models", model.Name))));
            var items = new List<OcrDependencyItem>();
            foreach (var file in files)
            {
                EnsureNoLinks(file.Path);
                items.Add(new(file.Component, File.Exists(file.Path) ? OcrDependencyState.NotChecked : OcrDependencyState.Missing,
                    File.Exists(file.Path) ? "文件存在，尚未验证。" : "Missing：" + Path.GetRelativePath(applicationRoot, file.Path)));
            }
            foreach (var package in new[] { ("RapidOCR", RapidOcrVersion, "rapidocr"), ("ONNX Runtime", OnnxRuntimeVersion, "onnxruntime") })
            {
                var path = Path.Combine(ResolveSitePackages(root), package.Item3);
                EnsureNoLinks(path);
                items.Add(new(package.Item1 + " " + package.Item2,
                    Directory.Exists(path) ? OcrDependencyState.NotChecked : OcrDependencyState.Missing,
                    Directory.Exists(path) ? "目录存在，尚未验证版本。" : "Missing：" + Path.GetRelativePath(applicationRoot, path)));
            }
            return Report(root, items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Report(root, [new("OCR runtime", OcrDependencyState.CheckFailed, "无法安全读取 OCR 依赖：" + ex.GetType().Name)]);
        }
    }

    public static async Task<OcrDependencyReport> CheckAsync(string applicationRoot, CancellationToken token,
        string? runtimeRoot = null)
    {
        var presence = InspectFiles(applicationRoot, runtimeRoot);
        if (presence.State != OcrDependencyState.NotChecked) return presence;
        try
        {
            var models = await Task.Run(() => ValidateModels(presence.RuntimeRoot, token), token).ConfigureAwait(false);
            if (models.Any(item => item.State != OcrDependencyState.Ready)) return Report(presence.RuntimeRoot, models);
            var info = CreateProbeStartInfo(applicationRoot, presence.RuntimeRoot);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var process = new Process { StartInfo = info };
            if (!process.Start()) return Report(presence.RuntimeRoot, [new("Python", OcrDependencyState.CheckFailed, "Python 检查进程未启动。")]);
            try
            {
                var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
                var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).ConfigureAwait(false);
                if (process.ExitCode != 0)
                    return Report(presence.RuntimeRoot, [new("Python", OcrDependencyState.CheckFailed, "Python 检查失败；未标记就绪，未下载依赖。")]);
                var metadata = ParseProbe(stdout.Result, presence.RuntimeRoot);
                return Report(presence.RuntimeRoot, metadata.Items.Concat(models).ToArray());
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            return Report(presence.RuntimeRoot, [new("OCR runtime", OcrDependencyState.CheckFailed,
                token.IsCancellationRequested ? "检查已取消，未标记就绪。" : "本地 Python 检查超时，未标记就绪。")]);
        }
        catch (Exception ex)
        {
            return Report(presence.RuntimeRoot, [new("OCR runtime", OcrDependencyState.CheckFailed,
                "本地 OCR 检查未通过：" + ex.GetType().Name + "。未下载依赖。")]);
        }
    }

    internal static IReadOnlyList<OcrDependencyItem> ValidateModels(string root, CancellationToken token)
    {
        var items = new List<OcrDependencyItem>();
        foreach (var model in Models)
        {
            token.ThrowIfCancellationRequested();
            var file = Path.Combine(ResolveSitePackages(root), "rapidocr", "models", model.Name);
            EnsureNoLinks(file);
            if (!File.Exists(file)) { items.Add(new(model.Name, OcrDependencyState.Missing, "Missing：" + model.Name)); continue; }
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var valid = Convert.ToHexString(SHA256.HashData(input)).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase);
            items.Add(new(model.Name, valid ? OcrDependencyState.Ready : OcrDependencyState.HashMismatch,
                valid ? "SHA-256 校验通过。" : "Hash mismatch：" + model.Name));
        }
        return items;
    }

    internal static ProcessStartInfo CreateProbeStartInfo(string applicationRoot, string runtimeRoot)
    {
        var info = new ProcessStartInfo(ResolvePython(runtimeRoot))
        {
            WorkingDirectory = runtimeRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("-I"); info.ArgumentList.Add("-S"); info.ArgumentList.Add("-B");
        info.ArgumentList.Add(Path.Combine(applicationRoot, "workers", "ocr_dependency_probe.py"));
        info.ArgumentList.Add(ResolveSitePackages(runtimeRoot));
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ST_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment.Remove("VIRTUAL_ENV"); info.Environment.Remove("CONDA_PREFIX");
        info.Environment["PATH"] = string.Join(Path.PathSeparator, Path.GetDirectoryName(ResolvePython(runtimeRoot)), Environment.SystemDirectory);
        return info;
    }

    internal static OcrDependencyReport ParseProbe(string json, string runtimeRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string Read(string name) => root.GetProperty(name).GetString() ?? "";
            if (root.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unknown probe schema.");
            var items = new List<OcrDependencyItem>();
            var architecture = root.GetProperty("bits").GetInt32();
            items.Add(new("Python architecture", architecture == 64 ? OcrDependencyState.Ready : OcrDependencyState.WrongArchitecture,
                architecture == 64 ? "x64" : "Wrong architecture：需要 x64 Python。"));
            foreach (var identity in new[] { ("Python", "pythonVersion", PythonVersion), ("RapidOCR", "rapidocrVersion", RapidOcrVersion), ("ONNX Runtime", "onnxruntimeVersion", OnnxRuntimeVersion) })
            {
                var found = Read(identity.Item2);
                var state = string.IsNullOrEmpty(found) ? OcrDependencyState.Missing : found == identity.Item3 ? OcrDependencyState.Ready : OcrDependencyState.InvalidVersion;
                items.Add(new(identity.Item1, state, state == OcrDependencyState.Ready ? found
                    : state == OcrDependencyState.Missing ? "Missing：" + identity.Item1
                    : $"Invalid version：{identity.Item1} 需要 {identity.Item3}，实际 {found}。"));
            }
            foreach (var property in new[] { "executable", "prefix", "basePrefix", "rapidocrLocation", "onnxruntimeLocation" })
            {
                var path = Read(property);
                if (string.IsNullOrEmpty(path) && property.EndsWith("Location", StringComparison.Ordinal)) continue;
                if (!Path.IsPathFullyQualified(path) || !IsWithin(path, runtimeRoot))
                    items.Add(new(property, OcrDependencyState.CheckFailed, "依赖指向约定运行目录之外，拒绝使用。"));
                else EnsureNoLinks(path);
            }
            return Report(runtimeRoot, items);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return Report(runtimeRoot, [new("OCR metadata", OcrDependencyState.CheckFailed, "无法验证本地 OCR 元数据。")]);
        }
    }

    private static OcrDependencyReport Report(string root, IReadOnlyList<OcrDependencyItem> items)
    {
        var error = items.FirstOrDefault(item => item.State is not (OcrDependencyState.Ready or OcrDependencyState.NotChecked));
        var state = error?.State ?? (items.All(item => item.State == OcrDependencyState.Ready) ? OcrDependencyState.Ready : OcrDependencyState.NotChecked);
        var detail = error is null ? state == OcrDependencyState.Ready
            ? "Ready：Python、RapidOCR、ONNX Runtime 版本及 3 个模型校验通过；实际识别可单独检查。"
            : "依赖文件存在，需检查版本、位数和 SHA-256；尚未标记 Ready。"
            : string.Join("；", items.Where(item => item.State is not (OcrDependencyState.Ready or OcrDependencyState.NotChecked)).Select(item => item.Detail));
        return new(state, detail, root, items);
    }

    private static bool IsWithin(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        return full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Runtime dependency links are not allowed.");
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (result.Length + length > 65536) throw new InvalidDataException("OCR probe output exceeded its limit.");
            result.Append(buffer, 0, length);
        }
        return result.ToString();
    }
}
