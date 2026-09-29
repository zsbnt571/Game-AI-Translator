namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly RenpyNumericFinder _renpyNumericFinder=new();
    private readonly TextBox _renpyFinderValue=new(){Width=140,PlaceholderText="当前数值，可留空",AccessibleName="查找的当前数值"};
    private readonly ComboBox _renpyFinderComparison=new GamePlanBox(){Width=156,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="数值变化条件"};
    private readonly Label _renpyFinderStatus=WorkspaceLabel("先开始查找，再回到游戏让数值变化，然后继续筛选。留空初值可记录全部数值。");
    private Button? _renpyFinderBegin,_renpyFinderNarrow,_renpyFinderReset;
    private Button? _renpyFinderToggle;
    private TableLayoutPanel? _renpyFinderPanel;
    private bool _renpyDataMainMenu;
    private IGameDataConnection? _renpyDataConnection;
    private IGameDataConnection? _renpyDataSnapshotConnection;

    private void BuildRenpyNumericFinder(TableLayoutPanel panel)
    {
        _renpyFinderComparison.Items.AddRange(["等于填写的数值","比上次增大","比上次减小","与上次相同","与上次不同"]);_renpyFinderComparison.SelectedIndex=0;
        _renpyFinderBegin=IconButton("开始查找","search",async(_,_)=>await RunRenpyNumericFinderAsync(true),false,108);
        _renpyFinderNarrow=IconButton("继续筛选","search",async(_,_)=>await RunRenpyNumericFinderAsync(false),false,108);
        _renpyFinderReset=IconButton("清除查找","back",(_,_)=>{_renpyNumericFinder.Reset();_renpyFinderStatus.Text="已清除查找，可重新填写数值或留空开始。";FilterRenpyData();RenderRenpyNumericFinder();},false,108);
        _renpyFinderPanel=WorkspaceColumn();_renpyFinderPanel.AutoSize=true;_renpyFinderPanel.Visible=false;
        _renpyFinderToggle=IconButton("数值查找","search",(_,_)=>{_renpyFinderPanel.Visible=!_renpyFinderPanel.Visible;FilterRenpyData();RenderRenpyNumericFinder();},false,140);
        _renpyFinderValue.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter){e.Handled=true;e.SuppressKeyPress=true;await RunRenpyNumericFinderAsync(!_renpyNumericFinder.Active);}};
        WorkspaceAdd(panel,WorkspaceRow(_renpyFinderToggle,_renpyDataStatus));
        var value=SearchField(_renpyFinderValue,"search");value.Width=156;
        WorkspaceAdd(_renpyFinderPanel,WorkspaceRow(value,_renpyFinderBegin,_renpyFinderComparison,_renpyFinderNarrow,_renpyFinderReset));
        WorkspaceAdd(_renpyFinderPanel,_renpyFinderStatus);WorkspaceAdd(panel,_renpyFinderPanel);
    }
    private bool IsCurrentRenpyConnection(string path,IGameDataConnection connection)=>!IsDisposed&&!Disposing&&SameGame(_selectedGame?.ExePath,path)&&
        connection.Connected&&ReferenceEquals(SelectedScriptTranslationSession()?.Connection,connection);

    private void ClearRenpyNumericSession(string reason)
    {
        bool active=_renpyNumericFinder.Active;_renpyNumericFinder.ClearSession();_renpyDataMainMenu=false;
        if(active||reason.Length>0)_renpyFinderStatus.Text=reason.Length>0?reason:"游戏连接已变化，请重新开始查找。";
    }
    private void RenderRenpyNumericFinder()
    {
        if(IsDisposed||Disposing||_renpyFinderBegin is null)return;
        bool ready=SelectedScriptTranslationSession()?.Connection.Connected==true&&!_renpyDataBusy;
        _renpyFinderBegin.Enabled=ready;
        _renpyFinderNarrow!.Enabled=ready&&_renpyNumericFinder.Active;
        _renpyFinderReset!.Enabled=!_renpyDataBusy&&_renpyNumericFinder.Active;
        _renpyFinderComparison.Enabled=!_renpyDataBusy;
        _renpyFinderValue.Enabled=!_renpyDataBusy;
        _renpyFinderToggle!.Text=_renpyFinderPanel!.Visible?"返回全部变量":_renpyNumericFinder.Active?$"数值查找（{_renpyNumericFinder.Count} 项）":"数值查找";
        if(_renpyNumericFinder.Active)_renpyFinderStatus.Text=$"候选 {_renpyNumericFinder.Count} / {_renpyNumericFinder.InitialCount} 项 · 已筛选 {_renpyNumericFinder.Round} 次 · 普通刷新不改变比较基准"+
            (_renpyNumericFinder.Truncated?" · 仅在已读取的数据中查找":"");
    }
    private async Task RunRenpyNumericFinderAsync(bool begin)
    {
        if(_renpyDataBusy||_selectedGame is not {} game||SelectedScriptTranslationSession()?.Connection is not {Connected:true} connection)return;
        string input=_renpyFinderValue.Text.Trim();RenpyNumericFinder.Number? value=null;
        var comparison=(RenpyNumericComparison)Math.Max(0,_renpyFinderComparison.SelectedIndex);
        if((begin&&input.Length>0)||(!begin&&comparison==RenpyNumericComparison.Equal))
        {
            if(!RenpyNumericFinder.TryQuery(input,out var parsed)){_renpyDataHint.Text="数值无效或精度超出范围；小数请使用小数点。初次查找可以留空。";return;}
            value=parsed;
        }
        if(!begin&&!_renpyNumericFinder.Active){_renpyDataHint.Text="请先开始数值查找。";return;}
        _renpyDataBusy=true;RenderRenpyModificationState();
        try
        {
            await LoadRenpySnapshotAsync(game.ExePath,connection);
            if(!IsCurrentRenpyConnection(game.ExePath,connection))return;
            // Observe may have cleared the search because a load happened while
            // the snapshot was pending. Never silently start over on Narrow.
            if(!begin&&!_renpyNumericFinder.Active)return;
            if(_renpyDataMainMenu){_renpyDataHint.Text="当前在主菜单，请进入游戏或读档后再次查找。";return;}
            _renpyFinderPanel!.Visible=true;
            if(begin)_renpyNumericFinder.Begin(value);else _renpyNumericFinder.Narrow(comparison,value);
            _renpyDataChangedOnly.Checked=false;
            if(begin){_renpyDataScope.SelectedIndex=3;_renpyDataSearch.Clear();}
            FilterRenpyData();
            _renpyDataHint.Text=_renpyNumericFinder.Count==0?"没有匹配项。点击“返回全部变量”查看其他数据，或清除查找重新开始。":
                "回到游戏改变数值，再选择变化条件并继续筛选。数值查找只读取数据，选中项目后才能应用修改。";
        }
        catch(Exception ex){if(IsCurrentRenpyConnection(game.ExePath,connection))_renpyDataHint.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{_renpyDataBusy=false;RenderRenpyModificationState();}
    }
}
