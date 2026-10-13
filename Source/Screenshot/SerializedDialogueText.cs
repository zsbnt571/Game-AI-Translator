using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

internal static class SerializedDialogueText
{
    // Recognize explicit page/wait instructions, not punctuation or guessed
    // sentence boundaries. Variables and all unrelated formatting stay intact.
    private static readonly Regex Page=new(@"\{(?:wc|c)\}",RegexOptions.Compiled);
    private static readonly Regex Wait=new(@"\{w(?:=\d+(?:\.\d+)?)?\}",RegexOptions.Compiled);
    private static readonly Regex Speed=new(@"\{(?:s=\d+(?:\.\d+)?|/s)\}",RegexOptions.Compiled);
    private static readonly Regex Boundary=new(@"\{(?:s=\d+(?:\.\d+)?|/s|w(?:=\d+(?:\.\d+)?)?)\}",RegexOptions.Compiled);
    internal static IEnumerable<string> VisibleParts(string value)
    {
        if(value.Length>6000||!Page.IsMatch(value)&&!Wait.IsMatch(value)&&!Speed.IsMatch(value)){yield return value;yield break;}
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(string page in Page.Split(value))
        {
            string text=Boundary.Replace(page,"").Trim();
            if(text.Length>0&&seen.Add(text))yield return text;
            // Speed/wait commands split writer calls. The writer appends each
            // next segment to its original buffer, so preload these exact
            // cumulative buffers as well as the complete line. Never invent
            // boundaries from punctuation, or interpret unknown commands.
            int count=0;
            foreach(Match boundary in Boundary.Matches(page))
            {
                if(++count>32)break;
                string prefix=Boundary.Replace(page[..boundary.Index],"").Trim();
                if(prefix.Length>0&&seen.Add(prefix))yield return prefix;
            }
        }
    }
}
