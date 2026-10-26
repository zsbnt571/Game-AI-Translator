using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Persisted values 0–3 belong to existing installations. Append new languages.
public enum ApplicationLanguage { SimplifiedChinese = 0, English = 1, Russian = 2, FollowSystem = 3, TraditionalChinese = 4, Japanese = 5, Korean = 6, Spanish = 7, French = 8, German = 9 }

internal sealed record InterfaceLanguageChoice(ApplicationLanguage Value,string NativeName)
{
    public override string ToString()=>Value==ApplicationLanguage.FollowSystem?UiLanguage.Text("跟随系统"):NativeName;
}

// Accept the named preference understood by startup diagnostics, while keeping
// the existing numeric on-disk format and leaving other enum fields untouched.
internal sealed class ApplicationLanguagePreferenceConverter : System.Text.Json.Serialization.JsonConverter<ApplicationLanguage>
{
    public ApplicationLanguagePreferenceConverter() { }
    public override ApplicationLanguage Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
    {
        if(reader.TokenType==JsonTokenType.Number && reader.TryGetInt32(out var number))return (ApplicationLanguage)number;
        if(reader.TokenType==JsonTokenType.String && Enum.TryParse<ApplicationLanguage>(reader.GetString(),true,out var named) && Enum.IsDefined(named))return named;
        throw new JsonException();
    }
    public override void Write(Utf8JsonWriter writer,ApplicationLanguage value,JsonSerializerOptions options)=>writer.WriteNumberValue((int)value);
}

// Display localization never rewrites combo values, profile names, API fields,
// translated game text or serialized identifiers.
internal static class UiLanguage
{
    internal const string SelectionHelp="保存后切换界面语言。默认跟随系统，不支持的系统语言使用英语。游戏译文语言在“翻译方案”中单独设置。";
    // Display order is independent of the persisted ApplicationLanguage values.
    internal static readonly InterfaceLanguageChoice[] Options=[
        new(ApplicationLanguage.FollowSystem,"跟随系统"),new(ApplicationLanguage.SimplifiedChinese,"简体中文"),
        new(ApplicationLanguage.TraditionalChinese,"繁體中文"),new(ApplicationLanguage.English,"English"),
        new(ApplicationLanguage.Japanese,"日本語"),
        new(ApplicationLanguage.Korean,"한국어"),new(ApplicationLanguage.Spanish,"Español"),
        new(ApplicationLanguage.French,"Français"),new(ApplicationLanguage.German,"Deutsch"),
        new(ApplicationLanguage.Russian,"Русский")];
    internal static IEnumerable<ApplicationLanguage> Supported=>Options.Where(x=>x.Value!=ApplicationLanguage.FollowSystem).Select(x=>x.Value);
    // Startup diagnostics must respect the saved preference before normal settings
    // loading. Read only this value; never migrate, save or expose credentials here.
    internal static void ReadStartupPreference()
    {
        Set(ApplicationLanguage.FollowSystem);
        try
        {
            using var stream=File.OpenRead(ConfigurationManager.ResolveSettingsPath());
            using var document=JsonDocument.Parse(stream);
            Set(ExistingPreference(document.RootElement));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OverflowException) { }
    }
    internal static bool HasSavedPreference(JsonElement settings) => settings.ValueKind==JsonValueKind.Object &&
        settings.EnumerateObject().Any(x=>x.Name.Equals("InterfaceLanguage",StringComparison.OrdinalIgnoreCase));
    // A settings file created before interface-language support kept the Chinese UI.
    // Only a genuinely new profile defaults to following the operating system.
    internal static ApplicationLanguage ExistingPreference(JsonElement settings)
    {
        if(settings.ValueKind!=JsonValueKind.Object)return ApplicationLanguage.SimplifiedChinese;
        foreach(var property in settings.EnumerateObject())
        {
            if(!property.Name.Equals("InterfaceLanguage",StringComparison.OrdinalIgnoreCase))continue;
            if(property.Value.ValueKind==JsonValueKind.Number&&property.Value.TryGetInt32(out var value)&&Enum.IsDefined((ApplicationLanguage)value))
                return (ApplicationLanguage)value;
            if(property.Value.ValueKind==JsonValueKind.String&&Enum.TryParse<ApplicationLanguage>(property.Value.GetString(),true,out var named)&&Enum.IsDefined(named))
                return named;
        }
        return ApplicationLanguage.SimplifiedChinese;
    }
    internal static ApplicationLanguage Resolve(ApplicationLanguage preference,System.Globalization.CultureInfo? systemCulture=null)
    {
        if(preference!=ApplicationLanguage.FollowSystem)
            return Enum.IsDefined(preference)?preference:ApplicationLanguage.SimplifiedChinese;
        var culture=systemCulture??System.Globalization.CultureInfo.CurrentUICulture;
        return culture.TwoLetterISOLanguageName switch
        {
            "zh"=>culture.Name.Contains("Hant",StringComparison.OrdinalIgnoreCase)||(!culture.Name.Contains("Hans",StringComparison.OrdinalIgnoreCase)&&culture.Name.Split('-').Any(x=>x.Equals("TW",StringComparison.OrdinalIgnoreCase)||x.Equals("HK",StringComparison.OrdinalIgnoreCase)||x.Equals("MO",StringComparison.OrdinalIgnoreCase)))
                ?ApplicationLanguage.TraditionalChinese:ApplicationLanguage.SimplifiedChinese,
            "ru"=>ApplicationLanguage.Russian,
            "ja"=>ApplicationLanguage.Japanese,
            "ko"=>ApplicationLanguage.Korean,
            "es"=>ApplicationLanguage.Spanish,
            "fr"=>ApplicationLanguage.French,
            "de"=>ApplicationLanguage.German,
            _=>ApplicationLanguage.English
        };
    }
    private sealed class Catalog(string resource)
    {
        internal readonly Lazy<Dictionary<string,string>> Entries=new(()=>ReadCatalog(resource));
        internal Dictionary<string,string> Reverse=>reverse??=UiLanguage.Reverse(Entries.Value);
        internal Pattern[] Patterns=>patterns??=BuildPatterns(Entries.Value);
        internal Pattern[] ReversePatterns=>reversePatterns??=BuildPatterns(Reverse);
        internal KeyValuePair<string,string>[] Fragments=>fragments??=BuildFragments(Entries.Value);
        private Dictionary<string,string>? reverse;
        private Pattern[]? patterns,reversePatterns;
        private KeyValuePair<string,string>[]? fragments;
    }
    private static readonly Dictionary<ApplicationLanguage,Catalog> Catalogs=new()
    {
        [ApplicationLanguage.English]=new("UiEnglish.json"),[ApplicationLanguage.Russian]=new("UiRussian.json"),
        [ApplicationLanguage.TraditionalChinese]=new("UiTraditionalChinese.json"),[ApplicationLanguage.Japanese]=new("UiJapanese.json"),
        [ApplicationLanguage.Korean]=new("UiKorean.json"),[ApplicationLanguage.Spanish]=new("UiSpanish.json"),
        [ApplicationLanguage.French]=new("UiFrench.json"),[ApplicationLanguage.German]=new("UiGerman.json")
    };
    private static Dictionary<string,string> ReadCatalog(string resource)
    {
        using var stream=typeof(UiLanguage).Assembly.GetManifestResourceStream("Fusion."+resource);
        if(stream is null)return new(StringComparer.Ordinal);
        try{return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)??new(StringComparer.Ordinal);}
        catch(JsonException){return new(StringComparer.Ordinal);}
    }
    private static Catalog CurrentCatalog=>Catalogs.GetValueOrDefault(Current)??Catalogs[ApplicationLanguage.English];
    private static Dictionary<string,string> Translations=>CurrentCatalog.Entries.Value;
    private static bool TryText(string source,out string translated)=>Translations.TryGetValue(source,out translated!)||Catalogs[ApplicationLanguage.English].Entries.Value.TryGetValue(source,out translated!);
    private static Dictionary<string,string> Reverse(Dictionary<string,string> source)=>source
        .GroupBy(x=>x.Value,StringComparer.Ordinal).ToDictionary(x=>x.Key,x=>x.First().Key,StringComparer.Ordinal);
    internal static string Original(string source)
    {
        // Canonical product strings win over a coincidentally identical localized
        // caption. Only catalogs already used by this UI participate in reversal.
        if(Catalogs[ApplicationLanguage.English].Entries.Value.ContainsKey(source))return source;
        foreach(var catalog in new[]{CurrentCatalog}.Concat(Catalogs.Values.Where(x=>x.Entries.IsValueCreated)).Distinct())
        {
            if(catalog.Reverse.TryGetValue(source,out var original))return original;
            foreach(var pattern in catalog.ReversePatterns)
            {
                if(!source.StartsWith(pattern.Prefix,StringComparison.Ordinal))continue;
                var match=pattern.Regex.Match(source);if(!match.Success)continue;
                return Regex.Replace(pattern.Replacement,@"\{(\d+)\}",m=>match.Groups["v"+m.Groups[1].Value].Value);
            }
        }
        return source;
    }
    private sealed class Binding(Control control)
    {
        internal string Source=Original(control.Text);
        internal readonly string? AccessibleSource=control.AccessibleName is {} name?Original(name):null;
        internal readonly bool TranslateText=control is Label or ButtonBase or Form or TabPage or GroupBox;
        internal readonly string? Placeholder=control is TextBox box?Original(box.PlaceholderText):null;
        internal bool Applying;
        internal void Refresh(Control target)
        {
            if(Applying||target.IsDisposed||Preserved.TryGetValue(target,out _))return;Applying=true;
            try{if(TranslateText){var text=Text(Source);if(target.Text!=text)target.Text=text;}if(AccessibleSource is not null)target.AccessibleName=Text(AccessibleSource);if(target is TextBox box&&Placeholder is not null)box.PlaceholderText=Text(Placeholder);}
            finally{Applying=false;}
        }
    }
    private static readonly ConditionalWeakTable<Control,Binding> Bound=new();
    private static readonly List<WeakReference<Control>> Controls=[];
    private static readonly ConditionalWeakTable<Control,object> Trees=new(),Preserved=new();
    private static bool watching;
    internal static void Preserve(Control control)=>Preserved.GetValue(control,_=>new object());
    internal static void WatchWindows()
    {
        if(watching)return;watching=true;
        Application.Idle+=(_,_)=>{foreach(Form form in Application.OpenForms)BindTree(form);};
    }
    private sealed record Pattern(Regex Regex,string Replacement,string Prefix);
    private static IEnumerable<Pattern> Patterns=>CurrentCatalog.Patterns.Concat(Current==ApplicationLanguage.English?[]:Catalogs[ApplicationLanguage.English].Patterns);
    private static Pattern[] BuildPatterns(Dictionary<string,string> translations)=>translations.Where(x=>Regex.IsMatch(x.Key,@"\{\d+\}")).Select(pair=>
    {
        var matches=Regex.Matches(pair.Key,@"\{(\d+)\}");int at=0;var expression=new System.Text.StringBuilder("^");
        foreach(Match match in matches){expression.Append(Regex.Escape(pair.Key[at..match.Index]));expression.Append("(?<v"+match.Groups[1].Value+">.*?)");at=match.Index+match.Length;}
        expression.Append(Regex.Escape(pair.Key[at..])).Append('$');
        // A one-line template must not swallow preceding status lines into a
        // placeholder (for example architecture plus a Godot details line).
        var options=RegexOptions.CultureInvariant|(pair.Key.Contains('\n')?RegexOptions.Singleline:RegexOptions.None);
        return new Pattern(new Regex(expression.ToString(),options,TimeSpan.FromMilliseconds(20)),pair.Value,pair.Key[..matches[0].Index]);
    }).OrderByDescending(x=>x.Prefix.Length).ThenByDescending(x=>x.Regex.ToString().Length).ToArray();
    private static IEnumerable<KeyValuePair<string,string>> Fragments=>CurrentCatalog.Fragments.Concat(Current==ApplicationLanguage.English?[]:Catalogs[ApplicationLanguage.English].Fragments);
    private static KeyValuePair<string,string>[] BuildFragments(Dictionary<string,string> translations)=>translations.Where(x=>(x.Key.Length>=4||x.Key.EndsWith('：'))&&!x.Key.Contains('{')&&x.Key.Any(c=>c>='\u3400'&&c<='\u9fff')).OrderByDescending(x=>x.Key.Length).ToArray();

    internal static ApplicationLanguage Current {get;private set;}=Resolve(ApplicationLanguage.FollowSystem);
    internal static bool IsEnglish=>Current==ApplicationLanguage.English;
    internal static bool IsTranslated=>Current!=ApplicationLanguage.SimplifiedChinese;
    // The Chinese source word also labels Close-window buttons. Additional
    // catalogs use explicit status keys for boolean fields, never that command.
    internal static string SwitchText(bool enabled)=>Text(Current is ApplicationLanguage.SimplifiedChinese or ApplicationLanguage.English or ApplicationLanguage.Russian
        ?enabled?"开启":"关闭":enabled?"已开启":"已关闭");
    // UI entry accepts the selected language's decimal separator. Persisted
    // numbers and game protocols continue to use invariant representations.
    internal static System.Globalization.CultureInfo NumericInputCulture=>Current switch
    {
        ApplicationLanguage.Russian=>System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),
        ApplicationLanguage.French=>System.Globalization.CultureInfo.GetCultureInfo("fr-FR"),
        ApplicationLanguage.German=>System.Globalization.CultureInfo.GetCultureInfo("de-DE"),
        ApplicationLanguage.Spanish=>System.Globalization.CultureInfo.GetCultureInfo("es-ES"),
        _=>System.Globalization.CultureInfo.InvariantCulture
    };
    internal static string Source(Control control)=>Bound.TryGetValue(control,out var binding)?binding.Source:control.Text;
    internal static string Choose(string chinese,string english,string? russian=null)=>IsEnglish?english:
        Current==ApplicationLanguage.Russian&&russian is not null?russian:Text(chinese);
    internal static string FormatTemplate(string template,params object?[] values)=>Format(FormattableStringFactory.Create(template,values));
    internal static string Format(FormattableString source)
    {
        // Translate the template, never opaque arguments such as user names,
        // paths or game text. Preserve numeric format specifiers in the output.
        string template = source.Format;
        if (IsTranslated)
        {
            var formats = Regex.Matches(template, @"\{(\d+)(?:,-?\d+)?(?::[^{}]+)?\}")
                .Cast<Match>().GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Value);
            string key = Regex.Replace(template, @"\{(\d+)(?:,-?\d+)?(?::[^{}]+)?\}", "{$1}");
            bool found=TryText(key,out var translated);
            if(!found&&key.Trim()!=key&&TryText(key.Trim(),out translated))
            {
                translated=key[..(key.Length-key.TrimStart().Length)]+translated+key[key.TrimEnd().Length..];found=true;
            }
            if (found)
                template = Regex.Replace(translated!, @"\{(\d+)\}", m => formats.GetValueOrDefault(m.Groups[1].Value, m.Value));
        }
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, template, source.GetArguments());
    }
    // Exact lookup for a diagnostic message that may also contain opaque paths.
    // Do not apply fragment or template matching to unknown external text.
    internal static string Exact(string? source)=>IsTranslated&&source is not null&&TryText(source,out var text)?text:source??"";
    internal static string Text(string? source)
    {
        if(string.IsNullOrEmpty(source)||!IsTranslated)return source??"";
        if(TryText(source,out var translated))return translated;
        if(source.Trim()!=source&&TryText(source.Trim(),out translated))
            return source[..(source.Length-source.TrimStart().Length)]+translated+source[source.TrimEnd().Length..];
        if(!source.Any(c=>c>='\u3400'&&c<='\u9fff'))return source;
        if(UiDiagnosticText.Translate(source) is {} diagnostic)return diagnostic;
        // These payloads contain nested UI status, not opaque game/user text.
        // Translate each component before formatting the surrounding message.
        if(source.StartsWith("实际插件：",StringComparison.Ordinal))
        {
            var parts=source[5..].Split('\n',2);
            return Format($"实际插件：{Text(parts[0])}")+(parts.Length>1?"\n"+Text(parts[1]):"");
        }
        const string missingPrefix="方案尚缺：",missingSuffix="。请在“编辑”中补全后应用。";
        if(source.StartsWith(missingPrefix,StringComparison.Ordinal)&&source.EndsWith(missingSuffix,StringComparison.Ordinal))
            return Format($"方案尚缺：{string.Join(", ",source[missingPrefix.Length..^missingSuffix.Length].Split('、').Select(Text))}。请在“编辑”中补全后应用。");
        const string connectionPrefix="翻译接口尚缺：",connectionSuffix="。若服务无需鉴权，请勾选“无需密钥（本地／免鉴权）”。";
        if(source.StartsWith(connectionPrefix,StringComparison.Ordinal)&&source.EndsWith(connectionSuffix,StringComparison.Ordinal))
            return Format($"翻译接口尚缺：{string.Join(", ",source[connectionPrefix.Length..^connectionSuffix.Length].Split('、').Select(Text))}。若服务无需鉴权，请勾选“无需密钥（本地／免鉴权）”。");
        // Compose known nested product statuses before a generic template can
        // consume the inner text as an opaque parameter.
        if(source.StartsWith("配置文件：",StringComparison.Ordinal))return Format($"配置文件：{source[5..]}");
        if(source.StartsWith("自动/混合模式会合并已安装语言：",StringComparison.Ordinal))return Format($"自动/混合模式会合并已安装语言：{source["自动/混合模式会合并已安装语言：".Length..]}");
        if(source.StartsWith("API Key：",StringComparison.Ordinal))
        {
            var value=source[8..].Replace("未配置",Text("未配置")).Replace("已配置",Text("已配置")).Replace("长度",Text("长度")).Replace("（"," (").Replace("）",")");
            return Format($"API Key：{value}");
        }
        var draft=Regex.Match(source,@"^(已保存|未保存修改) · 草稿，缺少：(.+)$");
        if(draft.Success){var missing=string.Join(", ",draft.Groups[2].Value.Split('、').Select(Text));return draft.Groups[1].Value=="已保存"?Format($"已保存 · 草稿，缺少：{missing}"):Format($"未保存修改 · 草稿，缺少：{missing}");}
        foreach(var suffix in new[]{"（未配置完成）","（未保存）"})
            if(source.EndsWith(suffix,StringComparison.Ordinal))return source[..^suffix.Length]+Exact(suffix);
        var origin=Regex.Match(source,@"^来源：(.+) · 接收服务：(.+)\n协议：OpenAI 兼容格式（实际请求发送至上方接口地址）$");
        if(origin.Success)return Format($"来源：{Text(origin.Groups[1].Value)} · 接收服务：{Text(origin.Groups[2].Value)}\n协议：OpenAI 兼容格式（实际请求发送至上方接口地址）");
        var usage=Regex.Match(source,@"^配置引用：(.+)。保存会影响这些功能后续使用的方案，不表示任务正在运行。$");
        if(usage.Success)return Format($"配置引用：{string.Join("; ",usage.Groups[1].Value.Split('；').Select(Text))}。保存会影响这些功能后续使用的方案，不表示任务正在运行。");
        foreach(var pattern in Patterns)
        {
            if(!source.StartsWith(pattern.Prefix,StringComparison.Ordinal))continue;
            var match=pattern.Regex.Match(source);if(!match.Success)continue;
            return Regex.Replace(pattern.Replacement,@"\{(\d+)\}",m=>match.Groups["v"+m.Groups[1].Value].Value);
        }
        foreach(var suffix in new[]{" ▾"," ▴","…"})
            if(source.EndsWith(suffix,StringComparison.Ordinal)&&TryText(source[..^suffix.Length],out translated))return translated+suffix;
        if(source.Contains('\n'))return string.Join("\n",source.Split('\n').Select(Text));
        foreach(var fragment in Fragments)
        {
            if(source.StartsWith(fragment.Key,StringComparison.Ordinal))return fragment.Value+Text(source[fragment.Key.Length..]);
            if(source.EndsWith(fragment.Key,StringComparison.Ordinal))return Text(source[..^fragment.Key.Length])+fragment.Value;
        }
        if(source.Contains(" · ",StringComparison.Ordinal))return string.Join(" · ",source.Split(" · ").Select(Text));
        return source;
    }
    internal static void Set(ApplicationLanguage language)
    {
        WatchWindows();var next=Resolve(language);
        if(Current==next)return;Current=next;
        for(int i=Controls.Count-1;i>=0;i--)
        {
            if(!Controls[i].TryGetTarget(out var c)||c.IsDisposed){Controls.RemoveAt(i);continue;}
            if(Bound.TryGetValue(c,out var binding))binding.Refresh(c);
            c.Invalidate();
        }
        for(int i=ColumnRefs.Count-1;i>=0;i--)
            if(ColumnRefs[i].TryGetTarget(out var column)&&Columns.TryGetValue(column,out var source))column.HeaderText=Text(source);else ColumnRefs.RemoveAt(i);
        for(int i=MenuRefs.Count-1;i>=0;i--)
            if(MenuRefs[i].TryGetTarget(out var item)&&!item.IsDisposed&&MenuBindings.TryGetValue(item,out var binding))RefreshMenuItem(item,binding);else MenuRefs.RemoveAt(i);
        WpfUiLanguage.Refresh();
        LanguageChanged?.Invoke(null,EventArgs.Empty);
    }
    internal static void Bind(Control c)
    {
        if(Bound.TryGetValue(c,out _))return;
        if(c is Label label)label.UseMnemonic=false;
        if(c is ButtonBase button)button.UseMnemonic=false;
        var binding=new Binding(c);Bound.Add(c,binding);Controls.Add(new(c));
        c.TextChanged+=(_,_)=>{if(binding.Applying)return;binding.Source=Original(c.Text);binding.Refresh(c);};
        binding.Refresh(c);
    }
    internal static void BindTree(Control root)
    {
        if(Trees.TryGetValue(root,out _))return;
        Trees.Add(root,new object());Bind(root);
        if(root is DataGridView grid)
        {
            foreach(DataGridViewColumn column in grid.Columns)BindColumn(column);
            grid.ColumnAdded+=(_,e)=>BindColumn(e.Column);
        }
        if(root.ContextMenuStrip is {} menu)BindMenu(menu);
        root.ContextMenuStripChanged+=(_,_)=>{if(root.ContextMenuStrip is {} added)BindMenu(added);};
        root.ControlAdded+=(_,e)=>{if(e.Control is {} child)BindTree(child);};
        foreach(Control child in root.Controls)BindTree(child);
    }

    private static readonly ConditionalWeakTable<DataGridViewColumn,string> Columns=new();
    private static readonly List<WeakReference<DataGridViewColumn>> ColumnRefs=[];
    private static void BindColumn(DataGridViewColumn column)
    {
        if(Columns.TryGetValue(column,out _))return;
        string source=Original(column.HeaderText);Columns.Add(column,source);ColumnRefs.Add(new(column));column.HeaderText=Text(source);
    }
    internal static event EventHandler? LanguageChanged;
    private sealed class MenuBinding(ToolStripItem item){internal string Source=Original(item.Text??"");internal bool Applying;}
    private static readonly ConditionalWeakTable<ToolStripItem,MenuBinding> MenuBindings=new();
    private static readonly List<WeakReference<ToolStripItem>> MenuRefs=[];
    private static readonly ConditionalWeakTable<ToolStrip,object> Menus=new();
    internal static void BindMenu(ToolStrip menu)
    {
        if(Menus.TryGetValue(menu,out _))return;Menus.Add(menu,new object());
        foreach(ToolStripItem item in menu.Items)BindMenuItem(item);
        menu.ItemAdded+=(_,e)=>{if(e.Item is {} item)BindMenuItem(item);};
    }
    private static void BindMenuItem(ToolStripItem item)
    {
        if(MenuBindings.TryGetValue(item,out _))return;
        var binding=new MenuBinding(item);MenuBindings.Add(item,binding);MenuRefs.Add(new(item));
        item.TextChanged+=(_,_)=>{if(binding.Applying)return;binding.Source=Original(item.Text??"");RefreshMenuItem(item,binding);};
        RefreshMenuItem(item,binding);
        if(item is ToolStripDropDownItem drop)BindMenu(drop.DropDown);
        if(item is ToolStripControlHost host)BindTree(host.Control);
    }
    private static void RefreshMenuItem(ToolStripItem item,MenuBinding binding)
    {if(binding.Applying||item.IsDisposed)return;binding.Applying=true;try{item.Text=Text(binding.Source);}finally{binding.Applying=false;}}
}

internal sealed class LocalizedContextMenuStrip : ContextMenuStrip
{
    internal LocalizedContextMenuStrip(){UiLanguage.BindMenu(this);}
    public new void Close() { if (!WpfNativeMenus.Close(this)) base.Close(); }
    protected override void OnClosed(ToolStripDropDownClosedEventArgs e) { WpfNativeMenus.Close(this); base.OnClosed(e); }
    protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
    {
        if (WpfNativeMenus.InterceptOpening(this, e)) return;
        base.OnOpening(e);
    }
    public new void Show(Control control,Point position)
    {
        if (WpfNativeMenus.Show(this, control, position)) return;
        if(WpfControlCoordinates.HasView(control))base.Show(WpfControlCoordinates.ToScreen(control,position));
        else base.Show(control,position);
    }
}
