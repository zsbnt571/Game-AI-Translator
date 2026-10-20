using System.Runtime.CompilerServices;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private sealed partial class NativeRpgController
    {
        private readonly ModificationSnapshotCache _dataSnapshots = new();
        private readonly ConditionalWeakTable<GameDataSession, RpgReturnView> _dataReturnViews = new();
        private GameDataSession? _dataViewSession;
        private IGameDataConnection? _dataSnapshotConnection;
        private bool _dataRestoringView;
        private string _dataRequestSignature = "";
        private string _dataAppliedSignature = "";
        private sealed class RpgReturnView
        {
            internal int Scope, EventKind;
            internal DataViewState? View;
        }
        private void SaveDataReturnView()
        {
            if (_dataViewSession is null) return;
            var saved = _dataReturnViews.GetValue(_dataViewSession,_=>new());
            saved.Scope = _dataScope.SelectedIndex; saved.EventKind = _dataEventKind.SelectedIndex;
            saved.View = new(_dataSearch.Text, _dataSelected?.GetProperty("key").GetString(), Math.Max(0, _dataGrid.FirstDisplayedScrollingRowIndex), _dataLimit, _dataSelectedMapId, _dataMapFollowCurrent);
            _dataViewStates[ViewStateKey(saved.Scope)] = saved.View;
        }
        private void RestoreDataReturnView(GameDataSession? session)
        {
            _dataViewSession = session; _dataSnapshotConnection = null; _dataRefreshPending = false;
            _dataRestoringView = true;
            try
            {
                RpgReturnView? saved = null;
                bool found = session is not null && _dataReturnViews.TryGetValue(session, out saved);
                _dataScope.SelectedIndex = found ? saved!.Scope : 0;
                _dataEventKind.SelectedIndex = found ? saved!.EventKind : 0;
                _dataTabPrevious = _dataScope.SelectedIndex;
                if (found && saved!.View is { } view)
                {
                    _dataSearch.Text = view.Query; _dataLimit = view.Limit; _dataSelectedMapId = view.Map; _dataMapFollowCurrent = view.FollowMap;
                    _dataViewStates[ViewStateKey(saved.Scope)] = view;
                }
                else
                {
                    _dataLimit = 160;
                    for (int i = 0; i < DataScopes.Length; i++) _dataViewStates.Remove(ViewStateKey(i));
                }
                foreach (var tab in _dataTabs) tab.Primary = _dataTabs.IndexOf(tab) == _dataScope.SelectedIndex;
                _dataMapToolbar!.Visible = _dataScope.SelectedIndex is 7 or 8;
                _dataEventKind.Visible = _dataScope.SelectedIndex == 8;
                _dataListSearch!.Visible = _dataScope.SelectedIndex != 7;
                _dataMapPanel!.Visible = _dataScope.SelectedIndex == 7;
                _dataGrid.Visible = _dataScope.SelectedIndex != 7;
                UpdateModificationPresentation();
            }
            finally { _dataRestoringView = false; }
        }
        internal Task Enter() => RefreshGameDataAsync(preferCached: true);
    }
}
