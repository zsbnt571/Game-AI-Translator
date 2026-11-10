using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal static class GameNoteLinks
{
    private static readonly Regex WebLink=new(@"https?://[^\s<>""'\u3000]+",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    internal static Uri? At(string text,int offset)
    {
        if(text.Length>GameNotesStore.MaxLength||offset<0||offset>text.Length)return null;
        foreach(Match match in WebLink.Matches(text))
        {
            string link=match.Value.TrimEnd('.',',',';',':','!','?',')',']','}','。','，','；','：','！','？','）','】');
            if(offset>=match.Index&&offset<match.Index+link.Length&&link.Length<=2048&&Uri.TryCreate(link,UriKind.Absolute,out var uri)
                &&uri.Scheme is "http" or "https"&&!string.IsNullOrWhiteSpace(uri.Host)&&uri.UserInfo.Length==0)return uri;
        }
        return null;
    }
}
