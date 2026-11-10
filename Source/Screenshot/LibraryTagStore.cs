using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed record LibraryTagValue(string Id, string Name) { public override string ToString() => LibraryTagStore.DisplayName(this); }
internal sealed record LibraryTagGroup(string Id, string Name, bool Multiple, LibraryTagValue[] Values, string Source = "user", string SourceName = "") { public override string ToString() => LibraryTagStore.DisplayName(this); }
internal sealed record LibraryTagSidebar(bool Engines = true, string Primary = "", string Secondary = "");
internal sealed record LibraryTagDocument(int Schema, long Revision, LibraryTagGroup[] Groups,
    Dictionary<string, Dictionary<string, string[]>> Games, LibraryTagSidebar Sidebar);
internal sealed record LibraryTagSelection(string Group, string Value)
{
    internal string Key => Uri.EscapeDataString(Group) + ":" + Uri.EscapeDataString(Value);
    internal static LibraryTagSelection? Parse(string key)
    {
        var split = key.IndexOf(':');
        if (split < 1) return null;
        try { return new(Uri.UnescapeDataString(key[..split]), Uri.UnescapeDataString(key[(split + 1)..])); }
        catch (UriFormatException) { return null; }
    }
}
internal sealed record LibraryTagFacet(string Id, string Name, string Source, LibraryTagValue[] Values);

// A separate optional document: opening an old library never rewrites its game
// catalog, paths, short notes, folders, long notes, credentials or AI history.
internal sealed class LibraryTagStore
{
    internal const string Unset = "__unset";
    internal const string Engine = "__engine";
    internal const string Legacy = "__legacy";
    internal const string Development = "development";
    private static readonly object Gate = new();
    private readonly string path;
    internal LibraryTagDocument Snapshot { get; private set; } = Defaults();
    internal string Notice { get; private set; } = "";
    internal LibraryTagStore(string? file = null) { path = file ?? Path.Combine(AppDataPaths.Root, "library-tags.json"); Reload(); }
    internal static LibraryTagDocument Defaults() => new(1, 0,
    [
        new(Development, "开发状态", false, [new("updating", "更新中"), new("complete", "完结"), new("abandoned", "废弃")], "internal"),
        new("rating", "我的评级", false, [new("top", "TOP"), new("like", "喜欢"), new("average", "一般")]),
        new("content", "内容与玩法", true, [new("casual", "休闲"), new("story", "剧情"), new("explore", "探索"), new("mystery", "悬疑")]),
        new("style", "画面风格", true, [new("anime", "动漫"), new("pixel", "像素"), new("3d", "3D")]),
        new("other", "其他标签", true, [new("ai", "AI")])
    ], new(StringComparer.Ordinal), new());
    internal static string DisplayName(LibraryTagGroup group) => Defaults().Groups.Any(g=>g.Id==group.Id&&g.Name==group.Name)?UiLanguage.Text(group.Name):group.Name;
    internal static string DisplayName(LibraryTagValue value) => value.Id is "" or Unset || Defaults().Groups.SelectMany(g=>g.Values).Any(v=>v.Id==value.Id&&v.Name==value.Name)?UiLanguage.Text(value.Name):value.Name;
    private LibraryTagDocument Read()
    {
        if (!File.Exists(path)) return Defaults();
        if (new FileInfo(path).Length > 16_000_000) throw new InvalidDataException("标签文件过大，原文件已保留。");
        var data = JsonSerializer.Deserialize<LibraryTagDocument>(File.ReadAllText(path)) ?? throw new InvalidDataException("标签文件为空。");
        Validate(data); return data;
    }
    internal void Reload()
    {
        try { Snapshot = Read(); Notice = ""; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
        { Notice = "分类标签无法读取，原文件已保留；暂不能保存标签。"; }
    }
    private static void Validate(LibraryTagDocument data)
    {
        if (data.Schema != 1 || data.Groups is null || data.Games is null || data.Sidebar is null || data.Groups.Length > 64 || data.Games.Count > 100_000)
            throw new InvalidDataException("分类标签格式无法识别。");
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in data.Groups)
        {
            if (group is null || !ValidId(group.Id) || !groupIds.Add(group.Id) || !ValidName(group.Name) || group.Values is null || group.Values.Length > 200 || group.Source is not ("user" or "internal" or "external") || group.SourceName is null)
                throw new InvalidDataException("标签组格式无效。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in group.Values)
                if (value is null || !ValidId(value.Id) || !ids.Add(value.Id) || !ValidName(value.Name) || !names.Add(value.Name)) throw new InvalidDataException("标签格式无效。");
        }
        if (data.Groups.Select(g => g.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Groups.Length) throw new InvalidDataException("存在同名标签组。");
        foreach (var game in data.Games)
        {
            if (!Guid.TryParseExact(game.Key, "N", out _) || game.Value is null) throw new InvalidDataException("标签游戏标识无效。");
            foreach (var entry in game.Value)
            {
                var group = data.Groups.FirstOrDefault(g => g.Id == entry.Key);
                if (group is null || entry.Value is null || entry.Value.Distinct().Count() != entry.Value.Length || (!group.Multiple && entry.Value.Length > 1) || entry.Value.Any(id => !group.Values.Any(v => v.Id == id))) throw new InvalidDataException("标签关联无效。");
            }
        }
        foreach (var id in new[] { data.Sidebar.Primary, data.Sidebar.Secondary })
            if (id is null || id.Length > 0 && !groupIds.Contains(id)) throw new InvalidDataException("分类选择无效。");
        if (data.Sidebar.Primary.Length > 0 && data.Sidebar.Primary == data.Sidebar.Secondary) throw new InvalidDataException("主副分类不能相同。");
    }
    private static bool ValidId(string? text) => !string.IsNullOrEmpty(text) && text.Length <= 64 && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    private static bool ValidName(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 40 && text == text.Trim() && !text.Any(char.IsControl);
    private static string Name(string text) { text = text.Trim(); if (!ValidName(text)) throw new ArgumentException("名称须为 1–40 个字符，不能含换行或控制字符。"); return text; }
    private void Change(Func<LibraryTagDocument, LibraryTagDocument> change)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var latest = Read(); // Always merge against the latest file, never a dialog's stale copy.
            var next = change(latest) with { Revision = checked(latest.Revision + 1) };
            Validate(next);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, next, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".previous"); else File.Move(temporary, path);
                Snapshot = next; Notice = "";
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    internal string CreateGroup(string name, bool multiple)
    {
        name = Name(name); string id = Guid.NewGuid().ToString("N");
        Change(data => data with { Groups = data.Groups.Append(new LibraryTagGroup(id, name, multiple, [])).ToArray() }); return id;
    }
    private static LibraryTagGroup EditableGroup(LibraryTagDocument data, string id)
    {
        var group = data.Groups.FirstOrDefault(g => g.Id == id) ?? throw new ArgumentException("标签组已不存在，请刷新后重试。");
        if (group.Source != "user") throw new ArgumentException("内部和外部标签组保留原定义，请新建自己的标签组。");
        return group;
    }
    internal void RenameGroup(string id, string name)
    {
        name = Name(name); Change(data => { var group = EditableGroup(data, id); return data with { Groups = data.Groups.Select(g => g.Id == id ? group with { Name = name } : g).ToArray() }; });
    }
    internal void DeleteGroup(string id)
    {
        Change(data =>
        {
            _ = EditableGroup(data, id);
            foreach (var assignments in data.Games.Values) assignments.Remove(id);
            return data with { Groups = data.Groups.Where(g => g.Id != id).ToArray(), Sidebar = data.Sidebar with { Primary = data.Sidebar.Primary == id ? "" : data.Sidebar.Primary, Secondary = data.Sidebar.Secondary == id ? "" : data.Sidebar.Secondary } };
        });
    }
    internal string CreateValue(string groupId, string name)
    {
        name = Name(name); string id = Guid.NewGuid().ToString("N");
        Change(data => { var group = EditableGroup(data, groupId); return data with { Groups = data.Groups.Select(g => g.Id == groupId ? group with { Values = group.Values.Append(new LibraryTagValue(id, name)).ToArray() } : g).ToArray() }; }); return id;
    }
    internal void RenameValue(string groupId, string id, string name)
    {
        name = Name(name); Change(data =>
        {
            var group = EditableGroup(data, groupId);
            if (!group.Values.Any(v => v.Id == id)) throw new ArgumentException("标签已不存在。");
            return data with { Groups = data.Groups.Select(g => g.Id == groupId ? group with { Values = group.Values.Select(v => v.Id == id ? v with { Name = name } : v).ToArray() } : g).ToArray() };
        });
    }
    internal void DeleteValue(string groupId, string id)
    {
        Change(data =>
        {
            var group = EditableGroup(data, groupId);
            foreach (var assignments in data.Games.Values)
                if (assignments.TryGetValue(groupId, out var values)) assignments[groupId] = values.Where(value => value != id).ToArray();
            return data with { Groups = data.Groups.Select(g => g.Id == groupId ? group with { Values = group.Values.Where(v => v.Id != id).ToArray() } : g).ToArray() };
        });
    }
    internal void SetSidebar(LibraryTagSidebar value) => Change(data => data with { Sidebar = value });
    internal void SetGameGroups(IEnumerable<string> games, Dictionary<string, string[]> changes)
    {
        var ids = games.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0 || ids.Any(id => !Guid.TryParseExact(id, "N", out _))) throw new ArgumentException("游戏标识无效。");
        Change(data =>
        {
            foreach (var change in changes)
            {
                var group = data.Groups.FirstOrDefault(g => g.Id == change.Key) ?? throw new ArgumentException("标签组已不存在，请重新打开编辑窗口。");
                if (group.Source == "external") throw new ArgumentException("外部来源标签不能在这里修改。");
                if (change.Value is null || (!group.Multiple && change.Value.Length > 1) || change.Value.Any(id => !group.Values.Any(v => v.Id == id))) throw new ArgumentException("所选标签已变化，请重新打开编辑窗口。");
            }
            foreach (var id in ids)
            {
                if (!data.Games.TryGetValue(id, out var current)) data.Games[id] = current = new(StringComparer.Ordinal);
                foreach (var change in changes) current[change.Key] = change.Value.Distinct(StringComparer.Ordinal).ToArray();
            }
            return data;
        });
    }
    internal static string[] Assigned(LibraryTagDocument data, string gameId, string groupId) =>
        data.Games.GetValueOrDefault(gameId)?.GetValueOrDefault(groupId) ?? [];
    internal static bool Search(LibraryTagDocument data, string gameId, string query) =>
        data.Groups.Any(group => group.Values.Any(value => Assigned(data, gameId, group.Id).Contains(value.Id) && (value.Name.Contains(query, StringComparison.OrdinalIgnoreCase)||DisplayName(value).Contains(query,StringComparison.OrdinalIgnoreCase))));
    internal static bool Matches(LibraryTagDocument data, RecentGame game, LibraryAnnotation note, string engine, LibraryTagSelection selection, LibraryPreferences? preferences = null)
    {
        if (selection.Group == Engine) return StringComparer.Ordinal.Equals(engine, selection.Value);
        if (selection.Group == Legacy) return LibraryCategories.Contains(preferences, game, note, selection.Value);
        var values = Assigned(data, game.Id, selection.Group);
        return selection.Value == Unset ? values.Length == 0 : values.Contains(selection.Value, StringComparer.Ordinal);
    }
    internal static LibraryTagFacet[] Facets(LibraryTagDocument data, RecentGame[] games, LibraryMetadata metadata, Dictionary<string, string> engines, LibraryPreferences preferences)
    {
        var result = new List<LibraryTagFacet> { new(Engine, "引擎", "内部", games.Select(g => engines.GetValueOrDefault(g.ExePath) ?? metadata.Get(g.Id).Engine).Select(e => string.IsNullOrWhiteSpace(e) ? "待识别" : e).Distinct(StringComparer.Ordinal).OrderBy(e => e).Select(e => new LibraryTagValue(e, e)).ToArray()) };
        result.AddRange(data.Groups.Select(g => new LibraryTagFacet(g.Id, DisplayName(g), g.Source == "internal" ? UiLanguage.Text("内部") : g.Source == "external" ? UiLanguage.Text("外部 · ") + g.SourceName : UiLanguage.Text("自定义"), g.Values.Select(v=>v with{Name=DisplayName(v)}).Append(new LibraryTagValue(Unset, UiLanguage.Text("未标注"))).ToArray())));
        var legacy = LibraryCategories.Names(preferences, games, metadata);
        if (legacy.Length > 0) result.Add(new(Legacy, "原有分类", "原有标签", legacy.Select(name => new LibraryTagValue(name, name)).ToArray()));
        return result.ToArray();
    }
}
