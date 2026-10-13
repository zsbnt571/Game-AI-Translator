using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Only scalar entries owned by Fusion are edited. All other Variant data, comments
// and formatting remain opaque. The game is allowed to save its own settings.
internal static class GodotOverrideConfiguration
{
    internal sealed record Entry(string Section,string Key,string Value,string? Before=null);
    internal const int MaxBytes=1024*1024;
    internal static string Decode(byte[] bytes)
    {
        if(bytes.Length>MaxBytes)throw new IOException("游戏配置超过 1 MB，未修改。");
        return new UTF8Encoding(false,true).GetString(bytes);
    }
    internal static Entry[] Entries(string script,bool isolated)=>isolated
        ?[new("autoload","FusionTranslation",JsonSerializer.Serialize("*"+script.Replace('\\','/'))),
          new("application","config/use_custom_user_dir","false"),new("application","config/custom_user_dir_name","\"\"")]
        :[new("autoload","FusionTranslation",JsonSerializer.Serialize("*"+script.Replace('\\','/')))];
    private sealed record Line(int Start,int Length,string Text,string Section,bool Header,bool TopLevel);
    private static List<Line> Parse(string text)
    {
        if(Encoding.UTF8.GetByteCount(text)>MaxBytes)throw new IOException("游戏配置超过 1 MB，未修改。");
        List<Line> lines=[];string section="";bool quoted=false,escape=false;int depth=0;
        for(int start=0;start<text.Length;)
        {
            int end=text.IndexOf('\n',start);if(end<0)end=text.Length;else end++;
            string raw=text[start..end],trim=raw.Trim().TrimStart('\uFEFF');bool header=false,topLevel=!quoted&&depth==0;
            if(!quoted&&depth==0&&trim.StartsWith('['))
            {
                int close=trim.IndexOf(']');
                string tail=close<0?"":trim[(close+1)..].TrimStart();
                if(close<1||tail.Length>0&&!tail.StartsWith(';')&&!tail.StartsWith('#'))
                    throw new IOException("游戏配置段落无法安全识别，未修改。");
                section=trim[1..close];header=true;
            }
            lines.Add(new(start,end-start,raw,section,header,topLevel));
            if(!header)foreach(char c in raw)
            {
                if(quoted){if(escape)escape=false;else if(c=='\\')escape=true;else if(c=='\"')quoted=false;}
                else if(c is ';' or '#')break;
                else if(c=='\"')quoted=true;
                else if(c is '(' or '[' or '{')depth++;
                else if(c is ')' or ']' or '}'){if(--depth<0)throw new IOException("游戏配置结构无效，未修改。");}
            }
            start=end;
        }
        if(quoted||depth!=0)throw new IOException("游戏配置结构不完整，未修改。");
        return lines;
    }
    private static (Line? Line,string? Value) Find(List<Line> lines,Entry entry)
    {
        if(lines.Count(l=>l.Header&&l.Section==entry.Section)>1)throw new IOException("游戏配置包含重复段落，未修改。");
        var matches=lines.Where(l=>l.TopLevel&&!l.Header&&l.Section==entry.Section&&l.Text.TrimStart().StartsWith(entry.Key,StringComparison.Ordinal))
            .Where(l=>l.Text.TrimStart()[entry.Key.Length..].TrimStart().StartsWith('=')).ToArray();
        if(matches.Length>1)throw new IOException("游戏配置包含重复翻译设置，未修改。");
        if(matches.Length==0)return(null,null);
        string value=matches[0].Text.TrimStart()[entry.Key.Length..].TrimStart()[1..].Trim();
        // Scalar ownership includes the complete value. Comments added by a third
        // party are not silently discarded or confused with a different path.
        return(matches[0],value);
    }
    private static bool Equal(string? a,string b)
    {
        if(a==b)return true;
        try{return a is not null&&a.StartsWith('"')&&b.StartsWith('"')&&JsonSerializer.Deserialize<string>(a)==JsonSerializer.Deserialize<string>(b);}catch(JsonException){return false;}
    }
    internal static bool Owns(string text,Entry[] entries)
    {
        try{var lines=Parse(text);return entries.All(e=>Equal(Find(lines,e).Value,e.Value));}catch(IOException){return false;}
    }
    internal static (string Text,Entry[] Entries) Install(string text,Entry[] entries)
    {
        List<Entry> recorded=[];string newline=text.Contains("\r\n")?"\r\n":"\n";
        foreach(var e in entries)
        {
            var lines=Parse(text);var found=Find(lines,e);
            if(e.Section=="autoload"&&found.Line is not null)throw new IOException("游戏已有同名自动加载组件，未覆盖。");
            recorded.Add(e with{Before=found.Line?.Text});
            string added=e.Key+"="+e.Value+newline;
            if(found.Line is {} old)text=text.Remove(old.Start,old.Length).Insert(old.Start,added);
            else if(lines.FirstOrDefault(l=>l.Header&&l.Section==e.Section) is {} header)
                text=text.Insert(header.Start+header.Length,(header.Text.EndsWith('\n')?"":newline)+added);
            else text+=(text.Length==0||text.EndsWith('\n')?"":newline)+"["+e.Section+"]"+newline+added;
        }
        return(text,recorded.ToArray());
    }
    internal static string Restore(string text,Entry[] entries)
    {
        if(!Owns(text,entries))throw new IOException("翻译组件设置被改变，已保留现有配置。");
        foreach(var e in entries)
        {
            var found=Find(Parse(text),e).Line!;
            text=text.Remove(found.Start,found.Length).Insert(found.Start,e.Before??"");
        }
        return text;
    }
}
