using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// A structural reader for label / speaker := line / quoted-choice scripts.
// Recognition is independent of resource names and never runs script commands.
internal static class LabelDialogueResource
{
    private static readonly Regex Label = new(@"^label\s*(?::\s*\S+|\s+\S+\s*:)$", RegexOptions.Compiled);
    private static readonly Regex Speech = new(@"^(?:[\p{L}\p{N}_ .\-]+|<[A-Z_0-9]+>)\s*:=\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex Choice = new("^\"((?:\\\\.|[^\"\\\\])*)\"\\s*:$", RegexOptions.Compiled);
    private static readonly Regex Directive = new(@"^[A-Za-z_][A-Za-z_0-9 -]*\s*:", RegexOptions.Compiled);
    internal static bool TryRead(string body, CancellationToken cancellation, out string[] texts, out bool complete)
    {
        texts = []; complete = true;
        if (body.Length > 16 * 1024 * 1024 || body.IndexOf('\0') >= 0) return false;
        var lines = body.Replace("\r\n", "\n").Split('\n');
        // Two independent syntax markers avoid treating an arbitrary note/file
        // as dialogue merely because it contains English sentences.
        if (!lines.Any(l => Label.IsMatch(l.Trim())) || !lines.Any(l => Speech.IsMatch(l.Trim()))) return false;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in lines)
        {
            cancellation.ThrowIfCancellationRequested(); string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith('#')) continue;
            var spoken = Speech.Match(line);
            if (spoken.Success) { Add(spoken.Groups[1].Value); continue; }
            var choice = Choice.Match(line);
            if (choice.Success) { Add(choice.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")); continue; }
            if (Label.IsMatch(line) || line.StartsWith("character ", StringComparison.Ordinal) ||
                Regex.IsMatch(line, @"^(?:jump|call|return|menu|if|else|elif|endif|endmenu|command|event|gender|wait|set|choice|option|scene|sound|music|background|animation)\s*(?::|$)")) continue;
            // Unknown directives are reported, not translated as visible prose.
            if (Directive.IsMatch(line) || line.StartsWith('"') && line.EndsWith(':')) { complete = false; continue; }
            Add(line);
        }
        texts = result.ToArray(); return true;
        void Add(string text)
        {
            foreach (string part in SerializedDialogueText.VisibleParts(text.Trim()))
                if (part.Length > 0) result.Add(part);
        }
    }
}
