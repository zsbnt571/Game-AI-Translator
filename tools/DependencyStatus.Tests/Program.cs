using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAiTranslator.Runtime;
using ScreenshotTranslationUiTester;

string scratch = Path.Combine(Path.GetTempPath(), "GameAiTranslator-DependencyStatus-" + Guid.NewGuid().ToString("N"));
string? previous = Environment.GetEnvironmentVariable(LocalRuntimeDependency.RootEnvironmentVariable);
var results = new List<string>();
Directory.CreateDirectory(scratch);
try
{
    string missing = Path.Combine(scratch, "missing");
    Environment.SetEnvironmentVariable(LocalRuntimeDependency.RootEnvironmentVariable, missing);
    Check(RuntimePayloadProvider.GetOverview().Count == 8 && RuntimePayloadProvider.GetOverview().All(p => p.State == DependencyState.Missing), "empty explicit root reports all eight missing");
    Check(!Directory.Exists(missing), "status inspection creates no dependency directory");
    Check(RuntimePayloadProvider.Check("unknown").State == DependencyState.Invalid, "unknown package is rejected without throwing");
    Check(RuntimePayloadProvider.Check("unity-mono-x64", "Unity", "Mono", "x64", "old").State == DependencyState.InvalidVersion, "wrong requested version is explicit");
    Check(RuntimePayloadProvider.Check("unity-mono-x64", "Unity", "Mono", "x86", RuntimePayloadProvider.PackageVersion).State == DependencyState.WrongArchitecture, "wrong requested architecture is explicit");
    Check(RuntimePayloadProvider.Check("unity-mono-x64", "Unity", "IL2CPP", "x64", RuntimePayloadProvider.PackageVersion).State == DependencyState.WrongBackend, "wrong requested backend is explicit");
    Check(RuntimePayloadProvider.GetRequiredFor("Unity", "Mono", "x86").Single().Id == "unity-mono-x86", "generic Mono x86 selection");
    Check(RuntimePayloadProvider.GetRequiredFor("Unity", "Mono", "x64", "unity-specialized").Single().Id == "unity-specialized", "existing specialized selection is respected");
    Check(RuntimePayloadProvider.GetRequiredFor("Unity", "Mono", "x64", "unity-legacy").Single().Id == "unity-legacy", "existing legacy selection is respected");
    Check(RuntimePayloadProvider.GetRequiredFor("Unity", "IL2CPP", "x64").Single().Id == "unity-il2cpp-x64", "IL2CPP x64 selection");
    Check(RuntimePayloadProvider.GetRequiredFor("Unity", "IL2CPP", "x86").Single().State == DependencyState.WrongArchitecture, "unsupported IL2CPP architecture");
    Check(RuntimePayloadProvider.GetRequiredFor("Unity", "Other", "x64").Single().State == DependencyState.WrongBackend, "unknown Unity backend");
    Check(RuntimePayloadProvider.GetRequiredFor("Unreal", "Native", "x64").Single().Id == "unreal-runtime", "Unreal required runtime selection");
    foreach (string engine in new[] { "RPG Maker", "RenPy", "Godot" }) Check(RuntimePayloadProvider.GetRequiredFor(engine, "", "").Single().State == DependencyState.NotRequired, engine + " does not require external runtime packages");
    Check(RuntimePayloadProvider.GetRequiredFor("Unknown engine", "", "").Single().State == DependencyState.Invalid, "unknown engine does not claim dependencies are unnecessary");
    Environment.SetEnvironmentVariable(LocalRuntimeDependency.RootEnvironmentVariable, "relative-root");
    Check(RuntimePayloadProvider.GetOverview().All(p => p.State == DependencyState.Invalid), "invalid root does not throw from overview");
    Environment.SetEnvironmentVariable(LocalRuntimeDependency.RootEnvironmentVariable, null);
    Check(LocalRuntimeDependency.ResolveRoot(scratch) == Path.Combine(scratch, "runtime-payloads"), "default root is application-local");

    byte[] bytes = Encoding.UTF8.GetBytes("synthetic offline dependency");
    var pin = new RuntimeDependencyDescriptor("fixture", "Unity", "Mono", "x64", "test-v1", "fixture.bin", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    string relative = LocalRuntimeDependency.RelativePath(pin), path = Path.Combine(scratch, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
    void Descriptor(RuntimeDependencyDescriptor value) => File.WriteAllText(path + ".json", JsonSerializer.Serialize(value));
    DependencyState State() => LocalRuntimeDependency.Check(scratch, pin).State;
    Check(State() == DependencyState.Missing, "missing sidecar is missing");
    Descriptor(pin);
    Check(State() == DependencyState.Ready, "exact synthetic pinned bytes are ready");
    using (var input = LocalRuntimeDependency.OpenVerified(scratch, pin))
    {
        Check(input.Position == 0 && input.Length == bytes.Length, "verified stream is reset and unchanged");
        if (OperatingSystem.IsWindows())
        {
            bool locked = false;
            try { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) { locked = true; }
            Check(locked, "verified handle prevents concurrent mutation");
        }
    }
    Descriptor(pin with { Version = "old" }); Check(State() == DependencyState.InvalidVersion, "wrong sidecar version");
    Descriptor(pin with { Architecture = "x86" }); Check(State() == DependencyState.WrongArchitecture, "wrong sidecar architecture");
    Descriptor(pin with { Backend = "IL2CPP" }); Check(State() == DependencyState.WrongBackend, "wrong sidecar backend");
    Descriptor(pin with { Sha256 = new string('0', 64) }); Check(State() == DependencyState.HashMismatch, "sidecar cannot approve its own hash");
    Descriptor(pin with { Id = "other" }); Check(State() == DependencyState.Invalid, "wrong identity");
    Descriptor(pin with { FileName = "other.bin" }); Check(State() == DependencyState.Invalid, "wrong filename");
    File.WriteAllText(path + ".json", "{"); Check(State() == DependencyState.Invalid, "malformed metadata is invalid without throwing");
    File.WriteAllText(path + ".json", new string('x', 16385)); Check(State() == DependencyState.Invalid, "oversized metadata is invalid");
    Descriptor(pin); File.WriteAllBytes(path, bytes.Reverse().ToArray()); Check(State() == DependencyState.HashMismatch, "same-size mutated bytes rejected");
    File.WriteAllBytes(path, []); Check(State() == DependencyState.HashMismatch, "wrong file size rejected");
    File.WriteAllBytes(path, bytes);
    string link = Path.Combine(scratch, "link");
    Directory.CreateSymbolicLink(link, Path.GetDirectoryName(path)!);
    try { Check(LocalRuntimeDependency.Check(scratch, pin with { Id = "link", Version = "." }).State == DependencyState.Invalid, "traversal-like identity rejected"); }
    finally { Directory.Delete(link); }

    if (args.Length == 2 && args[0] == "--payload-root")
    {
        Environment.SetEnvironmentVariable(LocalRuntimeDependency.RootEnvironmentVariable, Path.GetFullPath(args[1]));
        foreach (var package in RuntimePayloadProvider.GetOverview()) Check(package.State == DependencyState.Ready, package.Id + " exact lawful local dependency passes identity and SHA");
    }
    else if (args.Length != 0) throw new ArgumentException("Use --payload-root <explicit-local-root> or no arguments.");
    Console.WriteLine(JsonSerializer.Serialize(new { success = true, passed = results.Count, checks = results }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
finally
{
    Environment.SetEnvironmentVariable(LocalRuntimeDependency.RootEnvironmentVariable, previous);
    // Only this invocation's generated test-owned directory is removed.
    Directory.Delete(scratch, true);
}
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    results.Add(message); Console.WriteLine("PASS " + message);
}
