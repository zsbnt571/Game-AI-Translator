using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace ScreenshotTranslationUiTester;

/// <summary>Read-only structured Unity asset extraction. No game assembly is loaded or executed.</summary>
internal static class UnityEmbeddedCatalog
{
    private static readonly Regex TextField=new(@"(?:^m_?text$|dialog|speech|subtitle|caption|description|display.?name|localized|localised|(?:^|_)message$|^(?:m_?)?(?:storyText|narrativeText|dialogueText|desc|helpText|body|prompt|title|label|text|content|sentence)$)",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    private static readonly Regex AssetCandidate=new(@"(?:\.assets$|^level\d+$|^globalgamemanagers$|\.bundle$|\.unity3d$|\.assetbundle$|^cab-|^[a-f\d]{32}$)",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    internal static RpgTextCatalogResult Read(string exe,CancellationToken cancellation)=>Read(exe,cancellation,SourceLanguageMode.Auto);
    internal static RpgTextCatalogResult Read(string exe,CancellationToken cancellation,ApiSettings settings)
    {
        var explicitSources=TranslationLanguagePolicy.Selected(settings);
        return Read(exe,cancellation,settings.SourceLanguage,explicitSources.Length==1?explicitSources[0]:settings.CatalogLanguageHint);
    }
    internal static RpgTextCatalogResult Read(string exe,CancellationToken cancellation,SourceLanguageMode sourceLanguage,string preferredLanguage="")
    {
        if(sourceLanguage==SourceLanguageMode.Mixed)sourceLanguage=SourceLanguageMode.Auto;
        var game=UnityEmbeddedAdapter.Detect(exe);if(game is null)return new([],0,[]){Report=new(false,[new(CatalogIssueKind.Unsupported,"游戏目录","未识别到可读取的 Unity 数据结构")])};
        string types=Path.Combine(AppContext.BaseDirectory,"adapters","unity","classdata.tpk");
        var failures=new HashSet<string>(StringComparer.Ordinal);var issues=new HashSet<CatalogReadIssue>();int fileCount=0,objectCount=0;
        int metadataCount=0;bool objectLimitReached=false,readCompleted=true;
        var selected=new UnityTextResourceLanguageSelection(sourceLanguage,preferredLanguage);
        var localizedTables=new UnityLocalizedTableSelection(sourceLanguage,preferredLanguage);
        var origins=new CatalogOrigins();string originFile="",originContainer="",originLocation="",originHash="",originContext="";
        void Origin(string family,string location,string text,string? locale)
        {
            if(originFile.Length>0)origins.Add(new(text,originFile,originLocation+(location.Length>0?location:"/resource:"+family),originHash,"unity-resource","unicode",originContainer,locale??"",Context:originContext));
        }
        selected.ObserveOrigin=Origin;
        localizedTables.ObserveOrigin=(text,locale,path)=>Origin("StringTable",path,text,locale);
        string FileHash(string path)
        {
            using var input=File.OpenRead(path);using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];int length;
            while((length=input.Read(buffer))>0){cancellation.ThrowIfCancellationRequested();sha.AppendData(buffer,0,length);}return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        void Fail(string path,string reason,CatalogIssueKind kind=CatalogIssueKind.Failed){if(failures.Count<256){failures.Add(path+"："+reason);issues.Add(new(kind,path,reason));}}
        void Incomplete(string path,string reason,CatalogIssueKind kind=CatalogIssueKind.Limited){readCompleted=false;Fail(path,reason,kind);}
        void TraversalIncomplete()=>Incomplete("文本目录","文件遍历达到上限或遇到链接，尚不能确认目录无文本",CatalogIssueKind.Incomplete);
        var manager=new AssetsManager();
        using var scriptShapes=new UnityScriptShape(Path.Combine(game.DataRoot,"Managed"));
        var nativeSources=Array.Empty<EmbeddedCatalogSource>();
        try
        {
            if(File.Exists(types))manager.LoadClassPackage(types);else Incomplete("类型数据库","缺少 Unity 类型数据库，部分资源只能实时补译",CatalogIssueKind.Unsupported);
            string managed=Path.Combine(game.DataRoot,"Managed");
            if(game.Backend=="Mono"&&Directory.Exists(managed))manager.MonoTempGenerator=new MonoCecilTempGenerator(managed);
            else if(game.Backend=="IL2CPP")
            {
                var fields=UnityIl2CppSchema.Read(game,cancellation);
                if(fields.Directory is not null)
                {
                    manager.MonoTempGenerator=new MonoCecilTempGenerator(fields.Directory);
                }
                string? displayDirectory=fields.Directory??fields.DisplayDirectory;
                if(displayDirectory is not null)
                {
                    try{nativeSources=UnityIl2CppSchema.ReadDisplay(displayDirectory,cancellation,out bool complete);if(!complete)Incomplete("IL2CPP 脚本","部分显示流程尚无法提前读取，运行时继续补译",CatalogIssueKind.Incomplete);}
                    catch(Exception ex)when(ex is IOException or JsonException or ArgumentException){Incomplete("IL2CPP 脚本","显示来源读取未完成，运行时继续补译",CatalogIssueKind.Failed);}
                }
                if(fields.Warning is not null)Incomplete("IL2CPP",fields.Warning,CatalogIssueKind.Incomplete);
            }
            void Assets(AssetsFileInstance instance,string identity)
            {
                cancellation.ThrowIfCancellationRequested();
                if(File.Exists(types))try{manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);}catch{Fail(identity,"资源版本不在类型数据库中",CatalogIssueKind.Unsupported);}
                foreach(var asset in instance.file.AssetInfos)
                {
                    cancellation.ThrowIfCancellationRequested();
                    // Scene files contain large numbers of transforms, renderers
                    // and meshes. Those metadata rows must not exhaust the budget
                    // intended for decoding text-bearing objects.
                    if(++metadataCount>10000000){Incomplete("文本目录","资源元数据超过本次遍历上限，剩余内容由运行时补译");objectLimitReached=true;break;}
                    int kind=asset.TypeId;if(kind is not 49 and not 114 and not 102)continue;
                    if(++objectCount>1000000){Incomplete("文本目录","文本候选对象超过本次读取上限，剩余内容由运行时补译");objectLimitReached=true;break;}
                    if(asset.ByteSize>32*1024*1024){Incomplete(identity,"大型文本对象未展开");continue;}
                    try
                    {
                        originContainer=identity;originLocation="pathid:"+asset.PathId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var value=manager.GetBaseField(instance,asset);
                        originContext=kind==114?UnityCatalogPlaceholder.Component(manager,instance,value):"";
                        if(value.IsDummy||(kind==114&&value.Children.All(child=>child.FieldName is "m_GameObject" or "m_Enabled" or "m_Script" or "m_Name")))
                        {
                            if(UnityBuiltinTextCatalog.TryRead(manager,instance,asset,value,out var builtinText))
                            {selected.AddValue(identity+"#"+asset.PathId,null,builtinText);continue;}
                            if(!value.IsDummy&&game.Backend=="Mono"&&scriptShapes.HasNoSerializedFields(manager,instance,value))continue;
                            Fail(identity,"部分组件的字段结构尚未读出，不能据此判断没有文字",CatalogIssueKind.Unsupported);continue;
                        }
                        if(kind==114&&ReadLocalizedTable(value,localizedTables,cancellation,()=>Incomplete(identity,"文本字段层级超过本次读取上限")))continue;
                        if(kind==114&&ReadSharedTableMetadata(value,cancellation,reason=>Incomplete(identity,reason,CatalogIssueKind.Unsupported)))continue;
                        if(kind==114&&UnityNaninovelText.Read(value,source=>
                        {
                            originContext=source.Context;
                            selected.AddValue("Naninovel/"+source.ScriptPath,null,source.Text,location:source.Location);
                        },cancellation,reason=>Incomplete(identity,reason)))continue;
                        if(kind==114&&UnityFrameworkText.Read(value,(text,locale,path)=>selected.AddValue(identity+"#"+asset.PathId,locale,text,location:path),cancellation,reason=>Incomplete(identity,reason)))continue;
                        if(kind==49)
                        {
                            string name=value["m_Name"].AsString,body=value["m_Script"].AsString;
                            if(body.Length>16*1024*1024)Incomplete(identity,"大型文本资源未展开");
                            selected.Read(name,body,cancellation,(issueKind,reason)=>Fail(identity+"/"+name,reason,issueKind));
                        }
                        else
                        {
                            ReadSerializedFields(value,false,selected,identity+"#"+asset.PathId,cancellation,reason=>Incomplete(identity,reason));
                            if(kind==114&&game.Backend=="Mono")
                            {
                                var displayFields=scriptShapes.DisplayFields(manager,instance,value,cancellation);
                                foreach(var child in value.Children.Where(c=>displayFields.Contains(c.FieldName)))ReadSerializedFields(child,true,selected,identity+"#"+asset.PathId,cancellation,reason=>Incomplete(identity,reason));
                            }
                        }
                    }
                    catch(Exception ex)when(ex is not OperationCanceledException and not OutOfMemoryException)
                    {Fail(identity,"部分文本对象解析失败（"+ex.GetType().Name+"），运行时继续补译",CatalogIssueKind.Failed);}
                }
                fileCount++;
            }
            foreach(var file in CandidateFiles(game.DataRoot,cancellation,TraversalIncomplete))
            {
                if(objectLimitReached)break;
                cancellation.ThrowIfCancellationRequested();string relative=Path.GetRelativePath(game.DataRoot,file);
                try
                {
                    // Keep a read-sharing lease across hashing and decoding so a
                    // writer cannot swap the bytes after provenance was computed.
                    using var sourceGuard=File.OpenRead(file);
                    originFile=relative;originHash=FileHash(file);
                    long size=new FileInfo(file).Length;if(size<20){Fail(relative,"资源头不完整，无法读取");continue;}
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
                                {Incomplete(relative,"单个资源压缩块超过安全读取上限");throw new IOException("单个资源压缩块超过安全读取上限。");}
                            }
                            if(container.file.DataIsCompressed)
                            {
                                // Older LZMA bundles need whole-stream expansion in this library.
                                // Bound expanded bytes, not the compressed file's on-disk size.
                                if(expanded>512L*1024*1024){Incomplete(relative,"LZMA 资源展开后超过安全上限，运行时继续补译");continue;}
                                manager.UnloadBundleFile(container);container=null;
                                cancellation.ThrowIfCancellationRequested();
                                container=manager.LoadBundleFile(file,true);
                            }
                            for(int index=0;index<container.file.BlockAndDirInfo.DirectoryInfos.Count;index++)
                            {
                                if(objectLimitReached)break;
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
            if(Directory.Exists(streaming))foreach(var file in SafeFiles(streaming,cancellation,TraversalIncomplete).Where(p=>new[]{".json",".csv",".tsv",".txt",".ink",".yarn",".xml",".resx",".nani"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)))
            {
                cancellation.ThrowIfCancellationRequested();try{using var sourceGuard=File.OpenRead(file);if(new FileInfo(file).Length>16*1024*1024){Incomplete(Path.GetRelativePath(game.DataRoot,file),"大型文本文件未展开");continue;}originFile=Path.GetRelativePath(game.DataRoot,file);originContainer="";originLocation="";originContext="";originHash=FileHash(file);selected.Read(Path.GetRelativePath(streaming,file),File.ReadAllText(file,new UTF8Encoding(false,true)),cancellation,(kind,reason)=>Fail(Path.GetRelativePath(game.DataRoot,file),reason,kind));fileCount++;}catch(Exception ex)when(ex is IOException or DecoderFallbackException or UnauthorizedAccessException){Fail(Path.GetRelativePath(game.DataRoot,file),"文本文件未能读取");}
            }
        }
        finally{manager.UnloadAll();}
        originFile="";
        var scriptSources=nativeSources;
        foreach(var source in nativeSources)selected.AddValue("IL2CPP/"+source.ModuleId+"#"+source.MethodToken+"/"+source.Offset,null,source.Text);
        if(game.Backend=="Mono")
        {
            var script=UnityManagedDisplayCatalog.Read(Path.Combine(game.DataRoot,"Managed"),cancellation);
            fileCount+=script.Files;readCompleted&=script.Completed;
            foreach(var issue in script.Issues)Fail(issue.Source,issue.Reason,issue.Kind);
            scriptSources=script.Sources;
            foreach(var source in scriptSources)
                selected.AddValue("Managed/"+source.File+"#"+source.MethodToken+"/"+source.Offset,null,source.Text);
        }
        var result=CompleteSelection(selected,localizedTables,sourceLanguage,fileCount,failures,readCompleted,issues,preferredLanguage);
        var admitted=result.Texts.ToHashSet(StringComparer.Ordinal);
        result=result with{Sources=scriptSources.Where(source=>admitted.Contains(source.Text)).ToArray(),
            Origins=origins.Items.Where(o=>admitted.Contains(o.Text)&&(o.Language.Length==0||o.Language==result.SelectedLanguage)).ToArray(),OriginsTruncated=origins.Truncated};
        var skipped=UnityCatalogPlaceholder.Excluded(result);
        if(skipped.Count==0)return result;
        var report=result.Report??new CatalogReadReport(result.ReadCompleted,[]);
        var skipIssues=skipped.Select(text=>
        {
            var origin=result.Origins.First(o=>o.Text==text);
            return new CatalogReadIssue(CatalogIssueKind.Skipped,origin.File+"#"+origin.Location,"已确认界面占位样例，保留原文与来源，不进入整批预译");
        }).ToArray();
        return result with{SkippedSourceTexts=skipped,Report=report with{Issues=[..report.Issues,..skipIssues]}};
    }
    internal static RpgTextCatalogResult CompleteSelection(UnityTextResourceLanguageSelection selected,UnityLocalizedTableSelection localizedTables,SourceLanguageMode sourceLanguage,int fileCount,IEnumerable<string> warnings,bool readCompleted=false,IEnumerable<CatalogReadIssue>? readIssues=null,string preferredLanguage="")
    {
        var texts=new HashSet<string>(StringComparer.Ordinal);var failures=new HashSet<string>(warnings,StringComparer.Ordinal);
        void Fail(string path,string reason){if(failures.Count<256)failures.Add(path+"："+reason);}
        void Add(string value){if(IsDisplayText(value)&&texts.Count<150000)texts.Add(value);}
        string chosen=preferredLanguage.Length>0?preferredLanguage:UnityTextResourceLanguageSelection.RequestedLanguage(sourceLanguage)??new[]{localizedTables.SelectedLanguage,selected.PreferredLanguage}
            .Where(l=>l.Length>0).OrderBy(l=>l=="en"?0:l=="ja"?1:2).ThenBy(l=>l,StringComparer.Ordinal).FirstOrDefault()??"";
        if(sourceLanguage==SourceLanguageMode.Mixed)chosen="";
        bool tableChosen=sourceLanguage==SourceLanguageMode.Mixed||chosen==localizedTables.SelectedLanguage;
        localizedTables.Complete(Add,chosen);
        var otherTexts=new HashSet<string>(StringComparer.Ordinal);
        selected.Complete(text=>otherTexts.Add(text),chosen);
        if(chosen.Length==0&&sourceLanguage!=SourceLanguageMode.Mixed)chosen=selected.SelectedLanguage;
        var excludedOther=new HashSet<UInt128>(selected.ExcludedSourceIdentities);
        if(chosen.Length>0&&sourceLanguage is not (SourceLanguageMode.Mixed or SourceLanguageMode.Auto))
        {
            // An explicit user choice still filters untagged source text. Auto
            // already selected declared locale views structurally; a guessed
            // majority must not discard the other languages in shared originals.
            var policy=new EmbeddedSourceLanguagePolicy(sourceLanguage);policy.SelectCatalog([],chosen);
            foreach(string text in otherTexts.Where(t=>!selected.TrustedSourceTexts.Contains(t)&&!policy.Allows(t)).ToArray())
            {otherTexts.Remove(text);excludedOther.Add(UnityLocalizedTableSelection.Identity(text));}
        }
        foreach(string text in otherTexts)Add(text);
        var trusted=tableChosen?new HashSet<string>(localizedTables.Texts,StringComparer.Ordinal):new(StringComparer.Ordinal);
        trusted.UnionWith(selected.TrustedSourceTexts);trusted.IntersectWith(texts);
        var unlocalized=new HashSet<string>(selected.UnlocalizedSourceTexts,StringComparer.Ordinal);unlocalized.IntersectWith(texts);
        if(selected.LimitReached)Fail("文本目录","单语言候选文本超过本次读取上限");
        if(tableChosen&&localizedTables.LimitReached||texts.Count>=150000)Fail("文本目录","已达到本次目标语言文本条数或容量上限");
        var issues=readIssues?.ToHashSet()??failures.Select(reason=>new CatalogReadIssue(CatalogIssueKind.Incomplete,"文本目录",reason)).ToHashSet();
        if(selected.LimitReached||tableChosen&&localizedTables.LimitReached||texts.Count>=150000)issues.Add(new(CatalogIssueKind.Limited,"文本目录","达到本次候选文本条数或容量上限"));
        bool complete=readCompleted&&(fileCount>0||failures.Count>0)&&issues.All(i=>i.Kind==CatalogIssueKind.Skipped)&&!selected.LimitReached&&!localizedTables.LimitReached&&texts.Count<150000;
        return new(texts.ToArray(),fileCount,failures.ToArray()){
            Report=new(complete,issues.ToArray()),
            ReadCompleted=complete,
            SelectedLanguage=sourceLanguage==SourceLanguageMode.Mixed?"":chosen.Length>0?chosen:selected.SelectedLanguage,
            SkippedByLanguage=localizedTables.Excluded(otherTexts,excludedOther,chosen),TrustedSourceTexts=trusted,UnlocalizedSourceTexts=unlocalized};
    }
    internal static void ReadSerializedFields(AssetTypeValueField value,bool context,UnityTextResourceLanguageSelection selected,string family,CancellationToken cancellation,Action<string> incomplete)
    {
        int nodes=0;
        var seen=new HashSet<AssetTypeValueField>(ReferenceEqualityComparer.Instance);
        void Visit(AssetTypeValueField field,bool context,string family,string? locale=null,int depth=0,bool referenceRoot=false,string path="",bool actionContext=false)
        {
            cancellation.ThrowIfCancellationRequested();if(++nodes>200000){incomplete("文本字段数量超过本次读取上限");return;}if(depth>64){incomplete("文本字段层级超过本次读取上限");return;}if(field.IsDummy||!seen.Add(field))return;
            string name=field.TemplateField.Name;
            if(actionContext&&UnityTextResourceLanguageSelection.IsActionArguments(name))return;
            actionContext|=UnityTextResourceLanguageSelection.IsActionContainer(name);
            // SerializeReference objects live in Value, not Children. Traverse
            // decoded data only; never load or instantiate a game script type.
            if(field.TemplateField.ValueType==AssetValueType.ManagedReferencesRegistry)
            {
                var registry=field.Value?.AsManagedReferencesRegistry;
                if(registry?.references is null)return;
                foreach(var reference in registry.references)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(nodes>=200000){incomplete("文本字段数量超过本次读取上限");break;}
                    if(reference.data is {IsDummy:false} data)Visit(data,false,family,locale,depth+1,true,path+"/managed-reference:"+reference.rid,actionContext);
                }
                return;
            }
            bool excluded=!referenceRoot&&(UnityTextResourceLanguageSelection.IsIdentifier(name)||name.Equals("name",StringComparison.OrdinalIgnoreCase)||name.Equals("m_Name",StringComparison.OrdinalIgnoreCase));
            locale=UnityTextResourceLanguageSelection.Locale(name)??locale;
            bool textContext=!referenceRoot&&!excluded&&(context||TextField.IsMatch(name)||UnityTextResourceLanguageSelection.IsTextField(name)||name is "Languages" or "m_Localized"||UnityTextResourceLanguageSelection.Locale(name) is not null);
            if(field.TemplateField.ValueType==AssetValueType.String)
            {if(textContext)foreach(string part in SerializedDialogueText.VisibleParts(field.AsString))selected.AddValue(family,locale,part,location:path);return;}
            if(excluded)return;
            var paired=UnityTextResourceLanguageSelection.PairedStringFields(field.Children.Where(child=>child.TemplateField.ValueType==AssetValueType.String).Select(child=>child.TemplateField.Name));
            foreach(var child in field.Children.Where(child=>child.TemplateField.ValueType==AssetValueType.String&&new[]{"sourceLanguage","originalLanguage","baseLanguage"}.Contains(child.TemplateField.Name,StringComparer.OrdinalIgnoreCase)))
                selected.SetOriginalLanguage(family,child.AsString);
            foreach(var child in field.Children.Where(child=>child.TemplateField.ValueType==AssetValueType.String&&new[]{"locale","language","languageCode","culture"}.Contains(child.TemplateField.Name,StringComparer.OrdinalIgnoreCase)))
                locale=UnityTextResourceLanguageSelection.DeclaredLocale(child.AsString);
            int index=0;foreach(var child in field.Children)
            {
                if(actionContext&&UnityTextResourceLanguageSelection.IsActionArguments(child.TemplateField.Name)){index++;continue;}
                if(paired.TryGetValue(child.TemplateField.Name,out bool original)&&child.TemplateField.ValueType==AssetValueType.String)
                {if(original)selected.AddValue(family,null,child.AsString,true,path+"/"+child.FieldName);else if(locale is not null)selected.AddValue(family,locale,child.AsString,location:path+"/"+child.FieldName);continue;}
                if(nodes>=200000){incomplete("文本字段数量超过本次读取上限");break;}
                Visit(child,textContext,family,locale,depth+1,path:path+"/"+child.FieldName+"["+index+++"]",actionContext:actionContext);
            }
        }
        Visit(value,context,family);
    }
    internal static bool ReadLocalizedTable(AssetTypeValueField value,UnityLocalizedTableSelection selected,CancellationToken cancellation,Action? incomplete=null)
    {
        var locale=value.Children.FirstOrDefault(c=>c.FieldName=="m_LocaleId");
        var rows=value.Children.FirstOrDefault(c=>c.FieldName=="m_TableData");
        if(locale is null||rows is null||locale.TemplateField.Type!="LocaleIdentifier")return false;
        var code=locale.Children.FirstOrDefault(c=>c.FieldName=="m_Code"&&c.TemplateField.ValueType==AssetValueType.String);
        string? declared=code?.AsString;selected.ObserveLocale(declared);
        void Visit(AssetTypeValueField field,int depth,string path)
        {
            cancellation.ThrowIfCancellationRequested();if(depth>64){incomplete?.Invoke();return;}if(field.IsDummy)return;
            if(field.TemplateField.ValueType==AssetValueType.String)
            {if(field.FieldName=="m_Localized")selected.Add(declared,field.AsString,path);return;}
            int index=0;foreach(var child in field.Children)Visit(child,depth+1,path+"/"+child.FieldName+"["+index+++"]");
        }
        Visit(rows,0,"/m_TableData");return true;
    }
    internal static bool ReadSharedTableMetadata(AssetTypeValueField value,CancellationToken cancellation,Action<string> incomplete)
    {
        // SharedTableData stores key ids and editor metadata. Localized display
        // values live in the separate StringTables read above. Walking tens of
        // thousands of Comment references cannot discover more display text.
        var entries=value.Children.FirstOrDefault(c=>c.FieldName=="m_Entries");
        var collection=value.Children.FirstOrDefault(c=>c.FieldName=="m_TableCollectionName");
        var guid=value.Children.FirstOrDefault(c=>c.FieldName=="m_TableCollectionNameGuidString");
        var metadata=value.Children.FirstOrDefault(c=>c.FieldName=="m_Metadata");
        if(entries?.TypeName!="SharedTableEntry"||collection?.TemplateField.ValueType!=AssetValueType.String||
            guid?.TemplateField.ValueType!=AssetValueType.String||!Guid.TryParse(guid.AsString,out _)||metadata?.TypeName!="MetadataCollection")return false;
        var known=new HashSet<string>(StringComparer.Ordinal){"m_GameObject","m_Enabled","m_Script","m_Name","m_TableCollectionName","m_TableCollectionNameGuidString","m_Entries","m_Metadata","m_KeyGenerator","references"};
        if(value.Children.Any(c=>!known.Contains(c.FieldName)))return false;
        var registry=value.Children.FirstOrDefault(c=>c.FieldName=="references");
        if(registry is not null&&registry.TemplateField.ValueType!=AssetValueType.ManagedReferencesRegistry)return false;
        var references=registry?.Value?.AsManagedReferencesRegistry?.references;
        if(registry is not null&&references is null)
        {incomplete("共享本地化键表的元数据结构未能完整读取");return true;}
        if(references is not null)
            foreach(var reference in references)
            {
                cancellation.ThrowIfCancellationRequested();
                bool knownMetadata=reference.type is {AsmName:"Unity.Localization",Namespace:"UnityEngine.Localization.Metadata",ClassName:"Comment"}||
                    reference.type is {AsmName:"Unity.Localization",Namespace:"UnityEngine.Localization.Tables",ClassName:"DistributedUIDGenerator"};
                if(!knownMetadata||reference.data is not {IsDummy:false})
                {incomplete("共享本地化键表含尚未识别的元数据；未将键或注释当作显示文字");break;}
            }
        return true;
    }
    internal static bool IsDisplayText(string value)=>value.Length is >=1 and <=6000&&Regex.IsMatch(value,@"\p{L}")&&!Regex.IsMatch(value,@"^(?:[a-z]+://|[\w./\\-]+\.(?:png|jpg|jpeg|wav|ogg|mp3|json|prefab|asset|dll)|[A-F\d]{16,})$",RegexOptions.IgnoreCase)&&!value.Contains('\0')&&EmbeddedSourceLanguagePolicy.Identify(value).Length>0;
    private static IEnumerable<string> CandidateFiles(string data,CancellationToken cancellation,Action incomplete)=>SafeFiles(data,cancellation,incomplete).Where(path=>AssetCandidate.IsMatch(Path.GetFileName(path)));
    internal static IEnumerable<string> SafeFiles(string root,CancellationToken cancellation,Action incomplete,int entryLimit=100000)
    {
        var queue=new Queue<string>();queue.Enqueue(root);int count=0;
        while(queue.Count>0)
        {
            cancellation.ThrowIfCancellationRequested();string folder=queue.Dequeue();
            foreach(var path in Directory.EnumerateFileSystemEntries(folder))
            {
                cancellation.ThrowIfCancellationRequested();if(++count>entryLimit){incomplete();yield break;}
                var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0){incomplete();continue;}
                if((attributes&FileAttributes.Directory)!=0){if(!new[]{"Managed","il2cpp_data","Plugins","MonoBleedingEdge"}.Contains(Path.GetFileName(path),StringComparer.OrdinalIgnoreCase))queue.Enqueue(path);}else yield return path;
            }
        }
    }
    internal static void ReadTextResource(string name,string body,Action<string> add,CancellationToken cancellation)
        =>ReadTextResource(name,body,add,cancellation,SourceLanguageMode.Auto);
    internal static void ReadTextResource(string name,string body,Action<string> add,CancellationToken cancellation,SourceLanguageMode sourceLanguage,string preferredLanguage="")
    {
        var selected=new UnityTextResourceLanguageSelection(sourceLanguage,preferredLanguage);
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

