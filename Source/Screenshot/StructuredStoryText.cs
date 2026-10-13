using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Format-directed extraction only. No story interpreter, includes, commands,
// expressions or game assemblies are executed. Unproved fragments stay out of
// the shared exact-text cache and leave a specific coverage warning.
internal static class StructuredStoryText
{
    internal static bool ReadSource(string name,string body,Action<string> add,CancellationToken token,Action<CatalogIssueKind,string>? issue)
    {
        string extension=Path.GetExtension(name).ToLowerInvariant();
        if(extension is not (".ink" or ".yarn"))return false;
        bool yarn=extension==".yarn",inside=!yarn,partial=false,blockComment=false;
        foreach(string original in body.Replace("\r\n","\n").Split('\n'))
        {
            token.ThrowIfCancellationRequested();string text=original.Trim();
            if(blockComment){int end=text.IndexOf("*/",StringComparison.Ordinal);if(end<0)continue;text=text[(end+2)..].Trim();blockComment=false;}
            if(text.StartsWith("/*",StringComparison.Ordinal))
            {int end=text.IndexOf("*/",2,StringComparison.Ordinal);if(end<0){blockComment=true;continue;}text=text[(end+2)..].Trim();}
            if(yarn&&text=="---"){inside=true;continue;}
            if(yarn&&text=="==="){inside=false;continue;}
            if(!inside||text.Length==0||text.StartsWith("//",StringComparison.Ordinal))continue;
            if(yarn&&text.StartsWith("<<",StringComparison.Ordinal))continue;
            if(!yarn&&(text[0] is '~' or '='||text.StartsWith("->",StringComparison.Ordinal)
                ||Regex.IsMatch(text,@"^(?:VAR|CONST|LIST|INCLUDE|EXTERNAL|TODO)\b")))continue;
            int comment=text.IndexOf("//",StringComparison.Ordinal);if(comment>=0)text=text[..comment].TrimEnd();
            int tag=text.IndexOf('#');if(tag>=0)text=text[..tag].TrimEnd();
            // Escapes and inline logic need the interpreter's exact assembled
            // output; never publish a conveniently shortened fragment.
            if(text.IndexOfAny(['{','}','\\'])>=0||text.Contains("<<",StringComparison.Ordinal)||text.Contains("<>",StringComparison.Ordinal))
            {partial=true;continue;}
            if(yarn)
            {
                if(text.StartsWith("->",StringComparison.Ordinal))text=text[2..].TrimStart();
                if(text.Contains('[',StringComparison.Ordinal)){partial=true;continue;}
                Add(text);continue;
            }
            if(text.Length==0)continue;
            bool choice=text[0] is '*' or '+';
            if(choice)
            {
                text=text.TrimStart('*','+',' ');
                if(text.StartsWith('('))
                {int close=text.IndexOf(')');if(close<0){partial=true;continue;}text=text[(close+1)..].TrimStart();}
                int divert=text.IndexOf("->",StringComparison.Ordinal);if(divert>=0)text=text[..divert].TrimEnd();
                int open=text.IndexOf('['),closeChoice=text.IndexOf(']');
                if(open>=0&&closeChoice>open&&!text[(closeChoice+1)..].Contains('['))
                {
                    string prefix=text[..open];
                    Add(prefix+text[(open+1)..closeChoice]);
                    Add(prefix+text[(closeChoice+1)..]);
                }
                else if(open<0&&closeChoice<0)Add(text);
                else partial=true;
            }
            else if(text[0]=='-'||text.Contains('[')||text.Contains(']')||text.Contains("->",StringComparison.Ordinal))partial=true;
            else Add(text);
        }
        if(partial||blockComment)issue?.Invoke(CatalogIssueKind.Unsupported,"已读取静态对白和选项；含表达式、标记或跨行拼接的脚本仍需运行时捕获");
        return true;
        void Add(string value){value=value.Trim();if(value.Length>0)add(value);}
    }

    internal static bool ReadCompiledInk(JsonElement root,Action<string> add,CancellationToken token,Action<CatalogIssueKind,string>? issue)
    {
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("inkVersion",out var version))return false;
        if(version.ValueKind!=JsonValueKind.Number||!version.TryGetInt32(out int number)||number!=21||!root.TryGetProperty("root",out var container)||container.ValueKind!=JsonValueKind.Array)
        {issue?.Invoke(CatalogIssueKind.Unsupported,"Ink 编译资源版本或结构尚未支持");return true;}
        var texts=new HashSet<string>(StringComparer.Ordinal);bool incomplete=false;int nodes=0;
        void Visit(JsonElement value,int depth)
        {
            token.ThrowIfCancellationRequested();if(++nodes>200000||depth>64){incomplete=true;return;}
            if(value.ValueKind==JsonValueKind.Object)
            {
                foreach(var p in value.EnumerateObject())
                    if(p.Name!="global decl"&&p.Value.ValueKind==JsonValueKind.Array)Visit(p.Value,depth+1);
                return;
            }
            if(value.ValueKind!=JsonValueKind.Array)return;
            var line=new StringBuilder();bool unknown=false,tag=false;
            foreach(var child in value.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if(child.ValueKind==JsonValueKind.Array||child.ValueKind==JsonValueKind.Object)
                {
                    if(line.Length>0){incomplete=true;line.Clear();}
                    Visit(child,depth+1);
                    if(child.ValueKind==JsonValueKind.Object&&child.EnumerateObject().Any(p=>p.Value.ValueKind!=JsonValueKind.Array&&p.Name is not ("#f" or "#n" or "#")))unknown=true;
                    continue;
                }
                if(child.ValueKind!=JsonValueKind.String)continue;
                string item=child.GetString()!;
                if(item=="#"){tag=true;continue;}
                if(item=="/#"){tag=false;continue;}
                if(tag)continue;
                if(item.StartsWith('^')){if(line.Length+item.Length>6001){incomplete=true;unknown=true;}else line.Append(item.AsSpan(1));}
                else if(item=="\n")
                {
                    if(unknown)incomplete=true;
                    else if(line.Length>0)texts.Add(line.ToString());
                    line.Clear();unknown=false;
                }
                else if(item is not ("done" or "end"))unknown=true;
            }
            if(line.Length>0)incomplete=true;
        }
        Visit(container,0);
        // A partial span may divert/glue into another container. Without the
        // runtime state, emitting only that container's suffix would corrupt
        // exact cache identities, so retain no strings from an ambiguous file.
        if(incomplete)issue?.Invoke(CatalogIssueKind.Unsupported,"Ink 资源含动态或跨容器文字，未将片段当作完整原句；由运行时补充");
        else foreach(string text in texts)add(text);
        return true;
    }
}
