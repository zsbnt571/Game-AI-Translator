using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// A clock is a display value, not a numeric sentence template. The meridian
// and its position must match exactly; dates, versions and prose never match.
// No language is hardcoded here: the target comes from a checked cache pair.
internal static class ClockDisplayTemplate
{
    internal readonly record struct Source(string Key,string Time);
    internal readonly record struct Target(string Before,string After)
    {
        internal string Render(string time)=>Before+time+After;
    }
    private const string TimePattern=@"(?:[01]?[0-9]|2[0-3]):[0-5][0-9](?::[0-5][0-9])?";
    private static readonly Regex Pattern=new("^(?:(?<meridian>AM|PM)(?<space>[ \\t]+)(?<time>"+TimePattern+")|(?<time>"+TimePattern+")(?<space>[ \\t]+)(?<meridian>AM|PM))$",RegexOptions.Compiled|RegexOptions.CultureInvariant);
    internal static bool TrySource(string source,out Source value)
    {
        value=default;if(source.Length>80)return false;
        var match=Pattern.Match(source);if(!match.Success)return false;
        var time=match.Groups["time"];
        value=new((time.Index==0?"after:":"before:")+match.Groups["meridian"].Value+":"+match.Groups["space"].Value,time.Value);
        return true;
    }
    internal static bool TryTarget(Source source,string text,out Target value)
    {
        value=default;if(Encoding.UTF8.GetByteCount(text)>160)return false;
        int at=text.IndexOf(source.Time,StringComparison.Ordinal);
        if(at<0||text.IndexOf(source.Time,at+source.Time.Length,StringComparison.Ordinal)>=0)return false;
        string before=text[..at],after=text[(at+source.Time.Length)..],label=before+after;
        if(label.Length==0||!label.Any(char.IsLetter)||label.Any(c=>char.IsDigit(c)||c is '\r' or '\n' or '\\' or '<' or '>' or '[' or ']' or ':' or '\0'))return false;
        value=new(before,after);return true;
    }
}
