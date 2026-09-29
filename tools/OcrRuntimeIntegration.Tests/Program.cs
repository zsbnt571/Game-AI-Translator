using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length == 1)
{
    var target = Path.GetFullPath(args[0]);
    if (!File.Exists(target)) throw new FileNotFoundException("Pass the built candidate GameTranslator.dll.");
    var stage = Path.Combine(Path.GetTempPath(), "game-ai-ocr-integration-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(stage);
    try
    {
        var ownAssembly = Assembly.GetExecutingAssembly().Location;
        var stem = Path.GetFileNameWithoutExtension(ownAssembly);
        foreach (var suffix in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, stem + suffix), Path.Combine(stage, stem + suffix));
        var info = new ProcessStartInfo(Path.Combine(stage, stem + ".exe"))
        {
            WorkingDirectory = stage, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("--child"); info.ArgumentList.Add(target);
        foreach (var name in info.Environment.Keys.Where(name => name.StartsWith("ST_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GAME_AI_TRANSLATOR", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(name);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start isolated test host.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            Console.Write(stdout.Result);
            Console.Error.Write(stderr.Result);
            return process.ExitCode;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
    finally
    {
        var actual = Path.GetFullPath(stage);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!actual.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(actual).StartsWith("game-ai-ocr-integration-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the owned test directory.");
        Directory.Delete(actual, recursive: true);
    }
}
if (args.Length != 2 || args[0] != "--child")
{
    Console.Error.WriteLine("Usage: OcrRuntimeIntegration.Tests <built-candidate-GameTranslator.dll>");
    return 2;
}
var checks = new List<string>();
void Check(string name, bool condition) { if (!condition) throw new Exception("FAIL: " + name); checks.Add(name); }
var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
try
{
    var applicationRoot = Path.GetFullPath(AppContext.BaseDirectory);
    Check("Tests run in an isolated owned application directory", Path.GetFileName(applicationRoot.TrimEnd(Path.DirectorySeparatorChar)).StartsWith("game-ai-ocr-integration-", StringComparison.Ordinal));
    var context = new CandidateContext(Path.GetFullPath(args[1]));
    var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
    Type Type(string name) => assembly.GetType("ScreenshotTranslationUiTester." + name, throwOnError: true)!;
    var runtime = Type("FusionRuntime");
    var initialize = runtime.GetMethod("Initialize", flags)!;
    var rootProperty = runtime.GetProperty("Root", flags)!;
    var warningProperty = runtime.GetProperty("ConfigurationWarning", flags)!;
    string Root() => (string)rootProperty.GetValue(null)!;
    string Warning() => (string)warningProperty.GetValue(null)!;
    void Initialize() => initialize.Invoke(null, null);
    var defaultRoot = Path.Combine(applicationRoot, "runtime");
    var config = Path.Combine(applicationRoot, "dependencies.json");
    Check("No existing configuration is read", !File.Exists(config));
    Environment.SetEnvironmentVariable("ST_FUSION_RUNTIME_ROOT", Path.Combine(applicationRoot, "unrelated-private-root"));
    Initialize();
    Check("Absent config defaults to app/runtime", Root() == defaultRoot && Warning() == "");
    Check("Inherited runtime environment does not select a directory", Environment.GetEnvironmentVariable("ST_FUSION_RUNTIME_ROOT") == defaultRoot);
    Check("Initialize does not create missing runtime", !Directory.Exists(defaultRoot));

    foreach (var variant in new[]
    {
        ("Malformed JSON", "{"),
        ("Absolute directory", JsonSerializer.Serialize(new { RuntimeRoot = Path.Combine(applicationRoot, "absolute-local-directory") })),
        ("Escaping relative directory", JsonSerializer.Serialize(new { RuntimeRoot = "../outside-runtime" })),
        ("Missing field", "{}"),
        ("Nonstring field", "{\"RuntimeRoot\":42}"),
        ("Oversized file", new string(' ', 16385))
    })
    {
        File.WriteAllText(config, variant.Item2);
        Initialize();
        Check(variant.Item1 + " safely falls back with warning", Root() == defaultRoot && Warning().Length > 0);
    }
    File.WriteAllText(config, "{\"RuntimeRoot\":\"local-ocr\"}");
    Initialize();
    Check("Explicit app-contained relative directory is accepted", Root() == Path.Combine(applicationRoot, "local-ocr") && Warning() == "");
    Check("Custom root is not created by checking configuration", !Directory.Exists(Root()));
    File.Delete(config);
    Initialize();
    Check("Reinitialization resets to safe default", Root() == defaultRoot && Warning() == "");

    var visionRoot = Path.Combine(applicationRoot, "vision-runtime");
    Directory.CreateDirectory(visionRoot);
    var outsideVision = Path.Combine(applicationRoot, "unrelated-vision");
    Directory.CreateDirectory(outsideVision);
    File.WriteAllText(Path.Combine(visionRoot, "location.txt"), outsideVision);
    var operations = Type("ModelManagerOperations");
    var resolvedVision = (string)operations.GetMethod("ResolveVisionRoot", flags)!.Invoke(null, [applicationRoot])!;
    Check("Legacy vision location pointer is ignored", resolvedVision == visionRoot);
    var resolvedPython = (string)operations.GetMethod("ResolveRapidPython", flags)!.Invoke(null, [applicationRoot])!;
    Check("Python selection is fixed under runtime", resolvedPython == Path.Combine(defaultRoot, "python", "python.exe"));

    var managerType = Type("OcrRuntimeManager");
    var service = Activator.CreateInstance(Type("OcrService"));
    var manager = Activator.CreateInstance(managerType, [applicationRoot, service])!;
    await using var disposable = (IAsyncDisposable)manager;
    var rapid = Enum.Parse(Type("OcrEngineKind"), "Rapid");
    var statusType = Type("OcrRuntimeStatus");
    bool Ready(object value) => (bool)statusType.GetProperty("Ready")!.GetValue(value)!;
    string Code(object value) => (string)statusType.GetProperty("Code")!.GetValue(value)!;
    var cache = managerType.GetField("_lastRapidCheck", flags)!;
    var previousReady = Activator.CreateInstance(statusType, [rapid, true, "RapidOCR", "Synthetic previously verified state", "Ready"]);
    cache.SetValue(manager, previousReady);
    var status = managerType.GetMethod("GetStatus")!.Invoke(manager, [rapid])!;
    Check("Missing dependency invalidates cached Ready", !Ready(status) && Code(status) == "Missing");
    Check("Failed cached state replaces previous Ready", cache.GetValue(manager) == status);
    var task = (Task)managerType.GetMethod("CheckAsync")!.Invoke(manager, [rapid, CancellationToken.None])!;
    await task;
    var checkedStatus = task.GetType().GetProperty("Result")!.GetValue(task)!;
    Check("Full manager missing check returns without throwing", !Ready(checkedStatus) && Code(checkedStatus) == "Missing");
    Check("No OCR worker starts for missing environment", managerType.GetProperty("ActiveWorkerPid")!.GetValue(manager) is null);
    Check("Missing OCR check creates no runtime directory", !Directory.Exists(defaultRoot));
    var repairTask = (Task)managerType.GetMethod("RepairModelsAsync")!.Invoke(manager, [true, CancellationToken.None])!;
    await repairTask;
    var repairStatus = repairTask.GetType().GetProperty("Result")!.GetValue(repairTask)!;
    Check("Manager refuses network repair without worker launch", Code(repairStatus) == "LOCAL_DEPENDENCY_REQUIRED"
        && managerType.GetProperty("ActiveWorkerPid")!.GetValue(manager) is null);
    Console.WriteLine(JsonSerializer.Serialize(new { success = true, count = checks.Count, checks,
        candidateSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[1]))),
        boundary = "Isolated reflection checks of runtime configuration and missing-OCR state; no OCR engine or real API was executed." }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception error)
{
    Console.WriteLine(JsonSerializer.Serialize(new { success = false, count = checks.Count, checks, failure = error.ToString() }));
    return 1;
}

sealed class CandidateContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    public CandidateContext(string path) : base("OCR integration candidate", isCollectible: false) => _resolver = new(path);
    protected override Assembly? Load(AssemblyName name)
    {
        var file = _resolver.ResolveAssemblyToPath(name);
        return file is null ? null : LoadFromAssemblyPath(file);
    }
}
