using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed record LibraryDisplayName(string Text="",string Source="",string Original="");
internal sealed record LibraryAnnotation(string ChineseName="",string Note="",string Color="yellow",bool Bookmark=false,string Engine="",string Tags="",string ChineseNameSource="",string ChineseNameOriginal="",Dictionary<string,LibraryDisplayName>? LocalizedNames=null)
{
    // Legacy Chinese fields stay readable by old versions and are never reused
    // as an English/Russian translation. Custom original titles are separate.
    [System.Text.Json.Serialization.JsonIgnore]
    internal string DisplayName => NameFor(UiLanguage.Current).Text;
    internal LibraryDisplayName NameFor(ApplicationLanguage language)
    {
        var name=language==ApplicationLanguage.SimplifiedChinese?new LibraryDisplayName(ChineseName,ChineseNameSource,ChineseNameOriginal)
            :LocalizedNames?.GetValueOrDefault(InterfaceDisplayLanguage.Code(language))??new();
        return name.Source=="manual"?name:name with{Text=GameWindowNames.WithoutTranslatedEngineHints(name.Text,name.Original),Original=GameWindowNames.WithoutEngineHints(name.Original)};
    }
    internal LibraryAnnotation WithName(ApplicationLanguage language,LibraryDisplayName name)
    {
        if(language==ApplicationLanguage.SimplifiedChinese)return this with{ChineseName=name.Text,ChineseNameSource=name.Source,ChineseNameOriginal=name.Original};
        var names=new Dictionary<string,LibraryDisplayName>(LocalizedNames??new(),StringComparer.Ordinal){[InterfaceDisplayLanguage.Code(language)]=name};
        return this with{LocalizedNames=names};
    }
}

// Optional display metadata; game paths, installation ledgers and history remain in their own stores.
internal sealed class LibraryMetadata
{
    private static readonly object Gate=new();
    private readonly string file=Path.Combine(AppDataPaths.Root,"game-library-notes.json");
    private Dictionary<string,LibraryAnnotation> values=new(StringComparer.Ordinal);
    internal string Notice {get;private set;}="";
    internal LibraryMetadata()=>Reload();
    internal void Reload()
    {
        if(!File.Exists(file))return;
        try
        {
            values=Read();Notice="";
        }
        catch{values=new(StringComparer.Ordinal);Notice="游戏备注无法读取，原文件保留；暂不能保存备注。";}
    }
    private Dictionary<string,LibraryAnnotation> Read()
    {
        if(!File.Exists(file))return new(StringComparer.Ordinal);
        if(new FileInfo(file).Length>4_000_000)throw new InvalidDataException();
        var result=JsonSerializer.Deserialize<Dictionary<string,LibraryAnnotation>>(File.ReadAllText(file))??throw new InvalidDataException();
        if(result.Any(p=>!Guid.TryParseExact(p.Key,"N",out _)||p.Value is null||p.Value.ChineseName is null||p.Value.Note is null||p.Value.Tags is null||p.Value.LocalizedNames?.Any(n=>n.Key is not ("en" or "ru")||n.Value is null||n.Value.Text is null||n.Value.Text.Length>120)==true))throw new InvalidDataException();
        return result;
    }
    internal LibraryAnnotation Get(string id)=>values.GetValueOrDefault(id)??new();
    internal void RenameCategory(string category,string name)
    {
        if(Notice.Length>0)throw new IOException(Notice);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        lock(Gate)
        {
            using var ownership=new FileStream(file+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var next=Read();bool changed=false;
            foreach(var pair in next.ToArray())
            {
                var tags=pair.Value.Tags.Split([',','，',';','；'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
                if(tags.Contains(name,StringComparer.Ordinal))throw new ArgumentException("已有同名分类，请换一个名称。");
                if(!tags.Contains(category,StringComparer.Ordinal))continue;
                string renamed=string.Join(", ",tags.Select(t=>t==category?name:t).Distinct(StringComparer.Ordinal));
                if(renamed.Length>160)throw new ArgumentException("重命名后分类标签过长，请使用较短的名称。");
                next[pair.Key]=pair.Value with{Tags=renamed};changed=true;
            }
            if(changed)Persist(next);else values=next;
        }
    }
    internal void RemoveCategory(string category)
    {
        if(string.IsNullOrWhiteSpace(category))throw new ArgumentException("分类名称不能为空");
        if(Notice.Length>0)throw new IOException(Notice);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        lock(Gate)
        {
            using var ownership=new FileStream(file+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            // Read the latest document and alter only exact tag tokens. In particular,
            // deleting "1" must preserve "11", Chinese names and unrelated annotations.
            var next=Read();bool changed=false;
            foreach(var pair in next.ToArray())
            {
                var tags=pair.Value.Tags.Split([',','，',';','；'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
                if(!tags.Contains(category,StringComparer.Ordinal))continue;
                next[pair.Key]=pair.Value with{Tags=string.Join(", ",tags.Where(tag=>tag!=category))};changed=true;
            }
            if(changed)Persist(next);else values=next;
        }
    }
    internal void UpdateMany(IEnumerable<string> ids,Func<LibraryAnnotation,LibraryAnnotation> update)
    {
        if(Notice.Length>0)throw new IOException(Notice);
        var keys=ids.Distinct(StringComparer.Ordinal).ToArray();
        if(keys.Any(id=>!Guid.TryParseExact(id,"N",out _)))throw new ArgumentException("游戏标识无效");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        lock(Gate)
        {
            using var ownership=new FileStream(file+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var next=Read();
            foreach(var id in keys){var value=update(next.GetValueOrDefault(id)??new());if(value.Tags.Length>160)throw new ArgumentException("分类标签过长");next[id]=value;}
            Persist(next);
        }
    }
    internal void Save(string id,LibraryAnnotation value)
    {
        if(Notice.Length>0)throw new IOException(Notice);
        if(!Guid.TryParseExact(id,"N",out _)||value.ChineseName.Length>120||value.Note.Length>120||value.Tags.Length>160)throw new ArgumentException("名称或备注过长");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        lock(Gate)
        {
        using var ownership=new FileStream(file+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        // Apply only the edited fields to the latest document. An engine lookup or
        // a second window must not replace a newer Chinese name with its stale copy.
        var before=Get(id);var next=Read();var current=next.GetValueOrDefault(id)??new();
        var merged=current with{
            ChineseName=value.ChineseName!=before.ChineseName?value.ChineseName:current.ChineseName,
            ChineseNameSource=value.ChineseName!=before.ChineseName?"manual":current.ChineseNameSource,
            ChineseNameOriginal=value.ChineseName!=before.ChineseName?"":current.ChineseNameOriginal,
            Note=value.Note!=before.Note?value.Note:current.Note,
            Color=value.Color!=before.Color?value.Color:current.Color,
            Bookmark=value.Bookmark!=before.Bookmark?value.Bookmark:current.Bookmark,
            Engine=value.Engine!=before.Engine?value.Engine:current.Engine,
            Tags=value.Tags!=before.Tags?value.Tags:current.Tags};
        foreach(var language in new[]{ApplicationLanguage.English,ApplicationLanguage.Russian})
        {
            var name=value.NameFor(language);
            if(name.Text!=before.NameFor(language).Text)
            {
                if(name.Text.Length>120)throw new ArgumentException("名称或备注过长");
                merged=merged.WithName(language,name with{Source="manual",Original=""});
            }
        }
        if(merged==current&&next.ContainsKey(id)){values=next;return;}
        next[id]=merged;Persist(next);
        }
    }
    internal bool TrySaveGeneratedName(string id,string original,string translated,LibraryAnnotation expected,ApplicationLanguage? language=null)
    {
        var target=language??UiLanguage.Current;
        if(Notice.Length>0)throw new IOException(Notice);
        if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("游戏标识无效");
        translated=LibraryNameTranslation.Validate(translated,target);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        lock(Gate)
        {
            using var ownership=new FileStream(file+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var next=Read();var current=next.GetValueOrDefault(id)??new();values=next;
            if(current.NameFor(target)!=expected.NameFor(target)||!LibraryNameTranslation.NeedsName(original,current,target))return false;
            next[id]=current.WithName(target,new(translated,"generated",original));Persist(next);return true;
        }
    }
    private void Persist(Dictionary<string,LibraryAnnotation> next)
    {
        var temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,next,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}
            if(File.Exists(file))File.Replace(temp,file,file+".previous");else File.Move(temp,file);
            values=next;
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
