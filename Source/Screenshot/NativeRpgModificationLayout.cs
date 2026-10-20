using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
namespace ScreenshotTranslationUiTester;
public sealed partial class MainForm
{
    private sealed partial class NativeRpgController
    {

    private readonly List<NativeRpgButton> _dataTabs=[];
    private readonly NativeRpgLabel _dataSelectedName=WorkspaceLabel("选择一项数据"),_dataOriginalName=WorkspaceLabel(""),_dataCurrentLabel=WorkspaceLabel(""),_dataKeyLabel=WorkspaceLabel("");
    private readonly NativeRpgLabel _dataDescription=WorkspaceLabel("在左侧选择项目，查看详情并修改。"),_dataSectionHint=WorkspaceLabel("常用操作");
    private readonly NativeRpgText _dataTextValue=new(){Width=190,MaxLength=2000};
    private readonly NativeRpgChoice _dataMapChoice=new(){Width=260};
    private readonly NativeRpgChoice _dataEventKind=new(){Width=260};
    private NativeRpgButton? _dataLockButton;
    private bool _dataRefreshPending,_dataMapChoosing;
    private NativeRpgColumn? _dataScalarEditor,_dataObjectEditor;
    private NativeRpgElement? _dataListSearch,_dataListSurface,_dataDetailSurface;
    private NativeRpgCommonBoard? _dataCommonBoard;
    private NativeRpgButton? _dataApplyButton;
    private bool _dataScalarUpdating;
    private readonly NativeRpgIcon _dataSelectedIcon=new(){Margin=new(0,0,12,0),Visible=false};
    private NativeRpgElement? _dataSteps,_dataMapToolbar,_dataContextActions,_dataLockActions,_dataBattleActions;
    private NativeRpgElement? _dataMapLabel,_dataReturnMap;
    private NativeRpgScroll? _dataDetailScroll;
    private NativeRpgNumber? _dataNumberField;
    private readonly Dictionary<string,DataViewState> _dataViewStates=new();
    private int _dataTabPrevious,_dataRevision,_dataLimit=160,_dataTotal,_dataSelectedMapId,_dataDetailRevision;
    private Task _dataDetailLoad=Task.CompletedTask;
    private readonly Dictionary<string,int> _dataDetailSelections=new();
    private readonly Dictionary<NativeRpgElement,Action> _dataInterfaceRefresh=[];
    private void BindModificationLanguage(NativeRpgElement element,Action refresh)
    {
        if(!_dataInterfaceRefresh.ContainsKey(element))element.Disposed+=(_,_)=>_dataInterfaceRefresh.Remove(element);
        _dataInterfaceRefresh[element]=refresh;
    }
    private sealed record DataViewState(string Query,string? SelectedKey,int FirstRow,int Limit,int Map,bool FollowMap);
    private sealed record MapChoice(int Id,string Name,string Chinese,bool Current)
    {public override string ToString()=>$"{Id:0000}  {(Chinese.Length>0?Chinese+" · ":"")}{Name}{(Current?" · "+UiLanguage.Text("当前"):"")}";}
    private string ActiveDataScope=>_dataScope.SelectedIndex==8?(_dataEventKind.SelectedIndex switch{1=>"commonEvents",2=>"interpreters",_=>"mapEvents"}):DataScopes[Math.Max(0,_dataScope.SelectedIndex)];
    private string ViewStateKey(int tab)=>_dataViewPath+"|"+tab;
    private async Task AdjustGameDataValueAsync(int delta)
    {
        if(_dataLoading||SelectedDataSession() is not {Enabled:true,Ready:true}||_dataSelected is not {} row)return;
        decimal current=row.GetProperty("value").GetDecimal();
        _dataValue.Value=Math.Clamp(current+delta,_dataValue.Minimum,_dataValue.Maximum);await ChangeGameDataAsync("set");
    }
    private void UpdateModificationPresentation()
    {
        bool common=_dataScope.SelectedIndex==0;
        if(_dataCommonBoard is not null){_dataCommonBoard.Visible=common;}
        if(_dataListSurface is not null)_dataListSurface.Visible=!common;
        if(_dataDetailSurface is not null)_dataDetailSurface.Visible=!common;
        if(_dataContextActions is not null)_dataContextActions.Visible=common;
        if(_dataBattleActions is not null)_dataBattleActions.Visible=common;
        if(_dataLockActions is not null)_dataLockActions.Visible=_dataScope.SelectedIndex==9;
        bool browse=_dataScope.SelectedIndex==7||(_dataScope.SelectedIndex==8&&_dataEventKind.SelectedIndex==0);
        if(_dataMapLabel is not null)_dataMapLabel.Visible=browse;
        _dataMapChoice.Visible=browse;
        if(_dataReturnMap is not null)_dataReturnMap.Visible=browse;
        
    }
    }
}
