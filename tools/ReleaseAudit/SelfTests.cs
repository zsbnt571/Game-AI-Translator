using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Text;
using System.Text.Json;

namespace ReleaseAudit;

internal static class SelfTests
{
    internal static int Run(string output)
    {
        string root = Path.Combine(Path.GetFullPath(output), "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var results = new List<object>();
        int failures = 0;
        void Test(string name, Action<Fixture> arrange, string? expectedFailure)
        {
            var fixture = new Fixture(Path.Combine(root, name));
            try
            {
                arrange(fixture);
                var actual = Auditor.Run(fixture.Root, fixture.List, fixture.Policy, fixture.Local);
                bool passed = expectedFailure == null ? actual.Passed : !actual.Passed && actual.Violations.Any(v => v.Contains(expectedFailure, StringComparison.Ordinal));
                if (!passed) failures++;
                results.Add(new { name, passed, expectedFailure, auditPassed = actual.Passed, actual.Violations });
                Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
            }
            catch (Exception e)
            {
                failures++;
                results.Add(new { name, passed = false, error = e.ToString() });
                Console.WriteLine($"FAIL {name}: {e.GetType().Name}");
            }
        }
        Test("approved-text-and-empty-assembly", f => f.AddAssembly("Main.dll", []), null);
        Test("approved-source-resource", f => f.AddAssembly("Main.dll", [("Bridge.js", Encoding.UTF8.GetBytes("// own source\nexport {};"))]), null);
        Test("unknown-dll", f => File.WriteAllBytes(Path.Combine(f.Root, "Unknown.dll"), MakeAssembly([])), "Unknown file");
        Test("unknown-model", f => File.WriteAllBytes(Path.Combine(f.Root, "model.onnx"), [8, 7, 18, 0]), "Unknown file");
        Test("unknown-font", f => File.WriteAllBytes(Path.Combine(f.Root, "font.ttf"), [0, 1, 0, 0, 4, 5]), "Unknown file");
        Test("unknown-zip", f => File.WriteAllBytes(Path.Combine(f.Root, "extras.zip"), [80, 75, 3, 4]), "Unknown file");
        Test("missing-license", f => File.Delete(Path.Combine(f.Root, "LICENSE")), "Required file absent");
        Test("cannot-remove-mandatory-license-rule", f => { f.List.RequiredFiles.Clear(); f.List.Files.RemoveAll(x => x.Path == "LICENSE"); File.Delete(Path.Combine(f.Root, "LICENSE")); }, "Required file absent");
        Test("file-hash-changed", f => File.AppendAllText(Path.Combine(f.Root, "LICENSE"), "changed"), "File hash mismatch");
        Test("exclude-hash-under-innocent-name", f => { byte[] data = [1, 3, 7, 9]; f.AddFile("data.bin", data); f.Local.Files.Add(new() { Path = "private/dependency.bin", Sha256 = Auditor.Hash(data), ReleaseApproved = false }); }, "EXCLUDE fingerprint");
        Test("zip-renamed-as-text-resource", f => f.AddAssembly("Main.dll", [("Innocent.txt", new byte[] { 80, 75, 3, 4, 0, 0, 0, 0 })]), "Binary embedded resource forbidden (zip)");
        Test("pe-renamed-as-text-resource", f => f.AddAssembly("Main.dll", [("Readme.txt", MakeAssembly([]))]), "Binary embedded resource forbidden (pe)");
        Test("gzip-renamed-resource", f => f.AddAssembly("Main.dll", [("Data.txt", new byte[] { 31, 139, 8, 0, 1 })]), "Binary embedded resource forbidden (gzip)");
        Test("font-renamed-resource", f => f.AddAssembly("Main.dll", [("Data.txt", new byte[] { 0, 1, 0, 0, 5, 6 })]), "Binary embedded resource forbidden (font)");
        Test("unreviewed-model-resource", f => f.AddAssembly("Main.dll", [("Weights.bin", new byte[] { 8, 7, 18, 0 })]), "Binary embedded resource forbidden (unreviewed-binary)");
        Test("forbidden-original-resource-name", f => f.AddAssembly("Main.dll", [("Fusion.UnrealRuntime.zip", Encoding.UTF8.GetBytes("not even a ZIP"))]), "EXCLUDE resource");
        Test("unlisted-managed-resources", f => { f.AddAssembly("Main.dll", [("Hidden", Encoding.UTF8.GetBytes("source"))]); f.List.Assemblies[0].Resources.Clear(); }, "Unknown/changed resource");
        Test("managed-assembly-without-resource-review", f => f.AddFile("Main.dll", MakeAssembly([])), "Managed assembly lacks resource review");
        Test("resource-hash-changed", f => { f.AddAssembly("Main.dll", [("Bridge.js", Encoding.UTF8.GetBytes("source"))]); f.List.Assemblies[0].Resources[0].Sha256 = new('0', 64); }, "Unknown/changed resource");
        Test("exclude-resource-hash", f => { byte[] data = Encoding.UTF8.GetBytes("excluded resource"); f.AddAssembly("Main.dll", [("Bridge.js", data)]); f.List.ExcludedSha256.Add(Auditor.Hash(data)); }, "EXCLUDE resource");
        Test("dotnet-string-resource", f => f.AddAssembly("Main.dll", [("Ui.resources", MakeResources("hello", "safe"))], "dotnet-resources"), null);
        Test("dotnet-nested-zip", f => f.AddAssembly("Main.dll", [("Ui.resources", MakeResources("zip", new byte[] { 80, 75, 3, 4, 0, 0, 0, 0 }))], "dotnet-resources"), "Forbidden nested payload (zip)");
        Test("dotnet-nested-unreviewed-binary", f => f.AddAssembly("Main.dll", [("Ui.resources", MakeResources("image", new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))], "dotnet-resources"), "Unreviewed serialized/binary resource value");
        Test("allowlist-parent-path-traversal", f => f.List.Files.Add(new() { Path = "../escape.dll" }), "Unsafe allowlist path");
        Test("allowlist-rooted-path", f => f.List.Files.Add(new() { Path = "C:/private.bin" }), "Unsafe allowlist path");
        Test("allowlist-alternate-data-stream", f => f.List.Files.Add(new() { Path = "LICENSE:secret" }), "Unsafe allowlist path");
        Test("allowlist-glob-rejected", f => f.List.Files.Add(new() { Path = "*.dll" }), "Unsafe allowlist path");
        Test("oodle-always-excluded", f => f.AddFile("oo2core_9_win64.dll", Encoding.UTF8.GetBytes("not a game DLL")), "Forbidden filename");
        Test("classdata-always-excluded", f => f.AddFile("classdata.tpk", [7, 7, 7]), "Forbidden filename");
        Test("unapproved-third-party", f => { var file = f.AddFile("ThirdParty.dll", MakeAssembly([])); file.Kind = "thirdParty"; file.Component = "Unknown"; file.Version = "1"; }, "Third-party policy forbids file");
        Test("exclude-policy-cannot-be-overridden", f => { var file = f.AddThirdParty("EXCLUDE", true); file.RequiredNotices.Add("LICENSE"); }, "Third-party policy forbids file");
        Test("conditional-without-evidence", f => f.AddThirdParty("CONDITIONAL", false), "Third-party conditions/notices unverified");
        Test("conditional-missing-exact-notice", f => { var file = f.AddThirdParty("CONDITIONAL", true); file.RequiredNotices.Clear(); file.RequiredNotices.Add("THIRD_PARTY_NOTICES.md"); }, "Third-party conditions/notices unverified");
        Test("conditional-reviewed-with-notice", f => f.AddThirdParty("CONDITIONAL", true), null);
        Test("metadata-only-not-enough-for-redistribution", f => { var file = f.AddThirdParty("CONDITIONAL", true); f.Policy.Components[0].LicenseFile = "PACKAGE-METADATA.txt"; file.RequiredNotices = ["PACKAGE-METADATA.txt"]; f.AddFile("PACKAGE-METADATA.txt", Encoding.UTF8.GetBytes("MIT package metadata, not full licence")).Kind = "license"; }, "Third-party licence text missing");
        Test("stale-file-approval", f => { f.AddFile("needed.json", Encoding.UTF8.GetBytes("{}")); File.Delete(Path.Combine(f.Root, "needed.json")); }, "Approved file absent");
        Test("case-colliding-allowlist", f => f.List.Files.Add(new() { Path = "license", Kind = "license", ReviewStatus = "PASS", Evidence = "duplicate", Sha256 = f.List.Files[0].Sha256 }), "Duplicate allowlist file");
        Test("unapproved-hash-placeholder", f => f.List.Files[0].Sha256 = "REQUIRES_REVIEW", "Unapproved file rule");
        File.WriteAllText(Path.Combine(Path.GetFullPath(output), "self-tests.json"), JsonSerializer.Serialize(new { total = results.Count, passed = results.Count - failures, failed = failures, results }, Program.Json));
        Console.WriteLine($"Release gate tests: {results.Count - failures}/{results.Count}");
        return failures == 0 ? 0 : 1;
    }

    private sealed class Fixture
    {
        internal string Root { get; }
        internal Allowlist List { get; } = new() { SchemaVersion = 1, RequiredFiles = ["LICENSE", "THIRD_PARTY_NOTICES.md"] };
        internal Policy Policy { get; } = new() { DefaultReleaseDecision = "EXCLUDE" };
        internal LocalDependencies Local { get; } = new();
        internal Fixture(string root)
        {
            Root = root;
            Directory.CreateDirectory(root);
            AddFile("LICENSE", Encoding.UTF8.GetBytes("synthetic fixture license")).Kind = "license";
            AddFile("THIRD_PARTY_NOTICES.md", Encoding.UTF8.GetBytes("synthetic fixture notices")).Kind = "license";
        }
        internal FileRule AddFile(string path, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(Root, path), bytes);
            var rule = new FileRule { Path = path, Kind = "project", Sha256 = Auditor.Hash(bytes), ReviewStatus = "PASS", Evidence = "Synthetic offline test fixture, not a distribution approval" };
            List.Files.Add(rule);
            return rule;
        }
        internal void AddAssembly(string path, (string Name, byte[] Bytes)[] resources, string format = "utf8-text")
        {
            AddFile(path, MakeAssembly(resources));
            List.Assemblies.Add(new() { Path = path, Resources = resources.Select(r => new ResourceRule { Name = r.Name, Sha256 = Auditor.Hash(r.Bytes), Format = format }).ToList() });
        }
        internal FileRule AddThirdParty(string release, bool conditions)
        {
            AddAssembly("ThirdParty.dll", []);
            var rule = List.Files.Last();
            rule.Kind = "thirdParty";
            rule.Component = "SyntheticDependency";
            rule.Version = "1";
            rule.ConditionsSatisfied = conditions;
            rule.RequiredNotices = ["LICENSE"];
            Policy.Components.Add(new() { Component = rule.Component, Version = rule.Version, Release = release, LicenseFile = "LICENSE" });
            return rule;
        }
    }

    private static byte[] MakeResources(string key, object value)
    {
        using var output = new MemoryStream();
        using (var writer = new ResourceWriter(output))
        {
            if (value is string text) writer.AddResource(key, text);
            else writer.AddResource(key, (byte[])value);
            writer.Generate();
            return output.ToArray();
        }
    }

    // Creates inert PE metadata fixtures. The scanner never loads or executes these assemblies.
    private static byte[] MakeAssembly((string Name, byte[] Bytes)[] resources)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("Synthetic.dll"), metadata.GetOrAddGuid(Guid.Parse("4af8d5b0-72fb-443c-91df-e2e29a6d5cb5")), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("Synthetic"), new Version(1, 0), default, default, (AssemblyFlags)0, AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var resourceData = new BlobBuilder();
        foreach (var item in resources)
        {
            metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString(item.Name), default, (uint)resourceData.Count);
            resourceData.WriteInt32(item.Bytes.Length);
            resourceData.WriteBytes(item.Bytes);
        }
        var builder = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll), new MetadataRootBuilder(metadata), new BlobBuilder(), managedResources: resourceData, flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        builder.Serialize(image);
        return image.ToArray();
    }
}
