using System.ComponentModel;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed class RenpyVariableRow(JsonElement source, string label) : INotifyPropertyChanged
{
    internal JsonElement Source { get; } = source;
    public string Name => Source.GetProperty("name").GetString()!;
    private string label = label;
    public string Label { get => label; internal set { if (label == value) return; label = value; PropertyChanged?.Invoke(this, new(nameof(Label))); } }
    public string Value => Source.GetProperty("value").ValueKind switch { JsonValueKind.True => UiLanguage.SwitchText(true), JsonValueKind.False => UiLanguage.SwitchText(false), JsonValueKind.Null => UiLanguage.Text("暂无记录"), _ => Source.GetProperty("value").ToString() };
    public string Hint => UiLanguage.Text(Source.GetProperty("hint").GetString() ?? "");
    internal void RefreshLanguage(){PropertyChanged?.Invoke(this,new(nameof(Value)));PropertyChanged?.Invoke(this,new(nameof(Hint)));}
    public event PropertyChangedEventHandler? PropertyChanged;
}
internal sealed class NativeRenpyWorkspaceState
{
    internal string Search = "", Value = "", Note = "", Status = "", Hint = "", NamesStatus = "", SortStatus = "名称 · 自然顺序", FinderStatus = "先开始查找，再回到游戏让数值变化，然后继续筛选。留空初值可记录全部数值。", FinderInput = "", Selection = "请选择一项";
    internal int Scope=5, Comparison;
    internal bool Boolean, ChangedOnly, FinderVisible, NotesVisible, ValueEnabled, ValueVisible, BooleanVisible, FinderEnabled;
    internal RenpyVariableRow[] Rows = [], Selected = [];
    internal readonly DesktopActionState Launch = new("修改并启动"), Toggle = new("开启修改"), Refresh = new("刷新数据"), Apply = new("应用修改"), Undo = new("撤销上次修改"), Backup = new("打开存档备份"), Favorite = new("加入常用"), SaveNote = new("保存备注"), FinderBegin = new("开始查找"), FinderNarrow = new("继续筛选"), FinderReset = new("清除查找"), FinderToggle = new("数值查找");
}
internal sealed record RenpyEditSession(JsonElement? Row, IGameDataConnection? Connection);

public sealed partial class MainForm
{
    internal NativeRenpyWorkspaceState NativeRenpy { get; } = new();
    internal event Action? NativeRenpyChanged;
    private void ClearNativeRenpyRows() { NativeRenpy.Rows = []; NativeRenpy.Selected = []; }
    private void SelectNativeRenpyData()
    {
        if (_renpyLoading) return;
        _renpyLoading = true;
        try
        {
            var state = NativeRenpy; var row = SelectedRenpyRow();
            _renpyEditingRow = row; _renpyEditingConnection = _renpyDataSnapshotConnection;
            state.BooleanVisible = row?.GetProperty("kind").GetString() == "bool";
            state.ValueVisible = row is not null && !state.BooleanVisible;
            state.Selection = row?.GetProperty("name").GetString() ?? (state.Selected.Length > 1 ? $"已选择 {state.Selected.Length} 项 · 右键批量操作" : "请选择一项");
            string name = row?.GetProperty("name").GetString() ?? "";
            state.Note = _renpyDataPresentation.Notes.GetValueOrDefault(name, "");
            state.Favorite.Text = _renpyDataPresentation.Favorites.Contains(name) ? "移出常用" : "加入常用";
            state.Favorite.Enabled = state.SaveNote.Enabled = row is not null;
            state.Value = row?.GetProperty("value").ToString() ?? "";
            state.Boolean = row?.GetProperty("value").ValueKind == JsonValueKind.True;
            state.ValueEnabled = SelectedScriptTranslationSession()?.Enabled==true && row is not null && !_renpyDataBusy;
            state.Apply.Enabled = SelectedScriptTranslationSession()?.Enabled==true && row is not null && !_renpyDataBusy && SelectedScriptTranslationSession()?.Connection is { Connected: true } current && ReferenceEquals(_renpyDataSnapshotConnection, current);
        }
        finally { _renpyLoading = false; }
        NativeRenpyChanged?.Invoke();
    }
    private void FilterNativeRenpyData()
    {
        if (_renpyLoading) return;
        var selected = NativeRenpy.Selected.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
        _renpyLoading = true;
        try
        {
            string group = RenpyScopes[Math.Clamp(RenpyScope, 0, RenpyScopes.Length - 1)], query = RenpySearch.Trim();
            var visible = new List<JsonElement>();
            foreach (var row in _renpyDataRows)
            {
                string name = row.GetProperty("name").GetString()!, label = RenpyDataLabel(name);
                if (RenpyFinderVisible && !_renpyNumericFinder.Includes(row)) continue;
                if (group == "common")
                {
                    bool candidate = row.GetProperty("group").GetString()=="money" || _renpyDataPresentation.Favorites.Contains(name) || row.GetProperty("value").ValueKind==JsonValueKind.Number&&ModificationCommonCandidates.Label(name).Length>0;
                    if(!candidate)continue;
                }
                else if (group == "favorites" ? !_renpyDataPresentation.Favorites.Contains(name) : group != "variables" && row.GetProperty("group").GetString() != group) continue;
                if (RenpyChangedOnly && !_renpyChangedNames.Contains(name)) continue;
                if (query.Length > 0 && !name.Contains(query, StringComparison.OrdinalIgnoreCase) && !label.Contains(query, StringComparison.OrdinalIgnoreCase) && !RenpyValue(row.GetProperty("value")).Equals(query, StringComparison.OrdinalIgnoreCase) && !row.GetProperty("value").ToString().Equals(query, StringComparison.OrdinalIgnoreCase)) continue;
                visible.Add(row);
            }
            visible.Sort(CompareRenpyRows);
            NativeRenpy.Rows = visible.Select(row => new RenpyVariableRow(row, RenpyDataLabel(row.GetProperty("name").GetString()!))).ToArray();
            NativeRenpy.Selected = NativeRenpy.Rows.Where(row => selected.Contains(row.Name)).ToArray();
            if (NativeRenpy.Selected.Length == 0 && NativeRenpy.Rows.Length > 0) NativeRenpy.Selected = [NativeRenpy.Rows[0]];
            UpdateRenpyDataStatus();
        }
        finally { _renpyLoading = false; }
        SelectNativeRenpyData(); QueueRenpyDataNames();
    }
    private void RefreshNativeRenpyNames()
    {
        foreach (var row in NativeRenpy.Rows) row.Label = RenpyDataLabel(row.Name);
        if (SelectedScriptTranslationSession()?.Labels is { } labels) RenpyNamesStatus = labels.Status.Length > 0 ? labels.Status : "译名已更新，原文和游戏变量保持不变。";
    }
    internal void NativeRenpySelect(IEnumerable<RenpyVariableRow> rows) { NativeRenpy.Selected = rows.Where(NativeRenpy.Rows.Contains).ToArray(); SelectNativeRenpyData(); }
    internal void NativeRenpySearch(string query) { NativeRenpy.Search = query; FilterNativeRenpyData(); }
    internal void NativeRenpyScope(int scope) { scope=Math.Clamp(scope,0,RenpyScopes.Length-1);if(NativeRenpy.Scope==scope)return;NativeRenpy.Scope=scope;FilterNativeRenpyData(); }
    internal void NativeRenpyChangedOnly(bool changed) { NativeRenpy.ChangedOnly = changed; FilterNativeRenpyData(); if (!changed && RenpyHint.StartsWith("只列出与上次刷新")) RenpyHint = ""; }
    internal void NativeRenpySort(int column)
    {
        if (column == 2) { _renpyValueOrder = _renpySortColumn == 2 ? (_renpyValueOrder + 1) % 3 : 0; _renpySortDescending = _renpyValueOrder == 1; }
        else _renpySortDescending = _renpySortColumn == column && !_renpySortDescending;
        _renpySortColumn = column; UpdateRenpySortPresentation(); FilterNativeRenpyData();
    }
    internal RenpyEditSession NativeRenpyCaptureEdit() => new(_renpyEditingRow, _renpyEditingConnection);
    internal Task NativeRenpyCommit(RenpyEditSession edit, string input) => WriteRenpyCapturedAsync(false, edit.Row, false, input, edit.Connection);
    internal Task NativeRenpyBoolean(bool value) => WriteRenpyCapturedAsync(false, _renpyEditingRow, value, "", _renpyEditingConnection);
    internal async Task NativeRenpyAction(string action)
    {
        switch (action)
        {
            case "launch": await LaunchScriptTranslationSessionAsync("modify"); break;
            case "toggle": await ToggleRenpyModificationAsync(); break;
            case "refresh": await RefreshRenpyDataAsync(); break;
            case "apply": await WriteRenpyDataAsync(false); break;
            case "undo": await WriteRenpyDataAsync(true); break;
            case "backup": if (SelectedScriptTranslationSession()?.Connection is { } connection && _renpyDataBackups.TryGetValue(connection, out string? backup)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(backup) { UseShellExecute = true }); break;
            case "favorite": SaveRenpyDataView(true); break;
            case "note": SaveRenpyDataView(false); break;
            case "names": RequestRenpyDataNames(); break;
            case "notes": NativeRenpy.NotesVisible = !NativeRenpy.NotesVisible; break;
            case "finder": RenpyFinderVisible = !RenpyFinderVisible; FilterRenpyData(); break;
            case "begin": await RunRenpyNumericFinderAsync(true); break;
            case "narrow": await RunRenpyNumericFinderAsync(false); break;
            case "reset": _renpyNumericFinder.Reset(); RenpyFinderStatus = "已清除查找，可重新填写数值或留空开始。"; FilterRenpyData(); break;
            case "selectAll": NativeRenpySelect(NativeRenpy.Rows); break;
            case "clearSelection": NativeRenpySelect([]); break;
            case "enableMany": await WriteRenpyBatchAsync(true); break;
            case "disableMany": await WriteRenpyBatchAsync(false); break;
            case "cancelEdit": SelectRenpyData(); break;
            case "copy": string text = string.Join(Environment.NewLine, SelectedRenpyRows().Select(row => row.GetProperty("name").GetString())); if (text.Length > 0) System.Windows.Clipboard.SetText(text); break;
        }
        RenderNativeRenpyFinder(); NativeRenpyChanged?.Invoke();
    }
}
