using System.Text;

namespace ScreenshotTranslationUiTester;

// A bounded lexer for literal text the Tyrano parser sends to text.showMessage,
// and the text attribute of the three display tags used by our runtime bridge.
// It never evaluates expressions, macro arguments, HTML or JavaScript.
internal static class TyranoTextCatalog
{
    internal sealed record Field(string Location,string Text,string Kind);
    internal static bool PreservesAttributeWhitespace(string config,Action check)
    {
        bool preserve=false;
        foreach(string raw in config.Replace("\r\n","\n").Split('\n'))
        {
            check();string line=raw.Trim();if(!line.StartsWith(';'))continue;
            int comment=line.IndexOf("//",StringComparison.Ordinal);if(comment>=0)line=line[..comment];
            line=line.Replace(";","").Replace("\"","");int equal=line.IndexOf('=');
            if(equal>=0&&line[..equal].Trim()=="KeepSpaceInParameterValue")preserve=line[(equal+1)..].Trim()=="3";
        }
        return preserve;
    }
    internal static Field[] Read(string content,Action check,bool preserveAttributeWhitespace=false)
    {
        var result=new List<Field>();var paragraph=new List<string>();int paragraphStart=0;
        string block="";bool comment=false;
        void Add(string text,string at,string kind)
        {
            check();if(text.Length>6000)throw new ExperimentalRpgCatalog.LimitException("Tyrano 文本超过 6000 字符。");
            if(RpgTextCatalog.IsText(text)){if(result.Count>=100000)throw new ExperimentalRpgCatalog.LimitException("Tyrano 文本数量超过限制。");result.Add(new(at,text,kind));}
        }
        void Flush()
        {
            if(paragraph.Count>1)Add(string.Join("\n",paragraph),$"line:{paragraphStart+1}/paragraph","tyrano-paragraph");paragraph.Clear();
        }
        string[] lines=content.Replace("\r\n","\n").Split('\n');
        for(int lineNo=0;lineNo<lines.Length;lineNo++)
        {
            check();string line=lines[lineNo].Trim();string location=$"line:{lineNo+1}";
            if(block.Length>0)
            {
                // Only an actual closing command at the start of a line ends
                // isolation. A marker inside a quoted script string does not.
                string end=block=="iscript"?"endscript":"endhtml";
                if(line=="["+end+"]"||line=="@"+end)block="";
                continue;
            }
            if(comment){if(line=="*/")comment=false;continue;}
            if(line=="/*"){Flush();comment=true;continue;}
            if(line.Length==0||line[0] is ';' or '*' or '#'){Flush();continue;}
            if(line[0]=='@')
            {
                Flush();Tag(line[1..],location+"/tag:0");continue;
            }
            // Tyrano's leading '_' is an instruction to keep following spaces.
            if(line[0]=='_')line=line[1..];
            if(!line.Contains('[')&&!line.Contains('\\'))
            {
                if(paragraph.Count==0)paragraphStart=lineNo;
                if(paragraph.Sum(s=>s.Length+1)+line.Length>6000)Flush();
                if(paragraph.Count==0)paragraphStart=lineNo;paragraph.Add(line);Add(line,location+"/segment:0","tyrano-text");continue;
            }
            Flush();var text=new StringBuilder();int part=0;
            void Text(){if(text.Length>0){Add(text.ToString(),location+$"/segment:{part++}","tyrano-text");text.Clear();}}
            for(int at=0;at<line.Length;at++)
            {
                check();char c=line[at];
                if(c=='\\'){if(at+1<line.Length)text.Append(line[++at]);continue;}
                if(c!='['){text.Append(c);continue;}
                Text();int start=at+1,depth=1;char quote='\0';bool escaped=false;
                for(at++;at<line.Length;at++)
                {
                    check();c=line[at];
                    if(quote!='\0')
                    {if(escaped)escaped=false;else if(c=='\\')escaped=true;else if(c==quote)quote='\0';continue;}
                    if(c is '\'' or '"'){quote=c;continue;}
                    if(c=='['){if(++depth>32)throw new InvalidDataException("Tyrano 标签层级过深。");}
                    else if(c==']'&&--depth==0)break;
                }
                if(at>=line.Length||quote!='\0')throw new InvalidDataException("Tyrano 标签或引号未结束。");
                Tag(line[start..at],location+$"/tag:{part++}");
                if(block.Length>0)break;
            }
            if(block.Length==0)Text();
        }
        Flush();if(block.Length>0||comment)throw new InvalidDataException("Tyrano 脚本、HTML 或注释区域未结束。");return result.ToArray();

        void Tag(string body,string at)
        {
            int p=0;while(p<body.Length&&char.IsWhiteSpace(body[p]))p++;int begin=p;
            while(p<body.Length&&!char.IsWhiteSpace(body[p]))p++;
            string name=body[begin..p].ToLowerInvariant();
            if(name is "iscript" or "html"){block=name;return;}
            if(name is not ("ptext" or "mtext" or "glink"))return;
            var attributes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            while(p<body.Length)
            {
                check();while(p<body.Length&&char.IsWhiteSpace(body[p]))p++;if(p==body.Length)break;
                begin=p;while(p<body.Length&&!char.IsWhiteSpace(body[p])&&body[p]!='=')p++;
                string key=body[begin..p];while(p<body.Length&&char.IsWhiteSpace(body[p]))p++;
                if(key.Length==0||p==body.Length||body[p]!='=')throw new InvalidDataException("Tyrano 显示标签属性结构未识别。");
                p++;while(p<body.Length&&char.IsWhiteSpace(body[p]))p++;if(p==body.Length)throw new InvalidDataException("Tyrano 显示标签属性值缺失。");
                var value=new StringBuilder();char quote=body[p] is '\'' or '"'?body[p++]:'\0';bool closed=quote=='\0';
                while(p<body.Length)
                {
                    check();char c=body[p++];
                    if(quote!='\0'&&c==quote){closed=true;break;}
                    if(quote=='\0'&&char.IsWhiteSpace(c))break;
                    if(c=='\\'&&p<body.Length)c=body[p++];value.Append(c);
                }
                if(!closed||attributes.ContainsKey(key))throw new InvalidDataException("Tyrano 显示标签属性重复或引号未结束。");
                attributes.Add(key,preserveAttributeWhitespace?value.ToString():value.ToString().Trim());
            }
            if(attributes.TryGetValue("text",out var literal)&&!literal.StartsWith('&')&&!literal.StartsWith('%'))Add(literal,at+"/text","tyrano-display-attribute");
        }
    }
}
