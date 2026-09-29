// Diagnostic tools only. No implicit game location; all existing path components
// must remain under the explicitly registered, non-linked test-copy directory.
internal static class IsolatedGamePath
{
    internal static string Require(string target)
    {
        var configured=Environment.GetEnvironmentVariable("FUSION_TEST_GAME_ROOT");
        if(string.IsNullOrWhiteSpace(configured)||!Path.IsPathFullyQualified(configured))
            throw new InvalidOperationException("Set FUSION_TEST_GAME_ROOT to an absolute isolated-copy directory.");
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        if(root.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!),StringComparison.OrdinalIgnoreCase)||!Directory.Exists(root))
            throw new InvalidOperationException("An existing non-volume test-copy directory is required.");
        var full=Path.GetFullPath(target);
        if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Target must be below the registered test-copy directory.");
        for(string? part=full;!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))
            if((Directory.Exists(part)||File.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("Test-copy paths must not contain directory links.");
        return full;
    }
}
