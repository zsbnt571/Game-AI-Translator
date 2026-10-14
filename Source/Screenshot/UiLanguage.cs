using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

public enum ApplicationLanguage { SimplifiedChinese = 0, English = 1, Russian = 2 }

// Display localization never rewrites combo values, profile names, API fields,
// translated game text or serialized identifiers.
internal static class UiLanguage
{
    private static readonly Lazy<Dictionary<string,string>> English=new(()=>
    {
        using var stream=typeof(UiLanguage).Assembly.GetManifestResourceStream("Fusion.UiEnglish.json")
            ?? throw new InvalidOperationException("Interface language resources are missing.");
        return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;
    });
    private static readonly Lazy<Dictionary<string,string>> Russian=new(()=>
    {
        using var stream=typeof(UiLanguage).Assembly.GetManifestResourceStream("Fusion.UiRussian.json")
            ?? throw new InvalidOperationException("Russian interface resources are missing.");
        return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;
    });
    private static Dictionary<string,string> Translations=>Current==ApplicationLanguage.Russian?Russian.Value:English.Value;
    private static Dictionary<string,string> Reverse(Dictionary<string,string> source)=>source
        .GroupBy(x=>x.Value,StringComparer.Ordinal).ToDictionary(x=>x.Key,x=>x.First().Key,StringComparer.Ordinal);
    private static readonly Lazy<Dictionary<string,string>> EnglishReverse=new(()=>Reverse(English.Value));
    private static readonly Lazy<Dictionary<string,string>> RussianReverse=new(()=>Reverse(Russian.Value));
    private static string Original(string source)
    {
        if(EnglishReverse.Value.TryGetValue(source,out var original))return original;
        return Russian.IsValueCreated&&RussianReverse.Value.TryGetValue(source,out original)?original:source;
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
    private static readonly Lazy<Pattern[]> EnglishPatterns=new(()=>BuildPatterns(English.Value));
    private static readonly Lazy<Pattern[]> RussianPatterns=new(()=>BuildPatterns(Russian.Value));
    private static Pattern[] Patterns=>Current==ApplicationLanguage.Russian?RussianPatterns.Value:EnglishPatterns.Value;
    private static Pattern[] BuildPatterns(Dictionary<string,string> translations)=>translations.Where(x=>Regex.IsMatch(x.Key,@"\{\d+\}")).Select(pair=>
    {
        var matches=Regex.Matches(pair.Key,@"\{(\d+)\}");int at=0;var expression=new System.Text.StringBuilder("^");
        foreach(Match match in matches){expression.Append(Regex.Escape(pair.Key[at..match.Index]));expression.Append("(?<v"+match.Groups[1].Value+">.*?)");at=match.Index+match.Length;}
        expression.Append(Regex.Escape(pair.Key[at..])).Append('$');
        return new Pattern(new Regex(expression.ToString(),RegexOptions.Singleline|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(20)),pair.Value,pair.Key[..matches[0].Index]);
    }).OrderByDescending(x=>x.Prefix.Length).ThenByDescending(x=>x.Regex.ToString().Length).ToArray();
    private static readonly Lazy<KeyValuePair<string,string>[]> EnglishFragments=new(()=>BuildFragments(English.Value));
    private static readonly Lazy<KeyValuePair<string,string>[]> RussianFragments=new(()=>BuildFragments(Russian.Value));
    private static KeyValuePair<string,string>[] Fragments=>Current==ApplicationLanguage.Russian?RussianFragments.Value:EnglishFragments.Value;
    private static KeyValuePair<string,string>[] BuildFragments(Dictionary<string,string> translations)=>translations.Where(x=>x.Key.Length>=4&&!x.Key.Contains('{')&&x.Key.Any(c=>c>='\u3400'&&c<='\u9fff')).OrderByDescending(x=>x.Key.Length).ToArray();

    internal static ApplicationLanguage Current {get;private set;}
    internal static bool IsEnglish=>Current==ApplicationLanguage.English;
    internal static bool IsTranslated=>Current!=ApplicationLanguage.SimplifiedChinese;
    internal static string Source(Control control)=>Bound.TryGetValue(control,out var binding)?binding.Source:control.Text;
    internal static string Choose(string chinese,string english,string? russian=null)=>IsEnglish?english:
        Current==ApplicationLanguage.Russian&&russian is not null?russian:Text(chinese);
    internal static string Text(string? source)
    {
        if(string.IsNullOrEmpty(source)||!IsTranslated)return source??"";
        if(Translations.TryGetValue(source,out var translated))return translated;
        if(!source.Any(c=>c>='\u3400'&&c<='\u9fff'))return source;
        foreach(var pattern in Patterns)
        {
            if(!source.StartsWith(pattern.Prefix,StringComparison.Ordinal))continue;
            var match=pattern.Regex.Match(source);if(!match.Success)continue;
            return Regex.Replace(pattern.Replacement,@"\{(\d+)\}",m=>match.Groups["v"+m.Groups[1].Value].Value);
        }
        foreach(var suffix in new[]{" ▾"," ▴","…"})
            if(source.EndsWith(suffix,StringComparison.Ordinal)&&Translations.TryGetValue(source[..^suffix.Length],out translated))return translated+suffix;
        if(source.StartsWith("配置文件：",StringComparison.Ordinal))return (IsEnglish?"Settings file: ":"Файл настроек: ")+source[5..];
        if(source.StartsWith("自动/混合模式会合并已安装语言：",StringComparison.Ordinal))return (IsEnglish?"Automatic / mixed mode combines installed languages: ":"Автоматический / смешанный режим объединяет установленные языки: ")+source["自动/混合模式会合并已安装语言：".Length..];
        if(source.StartsWith("API Key：",StringComparison.Ordinal))
        {
            var value=source[8..].Replace("未配置",IsEnglish?"Not configured":"Не настроен").Replace("已配置",IsEnglish?"Configured":"Настроен").Replace("长度",IsEnglish?"length":"длина").Replace("（"," (").Replace("）",")");
            return (IsEnglish?"API key: ":"Ключ API: ")+value;
        }
        var draft=Regex.Match(source,@"^(已保存|未保存修改) · 草稿，缺少：(.+)$");
        if(draft.Success)return (draft.Groups[1].Value=="已保存"?(IsEnglish?"Saved draft. Missing: ":"Черновик сохранён. Не указано: "):(IsEnglish?"Unsaved draft. Missing: ":"Черновик не сохранён. Не указано: "))+string.Join(", ",draft.Groups[2].Value.Split('、').Select(Text));
        foreach(var pair in new[]{("（未配置完成）",IsEnglish?" (incomplete)":" (не настроен)"),("（未保存）",IsEnglish?" (unsaved)":" (не сохранено)")})
            if(source.EndsWith(pair.Item1,StringComparison.Ordinal))return source[..^pair.Item1.Length]+pair.Item2;
        var origin=Regex.Match(source,@"^来源：(.+) · 接收服务：(.+)\n协议：OpenAI 兼容格式（实际请求发送至上方接口地址）$");
        if(origin.Success)return (IsEnglish?"Origin: ":"Источник: ")+Text(origin.Groups[1].Value)+(IsEnglish?" · Service: ":" · Сервис: ")+Text(origin.Groups[2].Value)+(IsEnglish?"\nProtocol: OpenAI-compatible. Requests go to the service URL above.":"\nПротокол: совместимый с OpenAI. Запросы отправляются по указанному выше адресу сервиса.");
        var usage=Regex.Match(source,@"^配置引用：(.+)。保存会影响这些功能后续使用的方案，不表示任务正在运行。$");
        if(usage.Success)return (IsEnglish?"Used by: ":"Используется: ")+string.Join("; ",usage.Groups[1].Value.Split('；').Select(Text))+(IsEnglish?". Saving affects future use; it does not start a task.":". Сохранение влияет на последующее использование и не запускает задачу.");
        if(source.Contains('\n'))return string.Join("\n",source.Split('\n').Select(Text));
        foreach(var fragment in Fragments)
        {
            if(source.StartsWith(fragment.Key,StringComparison.Ordinal))return fragment.Value+Text(source[fragment.Key.Length..]);
            if(source.EndsWith(fragment.Key,StringComparison.Ordinal))return Text(source[..^fragment.Key.Length])+fragment.Value;
        }
        return source;
    }
    internal static void Set(ApplicationLanguage language)
    {
        WatchWindows();var next=Enum.IsDefined(language)?language:ApplicationLanguage.SimplifiedChinese;
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
}
