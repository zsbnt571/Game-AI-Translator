using System.Security.Cryptography;
using GameAiTranslator.Runtime;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Text.Json;

string temporary = Path.Combine(Path.GetTempPath(), "GameAiTranslator-LocalDependency-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
string? priorRoot = Environment.GetEnvironmentVariable(LocalRuntimeDependency.RootEnvironmentVariable);
Environment.SetEnvironmentVariable(LocalRuntimeDependency.RootEnvironmentVariable, null);
int passed = 0, failed = 0;
byte[] fixture = System.Text.Encoding.UTF8.GetBytes("offline synthetic dependency fixture");
string sha = Convert.ToHexString(SHA256.HashData(fixture));
string file = Path.Combine(temporary, "fixture.bin");
File.WriteAllBytes(file, fixture);
try
{
    Test("missing classdata remains optional with an explicit warning", () =>
    {
        using var result = LocalRuntimeDependency.TryOpenClassData(temporary, out var warning);
        Check(result is null && warning?.Contains("classdata.tpk") == true && warning.Contains("自带字段结构"));
    });
    Test("missing Oodle has a specific dependency error", () =>
    {
        Expect<FileNotFoundException>(() => LocalRuntimeDependency.OpenOodle(temporary), "oo2core_9_win64.dll");
    });
    Test("pinned stream contains identical bytes and starts at zero", () =>
    {
        using var stream = LocalRuntimeDependency.OpenPinned(temporary, "fixture.bin", fixture.Length, sha);
        Check(stream.Position == 0);
        using var copy = new MemoryStream(); stream.CopyTo(copy);
        Check(copy.ToArray().SequenceEqual(fixture));
    });
    Test("same-length changed bytes are rejected by SHA", () =>
    {
        Expect<IOException>(() => LocalRuntimeDependency.OpenPinned(temporary, "fixture.bin", fixture.Length, new string('0', 64)), "integrity");
    });
    Test("wrong file length is rejected", () =>
    {
        Expect<IOException>(() => LocalRuntimeDependency.OpenPinned(temporary, "fixture.bin", fixture.Length + 1, sha), "integrity");
    });
    Test("traversal and rooted paths are rejected", () =>
    {
        foreach (var value in new[] { "../fixture.bin", "part/../fixture.bin", "part//fixture.bin", file })
            Expect<ArgumentException>(() => LocalRuntimeDependency.OpenPinned(temporary, value, fixture.Length, sha));
    });
    Test("invalid pin is rejected", () =>
    {
        Expect<ArgumentException>(() => LocalRuntimeDependency.OpenPinned(temporary, "fixture.bin", fixture.Length, "unreviewed"));
    });
    Test("wrong classdata is not passed to the parser", () =>
    {
        WritePackage(LocalRuntimeDependency.ClassData, fixture);
        using var result = LocalRuntimeDependency.TryOpenClassData(temporary, out var warning);
        Check(result is null && warning?.Contains("integrity") == true);
    });
    Test("wrong Oodle is not passed to the native loader", () =>
    {
        WritePackage(LocalRuntimeDependency.Oodle, fixture);
        Expect<IOException>(() => LocalRuntimeDependency.OpenOodle(temporary), "integrity");
    });
    Test("verified file remains locked against mutation on Windows", () =>
    {
        Check(OperatingSystem.IsWindows());
        using var stream = LocalRuntimeDependency.OpenPinned(temporary, "fixture.bin", fixture.Length, sha);
        Expect<IOException>(() => new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        Expect<IOException>(() => File.Move(file, file + ".moved"));
    });
    Test("a rejected dependency leaves no open handle", () =>
    {
        Expect<IOException>(() => LocalRuntimeDependency.OpenPinned(temporary, "fixture.bin", fixture.Length, new string('0', 64)));
        using var reopened = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check(reopened.Length == fixture.Length);
    });
    Test("a directory link above the base directory is rejected even for valid pinned bytes", () =>
    {
        string target = Path.Combine(temporary, "ancestor-target");
        string nested = Path.Combine(target, "ordinary-child");
        string link = Path.Combine(temporary, "ancestor-link");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "fixture.bin"), fixture);
        Directory.CreateSymbolicLink(link, target);
        try
        {
            string throughAncestor = Path.Combine(link, "ordinary-child");
            Check((File.GetAttributes(throughAncestor) & FileAttributes.ReparsePoint) == 0);
            Expect<IOException>(() => LocalRuntimeDependency.OpenPinned(throughAncestor, "fixture.bin", fixture.Length, sha), "Linked");
        }
        finally { Directory.Delete(link); }
    });
    if (args.Length == 2 && args[0] == "--local-dependencies")
    {
        // Explicit read-only fixture sources; no game files are read or executed.
        string suppliedRoot = Path.GetFullPath(args[1]);
        Test("pinned classdata produces identical parser output through path and verified stream", () =>
        {
            string source = Path.Combine(suppliedRoot, "Source", "Plugin", "UnityEmbedded", "Resources", "classdata.tpk");
            WritePackage(LocalRuntimeDependency.ClassData, File.ReadAllBytes(source));
            using var verified = LocalRuntimeDependency.TryOpenClassData(temporary, out var warning);
            Check(verified is not null && warning is null);
            var oldManager = new AssetsManager(); var newManager = new AssetsManager();
            try
            {
                var oldPackage = oldManager.LoadClassPackage(source);
                var newPackage = newManager.LoadClassPackage(verified!);
                using var oldOutput = new MemoryStream(); using var newOutput = new MemoryStream();
                oldPackage.Write(new AssetsFileWriter(oldOutput), ClassFileCompressionType.Uncompressed);
                newPackage.Write(new AssetsFileWriter(newOutput), ClassFileCompressionType.Uncompressed);
                Check(oldOutput.ToArray().SequenceEqual(newOutput.ToArray()));
            }
            finally { oldManager.UnloadAll(); newManager.UnloadAll(); }
        });
        Test("legally supplied pinned Oodle is accepted without executing it", () =>
        {
            string source = Path.Combine(suppliedRoot, "Source", "UnrealCatalog", "Native", "oo2core_9_win64.dll");
            WritePackage(LocalRuntimeDependency.Oodle, File.ReadAllBytes(source));
            using var verified = LocalRuntimeDependency.OpenOodle(temporary);
            Check(Convert.ToHexString(SHA256.HashData(verified)) == LocalRuntimeDependency.OodleSha256);
        });
    }
    else if (args.Length != 0) throw new ArgumentException("Use --local-dependencies <candidate-directory> or no arguments.");
}
finally
{
    Environment.SetEnvironmentVariable(LocalRuntimeDependency.RootEnvironmentVariable, priorRoot);
    // The path is a newly generated test-owned directory, never an application or game directory.
    Directory.Delete(temporary, true);
}
Console.WriteLine($"Local runtime dependency checks: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

void WritePackage(RuntimeDependencyDescriptor package, byte[] bytes)
{
    string target = Path.Combine(LocalRuntimeDependency.ResolveRoot(temporary), LocalRuntimeDependency.RelativePath(package));
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllBytes(target, bytes);
    File.WriteAllText(target + ".json", JsonSerializer.Serialize(package));
}

void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}
static void Check(bool result) { if (!result) throw new Exception("Assertion failed."); }
static void Expect<T>(Action action, string? message = null) where T : Exception
{
    try { action(); }
    catch (T ex) { if (message is null || ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) return; throw; }
    throw new Exception("Expected " + typeof(T).Name);
}
