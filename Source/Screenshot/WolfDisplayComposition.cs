namespace ScreenshotTranslationUiTester;

// Dynamic UI labels may be joined after extraction. Reuse only complete cache
// entries with an explicit delimiter; never replace ordinary words in prose.
internal static class WolfDisplayComposition
{
    private static string Key(string text)=>text.Replace("\r\n","\n",StringComparison.Ordinal).Trim(' ','\r','\n','\t');
    internal static string Find(string source,Func<string,string?> exact,Func<string,string?>? single=null)
    {
        string key=Key(source);
        if(key.Length is <1 or >6000||key.Contains('\\')||key.Contains('\0'))return "";
        single??=exact;
        string? whole=single(key);if(!string.IsNullOrEmpty(whole))return whole;
        string[] lines=key.Split('\n');if(lines.Length>64)return "";
        var result=new List<string>(lines.Length);
        foreach(string line in lines)
        {
            string normalized=Key(line);
            if(normalized.Length==0){result.Add(line);continue;}
            string value=single(normalized)??"";
            if(value.Length==0)
            {
                string columns=Columns(normalized,single),label=LabelValue(normalized,exact);
                if(columns.Length>0&&label.Length>0&&columns!=label)return "";
                value=columns.Length==0?label:columns;
            }
            if(value.Length==0||value.IndexOfAny(['\r','\n'])>=0)return "";
            int start=0,end=line.Length;while(start<end&&" \t\r".Contains(line[start]))start++;
            while(end>start&&" \t\r".Contains(line[end-1]))end--;
            result.Add(line[..start]+value+line[end..]);
        }
        return string.Join('\n',result);
    }
    private static string Columns(string source,Func<string,string?> single)
    {
        static string Trim(string value)=>Key(Key(value).Trim('　'));
        string answer="";int candidates=0;
        for(int at=1;at<source.Length;at++)
        {
            if(source[at] is not ('\t' or '　'))continue;
            if(++candidates>16)return "";
            int end=at+1;while(end<source.Length&&source[end] is '\t' or '　')end++;
            string left=single(Key(source[..at]))??"",right=single(Key(source[end..]))??"";
            if(right.Length==0)right=single(Key(source[at..]))??"";
            if(left.Length>0&&right.Length>0&&left.IndexOfAny(['\r','\n'])<0&&right.IndexOfAny(['\r','\n'])<0)
            {
                string value=Trim(left)+source[at..end]+Trim(right);
                if(value.Length>12000||(answer.Length>0&&answer!=value))return "";
                answer=value;
            }
            at=end-1;
        }
        return answer;
    }
    private static string LabelValue(string source,Func<string,string?> exact)
    {
        if(source.Contains('\t'))return "";
        string answer="";int candidates=0;
        for(int at=1;at<source.Length;at++)
        {
            if(source[at] is not (':' or '：'))continue;
            if(++candidates>16)return "";
            int end=at+1,start=end;while(start<source.Length&&source[start]==' ')start++;
            if(start==source.Length)continue;
            string left=Key(exact(source[..end])??""),right=Key(exact(source[start..])??"");
            if(left.Length==0||left[^1] is not (':' or '：')||right.Length==0||
                left.IndexOfAny(['\\','\r','\n','\t','\0'])>=0||right.IndexOfAny(['\\','\r','\n','\t','\0'])>=0)continue;
            string value=left+source[end..start]+right;
            if(value.Length>12000||(answer.Length>0&&answer!=value))return "";
            answer=value;
        }
        return answer;
    }
}
