using System.Diagnostics;
using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class RgssTextCatalog
{
    internal static RpgTextCatalogResult Read(string exe,CancellationToken token=default)
    {
        var engine=RgssGameAdapter.Detect(exe)??throw new IOException("RGSS 资源结构已变化。");
        var texts=new HashSet<string>(StringComparer.Ordinal);var issues=new List<CatalogReadIssue>();int files=0;var clock=Stopwatch.StartNew();
        void Check(){token.ThrowIfCancellationRequested();if(clock.Elapsed.TotalSeconds>25||texts.Count>=100000)throw new IOException("RGSS 文本读取达到本轮限制。");}
        void Add(string value){if(value.Length<=6000&&RpgTranslationText.HasTranslatableText(value,EmbeddedTextSyntax.Rpg))texts.Add(value);}
        void ReadOne(string name,Func<byte[]> bytes)
        {
            Check();if(Path.GetFileNameWithoutExtension(name).Equals("Scripts",StringComparison.OrdinalIgnoreCase)){issues.Add(new(CatalogIssueKind.Incomplete,name,"脚本中的动态文字由运行时采集。"));return;}
            try{foreach(var value in DisplayFields(new RubyMarshalData(bytes(),Check).Read(),Check))Add(value);files++;}
            catch(Exception e)when(e is IOException or InvalidDataException or NotSupportedException or DecoderFallbackException or OverflowException){issues.Add(new(CatalogIssueKind.Failed,name,SafeDiagnosticOutput.ExceptionSummary(e)));}
        }
        string extension=Path.GetExtension(engine.Scripts);
        if(engine.Archive!=null)
        {
            using var pack=new RgssArchive(GameMakerGameAdapter.InGame(exe,engine.Archive),token);
            foreach(var entry in pack.Entries.Values.Where(e=>e.Name.StartsWith("Data/",StringComparison.OrdinalIgnoreCase)&&e.Name.EndsWith(extension,StringComparison.OrdinalIgnoreCase)).Take(1500))
            {ReadOne(entry.Name,()=>pack.Read(entry.Name,32*1024*1024,token));}
        }
        else
        {
            string data=GameMakerGameAdapter.InGame(exe,"Data");
            foreach(string path in Directory.EnumerateFiles(data,"*"+extension).Take(1500))
            {ReadOne(Path.GetFileName(path),()=>{if(new FileInfo(path).Length>32*1024*1024)throw new IOException("数据文件超过限制。");return File.ReadAllBytes(GameMakerGameAdapter.InGame(exe,"Data/"+Path.GetFileName(path)));});}
        }
        issues.Add(new(CatalogIssueKind.Incomplete,"RGSS","已读取事件和数据库显示字段；脚本及逐字显示的文字仍在运行时补充。"));
        return new(texts.ToArray(),files,[]){ReadCompleted=false,Report=new(false,issues.Take(128).ToArray())};
    }
    internal static IEnumerable<string> DisplayFields(RubyMarshalData.Node root,Action check)
    {
        var stack=new Stack<RubyMarshalData.Node>();var visited=new HashSet<RubyMarshalData.Node>(ReferenceEqualityComparer.Instance);stack.Push(root);
        while(stack.Count>0)
        {
            check();var n=stack.Pop();if(!visited.Add(n))continue;
            if(n.Kind=='o'&&n.Name=="RPG::EventCommand"&&n.Fields.TryGetValue("@code",out var code)&&n.Fields.TryGetValue("@parameters",out var parameters))
            {
                if(code.Number is 401 or 405 or 101&&parameters.Items.FirstOrDefault() is {} first&&first.Kind=='"')yield return Decode(first);
                else if(code.Number==102&&parameters.Items.FirstOrDefault() is {} choices)foreach(var choice in choices.Items)if(choice.Kind=='"')yield return Decode(choice);
                continue;
            }
            bool display=n.Kind=='o'&&n.Name is "RPG::Actor" or "RPG::Class" or "RPG::Skill" or "RPG::Item" or "RPG::Weapon" or "RPG::Armor" or "RPG::Enemy" or "RPG::State" or "RPG::Map" or "RPG::System" or "RPG::System::Terms" or "RPG::System::Words";
            foreach(var p in n.Fields)
            {
                if(display&&p.Value.Kind=='"'&&p.Key is "@name" or "@description" or "@nickname" or "@display_name" or "@game_title" or "@currency_unit" or "@message1" or "@message2" or "@message3" or "@message4")yield return Decode(p.Value);
                if(n.Name is "RPG::System::Terms" or "RPG::System::Words")
                {if(p.Value.Kind=='"')yield return Decode(p.Value);else foreach(var item in p.Value.Items)if(item.Kind=='"')yield return Decode(item);}
                stack.Push(p.Value);
            }
            foreach(var item in n.Items)stack.Push(item);
        }
    }
    private static string Decode(RubyMarshalData.Node node)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if(node.Encoding.Length>0)return Encoding.GetEncoding(node.Encoding,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(node.Bytes);
        try{return new UTF8Encoding(false,true).GetString(node.Bytes);}catch(DecoderFallbackException){return Encoding.GetEncoding(932,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(node.Bytes);}
    }
}
