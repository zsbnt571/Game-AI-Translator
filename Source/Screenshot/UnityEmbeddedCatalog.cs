using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace ScreenshotTranslationUiTester;

/// <summary>Read-only structured Unity asset extraction. No game assembly is loaded or executed.</summary>
internal static class UnityEmbeddedCatalog
{
    private static readonly Regex TextField=new(@"(?:^m_?text$|dialog|speech|subtitle|caption|description|display.?name|localized|localised|(?:^|_)message$|^(?:m_?)?(?:desc|helpText|body|prompt|title|label|text|content|sentence)$)",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    private static readonly Regex AssetCandidate=new(@"(?:\.assets$|^level\d+$|^globalgamemanagers$|\.bundle$|\.unity3d$|\.assetbundle$|^cab-|^[a-f\d]{32}$)",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    internal static RpgTextCatalogResult Read(string exe,CancellationToken cancellation)=>Read(exe,cancellation,SourceLanguageMode.Auto);
    internal static RpgTextCatalogResult Read(string exe,CancellationToken cancellation,SourceLanguageMode sourceLanguage)
    {
        var game=UnityEmbeddedAdapter.Detect(exe);if(game is null)return new([],0,[]);
        string types=Path.Combine(AppContext.BaseDirectory,"adapters","unity","classdata.tpk");
        var failures=new HashSet<string>(StringComparer.Ordinal);int fileCount=0,objectCount=0;
        var selected=new UnityTextResourceLanguageSelection(sourceLanguage);
        var localizedTables=new UnityLocalizedTableSelection(sourceLanguage);
        void Fail(string path,string reason){if(failures.Count<256)failures.Add(path+"："+reason);}
        void Visit(AssetTypeValueField field,bool context,string family,string? locale=null,int depth=0)
        {
            cancellation.ThrowIfCancellationRequested();if(depth>64||field.IsDummy)return;
            string name=field.TemplateField.Name;
            bool excluded=UnityTextResourceLanguageSelection.IsIdentifier(name)||name.Equals("name",StringComparison.OrdinalIgnoreCase)||name.Equals("m_Name",StringComparison.OrdinalIgnoreCase);
            locale=UnityTextResourceLanguageSelection.Locale(name)??locale;
            bool textContext=!excluded&&(context||TextField.IsMatch(name)||name is "Languages" or "m_Localized"||UnityTextResourceLanguageSelection.Locale(name) is not null);
            if(field.TemplateField.ValueType==AssetValueType.String)
            {if(textContext)selected.AddValue(family,locale,field.AsString);return;}
            if(excluded)return;
            var paired=UnityTextResourceLanguageSelection.PairedStringFields(field.Children.Where(child=>child.TemplateField.ValueType==AssetValueType.String).Select(child=>child.TemplateField.Name));
            foreach(var child in field.Children.Where(child=>child.TemplateField.ValueType==AssetValueType.String&&new[]{"sourceLanguage","originalLanguage","baseLanguage"}.Contains(child.TemplateField.Name,StringComparer.OrdinalIgnoreCase)))
                selected.SetOriginalLanguage(family,child.AsString);
            foreach(var child in field.Children.Where(child=>child.TemplateField.ValueType==AssetValueType.String&&new[]{"locale","language","languageCode","culture"}.Contains(child.TemplateField.Name,StringComparer.OrdinalIgnoreCase)))
                locale=UnityTextResourceLanguageSelection.DeclaredLocale(child.AsString);
            foreach(var child in field.Children)
            {
                if(paired.TryGetValue(child.TemplateField.Name,out bool original)&&child.TemplateField.ValueType==AssetValueType.String)
                {if(original)selected.AddValue(family,null,child.AsString,true);else if(locale is not null)selected.AddValue(family,locale,child.AsString);continue;}
                Visit(child,textContext,family,locale,depth+1);
            }
        }
        var manager=new AssetsManager();
        try
        {
            if(File.Exists(types))manager.LoadClassPackage(types);else Fail("类型数据库","缺少 Unity 类型数据库，部分资源只能实时补译");
            string managed=Path.Combine(game.DataRoot,"Managed");
            if(game.Backend=="Mono"&&Directory.Exists(managed))manager.MonoTempGenerator=new MonoCecilTempGenerator(managed);
            void Assets(AssetsFileInstance instance,string identity)
            {
                cancellation.ThrowIfCancellationRequested();
                if(File.Exists(types))try{manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);}catch{Fail(identity,"资源版本不在类型数据库中");}
                foreach(var asset in instance.file.AssetInfos)
                {
                    cancellation.ThrowIfCancellationRequested();if(++objectCount>1000000)throw new IOException("资源对象超过本次读取上限。");
                    int kind=asset.TypeId;if(kind is not 49 and not 114 and not 102)continue;
                    if(asset.ByteSize>32*1024*1024){Fail(identity,"大型文本对象未展开");continue;}
                    try
                    {
                        var value=manager.GetBaseField(instance,asset);
                        if(value.IsDummy||(kind==114&&value.Children.All(child=>child.FieldName is "m_GameObject" or "m_Enabled" or "m_Script" or "m_Name")))
                        {Fail(identity,"部分组件未提供字段结构，未能提前提取，运行时继续补译");continue;}
                        if(kind==114&&ReadLocalizedTable(value,localizedTables,cancellation))continue;
                        if(kind==49)
                        {
                            string name=value["m_Name"].AsString,body=value["m_Script"].AsString;
                            selected.Read(name,body,cancellation);
                        }
                        else Visit(value,false,identity+"#"+asset.PathId);
                    }
                    catch(Exception ex)when(ex is not OperationCanceledException and not OutOfMemoryException)
                    {Fail(identity,kind==114?"部分组件缺少可读字段结构，运行时继续补译":"部分文本对象读取失败");}
                }
                fileCount++;
            }
            foreach(var file in CandidateFiles(game.DataRoot,cancellation))
            {
                cancellation.ThrowIfCancellationRequested();string relative=Path.GetRelativePath(game.DataRoot,file);
                try
                {
                    long size=new FileInfo(file).Length;if(size<20)continue;
                    using var probe=File.OpenRead(file);var header=new byte[8];int read=probe.Read(header,0,header.Length);probe.Position=0;
                    bool bundle=read>=7&&Encoding.ASCII.GetString(header,0,7).StartsWith("Unity",StringComparison.Ordinal);
                    if(bundle)
                    {
                        // UnityFS LZ4 data is a seekable, lazy block stream. Asset entries use
                        // SegmentStream, so a multi-GB bundle does not need a multi-GB buffer.
                        BundleFileInstance? container=null;
                        try
                        {
                            container=manager.LoadBundleFile(file,false);
                            long expanded=0;
                            foreach(var block in container.file.BlockAndDirInfo.BlockInfos)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                expanded=checked(expanded+block.DecompressedSize);
                                if(!container.file.DataIsCompressed&&(block.Flags&0x3f)!=0&&block.DecompressedSize>64L*1024*1024)
                                    throw new IOException("单个资源压缩块超过安全读取上限。");
                            }
                            if(container.file.DataIsCompressed)
                            {
                                // Older LZMA bundles need whole-stream expansion in this library.
                                // Bound expanded bytes, not the compressed file's on-disk size.
                                if(expanded>512L*1024*1024){Fail(relative,"LZMA 资源展开后超过安全上限，运行时继续补译");continue;}
                                manager.UnloadBundleFile(container);container=null;
                                cancellation.ThrowIfCancellationRequested();
                                container=manager.LoadBundleFile(file,true);
                            }
                            for(int index=0;index<container.file.BlockAndDirInfo.DirectoryInfos.Count;index++)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                var entry=container.file.BlockAndDirInfo.DirectoryInfos[index];if(!entry.IsSerialized)continue;
                                string identity=relative+"/"+entry.Name;
                                if(entry.Offset<0||entry.DecompressedSize<0||entry.Offset>expanded||entry.DecompressedSize>expanded-entry.Offset)
                                {Fail(identity,"包内资源范围无效");continue;}
                                AssetsFileInstance? instance=null;
                                try{instance=manager.LoadAssetsFileFromBundle(container,index,false);Assets(instance,identity);}
                                catch(Exception ex)when(ex is not OperationCanceledException and not OutOfMemoryException){Fail(identity,"包内资源未能读取");}
                                finally{if(instance is not null)manager.UnloadAssetsFile(instance);}
                            }
                        }
                        finally{if(container is not null)manager.UnloadBundleFile(container);}
                    }
                    else
                    {
                        AssetsFileInstance? instance=null;
                        try{instance=manager.LoadAssetsFile(file,false);Assets(instance,relative);}
                        finally{if(instance is not null)manager.UnloadAssetsFile(instance);}
                    }
                }
                catch(Exception ex)when(ex is not OperationCanceledException and not OutOfMemoryException){Fail(relative,"资源读取失败");}
            }
            string streaming=Path.Combine(game.DataRoot,"StreamingAssets");
            if(Directory.Exists(streaming))foreach(var file in SafeFiles(streaming,cancellation).Where(p=>new[]{".json",".csv",".tsv",".txt",".ink",".yarn"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)))
            {
                cancellation.ThrowIfCancellationRequested();try{if(new FileInfo(file).Length>16*1024*1024){Fail(Path.GetRelativePath(game.DataRoot,file),"大型文本文件未展开");continue;}selected.Read(Path.GetRelativePath(streaming,file),File.ReadAllText(file,new UTF8Encoding(false,true)),cancellation);fileCount++;}catch(Exception ex)when(ex is IOException or DecoderFallbackException or UnauthorizedAccessException){Fail(Path.GetRelativePath(game.DataRoot,file),"文本文件未能读取");}
            }
        }
        finally{manager.UnloadAll();}
        return CompleteSelection(selected,localizedTables,sourceLanguage,fileCount,failures);
    }
    internal static RpgTextCatalogResult CompleteSelection(UnityTextResourceLanguageSelection selected,UnityLocalizedTableSelection localizedTables,SourceLanguageMode sourceLanguage,int fileCount,IEnumerable<string> warnings)
    {
        var texts=new HashSet<string>(StringComparer.Ordinal);var failures=new HashSet<string>(warnings,StringComparer.Ordinal);
        void Fail(string path,string reason){if(failures.Count<256)failures.Add(path+"："+reason);}
        void Add(string value){if(IsDisplayText(value)&&texts.Count<150000)texts.Add(value);}
        string chosen=UnityTextResourceLanguageSelection.RequestedLanguage(sourceLanguage)??new[]{localizedTables.SelectedLanguage,selected.PreferredLanguage}
            .Where(l=>l.Length>0).OrderBy(l=>l=="en"?0:l=="ja"?1:2).ThenBy(l=>l,StringComparer.Ordinal).FirstOrDefault()??"";
        if(sourceLanguage==SourceLanguageMode.Mixed)chosen="";
        bool tableChosen=sourceLanguage==SourceLanguageMode.Mixed||chosen==localizedTables.SelectedLanguage;
        localizedTables.Complete(Add,chosen);
        var otherTexts=new HashSet<string>(StringComparer.Ordinal);
        selected.Complete(text=>otherTexts.Add(text),chosen);
        if(chosen.Length==0&&sourceLanguage!=SourceLanguageMode.Mixed)chosen=selected.SelectedLanguage;
        var excludedOther=new HashSet<UInt128>(selected.ExcludedSourceIdentities);
        if(chosen.Length>0&&sourceLanguage!=SourceLanguageMode.Mixed)
        {
            // Once declared tables choose the source, filter untagged additions
            // before the shared output cap, without granting them table authority.
            var policy=new EmbeddedSourceLanguagePolicy(sourceLanguage);policy.SelectCatalog([],chosen);
            foreach(string text in otherTexts.Where(t=>!selected.TrustedSourceTexts.Contains(t)&&!policy.Allows(t)).ToArray())
            {otherTexts.Remove(text);excludedOther.Add(UnityLocalizedTableSelection.Identity(text));}
        }
        foreach(string text in otherTexts)Add(text);
        var trusted=tableChosen?new HashSet<string>(localizedTables.Texts,StringComparer.Ordinal):new(StringComparer.Ordinal);
        trusted.UnionWith(selected.TrustedSourceTexts);trusted.IntersectWith(texts);
        if(selected.LimitReached)Fail("文本目录","单语言候选文本超过本次读取上限");
        if(tableChosen&&localizedTables.LimitReached||texts.Count>=150000)Fail("文本目录","已达到本次目标语言文本条数或容量上限");
        return new(texts.ToArray(),fileCount,failures.ToArray()){SelectedLanguage=sourceLanguage==SourceLanguageMode.Mixed?"":chosen.Length>0?chosen:selected.SelectedLanguage,
            SkippedByLanguage=localizedTables.Excluded(otherTexts,excludedOther,chosen),TrustedSourceTexts=trusted};
    }
    internal static bool ReadLocalizedTable(AssetTypeValueField value,UnityLocalizedTableSelection selected,CancellationToken cancellation)
    {
        var locale=value.Children.FirstOrDefault(c=>c.FieldName=="m_LocaleId");
        var rows=value.Children.FirstOrDefault(c=>c.FieldName=="m_TableData");
        if(locale is null||rows is null||locale.TemplateField.Type!="LocaleIdentifier")return false;
        var code=locale.Children.FirstOrDefault(c=>c.FieldName=="m_Code"&&c.TemplateField.ValueType==AssetValueType.String);
        string? declared=code?.AsString;selected.ObserveLocale(declared);
        void Visit(AssetTypeValueField field,int depth)
        {
            cancellation.ThrowIfCancellationRequested();if(depth>64||field.IsDummy)return;
            if(field.TemplateField.ValueType==AssetValueType.String)
            {if(field.FieldName=="m_Localized")selected.Add(declared,field.AsString);return;}
            foreach(var child in field.Children)Visit(child,depth+1);
        }
        Visit(rows,0);return true;
    }
    internal static bool IsDisplayText(string value)=>value.Length is >=2 and <=6000&&Regex.IsMatch(value,@"\p{L}")&&!Regex.IsMatch(value,@"^(?:[a-z]+://|[\w./\\-]+\.(?:png|jpg|jpeg|wav|ogg|mp3|json|prefab|asset|dll)|[A-F\d]{16,})$",RegexOptions.IgnoreCase)&&!value.Contains('\0');
    private static IEnumerable<string> CandidateFiles(string data,CancellationToken cancellation)=>SafeFiles(data,cancellation).Where(path=>AssetCandidate.IsMatch(Path.GetFileName(path)));
    private static IEnumerable<string> SafeFiles(string root,CancellationToken cancellation)
    {
        var queue=new Queue<string>();queue.Enqueue(root);int count=0;
        while(queue.Count>0)
        {
            cancellation.ThrowIfCancellationRequested();string folder=queue.Dequeue();
            foreach(var path in Directory.EnumerateFileSystemEntries(folder))
            {
                cancellation.ThrowIfCancellationRequested();if(++count>100000)yield break;
                var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)continue;
                if((attributes&FileAttributes.Directory)!=0){if(!new[]{"Managed","il2cpp_data","Plugins","MonoBleedingEdge"}.Contains(Path.GetFileName(path),StringComparer.OrdinalIgnoreCase))queue.Enqueue(path);}else yield return path;
            }
        }
    }
    internal static void ReadTextResource(string name,string body,Action<string> add,CancellationToken cancellation)
        =>ReadTextResource(name,body,add,cancellation,SourceLanguageMode.Auto);
    internal static void ReadTextResource(string name,string body,Action<string> add,CancellationToken cancellation,SourceLanguageMode sourceLanguage)
    {
        var selected=new UnityTextResourceLanguageSelection(sourceLanguage);
        selected.Read(name,body,cancellation);
        selected.Complete(add);
    }
    internal static IEnumerable<string[]> Delimited(string input,char separator)
    {
        var row=new List<string>();var value=new StringBuilder();bool quote=false;
        for(int i=0;i<input.Length;i++)
        {
            char c=input[i];if(c=='"'){if(quote&&i+1<input.Length&&input[i+1]=='"'){value.Append('"');i++;}else quote=!quote;}
            else if(!quote&&c==separator){row.Add(value.ToString());value.Clear();}
            else if(!quote&&c=='\n'){row.Add(value.ToString().TrimEnd('\r'));value.Clear();yield return row.ToArray();row.Clear();}
            else value.Append(c);
        }
        if(value.Length>0||row.Count>0){row.Add(value.ToString().TrimEnd('\r'));yield return row.ToArray();}
    }
}
