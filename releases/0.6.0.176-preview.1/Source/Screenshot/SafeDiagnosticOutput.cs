using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Output-only protection. Registered credentials stay in process memory and never alter
// requests, parser input, accepted translations, or the persisted settings format.
internal static class SafeDiagnosticOutput
{
    private const string Hidden = "[REDACTED]";
    private static readonly object Gate = new();
    private static readonly HashSet<string> Credentials = new(StringComparer.Ordinal);
    private static bool _registrationFailed;
    private static readonly Regex BearerMarkers = new(@"\bBearer\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static void RegisterCredential(string? credential)
    {
        if (string.IsNullOrEmpty(credential)) return;
        try { lock (Gate) Credentials.Add(credential); }
        catch { lock (Gate) _registrationFailed = true; }
    }

    internal static string Redact(string? value)
    {
        try
        {
            string[] credentials;
            lock (Gate)
            {
                if (_registrationFailed) return "[DIAGNOSTIC_REDACTION_UNAVAILABLE]";
                credentials = Credentials.OrderByDescending(x => x.Length).ToArray();
            }
            var text = value ?? "";
            // Exact configured credentials are removed before token scanning so a
            // punctuation-containing credential cannot be partially masked first.
            foreach (var credential in credentials)
                text = text.Replace(credential, Hidden, StringComparison.Ordinal);
            // Match markers separately so "Bearer Bearer token" cannot consume the
            // second marker and leave its token unmasked. Every span is output-only.
            var spans = new List<(int Start, int End)>();
            foreach (Match marker in BearerMarkers.Matches(text))
            {
                var start = marker.Index + marker.Length;
                if (start < text.Length && (text[start] == '\'' || text[start] == '"')) start++;
                var end = start;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) &&
                       "\"'`,;<>[]{}()".IndexOf(text[end]) < 0) end++;
                if (end > start) spans.Add((start, end));
            }
            if (spans.Count > 0)
            {
                var builder = new StringBuilder(text.Length);
                var position = 0;
                foreach (var span in spans)
                {
                    if (span.Start < position) continue;
                    builder.Append(text, position, span.Start - position).Append(Hidden);
                    position = span.End;
                }
                builder.Append(text, position, text.Length - position);
                text = builder.ToString();
            }
            return text;
        }
        catch (Exception ex) { return Failure(ex); }
    }

    internal static string Compose(string? message, Exception? error = null)
    {
        try { return Redact(error is null ? message : $"{message} | {error}"); }
        catch (Exception ex) { return Failure(ex); }
    }

    internal static string ExceptionSummary(Exception error)
    {
        try { return Redact($"{error.GetType().Name}: {error.Message}"); }
        catch (Exception ex) { return Failure(ex); }
    }

    internal static string Summary(string? value, int maximumCharacters = 400)
    {
        // Redaction must precede whitespace folding and truncation: a truncated secret
        // fragment can no longer be found by an exact-credential replacement.
        var text = Redact(value).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > maximumCharacters ? text[..maximumCharacters] + "…" : text;
    }

    private static string Failure(Exception error) =>
        $"[DIAGNOSTIC_REDACTION_FAILED:{error.GetType().Name}]";
}
