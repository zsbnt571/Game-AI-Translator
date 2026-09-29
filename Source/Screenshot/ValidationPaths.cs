namespace ScreenshotTranslationUiTester;

// Used only by explicitly selected diagnostic/test harnesses, never game adapters.
internal static class ValidationPaths
{
    internal static string LayoutEvidenceRoot =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FUSION_LAYOUT_EVIDENCE_ROOT"))
            ? Path.Combine(AppContext.BaseDirectory,"test-fixtures","layout")
            : RequiredRoot("FUSION_LAYOUT_EVIDENCE_ROOT");

    internal static string RequiredRoot(string variable)
    {
        var value=Environment.GetEnvironmentVariable(variable);
        if(string.IsNullOrWhiteSpace(value)||!Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"An explicit absolute directory is required: {variable}");
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if(root.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"An entire volume is not a diagnostic root: {variable}");
        return root;
    }
}
