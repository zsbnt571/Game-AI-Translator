using System.Security.Cryptography;

namespace GameAiTranslator.Runtime;

/// <summary>Opens only explicitly located, pinned local dependencies; never downloads or searches.</summary>
internal static class LocalRuntimeDependency
{
    internal const string ClassDataRelativePath = "adapters/unity/classdata.tpk";
    internal const long ClassDataLength = 289605;
    internal const string ClassDataSha256 = "129E1F80F930415DB6779FE6089AFA75280CB51462BCEE812BEAB6CD81A764C6";
    internal const string OodleRelativePath = "oo2core_9_win64.dll";
    internal const long OodleLength = 637952;
    internal const string OodleSha256 = "6F5D41A7892EA6B2DB420F2458DAD2F84A63901C9A93CE9497337B16C195F457";

    internal static FileStream? TryOpenClassData(string baseDirectory, out string? warning)
    {
        try
        {
            var stream = OpenPinned(baseDirectory, ClassDataRelativePath, ClassDataLength, ClassDataSha256);
            warning = null;
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = ex.Message + "；Unity 类型数据库不可用，继续读取自带字段结构的资源，其他资源由运行时补译。";
            return null;
        }
    }

    internal static FileStream OpenOodle(string baseDirectory) =>
        OpenPinned(baseDirectory, OodleRelativePath, OodleLength, OodleSha256);

    // Keep the verified handle open while the consumer reads (or loads the native library).
    // FileShare.Read rejects a concurrent writer/replacement rather than re-opening unverified bytes.
    internal static FileStream OpenPinned(string baseDirectory, string relativePath, long expectedLength, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("A relative dependency path is required.", nameof(relativePath));
        string[] parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':')))
            throw new ArgumentException("Invalid dependency path.", nameof(relativePath));
        if (expectedLength < 0 || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid dependency integrity constraint.");

        string path = Path.GetFullPath(baseDirectory);
        for (string? ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            CheckLink(ancestor);
        foreach (string part in parts)
        {
            path = Path.Combine(path, part);
            CheckLink(path);
        }
        if (!File.Exists(path))
            throw new FileNotFoundException($"缺少本地依赖 {relativePath}。请合法自行提供指定版本；程序不会自动下载。 / Missing local dependency {relativePath}; provide the authorized pinned version locally.");

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != expectedLength || !Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"本地依赖 {relativePath} 完整性校验失败，拒绝使用。 / Local dependency integrity check failed: {relativePath}.");
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        void CheckLink(string candidate)
        {
            if ((File.Exists(candidate) || Directory.Exists(candidate)) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"本地依赖路径含链接，拒绝使用：{relativePath}。 / Linked local dependency paths are not accepted: {relativePath}.");
        }
    }
}
