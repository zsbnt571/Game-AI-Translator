using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Automatic prose only. Original keys, human corrections and structural
// markup remain untouched. No scene names, game IDs or translated phrases.
internal static class EmbeddedTextContinuity
{
    private static readonly Regex OldPercent = new(@"%(?:\([A-Za-z_][A-Za-z0-9_]*\))?[-+#0 ]*\d*(?:\.\d+)?[sdifouxXeEgGc%]",RegexOptions.Compiled|RegexOptions.IgnoreCase);
    private static bool Cjk(char c)=>c>='\u2e80'&&c<='\u9fff';
    internal static string FoldLines(string value)
    {
        // Preserve word boundaries in mixed Latin/CJK prose, but do not insert
        // spaces in the middle of a Chinese phrase.
        return Regex.Replace(value,@"[ \t]*\r?\n[ \t]*|[ \t]*\r[ \t]*",m=>
        {
            int before=m.Index-1,after=m.Index+m.Length;
            return before>=0&&after<value.Length&&Cjk(value[before])&&Cjk(value[after])?"":" ";
        });
    }
    internal static string RestoreRequest(string source,string value,string[] codes,EmbeddedTextSyntax syntax=EmbeddedTextSyntax.Generic)
    {
        int previous=-1;
        for(int i=0;i<codes.Length;i++)
        {
            if(codes[i] is not ("\r" or "\n" or "\r\n"))continue;
            int at=value.IndexOf("⟦RPG"+i+"⟧",StringComparison.Ordinal);
            if(at<=previous)throw new InvalidDataException("译文没有保留换行结构。");
            previous=at;
        }
        // Only placeholders can introduce authored breaks. A model's extra
        // physical line breaks must not become additional fixed layout rows.
        string restored=RpgTranslationText.Restore(FoldLines(value),codes);
        if(!RpgTranslationText.ValidCachedPair(source,restored,syntax))
            throw new InvalidDataException("译文没有保留格式结构。");
        return NormalizeAutomatic(source,restored);
    }
    internal static string NormalizeAutomatic(string source,string value)
    {
        // Authored line breaks may reserve room for images, labels or columns.
        // Storage cannot know the widget's available region. Rendering owns flow.
        return value;
    }
    internal static bool HasFormatArtifact(string source,string value)
    {
        if(!value.Any(Cjk))return false;
        // Detect only a proven old partial-word token. Deleting its first letter
        // cannot repair a mistranslated suffix ('for' became protected f + 'or').
        // The cache keeps its backup and schedules only damaged rows for retry.
        foreach(var group in OldPercent.Matches(source).Cast<Match>()
            .Where(m=>EmbeddedTranslationText.FormatLength(source,m.Index)==0)
            .Select(m=>
            {
                int at=m.Index+m.Length-1,end=at;
                while(end<source.Length&&source[end] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')end++;
                return new {Token=m.Value,Word=source[at..end]};
            }).Where(x=>x.Word.Length>=2).GroupBy(x=>x.Token,StringComparer.Ordinal))
        {
            var words=group.Select(x=>Regex.Escape(x.Word)).Distinct().OrderByDescending(x=>x.Length);
            string prefix=group.Key[..^1];
            var residue=new Regex(Regex.Escape(prefix)+"(?:"+string.Join("|",words)+"|"+Regex.Escape(group.Key[^1..])+")"+@"(?![A-Za-z])");
            if(residue.Matches(value).Count!=group.Count())continue;
            // A real occurrence of the same argument elsewhere is ambiguous.
            if(EmbeddedTranslationText.Protect(source).Codes.Contains(group.Key))continue;
            return true;
        }
        return false;
    }
}
