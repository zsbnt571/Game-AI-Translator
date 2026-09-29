using System.Text.Json;
using ScreenshotTranslationUiTester;

// No real credentials, APIs, Python runtime, OCR package, or model is needed.
var directory = Path.Combine(Path.GetTempPath(), "game-ai-ocr-dependency-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var passed = new List<string>();
void Check(string name, bool condition) { if (!condition) throw new Exception("FAIL: " + name); passed.Add(name); }
try
{
    var runtime = OcrDependencyChecker.ResolveRuntimeRoot(directory);
    var python = OcrDependencyChecker.ResolvePython(runtime);
    var site = OcrDependencyChecker.ResolveSitePackages(runtime);
    Check("Runtime is fixed below app directory", runtime == Path.Combine(directory, "runtime"));
    Check("Python is fixed and cannot fall back to PATH", python == Path.Combine(runtime, "python", "python.exe"));
    Check("Package directory is fixed", site == Path.Combine(runtime, "venv", "Lib", "site-packages"));
    var missing = OcrDependencyChecker.InspectFiles(directory);
    Check("Absent runtime reports Missing", missing.State == OcrDependencyState.Missing && !missing.Ready);
    Check("Each required local file and package is reported", missing.Items.Count == 9);
    Check("Version requirements are visible when missing", missing.Items.Any(item => item.Component.Contains("3.11.9"))
        && missing.Items.Any(item => item.Component.Contains("3.9.2")) && missing.Items.Any(item => item.Component.Contains("1.28.0")));
    var checkedMissing = await OcrDependencyChecker.CheckAsync(directory, CancellationToken.None);
    Check("Async missing check returns without launching Python", checkedMissing.State == OcrDependencyState.Missing);
    Check("Missing check creates no runtime directory", !Directory.Exists(runtime));
    Check("External runtime root is refused", OcrDependencyChecker.InspectFiles(directory, Path.GetTempPath()).State == OcrDependencyState.CheckFailed);
    Check("Sibling-prefix root is refused", OcrDependencyChecker.InspectFiles(directory, directory + "-other").State == OcrDependencyState.CheckFailed);
    Check("Invalid root returns diagnostic without throwing", OcrDependencyChecker.InspectFiles("\0").State == OcrDependencyState.CheckFailed);

    string Probe(Action<Dictionary<string, object>>? edit = null)
    {
        var result = new Dictionary<string, object>
        {
            ["schema"] = 1, ["bits"] = 64, ["pythonVersion"] = "3.11.9", ["rapidocrVersion"] = "3.9.2", ["onnxruntimeVersion"] = "1.28.0",
            ["executable"] = python, ["prefix"] = Path.Combine(runtime, "python"), ["basePrefix"] = Path.Combine(runtime, "python"),
            ["rapidocrLocation"] = site, ["onnxruntimeLocation"] = site
        };
        edit?.Invoke(result); return JsonSerializer.Serialize(result);
    }
    Check("Exact supported versions and x64 parse Ready", OcrDependencyChecker.ParseProbe(Probe(), runtime).Ready);
    foreach (var field in new[] { "pythonVersion", "rapidocrVersion", "onnxruntimeVersion" })
    {
        Check(field + " mismatch is InvalidVersion", OcrDependencyChecker.ParseProbe(Probe(value => value[field] = "9.9.9"), runtime).State == OcrDependencyState.InvalidVersion);
        Check(field + " missing is Missing", OcrDependencyChecker.ParseProbe(Probe(value => value[field] = ""), runtime).State == OcrDependencyState.Missing);
    }
    Check("x86 runtime is WrongArchitecture", OcrDependencyChecker.ParseProbe(Probe(value => value["bits"] = 32), runtime).State == OcrDependencyState.WrongArchitecture);
    foreach (var field in new[] { "executable", "prefix", "basePrefix", "rapidocrLocation", "onnxruntimeLocation" })
        Check(field + " outside runtime is refused", !OcrDependencyChecker.ParseProbe(Probe(value => value[field] = Path.Combine(directory, "private-environment")), runtime).Ready);
    Check("Relative interpreter identity is refused", !OcrDependencyChecker.ParseProbe(Probe(value => value["executable"] = "python.exe"), runtime).Ready);
    Check("Unknown metadata schema is refused", OcrDependencyChecker.ParseProbe(Probe(value => value["schema"] = 2), runtime).State == OcrDependencyState.CheckFailed);
    Check("Malformed JSON returns diagnostic", OcrDependencyChecker.ParseProbe("not json", runtime).State == OcrDependencyState.CheckFailed);
    Check("Incomplete JSON returns diagnostic", OcrDependencyChecker.ParseProbe("{}", runtime).State == OcrDependencyState.CheckFailed);
    Check("Nonobject JSON returns diagnostic", OcrDependencyChecker.ParseProbe("[]", runtime).State == OcrDependencyState.CheckFailed);
    Environment.SetEnvironmentVariable("PYTHONPATH", Path.Combine(directory, "unrelated-python"));
    Environment.SetEnvironmentVariable("ST_FUSION_RUNTIME_ROOT", Path.Combine(directory, "unrelated-runtime"));
    Environment.SetEnvironmentVariable("VIRTUAL_ENV", Path.Combine(directory, "unrelated-venv"));
    Environment.SetEnvironmentVariable("CONDA_PREFIX", Path.Combine(directory, "unrelated-conda"));
    var start = OcrDependencyChecker.CreateProbeStartInfo(directory, runtime);
    Check("Explicit interpreter executable only", start.FileName == python);
    Check("Probe ignores site and inherited import paths", start.ArgumentList.Take(3).SequenceEqual(new[] { "-I", "-S", "-B" }));
    Check("Probe child removes private Python overrides", !start.Environment.ContainsKey("PYTHONPATH") && !start.Environment.ContainsKey("ST_FUSION_RUNTIME_ROOT"));
    Check("Probe ignores inherited virtual environments", !start.Environment.ContainsKey("VIRTUAL_ENV") && !start.Environment.ContainsKey("CONDA_PREFIX"));
    Check("Probe does not inherit developer PATH", start.Environment["PATH"] == string.Join(Path.PathSeparator, Path.GetDirectoryName(python), Environment.SystemDirectory));
    Check("Probe starts hidden without shell", start.CreateNoWindow && !start.UseShellExecute);

    foreach (var relative in new[] { "runtime/python/python.exe", "workers/ocr_dependency_probe.py", "workers/rapid_worker.py", "workers/rapid_health.py" })
    {
        var path = Path.Combine(directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "inert offline fixture");
    }
    Directory.CreateDirectory(Path.Combine(site, "onnxruntime"));
    foreach (var model in OcrDependencyChecker.Models)
    {
        var path = Path.Combine(site, "rapidocr", "models", model.Name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "synthetic invalid model");
    }
    Check("Present files are not falsely marked Ready", OcrDependencyChecker.InspectFiles(directory).State == OcrDependencyState.NotChecked);
    Check("Corrupt models fail exact SHA-256", OcrDependencyChecker.ValidateModels(runtime, CancellationToken.None).All(item => item.State == OcrDependencyState.HashMismatch));
    Check("Corrupt model prevents Python execution", (await OcrDependencyChecker.CheckAsync(directory, CancellationToken.None)).State == OcrDependencyState.HashMismatch);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    Check("Cancellation returns nonthrowing diagnostic", (await OcrDependencyChecker.CheckAsync(directory, cancelled.Token)).State == OcrDependencyState.CheckFailed);

    var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(path => path).ToArray();
    await OcrDependencyChecker.CheckAsync(directory, CancellationToken.None);
    Check("Dependency checks create no repair or download files", before.SequenceEqual(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(path => path)));
    Console.WriteLine(JsonSerializer.Serialize(new { success = true, checks = passed.Count, passed,
        boundary = "Synthetic offline dependency validation only; no real OCR recognition or package redistributability claim." }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception error)
{
    Console.WriteLine(JsonSerializer.Serialize(new { success = false, checks = passed.Count, passed, failure = error.Message }));
    return 1;
}
finally
{
    // This directory is uniquely created by this test and never points at user OCR files.
    var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var actual = Path.GetFullPath(directory);
    if (!actual.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(actual).StartsWith("game-ai-ocr-dependency-tests-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing cleanup outside the owned test directory.");
    Directory.Delete(actual, recursive: true);
}
