using System.Text.Json;
namespace ScreenshotTranslationUiTester;
public sealed partial class MainForm
{
    private string RenpySearch {get=>WpfWorkspace.Enabled?NativeRenpy.Search:_renpyDataSearch.Text;set{if(WpfWorkspace.Enabled){if(NativeRenpy.Search==value)return;NativeRenpy.Search=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyDataSearch.Text=value;}}
    private string RenpyInput {get=>WpfWorkspace.Enabled?NativeRenpy.Value:_renpyDataValue.Text;set{if(WpfWorkspace.Enabled){if(NativeRenpy.Value==value)return;NativeRenpy.Value=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyDataValue.Text=value;}}
    private string RenpyNote {get=>WpfWorkspace.Enabled?NativeRenpy.Note:_renpyDataNote.Text;set{if(WpfWorkspace.Enabled){if(NativeRenpy.Note==value)return;NativeRenpy.Note=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyDataNote.Text=value;}}
    private bool RenpyBoolean {get=>WpfWorkspace.Enabled?NativeRenpy.Boolean:_renpyDataBoolean.Checked;set{if(WpfWorkspace.Enabled){if(NativeRenpy.Boolean==value)return;NativeRenpy.Boolean=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyDataBoolean.Checked=value;}}
    private bool RenpyChangedOnly {get=>WpfWorkspace.Enabled?NativeRenpy.ChangedOnly:_renpyDataChangedOnly.Checked;set{if(WpfWorkspace.Enabled){if(NativeRenpy.ChangedOnly==value)return;NativeRenpy.ChangedOnly=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyDataChangedOnly.Checked=value;}}
    private int RenpyScope {get=>WpfWorkspace.Enabled?NativeRenpy.Scope:_renpyDataScope.SelectedIndex;set{if(WpfWorkspace.Enabled){if(NativeRenpy.Scope==value)return;NativeRenpy.Scope=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyDataScope.SelectedIndex=value;}}
    private string RenpyStatus {get=>WpfWorkspace.Enabled?NativeRenpy.Status:_renpyDataStatus.Text;set{if(WpfWorkspace.Enabled){if(NativeRenpy.Status==value)return;NativeRenpy.Status=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyDataStatus.Text=value;}}
    private string RenpyHint {get=>WpfWorkspace.Enabled?NativeRenpy.Hint:_renpyDataHint.Text;set{if(WpfWorkspace.Enabled){if(NativeRenpy.Hint==value)return;NativeRenpy.Hint=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyDataHint.Text=value;}}
    private string RenpyNamesStatus {get=>WpfWorkspace.Enabled?NativeRenpy.NamesStatus:_renpyDataNamesStatus.Text;set{if(WpfWorkspace.Enabled){if(NativeRenpy.NamesStatus==value)return;NativeRenpy.NamesStatus=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyDataNamesStatus.Text=value;}}
    private string RenpySortStatus {get=>WpfWorkspace.Enabled?NativeRenpy.SortStatus:_renpyDataSortStatus.Text;set{if(WpfWorkspace.Enabled){if(NativeRenpy.SortStatus==value)return;NativeRenpy.SortStatus=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyDataSortStatus.Text=value;}}
    private string RenpyFinderStatus {get=>WpfWorkspace.Enabled?NativeRenpy.FinderStatus:_renpyFinderStatus.Text;set{if(WpfWorkspace.Enabled){if(NativeRenpy.FinderStatus==value)return;NativeRenpy.FinderStatus=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyFinderStatus.Text=value;}}
    private string RenpyFinderInput {get=>WpfWorkspace.Enabled?NativeRenpy.FinderInput:_renpyFinderValue.Text;set{if(WpfWorkspace.Enabled){if(NativeRenpy.FinderInput==value)return;NativeRenpy.FinderInput=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyFinderValue.Text=value;}}
    private int RenpyComparison {get=>WpfWorkspace.Enabled?NativeRenpy.Comparison:_renpyFinderComparison.SelectedIndex;set{if(WpfWorkspace.Enabled){if(NativeRenpy.Comparison==value)return;NativeRenpy.Comparison=value;if(!_renpyLoading)NativeRenpyChanged?.Invoke();}else _renpyFinderComparison.SelectedIndex=value;}}
    private bool RenpyFinderVisible {get=>WpfWorkspace.Enabled?NativeRenpy.FinderVisible:_renpyFinderPanel?.Visible==true;set{if(WpfWorkspace.Enabled)NativeRenpy.FinderVisible=value;else if(_renpyFinderPanel is not null)_renpyFinderPanel.Visible=value;}}
    private int RenpyVisibleCount=>WpfWorkspace.Enabled?NativeRenpy.Rows.Length:_renpyDataGrid.Rows.Count;
    private IEnumerable<JsonElement> VisibleRenpyRows()=>WpfWorkspace.Enabled?NativeRenpy.Rows.Select(row=>row.Source):_renpyDataGrid.Rows.Cast<DataGridViewRow>().Select(row=>(JsonElement)row.Tag!);
    private void RenderNativeRenpyState()
    {
        if(IsDisposed||Disposing)return;
        string? path=_selectedGame?.ExePath;
        if(!SameGame(path,_renpyDataPath))
        {
            _renpyDataPath=path;_renpyDataRows.Clear();ClearNativeRenpyRows();_renpyDataSaveDirectory=null;_renpyUndoCount=0;RenpyHint="";RenpyStatus="";RenpyNamesStatus="";_renpySnapshotFailure=false;_renpyDataTruncated=false;_renpyCgIncomplete=false;_renpyDataState="ready";_renpyCgState="notRecognized";_renpyCgRows=0;
            _renpyDataPresentation=path is null?new():RenpyDataPresentation.Load(path);_renpyDataGeneration=null;_renpyChangedNames.Clear();RenpyChangedOnly=false;SelectRenpyData();
            _renpyDataConnection=null;_renpyDataSnapshotConnection=null;ClearRenpyNumericSession("已切换游戏，请重新开始数值查找。");
        }
        var selectedConnection=SelectedScriptTranslationSession()?.Connection;
        bool connected=selectedConnection?.Connected==true;
        bool enabled=connected&&SelectedScriptTranslationSession()?.Enabled==true;
        if(!ReferenceEquals(selectedConnection,_renpyDataConnection))
        {
            _renpyDataConnection=selectedConnection;_renpyDataSnapshotConnection=null;_renpyDataGeneration=null;_renpyChangedNames.Clear();_renpyDataRows.Clear();ClearNativeRenpyRows();_renpyDataSaveDirectory=null;_renpyUndoCount=0;
            _renpySnapshotFailure=false;_renpyDataTruncated=false;_renpyCgIncomplete=false;_renpyDataState="ready";_renpyCgState="notRecognized";_renpyCgRows=0;RenpyStatus="";
            ClearRenpyNumericSession("连接已变化，请进入游戏后重新开始数值查找。");
        }
        bool stopped=SameGame(_desktopGameState?.Game.ExePath,path)&&_desktopGameState?.Process==FusionGameProcessState.Stopped&&_selectedGame is {} selectedGame&&!_gameSessions.Get(selectedGame).LaunchPending;
        NativeRenpy.Launch.Visible=!connected;NativeRenpy.Launch.Enabled=stopped&&!_renpyDataBusy&&_selectedGame is {} game&&!_gameSessions.Get(game).Busy;
        NativeRenpy.Toggle.Visible=connected;NativeRenpy.Toggle.Enabled=connected&&!_renpyDataBusy;NativeRenpy.Toggle.Text=enabled?"关闭修改":"开启修改";
        NativeRenpy.Refresh.Enabled=enabled&&!_renpyDataBusy;NativeRenpy.Undo.Enabled=enabled&&!_renpyDataBusy&&_renpyUndoCount>0;
        NativeRenpy.Backup.Visible=SelectedScriptTranslationSession()?.Connection is {} current&&_renpyDataBackups.ContainsKey(current);
        if(!connected){_renpyDataSnapshotConnection=null;if(_renpyDataGeneration is not null||_renpyNumericFinder.Active){_renpyDataGeneration=null;_renpyChangedNames.Clear();ClearRenpyNumericSession("游戏已断开连接，请重新连接后开始查找。");}_renpyDataRows.Clear();ClearNativeRenpyRows();RenpyStatus=_selectedGame is {} pendingGame&&_gameSessions.Get(pendingGame).LaunchPending?"正在等待游戏启动…":stopped?"点击“修改并启动”，进入游戏或读档后刷新。无需开启翻译。":"当前游戏未连接。保存并退出游戏后，使用“修改并启动”连接。";}
        else if(!enabled){RenpyStatus="已连接 · 修改关闭";}
        else if(RenpyStatus.Length==0&&!_renpyDataBusy)RenpyStatus="已连接。进入游戏或读档后点击“刷新数据”。";

        NativeRenpy.ValueEnabled=enabled&&!_renpyDataBusy&&SelectedRenpyRow() is not null;
        NativeRenpy.Apply.Enabled=enabled&&ReferenceEquals(_renpyDataSnapshotConnection,selectedConnection)&&!_renpyDataBusy&&SelectedRenpyRow() is not null;
        RenderNativeRenpyFinder();NativeRenpyChanged?.Invoke();

    }
    private void RenderNativeRenpyFinder()
    {
        if(IsDisposed||Disposing)return;
        bool ready=SelectedScriptTranslationSession() is {Enabled:true,Connection.Connected:true}&&!_renpyDataBusy;
        NativeRenpy.FinderBegin.Enabled=ready;
        NativeRenpy.FinderNarrow.Enabled=ready&&_renpyNumericFinder.Active;
        NativeRenpy.FinderReset.Enabled=!_renpyDataBusy&&_renpyNumericFinder.Active;
        NativeRenpy.FinderEnabled=!_renpyDataBusy;

        NativeRenpy.FinderToggle.Text=RenpyFinderVisible?"返回全部变量":_renpyNumericFinder.Active?UiLanguage.FormatTemplate("数值查找（{0} 项）",_renpyNumericFinder.Count):"数值查找";
        if(_renpyNumericFinder.Active)RenpyFinderStatus=$"候选 {_renpyNumericFinder.Count} / {_renpyNumericFinder.InitialCount} 项 · 已筛选 {_renpyNumericFinder.Round} 次 · 普通刷新不改变比较基准"+
            (_renpyNumericFinder.Truncated?" · 仅在已读取的数据中查找":"");

    }
}
