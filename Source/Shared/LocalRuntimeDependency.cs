using System.Security.Cryptography;
using System.Text.Json;

namespace GameAiTranslator.Runtime;

internal enum DependencyState { Ready, Missing, InvalidVersion, WrongArchitecture, WrongBackend, HashMismatch, NotRequired, Invalid }
internal sealed record DependencyStatus(string Id, string DisplayName, DependencyState State, string Message,
    string ExpectedPath, string Engine, string Backend, string Architecture, string Version);
internal sealed record RuntimeDependencyDescriptor(string Id, string Engine, string Backend, string Architecture,
    string Version, string FileName, long Bytes, string Sha256);
internal sealed class DependencyValidationException(DependencyState state, string message) : IOException(message)
{
    internal DependencyState State { get; } = state;
}

/// <summary>Reads only an explicit local root and compiled pins. No network, search, or fallback.</summary>
internal static class LocalRuntimeDependency
{
    internal const string RootEnvironmentVariable = "GAME_AI_TRANSLATOR_PAYLOAD_ROOT";
    internal const string ClassDataVersion = "classdata-129e1f80f930";
    internal const string ClassDataRelativePath = "unity-classdata/" + ClassDataVersion + "/classdata.tpk";
    internal const long ClassDataLength = 289605;
    internal const string ClassDataSha256 = "129E1F80F930415DB6779FE6089AFA75280CB51462BCEE812BEAB6CD81A764C6";
    internal const string OodleVersion = "oodle9-6f5d41a7892e";
    internal const string OodleRelativePath = "unreal-oodle/" + OodleVersion + "/oo2core_9_win64.dll";
    internal const long OodleLength = 637952;
    internal const string OodleSha256 = "6F5D41A7892EA6B2DB420F2458DAD2F84A63901C9A93CE9497337B16C195F457";
    internal static readonly RuntimeDependencyDescriptor ClassData = new("unity-classdata", "Unity", "Any", "Any", ClassDataVersion, "classdata.tpk", ClassDataLength, ClassDataSha256);
    internal static readonly RuntimeDependencyDescriptor Oodle = new("unreal-oodle", "Unreal", "Native", "x64", OodleVersion, "oo2core_9_win64.dll", OodleLength, OodleSha256);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static string ResolveRoot(string? applicationDirectory = null)
    {
        string? configured = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && !Path.IsPathFullyQualified(configured))
            throw new DependencyValidationException(DependencyState.Invalid, RootEnvironmentVariable + " 必须是明确的绝对目录。 / An absolute local directory is required.");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, "runtime-payloads") : configured);
    }

    internal static string RelativePath(RuntimeDependencyDescriptor package) => Path.Combine(package.Id, package.Version, package.FileName);

    internal static FileStream? TryOpenClassData(string baseDirectory, out string? warning)
    {
        try
        {
            var stream = OpenVerified(ResolveRoot(baseDirectory), ClassData);
            warning = null;
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            warning = ex.Message + "；Unity 类型数据库不可用，继续读取自带字段结构的资源，其他资源由运行时补译。";
            return null;
        }
    }

    internal static FileStream OpenOodle(string baseDirectory) => OpenVerified(ResolveRoot(baseDirectory), Oodle);

    internal static DependencyStatus Check(string root, RuntimeDependencyDescriptor package, string? displayName = null)
    {
        string path = "";
        try
        {
            path = Path.Combine(root, RelativePath(package));
            using var stream = OpenVerified(root, package);
            return Result(DependencyState.Ready, "Ready — 身份、版本、大小和 SHA-256 已校验。");
        }
        catch (FileNotFoundException ex) { return Result(DependencyState.Missing, ex.Message); }
        catch (DependencyValidationException ex) { return Result(ex.State, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        { return Result(DependencyState.Invalid, "Invalid — " + ex.Message); }
        DependencyStatus Result(DependencyState state, string message) => new(package.Id, displayName ?? package.Id, state,
            message, path, package.Engine, package.Backend, package.Architecture, package.Version);
    }

    internal static FileStream OpenVerified(string root, RuntimeDependencyDescriptor expected)
    {
        string relative = RelativePath(expected);
        string path = CheckedPath(root, relative);
        string metadata = CheckedPath(root, relative + ".json");
        if (!File.Exists(path) || !File.Exists(metadata))
            throw new FileNotFoundException($"Missing — 缺少本地依赖 {expected.Id}（{expected.Version}，{expected.Backend}/{expected.Architecture}）：{path} 及其 .json 清单。请合法提供；程序不会自动下载。 / Missing local dependency {expected.FileName}.");
        RuntimeDependencyDescriptor? actual;
        using (var descriptor = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (descriptor.Length > 16384) throw Invalid("依赖清单超过限制。");
            try { actual = JsonSerializer.Deserialize<RuntimeDependencyDescriptor>(descriptor, JsonOptions); }
            catch (JsonException ex) { throw Invalid("依赖清单格式无效：" + ex.Message); }
        }
        if (actual is null || actual.Id != expected.Id || actual.Engine != expected.Engine || actual.FileName != expected.FileName)
            throw Invalid("依赖身份、引擎或文件名不匹配。");
        if (actual.Version != expected.Version)
            throw new DependencyValidationException(DependencyState.InvalidVersion, "Invalid version — 需要固定版本 " + expected.Version + "：" + expected.Id);
        if (actual.Architecture != expected.Architecture)
            throw new DependencyValidationException(DependencyState.WrongArchitecture, "Wrong architecture — 需要位数 " + expected.Architecture + "：" + expected.Id);
        if (actual.Backend != expected.Backend)
            throw new DependencyValidationException(DependencyState.WrongBackend, "Wrong backend — 需要后端 " + expected.Backend + "：" + expected.Id);
        if (actual.Bytes != expected.Bytes || !string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new DependencyValidationException(DependencyState.HashMismatch, "Hash mismatch — 清单与编译时固定完整性约束不符：" + expected.Id);
        return OpenPinned(root, relative, expected.Bytes, expected.Sha256);
        DependencyValidationException Invalid(string text) => new(DependencyState.Invalid, "Invalid — " + text + " " + expected.Id);
    }

    // Retain the verified handle while the consumer reads or loads the native library.
    internal static FileStream OpenPinned(string baseDirectory, string relativePath, long expectedLength, string expectedSha256)
    {
        if (expectedLength < 0 || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid dependency integrity constraint.");
        string path = CheckedPath(baseDirectory, relativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("Missing local dependency: " + relativePath);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != expectedLength || !Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new DependencyValidationException(DependencyState.HashMismatch, $"Hash mismatch — 本地依赖完整性校验失败，拒绝使用。 / Local dependency integrity check failed: {relativePath}.");
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static string CheckedPath(string baseDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("A relative dependency path is required.", nameof(relativePath));
        string[] parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':')))
            throw new ArgumentException("Invalid dependency path.", nameof(relativePath));
        string path = Path.GetFullPath(baseDirectory);
        for (string? ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor)) CheckLink(ancestor);
        foreach (string part in parts) { path = Path.Combine(path, part); CheckLink(path); }
        return path;
        static void CheckLink(string candidate)
        {
            if ((File.Exists(candidate) || Directory.Exists(candidate)) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new DependencyValidationException(DependencyState.Invalid, "Invalid — Linked local dependency paths are not accepted.");
        }
    }
}
