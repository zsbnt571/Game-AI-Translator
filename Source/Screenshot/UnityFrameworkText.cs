using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetsTools.NET;

namespace ScreenshotTranslationUiTester;

// Format descriptions only. No plugin code, expressions or script commands run.
// The same reader sees TextAsset/loose JSON and decoded serialized MonoBehaviours.
internal static class UnityFrameworkText
{
    private readonly struct Node(JsonElement json,AssetTypeValueField? field=null)
    {
        internal bool Exists=>field is not null||json.ValueKind!=JsonValueKind.Undefined;
        internal string? Text=>field is not null?field.TemplateField.ValueType==AssetValueType.String?field.AsString:null:json.ValueKind==JsonValueKind.String?json.GetString():null;
        internal int? Number=>field is not null?field.TemplateField.ValueType==AssetValueType.Int32?field.AsInt:null:json.ValueKind==JsonValueKind.Number&&json.TryGetInt32(out int i)?i:null;
        internal Node Child(string name)
        {
            if(field is not null)return new(default,field.Children.FirstOrDefault(f=>f.FieldName==name));
            return json.ValueKind==JsonValueKind.Object&&json.TryGetProperty(name,out var value)?new(value):default;
        }
        internal IEnumerable<(string Name,Node Value)> Children()
        {
            if(field is not null)
            {
                var array=field.Children.FirstOrDefault(f=>f.FieldName=="Array");
                var children=array?.Children??field.Children;bool indexed=array is not null||field.TemplateField.IsArray;int i=0;
                foreach(var child in children)
                {if(indexed&&child.FieldName=="size")continue;yield return(indexed?""+(i++):child.FieldName,new(default,child));}
            }
            else if(json.ValueKind==JsonValueKind.Object)foreach(var p in json.EnumerateObject())yield return(p.Name,new(p.Value));
            else if(json.ValueKind==JsonValueKind.Array){int i=0;foreach(var value in json.EnumerateArray())yield return(""+(i++),new(value));}
        }
        internal Node[] Array()=>Children().Where(p=>int.TryParse(p.Name,out _)||p.Name=="data").Select(p=>p.Value).ToArray();
    }
    private static readonly HashSet<string> DialogueFields=new(StringComparer.OrdinalIgnoreCase)
        {"Dialogue Text","Menu Text","Display Name","Description","Title","Success Description","Failure Description"};
    internal static bool Read(JsonElement root,Action<string,string?,string> add,CancellationToken token,Action<string> incomplete)
        =>Read(new Node(root),add,token,incomplete);
    internal static bool Read(AssetTypeValueField root,Action<string,string?,string> add,CancellationToken token,Action<string> incomplete)
        =>Read(new Node(default,root),add,token,incomplete);
    private static bool Read(Node root,Action<string,string?,string> add,CancellationToken token,Action<string> incomplete)
    {
        Node source=root.Child("mSource");string prefix="/mSource";if(!source.Exists){source=root;prefix="";}
        if(source.Child("mTerms").Exists&&source.Child("mLanguages").Exists)
        {
            var languages=source.Child("mLanguages").Array();var terms=source.Child("mTerms").Array();
            if(languages.Length>256||terms.Length>150000){incomplete("I2 表超过本次读取上限");return true;}
            string[] codes=languages.Select(l=>
            {
                string code=UnityTextResourceLanguageSelection.DeclaredLocale(l.Child("Code").Text);
                return code!="und-declared"?code:UnityTextResourceLanguageSelection.Locale(l.Child("Name").Text??"")??"";
            }).ToArray();
            if(codes.All(c=>c.Length==0)&&terms.Length>0)incomplete("I2 未提供可识别的语言列；未猜测第一列为原文");
            for(int row=0;row<terms.Length;row++)
            {
                token.ThrowIfCancellationRequested();var term=terms[row];var type=term.Child("TermType");
                // Missing type is accepted by older exports; an explicit non-text
                // term (font, audio, texture, object) is never translated.
                if(type.Exists&&type.Number!=0&&!string.Equals(type.Text,"Text",StringComparison.OrdinalIgnoreCase))continue;
                var cells=term.Child("Languages").Array();
                for(int column=0;column<Math.Min(codes.Length,cells.Length);column++)
                    if(codes[column].Length>0&&cells[column].Text is {} text&&UnityEmbeddedCatalog.IsDisplayText(text))
                        add(text,codes[column],prefix+"/mTerms/"+row+"/Languages/"+column);
            }
            return true;
        }
        if(!root.Child("conversations").Exists||!root.Child("actors").Exists)return false;
        int nodes=0;
        void Walk(Node node,string path,int depth)
        {
            token.ThrowIfCancellationRequested();if(++nodes>500000||depth>64){incomplete("Dialogue System 字段超过本次读取上限");return;}
            if(node.Child("fields").Exists)
            {
                var fields=node.Child("fields").Array();
                for(int i=0;i<fields.Length;i++)
                {
                    string title=fields[i].Child("title").Text??"";
                    if(DialogueFields.Contains(title)&&fields[i].Child("value").Text is {} text)
                        foreach(var part in SerializedDialogueText.VisibleParts(text))add(part,null,path+"/fields/"+i+"/value");
                }
            }
            foreach(var (name,child) in node.Children())
            {if(nodes>500000)break;if(name is not ("fields" or "scripts" or "conditions" or "variables" or "luaScript" or "sequence"))Walk(child,path+"/"+name.Replace("~","~0").Replace("/","~1"),depth+1);}
        }
        Walk(root,"",0);return true;
    }
    internal static bool ReadNaninovel(string name,string body,Action<string,string> add,CancellationToken token,Action<string> incomplete)
    {
        if(!Path.GetExtension(name).Equals(".nani",StringComparison.OrdinalIgnoreCase)&&
            !(Path.GetExtension(name).Length==0&&Regex.IsMatch(body,@"(?m)^\s*@(?:choice|char|back|print|goto)\b")&&
              Regex.IsMatch(body,@"(?m)^\s*(?:#\s*[\p{L}\p{N}_]|[\p{L}\p{N}_.]+:\s)")))return false;
        int line=0;bool partial=false;
        foreach(string raw in body.Replace("\r\n","\n").Split('\n'))
        {
            token.ThrowIfCancellationRequested();line++;string value=raw.Trim();
            if(value.Length==0||value[0] is ';' or '#')continue;
            if(value.StartsWith('@'))
            {
                if(!Regex.IsMatch(value,@"^@choice\s+"))continue;
                var choice=Regex.Match(value,"^@choice\\s+\"(?<text>(?:[^\"\\\\]|\\\\.)*)\"(?:\\s|$)");
                if(!choice.Success){partial=true;continue;}
                try{value=JsonSerializer.Deserialize<string>("\""+choice.Groups["text"].Value+"\"")!;}
                catch(JsonException){partial=true;continue;}
            }
            else value=Regex.Replace(value,@"^[\p{L}\p{N}_.]+:\s*","");
            // Inline commands can split/alter display state. Do not put shortened
            // fragments into an exact-source cache and call them whole dialogue.
            if(value.Contains('[')||value.Contains(']')||value.Contains('{')||value.Contains('}')){partial=true;continue;}
            if(UnityEmbeddedCatalog.IsDisplayText(value))add(value,"line:"+line.ToString(CultureInfo.InvariantCulture));
        }
        if(partial)incomplete("Naninovel 含动态表达式或内联命令；未将片段当作整句，运行时继续补译");
        return true;
    }

    // Some TextAssets are line records prefixed with metadata, not JSON arrays.
    // Require repeated, well-formed known directives and a body on each record.
    // This deliberately does not guess arbitrary bracketed prose, malformed JSON,
    // dynamic interpolation or the engine's string-unescaping rules.
    internal static bool ReadTaggedLines(string name,string body,Action<string,string> add,CancellationToken token,Action<string> incomplete)
    {
        if(Path.GetExtension(name).Equals(".json",StringComparison.OrdinalIgnoreCase))return false;
        var lines=body.Replace("\r\n","\n").Split('\n');int evidence=0;
        bool Prefix(string line,out int offset)
        {
            offset=0;bool found=false;int directives=0;
            while(offset<line.Length&&line[offset]=='[')
            {
                token.ThrowIfCancellationRequested();if(++directives>64)return false;
                int close=line.IndexOf(']',offset+1);if(close<0)return false;
                string directive=line[(offset+1)..close];
                if(!Regex.IsMatch(directive,@"^(?:(?:id|donation)\s*,\s*\d+|image\s*,\s*[A-Za-z0-9_.-]+|condition\s*,\s*[A-Za-z0-9_]+\s*,\s*(?:above|below|equals|isset|isnotset)\s*,\s*[^\[\]{}\r\n]+|onetime|never|sub)$",RegexOptions.CultureInvariant))return false;
                found=true;offset=close+1;
            }
            return found&&offset<line.Length&&!string.IsNullOrWhiteSpace(line[offset..]);
        }
        foreach(string raw in lines)
        {
            token.ThrowIfCancellationRequested();string line=raw.TrimStart('\ufeff',' ','\t');
            if(Prefix(line,out _)&&++evidence>=2)break;
        }
        if(evidence<2)return false;
        bool partial=false;int number=0;
        foreach(string raw in lines)
        {
            token.ThrowIfCancellationRequested();number++;string line=raw.TrimStart('\ufeff',' ','\t');
            if(string.IsNullOrWhiteSpace(line)||line.StartsWith("//",StringComparison.Ordinal))continue;
            if(!Prefix(line,out int offset)){partial=true;continue;}
            string text=line[offset..];
            if(text.Contains('{')||text.Contains('}')||text.Contains('[')||text.Contains(']')||text.Contains('\\')||text.Contains('|')){partial=true;continue;}
            if(UnityEmbeddedCatalog.IsDisplayText(text))add(text,"line:"+number.ToString(CultureInfo.InvariantCulture)+"/body");
        }
        if(partial)incomplete("标记行资源含动态表达式、未确认转义或分隔符、未知指令；相应行未作为完整正文提取，运行时继续补译");
        return true;
    }
}
