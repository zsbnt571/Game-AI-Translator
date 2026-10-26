using System.Text;
using System.Text.RegularExpressions;
using AssetsTools.NET;

namespace ScreenshotTranslationUiTester;

// Read the serialized Script format, never a game class or expression. Text IDs
// are scoped to the script: equal English strings may have different native
// translations, so this reader grants no bilingual flat-cache import authority.
internal static class UnityNaninovelText
{
    internal const string ReaderIdentity="naninovel-script-v1";
    internal sealed record Source(string Text,string ScriptPath,string TextIds,string Location,string Role,int LineIndex=-1)
    {
        internal string Context=>ReaderIdentity+"|script="+Uri.EscapeDataString(ScriptPath)+"|ids="+TextIds+"|role="+Role+(LineIndex>=0?"|line="+LineIndex:"");
    }
    private static readonly Regex Identifier=new(@"^[A-Za-z_][A-Za-z_0-9]{0,63}$",RegexOptions.CultureInvariant);
    private static AssetTypeValueField? Child(AssetTypeValueField? value,string name)=>value?.Children.FirstOrDefault(c=>c.FieldName==name&&!c.IsDummy);
    private static string? Text(AssetTypeValueField? value)=>value?.TemplateField.ValueType==AssetValueType.String?value.AsString:null;
    private static AssetTypeValueField[]? Array(AssetTypeValueField? value)
    {
        if(value is null)return null;
        var array=Child(value,"Array")??(value.TemplateField.IsArray?value:null);
        return array?.Children.Where(c=>c.FieldName!="size").ToArray();
    }
    private static string Ids(IEnumerable<string> ids)=>string.Join(",",ids.Select(Uri.EscapeDataString));
    private static bool SafePath(string path)=>path.Length is >0 and <=2048&&!path.Any(char.IsControl)&&
        !Path.IsPathRooted(path)&&!path.Split('/').Any(p=>p is "" or "." or "..");
    internal static bool Read(AssetTypeValueField value,Action<Source> add,CancellationToken token,Action<string> incomplete)
    {
        token.ThrowIfCancellationRequested();
        var map=Child(value,"textMap");var cells=Child(map,"idToText");var playlist=Child(value,"playlist");
        // Names alone also occur in unrelated assets. Require the framework's
        // serialized types and the independent playlist/path agreement.
        if(map?.TypeName!="ScriptTextMap"||cells?.TypeName!="SerializableTextMap"||playlist?.TypeName!="ScriptPlaylist")return false;
        string? originalPath=Text(Child(value,"path"));string? playlistPath=Text(Child(playlist,"scriptPath"));
        string path=(originalPath??"").Replace('\\','/');
        if(!SafePath(path)||playlistPath?.Replace('\\','/')!=path)
        {incomplete("Naninovel 剧情路径未能核对；未猜测文本身份");return true;}
        var keys=Array(Child(cells,"keys"));var values=Array(Child(cells,"values"));
        if(keys is null||values is null||keys.Length!=values.Length||keys.Length>150000)
        {incomplete("Naninovel 剧情文本 ID 与正文结构不完整或达到读取上限");return true;}
        var textById=new Dictionary<string,(string Text,int Index)>(StringComparer.Ordinal);
        for(int i=0;i<keys.Length;i++)
        {
            token.ThrowIfCancellationRequested();string? id=Text(keys[i]),text=Text(values[i]);
            if(string.IsNullOrWhiteSpace(id)||id.Length>256||id.Any(char.IsControl)||text is null||!textById.TryAdd(id,(text,i)))
            {incomplete("Naninovel 剧情含无效或重复文本 ID；未按数组位置猜测对应关系");return true;}
        }
        foreach(var pair in textById)
        {
            token.ThrowIfCancellationRequested();
            add(new(pair.Value.Text,path,Ids([pair.Key]),"/textMap/idToText/values/"+pair.Value.Index+"/id:"+Uri.EscapeDataString(pair.Key),"mapped-part"));
        }
        var registry=Child(value,"references");
        if(registry?.TemplateField.ValueType!=AssetValueType.ManagedReferencesRegistry||registry.Value?.AsManagedReferencesRegistry?.references is not {} references||references.Count>150000)
        {incomplete("Naninovel 已读取文本片段，完整台词结构尚未读出；运行时继续补译");return true;}
        var commands=Array(Child(playlist,"commands"));
        if(commands is null||commands.Length>150000)
        {incomplete("Naninovel 已读取文本片段，剧情命令结构未完整读出");return true;}
        var byRid=new Dictionary<long,AssetTypeReferencedObject>();
        foreach(var reference in references)
        {
            token.ThrowIfCancellationRequested();
            if(!byRid.TryAdd(reference.rid,reference))
            {incomplete("Naninovel 剧情命令引用重复；未拼接完整台词");return true;}
        }
        bool uncertain=false;
        for(int commandIndex=0;commandIndex<commands.Length;commandIndex++)
        {
            token.ThrowIfCancellationRequested();var rid=Child(commands[commandIndex],"rid");
            if(rid?.TemplateField.ValueType!=AssetValueType.Int64||!byRid.TryGetValue(rid.AsLong,out var command)||command.data is not {IsDummy:false} data)
            {uncertain=true;continue;}
            if(command.type is not {Namespace:"Naninovel.Commands"})continue;
            string? parameter=command.type.ClassName switch
            {"PrintText"=>"Text","AddChoice"=>"ChoiceSummary","ShowToastUI"=>"Text",_=>null};
            if(parameter is null)continue;
            var field=Child(data,parameter);
            if(field?.TypeName!="LocalizableTextParameter") {uncertain=true;continue;}
            // An unassigned parameter does not represent a missing line.
            var hasValue=Child(field,"hasValue");
            if(hasValue?.TemplateField.ValueType is not (AssetValueType.UInt8 or AssetValueType.Bool)||
                hasValue.TemplateField.ValueType==AssetValueType.UInt8&&hasValue.AsByte>1){uncertain=true;continue;}
            if(hasValue.TemplateField.ValueType==AssetValueType.Bool?!hasValue.AsBool:hasValue.AsByte==0)continue;
            var spot=Child(field,"spot");
            var commandSpot=Child(data,"playbackSpot");
            var line=Child(spot,"lineIndex");var commandLine=Child(commandSpot,"lineIndex");
            var inline=Child(spot,"inlineIndex");var commandInline=Child(commandSpot,"inlineIndex");
            if(Text(Child(spot,"scriptPath"))?.Replace('\\','/')!=path||Text(Child(commandSpot,"scriptPath"))?.Replace('\\','/')!=path||
                line?.TemplateField.ValueType!=AssetValueType.Int32||commandLine?.TemplateField.ValueType!=AssetValueType.Int32||line.AsInt<0||line.AsInt!=commandLine.AsInt||
                inline?.TemplateField.ValueType!=AssetValueType.Int32||commandInline?.TemplateField.ValueType!=AssetValueType.Int32||inline.AsInt!=commandInline.AsInt)
            {uncertain=true;continue;}
            var raw=Array(Child(Child(field,"raw"),"parts"));
            if(raw is null||raw.Length is 0 or >64){uncertain=true;continue;}
            var builder=new StringBuilder();var used=new List<string>();int tokens=0,literalLength=0;bool valid=true;
            foreach(var part in raw)
            {
                token.ThrowIfCancellationRequested();string? id=Text(Child(part,"id")),literal=Text(Child(part,"text")),expression=Text(Child(part,"expression"));
                if(id is null||literal is null||expression is null){valid=false;break;}
                if(id.Length>0)
                {
                    if(literal.Length>0||expression.Length>0||!textById.TryGetValue(id,out var source)){valid=false;break;}
                    builder.Append(source.Text);literalLength+=source.Text.Length;used.Add(id);
                }
                else if(expression.Length>0)
                {
                    // Only a named placeholder; no function, member access,
                    // arithmetic, evaluation or guessed player name is allowed.
                    if(literal.Length>0||!Identifier.IsMatch(expression)){valid=false;break;}
                    builder.Append('{').Append(expression).Append('}');tokens++;
                }
                else {builder.Append(literal);literalLength+=literal.Length;}
                if(builder.Length>6000){valid=false;break;}
            }
            // Match the existing startup TextLookup's conservative template
            // admission. Short or adjacent-token patterns remain runtime work.
            if(valid&&builder.Length==0)continue;
            if(!valid||tokens>8||tokens>0&&(literalLength<12||Regex.IsMatch(builder.ToString(),@"\}\{")))
            {uncertain=true;continue;}
            string role=tokens>0?"display-template":"display-line";
            add(new(builder.ToString(),path,Ids(used),"/playlist/commands/"+commandIndex+"/rid:"+command.rid+"/"+parameter,role,line.AsInt));
        }
        if(uncertain)incomplete("Naninovel 部分动态台词或命令尚不能安全预读，运行时继续补译；未执行表达式");
        return true;
    }
}
