using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Equal text carries no authority to restyle the source pixels.
/// This is a render no-op, not a claim that OCR is correct or translation is complete.</summary>
internal static class SourceTextNoOp
{
    internal const string Result = "ACCEPTED_SOURCE_TEXT_UNCHANGED_NO_REPAINT";
    internal static bool Applies(string source, string target) =>
        !string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(target) &&
        string.Equals(Regex.Replace(source.Trim(), @"\s+", " "),
            Regex.Replace(target.Trim(), @"\s+", " "), StringComparison.Ordinal);
}
