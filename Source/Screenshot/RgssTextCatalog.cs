using System.Diagnostics;
using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class RgssTextCatalog
{
    internal static RpgTextCatalogResult Read(string exe,CancellationToken token=default)
    {
        var engine=RgssGameAdapter.Detect(exe)??throw new IOException("RGSS 资源结构已变化。");
        var texts=new HashSet<string>(StringComparer.Ordinal);var origins=new CatalogOrigins();var unlocalized=new HashSet<string>(StringComparer.Ordinal);var issues=new List<CatalogReadIssue>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int files=0;var clock=Stopwatch.StartNew();
        void Check(){token.ThrowIfCancellationRequested();if(clock.Elapsed.TotalSeconds>25||texts.Count>=100000)throw new IOException("RGSS 文本读取达到本轮限制。");}
        void Add(string value){if(value.Length<=6000&&RpgTranslationText.HasTranslatableText(value,EmbeddedTextSyntax.Rpg))texts.Add(value);}
        void ReadOne(string name,Func<byte[]> bytes)
        {
            Check();string canonical=name.Replace('\\','/');if(!canonical.StartsWith("Data/",StringComparison.OrdinalIgnoreCase))canonical="Data/"+canonical;
            if(!seen.Add(canonical))return;
            try
            {
                bool baseData=System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(name),@"^(?:Actors|Classes|Skills|Items|Weapons|Armors|Enemies|Troops|States|System|CommonEvents|Map\d+)$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                byte[] image=bytes();string hash=CatalogOrigins.Hash(image);var document=new RubyMarshalData(image,Check).Read();
                if(canonical.Equals(engine.Scripts.Replace('\\','/'),StringComparison.OrdinalIgnoreCase))
                {
                    var scripts=RgssScriptCatalog.Read(document,Check);
                    foreach(var field in scripts.Fields)
                    {
                        Add(field.Text);if(!texts.Contains(field.Text))continue;unlocalized.Add(field.Text);
                        origins.Add(new(field.Text,name,field.Location,hash,"rgss-script-display",field.Encoding,engine.Archive??"",Context:field.Context));
                    }
                    foreach(string issue in scripts.Issues)issues.Add(new(CatalogIssueKind.Incomplete,name,issue));
                    files++;return;
                }
                foreach(var field in LocatedFields(document,Check))
                {Add(field.Text);if(baseData&&texts.Contains(field.Text))unlocalized.Add(field.Text);
                    if(texts.Contains(field.Text))origins.Add(new(field.Text,name,field.Location,hash,"rgss-display-field",field.Encoding,engine.Archive??""));}
                files++;
            }
            catch(Exception e)when(e is IOException or InvalidDataException or NotSupportedException or DecoderFallbackException or OverflowException){issues.Add(new(CatalogIssueKind.Failed,name,SafeDiagnosticOutput.ExceptionSummary(e)));}
        }
        string extension=Path.GetExtension(engine.Scripts);
        if(engine.Archive!=null)
        {
            using var pack=new RgssArchive(GameMakerGameAdapter.InGame(exe,engine.Archive),token);
            ReadOne(engine.Scripts,()=>pack.Read(engine.Scripts,32*1024*1024,token));
            foreach(var entry in pack.Entries.Values.Where(e=>e.Name.StartsWith("Data/",StringComparison.OrdinalIgnoreCase)&&e.Name.EndsWith(extension,StringComparison.OrdinalIgnoreCase)).Take(1500))
            {ReadOne(entry.Name,()=>pack.Read(entry.Name,32*1024*1024,token));}
        }
        else
        {
            string data=GameMakerGameAdapter.InGame(exe,"Data");
            ReadOne(engine.Scripts,()=>{string path=GameMakerGameAdapter.InGame(exe,engine.Scripts);if(new FileInfo(path).Length>32*1024*1024)throw new IOException("脚本文件超过限制。");return File.ReadAllBytes(path);});
            foreach(string path in Directory.EnumerateFiles(data,"*"+extension).Take(1500))
            {ReadOne(Path.GetFileName(path),()=>{if(new FileInfo(path).Length>32*1024*1024)throw new IOException("数据文件超过限制。");return File.ReadAllBytes(GameMakerGameAdapter.InGame(exe,"Data/"+Path.GetFileName(path)));});}
        }
        issues.Add(new(CatalogIssueKind.Incomplete,"RGSS","已读取事件、数据库及受限脚本显示字面量；动态拼接、未支持的脚本语法及逐字显示仍在运行时补充。"));
        return new(texts.ToArray(),files,[]){Origins=origins.Items,OriginsTruncated=origins.Truncated,UnlocalizedSourceTexts=unlocalized,ReadCompleted=false,Report=new(false,issues.Take(128).ToArray())};
    }
    internal static IEnumerable<string> DisplayFields(RubyMarshalData.Node root,Action check)
        =>LocatedFields(root,check).Select(e=>e.Text);
    internal sealed record LocatedField(string Location,string Text,string Encoding);
    internal static IEnumerable<LocatedField> LocatedFields(RubyMarshalData.Node root,Action check)
    {
        var stack=new Stack<(RubyMarshalData.Node Node,string Path)>();var visited=new HashSet<RubyMarshalData.Node>(ReferenceEqualityComparer.Instance);stack.Push((root,"$"));
        while(stack.Count>0)
        {
            check();var (n,path)=stack.Pop();if(!visited.Add(n))continue;
            if(n.Kind=='o'&&n.Name=="RPG::EventCommand"&&n.Fields.TryGetValue("@code",out var code)&&n.Fields.TryGetValue("@parameters",out var parameters))
            {
                if((code.Number is 401 or 405||code.Number==101&&parameters.Items.Count==1)&&parameters.Items.FirstOrDefault() is {} first&&first.Kind=='"')
                    yield return new(path+"/@parameters/0",Decode(first),first.Encoding);
                else if(code.Number==102&&parameters.Items.FirstOrDefault() is {} choices)
                {
                    for(int i=0;i<choices.Items.Count;i++)if(choices.Items[i].Kind=='"')yield return new(path+"/@parameters/0/"+i,Decode(choices.Items[i]),choices.Items[i].Encoding);
                }
                else if(code.Number is 320 or 324 or 325&&parameters.Items.Count>1&&parameters.Items[1].Kind=='"')
                    yield return new(path+"/@parameters/1",Decode(parameters.Items[1]),parameters.Items[1].Encoding);
                continue;
            }
            bool display=n.Kind=='o'&&n.Name is "RPG::Actor" or "RPG::Class" or "RPG::Skill" or "RPG::Item" or "RPG::Weapon" or "RPG::Armor" or "RPG::Enemy" or "RPG::State" or "RPG::Map" or "RPG::System" or "RPG::System::Terms" or "RPG::System::Words";
            foreach(var p in n.Fields)
            {
                if(display&&p.Value.Kind=='"'&&p.Key is "@name" or "@description" or "@nickname" or "@display_name" or "@game_title" or "@currency_unit" or "@message1" or "@message2" or "@message3" or "@message4")yield return new(path+"/"+p.Key,Decode(p.Value),p.Value.Encoding);
                if(n.Name is "RPG::System::Terms" or "RPG::System::Words")
                {if(p.Value.Kind=='"')yield return new(path+"/"+p.Key,Decode(p.Value),p.Value.Encoding);else for(int i=0;i<p.Value.Items.Count;i++)if(p.Value.Items[i].Kind=='"')yield return new(path+"/"+p.Key+"/"+i,Decode(p.Value.Items[i]),p.Value.Items[i].Encoding);}
                stack.Push((p.Value,path+"/"+p.Key));
            }
            for(int i=n.Items.Count-1;i>=0;i--)stack.Push((n.Items[i],path+"/"+i));
        }
    }
    private static string Decode(RubyMarshalData.Node node)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if(node.Encoding.Length>0)return Encoding.GetEncoding(node.Encoding,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(node.Bytes);
        try{return new UTF8Encoding(false,true).GetString(node.Bytes);}catch(DecoderFallbackException){return Encoding.GetEncoding(932,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(node.Bytes);}
    }
}
