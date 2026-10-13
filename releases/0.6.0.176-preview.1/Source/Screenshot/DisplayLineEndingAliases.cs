using System.Collections.Concurrent;

namespace ScreenshotTranslationUiTester;

// CRLF and LF encode the same display line break. Keep every original key;
// this index neither strips markup/spacing nor changes writeback permission.
internal sealed class DisplayLineEndingAliases
{
    private readonly ConcurrentDictionary<string,string[]> sources=new(StringComparer.Ordinal);
    internal static string Key(string value)=>value.Replace("\r\n","\n",StringComparison.Ordinal);
    internal void Add(string source)
    {
        if(!source.Contains('\n'))return;
        sources.AddOrUpdate(Key(source),[source],(_,previous)=>
            previous.Length==0||previous.Contains(source,StringComparer.Ordinal)?previous:
            previous.Length>=16?[]:previous.Append(source).ToArray());
    }
    internal bool TryGet(string source,Func<string,string?> read,out string text)
    {
        text="";
        if(!source.Contains('\n')||!sources.TryGetValue(Key(source),out var variants))return false;
        foreach(string original in variants)
        {
            var value=read(original);if(string.IsNullOrEmpty(value))continue;
            string normalized=Key(value);
            // Two different translations are evidence of ambiguity, not a
            // reason to pick whichever cache row happens to enumerate first.
            if(text.Length>0&&text!=normalized){text="";return false;}
            text=normalized;
        }
        if(text.Length==0)return false;
        // Use the observed line-ending convention without touching literal
        // backslash escapes, placeholders, interior spaces or punctuation.
        if(source.Contains("\r\n",StringComparison.Ordinal))text=text.Replace("\n","\r\n",StringComparison.Ordinal);
        return true;
    }
}
