using W = System.Windows;
using WC = System.Windows.Controls;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private NativeRpgController? nativeRpg;
    internal W.FrameworkElement NativeRpgView => (nativeRpg ??= new(this)).View;
    private sealed partial class NativeRpgController : IDisposable
    {
        private readonly MainForm owner;
        private readonly NativeRpgColumn root = new();
        internal readonly WC.ContentControl CommonSpeedSlot = new() { Margin = new(0, 8, 0, 12) };
        private bool disposed;
        internal W.FrameworkElement View => root.View;
        private bool IsDisposed => disposed || owner.IsDisposed;
        private bool Disposing => owner.Disposing;
        private bool IsHandleCreated => View.IsLoaded;
        private void BeginInvoke(Action action) => View.Dispatcher.BeginInvoke(action);
        internal NativeRpgController(MainForm owner)
        {
            this.owner = owner;
            _dataSelectedName.Localize = _dataOriginalName.Localize = _dataDescription.Localize = _dataCurrentLabel.Localize = _dataKeyLabel.Localize = false;
            root.Name = "RpgModificationWorkspace";
            _dataLaunch = IconButton("修改并启动", "play", async (_, _) => await LaunchGameDataAsync(false), true, 116);
            _dataToggle = WorkspaceButton("开启修改", async (_, _) => await ToggleGameDataAsync());
            _dataToggle.Primary = true;
            _dataRestore = WorkspaceButton("移除连接组件", async (_, _) => await LaunchGameDataAsync(true));
            _dataRefresh = IconButton("刷新", "refresh", async (_, _) => { SelectedDataSession()?.Labels?.RetryMissing(); await RefreshGameDataAsync(); }, false, 92);
            string[] names = ["常用", "角色", "物品", "武器", "防具", "变量", "开关", "地图", "事件", "已锁定", "通用数据"];
            _dataScope.Items.AddRange(names); _dataScope.SelectedIndex = 0;
            var tabs = ModificationRow();
            for (int i = 0; i < names.Length; i++) { int index = i; var tab = IconButton(names[i], "", (_, _) => _dataScope.SelectedIndex = index, i == 0, 56); tab.Name = "RpgTab" + index; tab.TabStyle = true; _dataTabs.Add(tab); tabs.Controls.Add(tab); }
            WorkspaceAdd(root, ModificationRow(_dataLaunch, _dataToggle, _dataRefresh, IconButton("撤销上次修改", "back", async (_, _) => await ExecuteGameDataAsync(new() { ["op"] = "undo" }), false, 116))); WorkspaceAdd(root, tabs);
            root.Controls.Add(new NativeRpgElement(CommonSpeedSlot));
            _dataEventKind.Items.AddRange(new[]{"地图事件", "公共事件", "运行中事件"}.Select(UiLanguage.Text)); _dataEventKind.SelectedIndex = 0;
            _dataMapLabel = WorkspaceLabel("浏览地图"); _dataReturnMap = IconButton("跟随当前地图", "", async (_, _) => { _dataMapFollowCurrent = true; _dataSelectedMapId = 0; _dataMapSyncRevision = 0; _dataRevision++; ResetDataDetails(); await RefreshGameDataAsync(); }, false, 116);
            _dataMapToolbar = ModificationRow(_dataMapLabel, _dataMapChoice, _dataReturnMap, _dataEventKind); _dataMapToolbar.Visible = false; WorkspaceAdd(root, _dataMapToolbar);
            _dataMapChoice.SelectedIndexChanged += async (_, _) => { if (_dataMapChoosing) return; var choice = _dataMapChoice.SelectedItem as MapChoice; _dataSelectedMapId = choice?.Id ?? 0; _dataMapFollowCurrent = choice?.Current ?? true; _dataMapSyncRevision = 0; _dataRevision++; ResetDataDetails(); await RefreshGameDataAsync(); };
            _dataEventKind.SelectedIndexChanged += async (_, _) => { if (_dataRestoringView || _dataScope.SelectedIndex != 8) return; UpdateModificationPresentation(); _dataRevision++; _dataMap.ClearMap(); _dataPage.Text = ""; ResetDataDetails(); await RefreshGameDataAsync(); };
            var columns = new WpfModificationColumns(); _dataBrowser = new NativeRpgElement(columns); WorkspaceAdd(root, _dataBrowser);
            var list = ModificationColumn(); _dataListSurface = list; columns.Children.Add(list.View);
            _dataListSearch = new NativeRpgElement(WpfModificationLayout.Search((WC.TextBox)_dataSearch.View, "搜索原名、译名或编号")); WorkspaceAdd(list, _dataListSearch); _dataGrid.Height = WpfModificationLayout.ListHeight; WorkspaceAdd(list, _dataGrid); WorkspaceAdd(list, _dataPage);
            _dataSearch.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _dataRevision++; _dataLimit = 160; ResetDataDetails(); await RefreshGameDataAsync(); } };
            _dataGrid.SelectionChanged += (_, _) => SelectGameDataRow();
            _dataGrid.LockClicked += async row => { if (row.Tag is JsonElement data && data.GetProperty("canLock").GetBoolean()) await ExecuteGameDataAsync(new() { ["op"] = data.GetProperty("locked").GetBoolean() ? "unlock" : "lock", ["key"] = data.GetProperty("key").GetString(), ["value"] = System.Text.Json.Nodes.JsonNode.Parse(data.GetProperty("value").GetRawText()) }); };
            _dataGrid.Scroll += async (_, _) => { if (!_dataLoading && _dataLimit < _dataTotal && _dataGrid.FirstDisplayedScrollingRowIndex + _dataGrid.DisplayedRowCount(false) >= _dataGrid.Rows.Count - 5) { _dataLimit += 160; await RefreshGameDataAsync(); } };
            var map = ModificationColumn(); _dataMapPanel = map; map.Visible = false; _dataMap.Height = 360; WorkspaceAdd(map, _dataMap); WorkspaceAdd(map, _dataMapSelection);
            WorkspaceAdd(map, ModificationRow(IconButton("−", "", (_, _) => _dataMap.Zoom(.8f), false, 36), IconButton("适合窗口", "", (_, _) => _dataMap.Fit(), false, 88), IconButton("+", "", (_, _) => _dataMap.Zoom(1.25f), false, 36)));
            WorkspaceAdd(map, WorkspaceLabel("绿：通行  灰：阻挡  蓝：水域  紫：地形标记\n橙：事件  亮蓝：玩家 · 滚轮缩放 / 右键拖动")); WorkspaceAdd(list, map);
            _dataMap.TileSelected += async (_, _) => await ShowSelectedMapTileAsync();
            var detail = ModificationColumn(); _dataEditor = detail;
            ((WC.TextBlock)_dataSelectedName.View).FontSize = 20; ((WC.TextBlock)_dataSelectedName.View).FontWeight = W.FontWeights.SemiBold;
            var titles = ModificationColumn(); WorkspaceAdd(titles, _dataSelectedName); WorkspaceAdd(titles, _dataOriginalName);
            WorkspaceAdd(detail, ModificationRow(_dataSelectedIcon, titles)); WorkspaceAdd(detail, _dataDescription); WorkspaceAdd(detail, _dataKeyLabel); WorkspaceAdd(detail, _dataCurrentLabel);
            _dataScalarEditor = ModificationColumn(); _dataNumberField = _dataValue;
            _dataLockButton = IconButton("锁定", "", async (_, _) => await ChangeGameDataAsync(_dataSelected?.GetProperty("locked").GetBoolean() == true ? "unlock" : "lock"), false, 92);
            _dataApplyButton = IconButton("应用", "check", async (_, _) => await ChangeGameDataAsync("set"), true, 76);
            WorkspaceAdd(_dataScalarEditor, ModificationRow(WorkspaceLabel("目标值"), _dataLockButton, _dataValue, _dataBoolean, _dataTextValue, _dataApplyButton));
            _dataValue.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ChangeGameDataAsync("set"); } };
            _dataBoolean.CheckedChanged += async (_, _) => { if (!_dataScalarUpdating && _dataSelected?.GetProperty("type").GetString() == "boolean") await ChangeGameDataAsync("set"); };
            NativeRpgButton Step(string caption, int delta) => IconButton(caption, "", async (_, _) => await AdjustGameDataValueAsync(delta), false, 60);
            _dataSteps = ModificationRow(Step("− 10", -10), Step("− 1", -1), Step("+ 1", 1), Step("+ 10", 10)); WorkspaceAdd(_dataScalarEditor, _dataSteps);
            var aliases = ModificationColumn(); aliases.Visible = false; WorkspaceAdd(aliases, _dataChinese); WorkspaceAdd(aliases, WorkspaceButton("保存译名", (_, _) => SaveGameDataAlias()));
            WorkspaceAdd(_dataScalarEditor, ModificationRow(IconButton("恢复输入", "", (_, _) => ShowGameDataScalar(_dataSelected), false, 86), IconButton("编辑译名", "edit", (_, _) => aliases.Visible = !aliases.Visible, false, 110))); WorkspaceAdd(_dataScalarEditor, aliases); WorkspaceAdd(detail, _dataScalarEditor);
            _dataObjectEditor = ModificationColumn(); WorkspaceAdd(detail, _dataObjectEditor);
            _dataDetailScroll = new() { Height = WpfModificationLayout.DetailHeight }; _dataDetailScroll.Controls.Add(detail); _dataDetailSurface = _dataDetailScroll; WC.Grid.SetColumn(_dataDetailScroll.View, 1); columns.Children.Add(_dataDetailScroll.View);
            _dataCommonBoard = new() { Execute = ExecuteGameDataAsync }; WC.Grid.SetColumnSpan(_dataCommonBoard.View, 2); columns.Children.Add(_dataCommonBoard.View);
            _dataContextActions = ModificationRow(IconButton("恢复全队", "", async (_, _) => await ExecuteGameDataAsync(new() { ["op"] = "recoverParty" }), false, 84), IconButton("备份存档", "folder", (_, _) => BackupRpgSaves(), false, 94)); WorkspaceAdd(root, _dataContextActions);
            var battle = ModificationRow(); _dataBattleActions = battle; battle.Controls.Add(WorkspaceLabel("战斗操作"));
            foreach (var (caption, action) in new[] { ("胜利", "victory"), ("逃跑", "escape"), ("敌人 HP: 1", "enemyOne"), ("敌人 HP: 最大", "enemyMax"), ("队伍 HP: 1", "partyOne"), ("失败", "defeat"), ("队伍 HP: 0", "partyZero") }) battle.Controls.Add(IconButton(caption, "", async (_, _) => await ExecuteGameDataAsync(new() { ["op"] = "battleAction", ["action"] = action }), false, 68)); WorkspaceAdd(root, battle);
            _dataLockActions = ModificationRow(IconButton("保存锁定", "", async (_, _) => await SaveRpgLocksAsync(), false, 92), IconButton("读取锁定", "", async (_, _) => await LoadRpgLocksAsync(), false, 92)); WorkspaceAdd(root, _dataLockActions);
            WorkspaceAdd(root, ModificationRow(_dataFeedback, IconButton("解除全部锁定", "", async (_, _) => await ExecuteGameDataAsync(new() { ["op"] = "unlockAll" }), false, 116)));
            _dataScope.SelectedIndexChanged += async (_, _) =>
            {
                if (_dataRestoringView) return;
                _dataViewStates[ViewStateKey(_dataTabPrevious)] = new(_dataSearch.Text, _dataSelected?.GetProperty("key").GetString(), Math.Max(0, _dataGrid.FirstDisplayedScrollingRowIndex), _dataLimit, _dataSelectedMapId, _dataMapFollowCurrent);
                _dataTabPrevious = _dataScope.SelectedIndex; _dataRevision++; _dataLimit = 160; _dataSearch.Clear(); _dataSelectedMapId = 0; _dataMapFollowCurrent = true; _dataMapSyncRevision = 0;
                if (_dataViewStates.TryGetValue(ViewStateKey(_dataTabPrevious), out var saved)) { _dataSearch.Text = saved.Query; _dataLimit = saved.Limit; _dataSelectedMapId = saved.Map; _dataMapFollowCurrent = saved.FollowMap; }
                foreach (var tab in _dataTabs) tab.Primary = _dataTabs.IndexOf(tab) == _dataScope.SelectedIndex;
                _dataMapToolbar.Visible = _dataScope.SelectedIndex is 7 or 8; _dataEventKind.Visible = _dataScope.SelectedIndex == 8; _dataListSearch.Visible = _dataScope.SelectedIndex != 7; _dataMapPanel.Visible = _dataScope.SelectedIndex == 7; _dataGrid.Visible = _dataScope.SelectedIndex != 7;
                ResetDataDetails(); UpdateModificationPresentation(); await RefreshGameDataAsync(preferCached:true);
            };
            _dataMapTimer.Tick += async (_, _) => await SyncGameMapAsync(); View.Loaded += (_, _) => _dataMapTimer.Start(); View.Unloaded += (_, _) => _dataMapTimer.Stop(); UpdateModificationPresentation();
            UiLanguage.LanguageChanged+=RefreshModificationLanguage;
        }
        private void RefreshModificationLanguage(object? sender,EventArgs e)
        {
            if(IsDisposed)return;
            if(!View.Dispatcher.CheckAccess()){View.Dispatcher.BeginInvoke((Action)(()=>RefreshModificationLanguage(sender,e)));return;}
            _dataGrid.RefreshLanguage();
            foreach(var (element,refresh) in _dataInterfaceRefresh.ToArray())if(!element.IsDisposed)refresh();
            // Only change product-owned choices. Do not re-query the game or
            // rebuild role details while a numeric/text editor has a draft.
            bool restoring=_dataRestoringView,choosing=_dataMapChoosing;_dataRestoringView=_dataMapChoosing=true;
            try
            {
                int selectedKind=_dataEventKind.SelectedIndex;string[] kinds=["地图事件","公共事件","运行中事件"];
                for(int i=0;i<Math.Min(kinds.Length,_dataEventKind.Items.Count);i++)_dataEventKind.Items[i]=UiLanguage.Text(kinds[i]);
                _dataEventKind.SelectedIndex=selectedKind;
                ((WC.ComboBox)_dataMapChoice.View).Items.Refresh();
            }
            finally{_dataRestoringView=restoring;_dataMapChoosing=choosing;}
            if(_dataSelected is null&&_dataScope.SelectedIndex!=7)
            {_dataSelectedName.Text=UiLanguage.Text("选择一项数据");_dataDescription.Text=UiLanguage.Text("在左侧选择项目，查看详情并修改。");}
            if(_dataSelected is {} selected)
            {
                _dataKeyLabel.Text=ReadableDataKey(selected.GetProperty("key").GetString()!);
                if(selected.GetProperty("type").GetString() is not ("actor" or "event"))
                    _dataCurrentLabel.Text=UiLanguage.Format($"当前值  {DataDisplayValue(selected.GetProperty("value"))}");
            }
            if(_dataScope.SelectedIndex==7)
            {
                _dataMap.Invalidate();
                _dataSelectedName.Text=_dataMap.SelectedTile is {} tile?UiLanguage.Format($"格子 {tile.X}, {tile.Y}"):UiLanguage.Text("地图浏览");
                _dataOriginalName.Text=UiLanguage.Text(_dataMap.SelectionText);_dataDescription.Text=UiLanguage.Text(_dataMap.TileDetails);
            }
        }
        internal void Render() { _dataMap.Font = owner.Font; RenderGameModificationState(); }
        internal Task Refresh() => RefreshGameDataAsync();
        internal Task RefreshStatus() => RefreshGameDataStatusAsync();
        private static NativeRpgColumn ModificationColumn() => new();
        private static NativeRpgRow ModificationRow(params NativeRpgElement[] elements) { var row = new NativeRpgRow(); foreach (var element in elements) { element.Margin = new(0, 3, 10, 3); row.Controls.Add(element); } return row; }
        private static NativeRpgLabel WorkspaceLabel(string text) => new() { Text = text, Margin = new(0, 4, 0, 6) };
        private static NativeRpgButton IconButton(string text, string icon, EventHandler action, bool primary = false, int width = 96) => new(text, icon, action, primary) { Margin = new(0, 3, 8, 3) };
        private static NativeRpgButton WorkspaceButton(string text, EventHandler action) => IconButton(text, "", action);
        private static NativeRpgText SearchField(NativeRpgText field, string icon = "search") => field;
        private static void WorkspaceAdd(NativeRpgColumn parent, NativeRpgElement element, SizeType size = SizeType.AutoSize, int height = 0) { element.Margin = new(0, 4, 0, 8); parent.Controls.Add(element); }
        private void AddDataDetail(NativeRpgElement element) => DynamicDetailAdd(_dataObjectEditor!, element);
        private static NativeRpgLabel DataDetailLabel(string text) => WorkspaceLabel(text);
        private static void DynamicDetailAdd(NativeRpgColumn host, NativeRpgElement element) => WorkspaceAdd(host, element);
        public void Dispose() { if (disposed) return; disposed = true;UiLanguage.LanguageChanged-=RefreshModificationLanguage; _dataMapTimer.Stop(); _dataCommonBoard?.Dispose();root.Dispose(); _dataGrid.IconAtlas?.Dispose(); }
    }
}
