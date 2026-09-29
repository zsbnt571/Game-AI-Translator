using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReleaseAudit;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "self-test") return SelfTests.Run(args[1]);
            if (args.Length == 2 && args[0] == "inspect")
            {
                Console.WriteLine(JsonSerializer.Serialize(Auditor.Inspect(args[1]), Json));
                return 0;
            }
            if (args.Length != 6 || args[0] != "audit")
            {
                Console.Error.WriteLine("audit <candidate-directory> <allowlist.json> <redistribution-policy.json> <local-dependencies.json> <report.json> | inspect <assembly> | self-test <output-directory>");
                return 2;
            }
            var report = Auditor.Run(args[1], Read<Allowlist>(args[2]), Read<Policy>(args[3]), Read<LocalDependencies>(args[4]));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[5]))!);
            File.WriteAllText(args[5], JsonSerializer.Serialize(report, Json));
            Console.WriteLine($"Release audit: {(report.Passed ? "PASS" : "FAIL")}; files={report.Files.Count}; assemblies={report.Assemblies.Count}; violations={report.Violations.Count}");
            foreach (string violation in report.Violations) Console.WriteLine(violation);
            return report.Passed ? 0 : 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Release audit failed closed: {e.GetType().Name}: {e.Message}");
            return 2;
        }
    }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty audit input");
}

internal sealed class Allowlist
{
    public int SchemaVersion { get; set; }
    public string Description { get; set; } = "";
    public List<string> RequiredFiles { get; set; } = [];
    public List<FileRule> Files { get; set; } = [];
    public List<AssemblyRule> Assemblies { get; set; } = [];
    public List<string> ExcludedSha256 { get; set; } = [];
    public List<string> ForbiddenFileNames { get; set; } = [];
}
internal sealed class FileRule
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string ReviewStatus { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Component { get; set; } = "";
    public string Version { get; set; } = "";
    public bool ConditionsSatisfied { get; set; }
    public List<string> RequiredNotices { get; set; } = [];
}
internal sealed class AssemblyRule
{
    public string Path { get; set; } = "";
    public List<ResourceRule> Resources { get; set; } = [];
}
internal sealed class ResourceRule
{
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Format { get; set; } = "";
    public List<BinaryValueRule> AllowedBinaryValues { get; set; } = [];
}
internal sealed class BinaryValueRule
{
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Evidence { get; set; } = "";
}
internal sealed class Policy { public string DefaultReleaseDecision { get; set; } = ""; public List<ComponentRule> Components { get; set; } = []; }
internal sealed class ComponentRule { public string Component { get; set; } = ""; public string Version { get; set; } = ""; public string Release { get; set; } = ""; public string LicenseFile { get; set; } = ""; }
internal sealed class LocalDependencies { public List<LocalDependency> Files { get; set; } = []; }
internal sealed class LocalDependency { public string Path { get; set; } = ""; public string Sha256 { get; set; } = ""; public bool ReleaseApproved { get; set; } }
internal sealed class AuditReport
{
    public bool Passed => Violations.Count == 0;
    public List<string> Violations { get; } = [];
    public List<FileEvidence> Files { get; } = [];
    public List<AssemblyEvidence> Assemblies { get; } = [];
}
internal sealed record FileEvidence(string Path, string Sha256, long Bytes);
internal sealed record ResourceEvidence(string Name, string Sha256, int Bytes, string DetectedFormat);
internal sealed record AssemblyEvidence(string Path, List<ResourceEvidence> Resources);

internal static class Auditor
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] AlwaysForbidden = ["UnityEmbeddedMono32.zip", "UnityEmbeddedMono64.zip", "UnityEmbeddedIl2Cpp64.zip", "UnityMono.zip", "CloudMeadow.zip", "UnrealRuntime.zip", "classdata.tpk"];
    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pdb", ".dmp", ".log", ".bak", ".mp4", ".tmp", ".temp" };
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static bool ValidHash(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);
    private static bool EqualHash(string a, string b) => ValidHash(a) && ValidHash(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static void Fail(AuditReport report, string message) => report.Violations.Add(message);
    private static bool ValidPath(string path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':') && !path.Contains('*') && !path.Contains('?') && path.Split('/').All(s => s.Length > 0 && s != "." && s != ".." && !s.EndsWith(' ') && !s.EndsWith('.'));

    internal static AuditReport Run(string directory, Allowlist list, Policy policy, LocalDependencies dependencies)
    {
        var report = new AuditReport();
        string root = Path.GetFullPath(directory);
        if (list.SchemaVersion != 1 || policy.DefaultReleaseDecision != "EXCLUDE") throw new InvalidDataException("Unsupported allowlist or policy default; EXCLUDE default is required");
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Candidate root is missing or is a reparse point");
        var files = new Dictionary<string, FileRule>(Paths);
        foreach (var rule in list.Files)
        {
            if (!ValidPath(rule.Path)) { Fail(report, $"Unsafe allowlist path: {rule.Path}"); continue; }
            if (!files.TryAdd(rule.Path, rule)) Fail(report, $"Duplicate allowlist file: {rule.Path}");
            if (!ValidHash(rule.Sha256) || rule.ReviewStatus != "PASS" || string.IsNullOrWhiteSpace(rule.Evidence)) Fail(report, $"Unapproved file rule: {rule.Path}");
            if (rule.Kind is not ("project" or "thirdParty" or "license" or "config")) Fail(report, $"Unsupported file kind: {rule.Path}");
            foreach (var notice in rule.RequiredNotices)
                if (!ValidPath(notice)) Fail(report, $"Unsafe notice path: {notice}");
            if (rule.Kind == "thirdParty")
            {
                var component = policy.Components.SingleOrDefault(p => p.Component == rule.Component && p.Version == rule.Version);
                if (component == null || component.Release == "EXCLUDE" || component.Release is not ("PASS" or "CONDITIONAL")) Fail(report, $"Third-party policy forbids file: {rule.Path}");
                else if (!rule.ConditionsSatisfied || rule.RequiredNotices.Count == 0 || !rule.RequiredNotices.Contains(component.LicenseFile, StringComparer.Ordinal)) Fail(report, $"Third-party conditions/notices unverified: {rule.Path}");
                else if (!rule.RequiredNotices.Any(IsLicenseTextPath)) Fail(report, $"Third-party licence text missing (metadata is not a licence): {rule.Path}");
            }
        }
        var assemblies = new Dictionary<string, AssemblyRule>(Paths);
        foreach (var item in list.Assemblies)
        {
            if (!ValidPath(item.Path) || !assemblies.TryAdd(item.Path, item)) { Fail(report, $"Unsafe or duplicate assembly rule: {item.Path}"); continue; }
            if (!files.ContainsKey(item.Path)) Fail(report, $"Assembly has no file approval: {item.Path}");
            if (item.Resources.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != item.Resources.Count) Fail(report, $"Duplicate resource rule: {item.Path}");
            foreach (var resource in item.Resources)
            {
                if (!ValidHash(resource.Sha256) || resource.Format is not ("utf8-text" or "dotnet-resources")) Fail(report, $"Unapproved resource rule: {item.Path}:{resource.Name}");
                if (resource.AllowedBinaryValues.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != resource.AllowedBinaryValues.Count) Fail(report, $"Duplicate binary value approval: {item.Path}:{resource.Name}");
            }
        }
        var excluded = new HashSet<string>(dependencies.Files.Where(x => !x.ReleaseApproved).Select(x => x.Sha256).Concat(list.ExcludedSha256), StringComparer.OrdinalIgnoreCase);
        if (excluded.Any(x => !ValidHash(x))) throw new InvalidDataException("Invalid EXCLUDE fingerprint");
        var forbidden = new HashSet<string>(AlwaysForbidden.Concat(list.ForbiddenFileNames), Paths);
        var seen = new HashSet<string>(Paths);
        var seenAssemblies = new HashSet<string>(Paths);
        foreach (string path in Enumerate(root, report))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!ValidPath(relative) || !seen.Add(relative)) { Fail(report, $"Unsafe/duplicate actual file: {relative}"); continue; }
            byte[] bytes = File.ReadAllBytes(path);
            string hash = Hash(bytes);
            report.Files.Add(new(relative, hash, bytes.LongLength));
            string name = Path.GetFileName(relative);
            if (forbidden.Contains(name) || (name.StartsWith("oo2core_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) || ForbiddenExtensions.Contains(Path.GetExtension(relative))) Fail(report, $"Forbidden filename: {relative}");
            if (excluded.Contains(hash)) Fail(report, $"EXCLUDE fingerprint: {relative}");
            if (!files.TryGetValue(relative, out var rule)) Fail(report, $"Unknown file: {relative}");
            else
            {
                if (!EqualHash(hash, rule.Sha256)) Fail(report, $"File hash mismatch: {relative}");
                foreach (string notice in rule.RequiredNotices)
                    if (!files.TryGetValue(notice, out var license) || license.Kind != "license" || !File.Exists(Path.Combine(root, notice))) Fail(report, $"Missing approved notice for {relative}: {notice}");
            }
            if (bytes.Length >= 2 && bytes[0] == 'M' && bytes[1] == 'Z')
            {
                try
                {
                    using var pe = new PEReader(new MemoryStream(bytes, false));
                    if (pe.PEHeaders.CorHeader != null)
                    {
                        seenAssemblies.Add(relative);
                        if (!assemblies.TryGetValue(relative, out var assembly)) Fail(report, $"Managed assembly lacks resource review: {relative}");
                        InspectResources(pe, relative, assembly, excluded, report);
                    }
                }
                catch (Exception e) when (e is BadImageFormatException or IOException or InvalidDataException or OverflowException or ArgumentException) { Fail(report, $"Unreadable PE/resource: {relative}: {e.GetType().Name}"); }
            }
        }
        foreach (string required in list.RequiredFiles.Concat(["LICENSE", "THIRD_PARTY_NOTICES.md"]).Distinct(Paths))
            if (!ValidPath(required) || !files.ContainsKey(required) || !seen.Contains(required)) Fail(report, $"Required file absent/unapproved: {required}");
        foreach (string legal in new[] { "LICENSE", "THIRD_PARTY_NOTICES.md" })
            if (files.TryGetValue(legal, out var legalRule) && legalRule.Kind != "license") Fail(report, $"Required legal document is not reviewed as a license: {legal}");
        // Every allowlisted file is required: stale manifests cannot silently omit a runtime or notice.
        foreach (string approved in files.Keys) if (!seen.Contains(approved)) Fail(report, $"Approved file absent: {approved}");
        foreach (string approved in assemblies.Keys) if (!seenAssemblies.Contains(approved)) Fail(report, $"Approved assembly absent/not managed: {approved}");
        return report;
    }

    private static IEnumerable<string> Enumerate(string root, AuditReport report)
    {
        foreach (string child in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { Fail(report, $"Reparse point not allowed: {Path.GetFileName(child)}"); continue; }
            if ((attributes & FileAttributes.Directory) != 0) { foreach (var file in Enumerate(child, report)) yield return file; }
            else yield return child;
        }
    }

    private static bool IsLicenseTextPath(string path)
    {
        string name = Path.GetFileName(path);
        return !name.Contains("METADATA", StringComparison.OrdinalIgnoreCase) &&
            (name.Contains("LICENSE", StringComparison.OrdinalIgnoreCase) || name.Contains("LICENCE", StringComparison.OrdinalIgnoreCase) || name.Contains("COPYING", StringComparison.OrdinalIgnoreCase) || name.EndsWith("OFL.txt", StringComparison.OrdinalIgnoreCase));
    }

    internal static AssemblyEvidence Inspect(string file)
    {
        using var stream = File.OpenRead(file);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader == null) throw new InvalidDataException("Not a managed PE assembly");
        return new(Path.GetFileName(file), ReadResources(pe).Select(r => new ResourceEvidence(r.Name, Hash(r.Bytes), r.Bytes.Length, Detect(r.Bytes))).ToList());
    }

    private sealed record ResourceBytes(string Name, byte[] Bytes);
    private static List<ResourceBytes> ReadResources(PEReader pe)
    {
        var metadata = pe.GetMetadataReader();
        var directory = pe.PEHeaders.CorHeader!.ResourcesDirectory;
        var result = new List<ResourceBytes>();
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            string name = metadata.GetString(resource.Name);
            if (!resource.Implementation.IsNil) throw new InvalidDataException($"Linked resource is not audited: {name}");
            if (resource.Offset > int.MaxValue || directory.Size < 4 || resource.Offset > directory.Size - 4) throw new InvalidDataException("Resource offset outside managed-resource directory");
            var block = pe.GetSectionData(checked(directory.RelativeVirtualAddress + (int)resource.Offset));
            var reader = block.GetReader();
            int length = reader.ReadInt32();
            if (length < 0 || length > directory.Size - (int)resource.Offset - 4 || length > reader.RemainingBytes) throw new InvalidDataException("Invalid resource length");
            result.Add(new(name, reader.ReadBytes(length)));
        }
        return result;
    }

    private static void InspectResources(PEReader pe, string path, AssemblyRule? approval, HashSet<string> excluded, AuditReport report)
    {
        var resources = ReadResources(pe);
        report.Assemblies.Add(new(path, resources.Select(r => new ResourceEvidence(r.Name, Hash(r.Bytes), r.Bytes.Length, Detect(r.Bytes))).ToList()));
        if (resources.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != resources.Count) Fail(report, $"Duplicate managed resources: {path}");
        foreach (var item in resources)
        {
            var rule = approval?.Resources.FirstOrDefault(r => r.Name == item.Name);
            if (rule == null || !EqualHash(rule.Sha256, Hash(item.Bytes))) Fail(report, $"Unknown/changed resource: {path}:{item.Name}");
            if (AlwaysForbidden.Any(name => item.Name.EndsWith(name, StringComparison.OrdinalIgnoreCase)) || excluded.Contains(Hash(item.Bytes))) Fail(report, $"EXCLUDE resource: {path}:{item.Name}");
            string format = Detect(item.Bytes);
            if (format is not ("utf8-text" or "dotnet-resources")) { Fail(report, $"Binary embedded resource forbidden ({format}): {path}:{item.Name}"); continue; }
            if (rule != null && rule.Format != format) Fail(report, $"Resource format mismatch: {path}:{item.Name}");
            if (format == "dotnet-resources") InspectResx(item, path, rule, excluded, report);
        }
        if (approval != null)
            foreach (var rule in approval.Resources)
                if (!resources.Any(r => r.Name == rule.Name)) Fail(report, $"Approved resource absent: {path}:{rule.Name}");
    }

    private static void InspectResx(ResourceBytes item, string path, ResourceRule? rule, HashSet<string> excluded, AuditReport report)
    {
        using var input = new MemoryStream(item.Bytes, false);
        using var resources = new ResourceReader(input);
        var enumerator = resources.GetEnumerator();
        while (enumerator.MoveNext())
        {
            // GetResourceData returns serialized bytes; never access Value or deserialize user types.
            string name = (string)enumerator.Key;
            resources.GetResourceData(name, out string type, out byte[] bytes);
            bool safePrimitive = type is "ResourceTypeCode.Null" or "ResourceTypeCode.String" or "ResourceTypeCode.Boolean" or "ResourceTypeCode.Char" or "ResourceTypeCode.Byte" or "ResourceTypeCode.SByte" or "ResourceTypeCode.Int16" or "ResourceTypeCode.UInt16" or "ResourceTypeCode.Int32" or "ResourceTypeCode.UInt32" or "ResourceTypeCode.Int64" or "ResourceTypeCode.UInt64" or "ResourceTypeCode.Single" or "ResourceTypeCode.Double" or "ResourceTypeCode.Decimal" or "ResourceTypeCode.DateTime" or "ResourceTypeCode.TimeSpan";
            if (safePrimitive) continue;
            byte[] payload = bytes;
            if (type is "ResourceTypeCode.ByteArray" or "ResourceTypeCode.Stream")
            {
                if (bytes.Length < 4 || BinaryPrimitives.ReadInt32LittleEndian(bytes) != bytes.Length - 4) { Fail(report, $"Malformed binary resource value: {path}:{item.Name}:{name}"); continue; }
                payload = bytes[4..];
            }
            string format = Detect(payload);
            if (excluded.Contains(Hash(payload)) || format is "zip" or "gzip" or "pe" or "font" or "dotnet-resources") { Fail(report, $"Forbidden nested payload ({format}): {path}:{item.Name}:{name}"); continue; }
            var permit = rule?.AllowedBinaryValues.FirstOrDefault(x => x.Name == name);
            if (permit == null || !EqualHash(permit.Sha256, Hash(bytes)) || string.IsNullOrWhiteSpace(permit.Evidence)) Fail(report, $"Unreviewed serialized/binary resource value ({type}): {path}:{item.Name}:{name}");
        }
    }

    private static string Detect(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && (bytes[2] is 3 or 5 or 7)) return "zip";
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b) return "gzip";
        if (bytes.Length >= 2 && bytes[0] == 'M' && bytes[1] == 'Z') return "pe";
        if (bytes.Length >= 4 && (bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 1, 0, 0 }) || Encoding.ASCII.GetString(bytes, 0, 4) is "OTTO" or "ttcf" or "wOFF" or "wOF2")) return "font";
        if (bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0xBEEFCACE) return "dotnet-resources";
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.All(c => !char.IsControl(c) || c is '\r' or '\n' or '\t')) return "utf8-text";
        }
        catch (DecoderFallbackException) { }
        return "unreviewed-binary";
    }
}
