using System.Security.Cryptography;

namespace ScreenshotTranslationUiTester;

// Old game runtimes cannot necessarily open the long portable data path that
// the desktop can. Keep preferences there; publish immutable runtime copies
// under the current Windows user's known local folder, never TEMP (the app
// redirects TEMP into the portable data root). Games may outlive this process.
internal static class GameFontRuntimeFiles
{
    internal const int MaximumRuntimePath = 200;
    private const int MaximumBytes = 32 * 1024 * 1024;

    internal static string Prepare(string sourcePath, byte[] verifiedBytes, string expectedHash)
    {
        if (sourcePath.Length <= MaximumRuntimePath) return sourcePath;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(local)) throw new IOException("无法准备较短的字体路径，未应用字体。");
        return PrepareInRoot(sourcePath, verifiedBytes, expectedHash, Path.Combine(local, "Fusion", "RuntimeFonts-v1"));
    }

    // Test-only callers can use a private root; no setting, environment variable
    // or game command can replace the production root above.
    internal static string PrepareInRoot(string sourcePath, byte[] verifiedBytes, string expectedHash, string cacheRoot)
    {
        if (verifiedBytes.Length is < 1 or > MaximumBytes)
            throw new IOException("自定义字体文件缺失或已改变，请重新选择或恢复默认。");
        string hash = Convert.ToHexString(SHA256.HashData(verifiedBytes));
        if (!hash.Equals(expectedHash, StringComparison.Ordinal))
            throw new IOException("自定义字体文件缺失或已改变，请重新选择或恢复默认。");
        string source = Path.GetFullPath(sourcePath);
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".ttf" or ".otf" or ".ttc")) throw new IOException("请选择 TTF、OTF 或 TTC 字体文件。");
        if (source.Length <= MaximumRuntimePath) return source;
        string root = Path.GetFullPath(cacheRoot), target = Path.Combine(root, hash + extension);
        if (target.Length > MaximumRuntimePath) throw new IOException("无法准备较短的字体路径，未应用字体。");
        RejectLinks(target);
        if (Exists(target)) { Verify(target, verifiedBytes.Length, hash); return target; }
        Directory.CreateDirectory(root);
        RejectLinks(target);
        string pending = Path.Combine(root, Guid.NewGuid().ToString("N") + ".pending");
        bool created = false;
        try
        {
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                output.Write(verifiedBytes); output.Flush(true);
            }
            Verify(pending, verifiedBytes.Length, hash);
            RejectLinks(target);
            try { File.Move(pending, target, overwrite: false); }
            catch (IOException) when (Exists(target))
            {
                // Another game/process may have published the same immutable
                // bytes. Only an exact regular-file match permits reuse.
                Verify(target, verifiedBytes.Length, hash);
            }
            Verify(target, verifiedBytes.Length, hash);
            return target;
        }
        finally
        {
            // Remove only our still-intact pending file. Unknown/changed files
            // and final cache entries are never overwritten or removed here.
            if (created)
                try { if (Exists(pending)) { Verify(pending, verifiedBytes.Length, hash); File.Delete(pending); } }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }
    private static void Verify(string path, int length, string hash)
    {
        RejectLinks(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new IOException("字体缓存内容与所选字体不一致，未应用字体。");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != length || input.Length > MaximumBytes || Convert.ToHexString(SHA256.HashData(input)) != hash)
            throw new IOException("字体缓存内容与所选字体不一致，未应用字体。");
        RejectLinks(path);
    }
    private static void RejectLinks(string path)
    {
        for (string? item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            try
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("字体缓存路径包含链接，未应用字体。");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
    }
}
