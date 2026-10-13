using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

/// <summary>Selects one source-language view of a structured resource, without rewriting it.</summary>
internal sealed class UnityTextResourceLanguageSelection
{
    private readonly SourceLanguageMode sourceLanguage;
    private readonly string preferredLanguage;
    internal UnityTextResourceLanguageSelection(SourceLanguageMode mode,string preferredLanguage="")
    {sourceLanguage=mode==SourceLanguageMode.Mixed?SourceLanguageMode.Auto:mode;this.preferredLanguage=preferredLanguage;}
    private string? Requested=>preferredLanguage.Length>0?preferredLanguage:RequestedLanguage(sourceLanguage);
    private sealed class Family
    {
        internal readonly HashSet<string> Original=new(StringComparer.Ordinal);
        // Shared fields can coexist with parallel locale columns in one resource.
        // Keep their provenance so an inferred language does not turn them into
        // an unselected language pack.
        internal readonly HashSet<string> SharedOriginal=new(StringComparer.Ordinal);
        internal readonly Dictionary<string,HashSet<string>> Localized=new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> OriginalLanguages=new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<UInt128> OmittedOriginal=new();
        internal readonly Dictionary<string,HashSet<UInt128>> OmittedLocalized=new(StringComparer.OrdinalIgnoreCase);
    }
    private readonly Dictionary<string,Family> families=new(StringComparer.OrdinalIgnoreCase);
    // Four bounded pools prevent a foreign pack read first from consuming the
    // English/Japanese/original-source reservation. Manual mode drops other
    // declared languages before retaining their strings.
    private readonly Dictionary<string,long> retainedCharacters=new(StringComparer.Ordinal);
    private readonly HashSet<string> limitedPools=new(StringComparer.Ordinal);
    private readonly HashSet<UInt128> candidateIdentities=new();
    private bool countLimit;
    internal bool LimitReached {get;private set;}
    internal string SelectedLanguage {get;private set;}="";
    internal int SkippedByLanguage {get;private set;}
    internal HashSet<string> TrustedSourceTexts {get;}=new(StringComparer.Ordinal);
    internal HashSet<UInt128> ExcludedSourceIdentities {get;}=new();
    private static readonly Regex DisplayField=new(@"(?:^m_?text$|dialog|speech|subtitle|caption|description|display.?name|localized|localised|(?:^|_)message$|^(?:m_?)?(?:desc|helpText|body|prompt|title|label|text|content|sentence|originalText|sourceText)$)",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    private static readonly Regex IdentifierField=new(@"^(?:m_)?(?:id|key|guid|path|file|fileName|asset|speakerId|lineId|choiceId|identifier|type|command|locale|language|culture|languageCode|sourceLanguage|originalLanguage|baseLanguage|condition|conditions|script|onClick|callback|eventName|targetId|nextId|expression|variable|variables)$",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    private static readonly Regex ResourceContext=new(@"dialog|locali[sz]|language|translation|subtitle|story|scenario|conversation|\.(?:ink|yarn)$",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    private static readonly Regex LocaleToken=new(@"(?<![\p{L}\p{N}])(?:en(?:[-_]us|[-_]gb)?|english|ja(?:[-_]jp)?|jp|japanese|zh(?:[-_]cn|[-_]hans|[-_]tw|[-_]hant)?|chinese|ko(?:[-_]kr)?|korean|es(?:[-_]es)?|spanish|ru(?:[-_]ru)?|russian|fr(?:[-_]fr)?|french|de(?:[-_]de)?|german|pt(?:[-_]br|[-_]pt)?|portuguese)(?![\p{L}\p{N}])",RegexOptions.IgnoreCase|RegexOptions.Compiled);

    internal static bool IsTextField(string name)
    {
        name=name.TrimStart('_');
        return DisplayField.IsMatch(name)||Regex.IsMatch(name,@"^(?:storyText|narrativeText|randomLines|dialogueLines|textLines|barks|choiceSummary|choiceText|optionText|buttonText|choices|options|responses|replies|lines|paragraphs|textLines|barks)$",RegexOptions.IgnoreCase);
    }
    internal static bool IsIdentifier(string name)=>IdentifierField.IsMatch(name.TrimStart('_'))||Regex.IsMatch(name,@"^_?(?:Tool|Editor)_",RegexOptions.IgnoreCase);
    internal static string? Locale(string name)
    {
        string value=name.Trim().Replace('_','-').ToLowerInvariant();
        return value switch
        {
            "en" or "en-us" or "en-gb" or "english"=>"en",
            "ja" or "ja-jp" or "jp" or "japanese"=>"ja",
            "zh" or "zh-cn" or "zh-hans" or "chinese"=>"zh",
            "zh-tw" or "zh-hant"=>"zh-Hant",
            "ko" or "ko-kr" or "korean"=>"ko",
            "es" or "es-es" or "spanish"=>"es",
            "ru" or "ru-ru" or "russian"=>"ru",
            "fr" or "fr-fr" or "french"=>"fr",
            "de" or "de-de" or "german"=>"de",
            "pt" or "pt-br" or "pt-pt" or "portuguese"=>"pt",
            _=>null
        };
    }
    // Only explicit metadata values enter this path. Arbitrary field names such
    // as id/no are never promoted to locale identifiers by this parser.
    internal static string DeclaredLocale(string? name)
    {
        if(string.IsNullOrWhiteSpace(name))return "und-declared";
        string value=name.Trim().Replace('_','-');
        string? known=Locale(value);if(known is not null)return known;
        if(!Regex.IsMatch(value,@"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$"))return "und-declared";
        string primary=value.Split('-')[0].ToLowerInvariant();
        primary=primary switch {"eng"=>"en","jpn"=>"ja","kor"=>"ko","zho" or "chi"=>"zh",_=>primary};
        return Locale(primary)??primary;
    }
    internal string PreferredLanguage
    {
        get
        {
            var available=families.Values.SelectMany(f=>f.Localized.Where(p=>p.Value.Count>0).Select(p=>p.Key).Concat(f.Original.Count>0?f.OriginalLanguages:[]))
                .Where(l=>l!="und-declared").ToHashSet(StringComparer.Ordinal);
            // Strong original-language evidence participates across formats; an
            // ordinary untagged Latin name cannot overrule a declared Japanese table.
            string inferred=OriginalLocale(families.Values.Where(f=>f.OriginalLanguages.Count==0).SelectMany(f=>f.Original),false);
            if(inferred is not ("und" or "han"))available.Add(inferred);
            return available.OrderBy(l=>l=="en"?0:l=="ja"?1:2).ThenBy(l=>l,StringComparer.Ordinal).FirstOrDefault()??"";
        }
    }
    internal static string? ResourceLocale(string name)
    {
        var matches=LocaleToken.Matches(name.Replace('\\','/'));
        return matches.Count==0?null:Locale(matches[^1].Value);
    }
    private static string ResourceFamily(string name)=>LocaleToken.Replace(name.Replace('\\','/'),"{locale}");
    internal static string? RequestedLanguage(SourceLanguageMode mode)=>mode switch
    {SourceLanguageMode.English=>"en",SourceLanguageMode.Japanese=>"ja",SourceLanguageMode.SimplifiedChinese=>"zh",SourceLanguageMode.Korean=>"ko",_=>null};

    // Untagged resources use aggregate evidence. A single kana-bearing language
    // selector or bundled font example must not select Japanese for all UI text.
    // Declared resource/table locales are handled separately and remain authoritative.
    private static string OriginalLocale(IEnumerable<string> values,bool allowLatinFallback=true)
    {
        // The retained pools already impose a character/count bound. Do not stop
        // after the first 512 rows: asset order is not a source-language signal.
        string language=EmbeddedSourceLanguagePolicy.Choose(values,allowLatinFallback);
        return language switch {"zh"=>"han",""=>"und",_=>language};
    }
    internal void AddValue(string familyKey,string? locale,string value,bool original=false)
    {
        if(countLimit||!UnityEmbeddedCatalog.IsDisplayText(value))return;
        UInt128 identity=UnityLocalizedTableSelection.Identity(value);
        if(candidateIdentities.Count<2_000_000)candidateIdentities.Add(identity);
        else if(!candidateIdentities.Contains(identity)){countLimit=true;return;}
        if(!families.TryGetValue(familyKey,out var family))families[familyKey]=family=new();
        string? declared=original||locale is null?family.OriginalLanguages.Count==1?family.OriginalLanguages.Single():null:locale;
        string? requested=Requested;
        if(requested is not null&&declared is not null&&declared!=requested)return;
        HashSet<string> destination;
        if(original||locale is null)destination=family.Original;
        else if(!family.Localized.TryGetValue(locale,out destination!))family.Localized[locale]=destination=new(StringComparer.Ordinal);
        if(destination.Contains(value))
        {if(!original&&locale is null)family.SharedOriginal.Add(value);return;}
        string pool=Pool(declared);long used=retainedCharacters.GetValueOrDefault(pool);
        if(used+value.Length>32L*1024*1024)
        {
            limitedPools.Add(pool);
            if(ReferenceEquals(destination,family.Original))family.OmittedOriginal.Add(identity);
            else {if(!family.OmittedLocalized.TryGetValue(locale!,out var omitted))family.OmittedLocalized[locale!]=omitted=new();omitted.Add(identity);}
            return;
        }
        destination.Add(value);retainedCharacters[pool]=used+value.Length;
        if(!original&&locale is null)family.SharedOriginal.Add(value);
    }
    private string Pool(string? locale)=>locale is null?"original":locale is "en" or "ja"?locale:locale==Requested?"requested":"other";
    internal void SetOriginalLanguage(string familyKey,string language)
    {
        if(countLimit)return;
        string locale=DeclaredLocale(language);
        if(!families.TryGetValue(familyKey,out var family))families[familyKey]=family=new();
        family.OriginalLanguages.Add(locale);
    }
    internal void Complete(Action<string> add,string? preferredLanguage=null)
    {
        if(countLimit)throw new IOException("语言候选超过安全统计上限，未能完成目录；运行时继续补译。");
        var emitted=new HashSet<string>(StringComparer.Ordinal);
        var omittedSource=new HashSet<UInt128>();
        var chosenLanguages=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? requested=(string.IsNullOrEmpty(preferredLanguage)?null:preferredLanguage)??Requested;
        if(sourceLanguage==SourceLanguageMode.Auto&&requested is null)
        {
            // Related files can have different family names. Select a single
            // catalogue language before publishing any family's strings.
            var available=families.Values.SelectMany(f=>f.Localized.Where(p=>p.Value.Count>0).Select(p=>p.Key)).Where(l=>l!="und-declared").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var declaredOriginal=families.Values.Where(f=>f.Original.Count>0).SelectMany(f=>f.OriginalLanguages).Where(l=>l!="und-declared");available.UnionWith(declaredOriginal);
            var originals=families.Values.Where(f=>f.OriginalLanguages.Count==0).SelectMany(f=>f.Original).ToArray();
            if(originals.Length>0)available.Add(OriginalLocale(originals));
            requested=available.Contains("en")?"en":available.Contains("ja")?"ja":available.Order(StringComparer.Ordinal).FirstOrDefault();
        }
        LimitReached=false;
        TrustedSourceTexts.Clear();
        foreach(var family in families.Values)
        {
            if(sourceLanguage==SourceLanguageMode.Mixed)
            {
                emitted.UnionWith(family.Original);
                foreach(var translations in family.Localized.Values)emitted.UnionWith(translations);
                omittedSource.UnionWith(family.OmittedOriginal);foreach(var omitted in family.OmittedLocalized.Values)omittedSource.UnionWith(omitted);
                LimitReached=limitedPools.Count>0;
                continue;
            }
            if(sourceLanguage==SourceLanguageMode.Auto&&family.OriginalLanguages.Count==0)
            {
                // Text without locale/parallel-column provenance is source text,
                // even when several languages appear in the same menu/dialogue.
                // Explicit source/translation columns still select one view below.
                emitted.UnionWith(family.Localized.Count==0?family.Original:family.SharedOriginal);
                omittedSource.UnionWith(family.OmittedOriginal);
                LimitReached|=limitedPools.Contains("original");
            }
            string originalLocale=family.OriginalLanguages.Count==1?family.OriginalLanguages.Single():OriginalLocale(family.Original);
            var locales=family.Localized.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(family.Original.Count>0)locales.Add(originalLocale);
            string? chosen=requested;
            if(chosen is null)chosen=locales.Contains("en")?"en":locales.Contains("ja")?"ja":family.Original.Count>0?originalLocale:locales.Order(StringComparer.Ordinal).FirstOrDefault();
            if(chosen=="und-declared")continue;
            // Prefer the original column when it already supplies the chosen source.
            bool ambiguousHan=originalLocale=="han"&&requested is "ja" or "zh"&&!family.Localized.ContainsKey(chosen??"");
            bool explicitLanguagePack=family.OriginalLanguages.Count==0&&family.Localized.ContainsKey(chosen??"");
            if(!string.IsNullOrEmpty(preferredLanguage)&&family.OriginalLanguages.Count==0&&family.Original.Count>0&&!family.Localized.ContainsKey(chosen??""))
            {
                // An unrelated untagged resource may mix UI language names and
                // actual dialogue. Preserve its candidates without inheriting
                // the table's locale authority; the queue filters each string.
                emitted.UnionWith(family.Original);
                omittedSource.UnionWith(family.OmittedOriginal);
                LimitReached|=limitedPools.Contains("original");
            }
            else if(family.Original.Count>0&&!explicitLanguagePack&&(chosen==originalLocale||ambiguousHan))
            {emitted.UnionWith(family.Original);omittedSource.UnionWith(family.OmittedOriginal);chosenLanguages.Add(originalLocale);LimitReached|=limitedPools.Contains(Pool(family.OriginalLanguages.Count==1?originalLocale:null));if(family.OriginalLanguages.Count==1&&originalLocale!="und-declared")TrustedSourceTexts.UnionWith(family.Original);}
            else if(chosen is not null&&family.Localized.TryGetValue(chosen,out var selected))
            {emitted.UnionWith(selected);if(family.OmittedLocalized.TryGetValue(chosen,out var omitted))omittedSource.UnionWith(omitted);chosenLanguages.Add(chosen);LimitReached|=limitedPools.Contains(Pool(chosen));if(chosen!="und-declared")TrustedSourceTexts.UnionWith(selected);}
            // An explicit unavailable language must not fall back to another pack.
        }
        SelectedLanguage=sourceLanguage==SourceLanguageMode.Mixed?"":requested??(chosenLanguages.Count==1?chosenLanguages.Single():"");
        if(SelectedLanguage is "und" or "han" or "und-declared")SelectedLanguage="";
        ExcludedSourceIdentities.Clear();ExcludedSourceIdentities.UnionWith(candidateIdentities);ExcludedSourceIdentities.ExceptWith(emitted.Select(UnityLocalizedTableSelection.Identity));
        ExcludedSourceIdentities.ExceptWith(omittedSource);
        SkippedByLanguage=ExcludedSourceIdentities.Count;
        foreach(string text in emitted)add(text);
    }
    private static bool UserFacingNameContext(string context)=>Regex.IsMatch(context,@"^(?:items|missions|quests|tasks|journalEntries|abilities|scenes|sceneNames|characters|options|buttons)$",RegexOptions.IgnoreCase);
    private static bool OriginalField(string name)=>name.Equals("originalText",StringComparison.OrdinalIgnoreCase)||name.Equals("sourceText",StringComparison.OrdinalIgnoreCase);
    private static bool TranslationField(string name)=>name.Equals("text",StringComparison.OrdinalIgnoreCase)||name.Equals("translatedText",StringComparison.OrdinalIgnoreCase)||name.Equals("translation",StringComparison.OrdinalIgnoreCase)||name.Equals("targetText",StringComparison.OrdinalIgnoreCase);

    // A translated sibling identifies a display field, including otherwise ambiguous
    // names such as Name/TranslatedName. Internal object names have no such pair.
    internal static Dictionary<string,bool> PairedStringFields(IEnumerable<string> fields)
    {
        var names=fields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        if(names.Any(OriginalField))
        {
            foreach(string name in names)
                if(OriginalField(name))result[name]=true;
                else if(TranslationField(name))result[name]=false;
        }
        foreach(string name in names)
            if(name.StartsWith("Translated",StringComparison.OrdinalIgnoreCase)&&name.Length>10&&names.Contains(name[10..])&&!IsIdentifier(name[10..]))
            {result.TryAdd(name[10..],true);result[name]=false;}
        return result;
    }

    internal void Read(string name,string body,CancellationToken cancellation,Action<CatalogIssueKind,string>? reportIssue=null)
        =>ReadCore(name,body,cancellation,reportIssue,0,null);
    private void ReadCore(string name,string body,CancellationToken cancellation,Action<CatalogIssueKind,string>? reportIssue,int embeddedDepth,string? inheritedLocale)
    {
        cancellation.ThrowIfCancellationRequested();
        if(body.Length>16*1024*1024){reportIssue?.Invoke(CatalogIssueKind.Limited,"文本资源超过本次大小上限");return;}
        if(body.IndexOf('\0')>=0){reportIssue?.Invoke(CatalogIssueKind.Unsupported,"资源包含二进制内容，未作为普通文字读取");return;}
        string family=ResourceFamily(name);string? resourceLocale=ResourceLocale(name)??inheritedLocale;
        if(XmlDisplayText.Read(name,body,(text,locale)=>AddValue(family,locale??resourceLocale,text),cancellation,reportIssue))return;
        if(StructuredStoryText.ReadSource(name,body,text=>AddValue(family,resourceLocale,text),cancellation,reportIssue))return;
        if(LabelDialogueResource.TryRead(body,cancellation,out var dialogue,out bool complete))
        {
            foreach(string line in dialogue)AddValue(family,resourceLocale,line);
            if(!complete)reportIssue?.Invoke(CatalogIssueKind.Unsupported,"已读取对白和选项，部分脚本指令尚未识别");
            return;
        }
        bool resourceContext=ResourceContext.IsMatch(name)||resourceLocale is not null;string trimmed=body.TrimStart('\ufeff',' ','\t','\r','\n');
        if(trimmed.StartsWith('{')||trimmed.StartsWith('['))
        {
            try
            {
                using var document=JsonDocument.Parse(trimmed,new JsonDocumentOptions{MaxDepth=64,CommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true});
                if(StructuredStoryText.ReadCompiledInk(document.RootElement,text=>AddValue(family,resourceLocale,text),cancellation,reportIssue))return;
                void Visit(JsonElement element,bool context,string parent,string? locale,int depth)
                {
                    cancellation.ThrowIfCancellationRequested();if(depth>64)return;
                    if(element.ValueKind==JsonValueKind.String)
                    {
                        string value=element.GetString()!;string embedded=value.TrimStart();
                        if(embeddedDepth<2&&parent is "payload" or "json" or "serializedData"&&(embedded.StartsWith('{')||embedded.StartsWith('[')))
                        {ReadCore(name+".embedded.json",value,cancellation,reportIssue,embeddedDepth+1,locale);return;}
                        if(context)foreach(var part in SerializedDialogueText.VisibleParts(value))AddValue(family,locale,part);return;
                    }
                    if(element.ValueKind==JsonValueKind.Array){foreach(var child in element.EnumerateArray())Visit(child,context,parent,locale,depth+1);return;}
                    if(element.ValueKind!=JsonValueKind.Object)return;
                    var properties=element.EnumerateObject().ToArray();
                    foreach(var property in properties.Where(p=>p.Value.ValueKind==JsonValueKind.String&&(p.Name.Equals("sourceLanguage",StringComparison.OrdinalIgnoreCase)||p.Name.Equals("originalLanguage",StringComparison.OrdinalIgnoreCase)||p.Name.Equals("baseLanguage",StringComparison.OrdinalIgnoreCase))))
                        SetOriginalLanguage(family,property.Value.GetString()!);
                    string? declaredLocale=properties.Where(p=>p.Name.Equals("locale",StringComparison.OrdinalIgnoreCase)||p.Name.Equals("language",StringComparison.OrdinalIgnoreCase)||p.Name.Equals("languageCode",StringComparison.OrdinalIgnoreCase)||p.Name.Equals("culture",StringComparison.OrdinalIgnoreCase)).Where(p=>p.Value.ValueKind==JsonValueKind.String).Select(p=>DeclaredLocale(p.Value.GetString())).FirstOrDefault();
                    locale=declaredLocale??locale;
                    context=context||declaredLocale is not null;
                    var paired=PairedStringFields(properties.Where(p=>p.Value.ValueKind==JsonValueKind.String).Select(p=>p.Name));
                    foreach(var property in properties)
                    {
                        string field=property.Name;
                        if(IsIdentifier(field))continue;
                        if(paired.TryGetValue(field,out bool original))
                        {
                            // Unknown target columns are not an alternate source language.
                            if(original)AddValue(family,null,property.Value.GetString()!,true);
                            else if(locale is not null)AddValue(family,locale,property.Value.GetString()!);
                            continue;
                        }
                        if(field.Equals("m_Name",StringComparison.OrdinalIgnoreCase)||field.Equals("name",StringComparison.OrdinalIgnoreCase)&&!UserFacingNameContext(parent))continue;
                        string? fieldLocale=Locale(field);
                        bool listField=Regex.IsMatch(field,@"^(?:choices|options|responses|replies|lines|paragraphs)$",RegexOptions.IgnoreCase);
                        Visit(property.Value,!IsIdentifier(field)&&(context||IsTextField(field)&&(!listField||property.Value.ValueKind==JsonValueKind.Array)||UserFacingNameContext(parent)&&field.Equals("name",StringComparison.OrdinalIgnoreCase)||fieldLocale is not null),field,fieldLocale??locale,depth+1);
                    }
                }
                Visit(document.RootElement,resourceContext,"",resourceLocale,0);return;
            }
            catch(JsonException){reportIssue?.Invoke(CatalogIssueKind.Failed,"JSON 文本资源解析失败");return;}
        }
        string extension=Path.GetExtension(name);
        // Unity TextAssets often lose their original .csv/.tsv suffix. Require
        // a structured header and at least one complete row before recognizing
        // such a table; never split arbitrary prose on commas.
        if(!extension.Equals(".csv",StringComparison.OrdinalIgnoreCase)&&!extension.Equals(".tsv",StringComparison.OrdinalIgnoreCase))
        {
            foreach(char delimiter in new[]{'\t',','})
            {
                var probe=UnityEmbeddedCatalog.Delimited(body,delimiter).Take(3).ToArray();
                if(probe.Length<2||probe[0].Length<2||probe[0].Length>128||probe[1].Length!=probe[0].Length)continue;
                bool content=probe[0].Any(h=>IsTextField(h)||OriginalField(h)||Locale(h) is not null);
                bool structure=probe[0].Any(IsIdentifier)||probe[0].Count(h=>Locale(h) is not null)>=2;
                if(content&&structure){extension=delimiter=='\t'?".tsv":".csv";break;}
            }
        }
        if(extension.Equals(".csv",StringComparison.OrdinalIgnoreCase)||extension.Equals(".tsv",StringComparison.OrdinalIgnoreCase))
        {
            var rows=UnityEmbeddedCatalog.Delimited(body,extension.Equals(".tsv",StringComparison.OrdinalIgnoreCase)?'\t':',').ToArray();if(rows.Length==0)return;
            var paired=PairedStringFields(rows[0]);
            bool sourceColumns=rows[0].Any(label=>OriginalField(label)||label.Equals("source",StringComparison.OrdinalIgnoreCase)||label.Equals("original",StringComparison.OrdinalIgnoreCase));
            foreach(var row in rows.Skip(1))
            {
                cancellation.ThrowIfCancellationRequested();
                for(int i=0;i<Math.Min(row.Length,rows[0].Length);i++)
                {
                    string label=rows[0][i];if(IsIdentifier(label))continue;
                    if(paired.TryGetValue(label,out bool pairedOriginal))
                    {if(pairedOriginal)AddValue(family,null,row[i],true);else if(resourceLocale is not null)AddValue(family,resourceLocale,row[i]);continue;}
                    if(label.Equals("name",StringComparison.OrdinalIgnoreCase))continue;
                    string? locale=Locale(label);
                    bool original=OriginalField(label)||label.Equals("source",StringComparison.OrdinalIgnoreCase)||label.Equals("original",StringComparison.OrdinalIgnoreCase);
                    if(original)AddValue(family,null,row[i],true);
                    else if(locale is not null)AddValue(family,locale,row[i]);
                    else if(IsTextField(label)&&(!sourceColumns||resourceLocale is not null))AddValue(family,resourceLocale,row[i]);
                }
            }
            return;
        }
        if(!resourceContext)return;
        foreach(string line in body.Replace("\r\n","\n").Split('\n'))
        {
            cancellation.ThrowIfCancellationRequested();string text=line.Trim();
            if(text.Length==0||text.StartsWith("//")||text.StartsWith("#")||text.StartsWith("<<")||text is "===" or "---"||text.StartsWith("title:",StringComparison.OrdinalIgnoreCase))continue;
            if(extension.Equals(".ink",StringComparison.OrdinalIgnoreCase)&&text[0] is '~' or '=' or '-' or '*')continue;
            AddValue(family,resourceLocale,text);
        }
    }
}
