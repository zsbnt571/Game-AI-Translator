using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private bool _renpyLoading;
    private JsonElement? _renpyEditingRow;
    private IGameDataConnection? _renpyEditingConnection;
    private int _renpySortColumn,_renpyValueOrder;
    private bool _renpySortDescending;
    private int _renpyNamesQueue;

    private void ConfigureRenpyEditing()
    {
        _renpyDataBoolean.CheckedChanged+=async(_,_)=>
        {
            if(!_renpyLoading&&!_renpyDataBusy)await WriteRenpyCapturedAsync(false,_renpyEditingRow,_renpyDataBoolean.Checked,"",_renpyEditingConnection);
        };
        _renpyDataValue.KeyDown+=async(_,e)=>
        {
            if(e.KeyCode==Keys.Escape){e.Handled=true;e.SuppressKeyPress=true;SelectRenpyData();}
            else if(e.KeyCode==Keys.Enter){e.Handled=true;e.SuppressKeyPress=true;await CommitRenpyNumberAsync();}
        };
        _renpyDataValue.Leave+=async(_,_)=>await CommitRenpyNumberAsync();
        _renpyDataGrid.KeyDown+=(_,e)=>{if(e.Control&&e.KeyCode==Keys.A){_renpyDataGrid.SelectAll();e.Handled=true;e.SuppressKeyPress=true;}};
        _renpyDataGrid.ColumnHeaderMouseClick+=(_,e)=>
        {
            if(e.ColumnIndex==2){_renpyValueOrder=_renpySortColumn==2?(_renpyValueOrder+1)%3:0;_renpySortDescending=_renpyValueOrder==1;}
            else _renpySortDescending=_renpySortColumn==e.ColumnIndex&&!_renpySortDescending;
            _renpySortColumn=e.ColumnIndex;
            UpdateRenpySortPresentation();
            FilterRenpyData();
        };
        UpdateRenpySortPresentation();
        var menu=new ContextMenuStrip();
        menu.Items.Add("全选当前结果",null,(_,_)=>_renpyDataGrid.SelectAll());
        menu.Items.Add("取消选择",null,(_,_)=>_renpyDataGrid.ClearSelection());
        var enable=menu.Items.Add("开启所选项",null,async(_,_)=>await WriteRenpyBatchAsync(true));
        var disable=menu.Items.Add("关闭所选项",null,async(_,_)=>await WriteRenpyBatchAsync(false));
        menu.Items.Add("复制原文 / 变量路径",null,(_,_)=>
        {
            string text=string.Join(Environment.NewLine,SelectedRenpyRows().Select(r=>r.GetProperty("name").GetString()));
            if(text.Length>0)Clipboard.SetText(text);
        });
        menu.Opening+=(_,_)=>enable.Enabled=disable.Enabled=!_renpyDataBusy&&SelectedRenpyRows().Any(r=>r.GetProperty("kind").GetString()=="bool");
        _renpyDataGrid.CellMouseDown+=(_,e)=>
        {
            if(e.Button==MouseButtons.Right&&e.RowIndex>=0&&!_renpyDataGrid.Rows[e.RowIndex].Selected)
            {_renpyDataGrid.ClearSelection();_renpyDataGrid.CurrentCell=_renpyDataGrid.Rows[e.RowIndex].Cells[0];_renpyDataGrid.Rows[e.RowIndex].Selected=true;}
        };
        _renpyDataGrid.ContextMenuStrip=menu;
        _renpyDataGrid.Disposed+=(_,_)=>menu.Dispose();
    }
    private void UpdateRenpySortPresentation()
    {
        var grid=(WorkspaceDataGrid)_renpyDataGrid;grid.ActiveSortColumn=_renpySortColumn;grid.SortDescending=_renpySortDescending;
        string valueOrder=new[]{"开启优先","关闭优先","暂无记录优先"}[_renpyValueOrder];
        _renpyDataSortStatus.Text=_renpySortColumn==2?"当前值 · "+valueOrder:(_renpySortColumn==0?"中文名":_renpySortColumn==1?"原文 / 路径":"生效范围")+(_renpySortDescending?" · 降序":" · 自然顺序");
        _renpyDataSortStatus.AccessibleName="当前排序："+_renpyDataSortStatus.Text;
        foreach(DataGridViewColumn c in grid.Columns){c.HeaderCell.SortGlyphDirection=SortOrder.None;c.HeaderCell.ToolTipText=c.Index==2?"点击依次切换：开启优先、关闭优先、暂无记录优先；数值按升序、降序、升序排列。":"点击切换升序或降序；数字按自然顺序排列。";}
        grid.Invalidate();
    }
    private Control BuildRenpyBatchActions()=>WorkspaceRow(
        IconButton("全选当前结果","check",(_,_)=>_renpyDataGrid.SelectAll(),false,125),
        IconButton("开启所选","check",async(_,_)=>await WriteRenpyBatchAsync(true),false,108),
        IconButton("关闭所选","close",async(_,_)=>await WriteRenpyBatchAsync(false),false,108),_renpyDataSortStatus);
    private Task CommitRenpyNumberAsync()
    {
        if(_renpyLoading||_renpyDataBusy||_renpyEditingRow is not {} row||row.GetProperty("kind").GetString()=="bool")return Task.CompletedTask;
        return WriteRenpyCapturedAsync(false,row,false,_renpyDataValue.Text,_renpyEditingConnection);
    }
    private JsonElement[] SelectedRenpyRows()=>_renpyDataGrid.SelectedRows.Cast<DataGridViewRow>().Where(r=>r.Tag is JsonElement).OrderBy(r=>r.Index).Select(r=>(JsonElement)r.Tag!).ToArray();

    internal static int NaturalRenpyCompare(string left,string right)
    {
        var a=Regex.Split(left,@"(\d+)");var b=Regex.Split(right,@"(\d+)");
        for(int i=0;i<Math.Min(a.Length,b.Length);i++)
        {
            int result;
            if(i%2==1){string x=a[i].TrimStart('0'),y=b[i].TrimStart('0');result=x.Length.CompareTo(y.Length);if(result==0)result=string.CompareOrdinal(x,y);}
            else result=StringComparer.CurrentCultureIgnoreCase.Compare(a[i],b[i]);
            if(result!=0)return result;
        }
        return a.Length.CompareTo(b.Length);
    }
    private int CompareRenpyRows(JsonElement a,JsonElement b)
    {
        string x=a.GetProperty("name").GetString()!,y=b.GetProperty("name").GetString()!;int result;
        if(_renpySortColumn==2)
        {
            var av=a.GetProperty("value");var bv=b.GetProperty("value");
            int Rank(JsonElement v)=>v.ValueKind switch{JsonValueKind.True=>0,JsonValueKind.False=>1,JsonValueKind.Null=>2,_=>3};
            if(av.ValueKind==JsonValueKind.Number&&bv.ValueKind==JsonValueKind.Number)
                result=av.GetDouble().CompareTo(bv.GetDouble())*(_renpySortDescending?-1:1);
            else {int ar=Rank(av),br=Rank(bv);if(ar<3)ar=(ar-_renpyValueOrder+3)%3;if(br<3)br=(br-_renpyValueOrder+3)%3;result=ar.CompareTo(br);}
        }
        else {result=NaturalRenpyCompare(_renpySortColumn==0?RenpyDataLabel(x):_renpySortColumn==3?a.GetProperty("hint").ToString():x,_renpySortColumn==0?RenpyDataLabel(y):_renpySortColumn==3?b.GetProperty("hint").ToString():y);if(_renpySortDescending)result=-result;}
        return result!=0?result:NaturalRenpyCompare(x,y);
    }
    private async void QueueRenpyDataNames()
    {
        int round=++_renpyNamesQueue;
        try{await Task.Delay(350,_desktopLifetime.Token);if(!IsDisposed&&round==_renpyNamesQueue)RequestRenpyNamesCore(true);}
        catch(OperationCanceledException){}
    }
    private async Task WriteRenpyBatchAsync(bool value)
    {
        if(_renpyDataBusy||_selectedGame is not {} game||SelectedScriptTranslationSession()?.Connection is not {Connected:true} connection||!ReferenceEquals(connection,_renpyDataSnapshotConnection))return;
        var rows=SelectedRenpyRows().Where(r=>r.GetProperty("kind").GetString()=="bool"&&r.GetProperty("value").ValueKind!=(value?JsonValueKind.True:JsonValueKind.False)).ToArray();
        if(rows.Length==0){_renpyDataHint.Text="请选择需要更改的开关项。数值项不会被批量开启或关闭。";return;}
        var changes=new JsonArray();foreach(var row in rows)changes.Add(new JsonObject{["entry"]=row.GetProperty("id").GetString(),["value"]=value});
        _renpyDataBusy=true;RenderRenpyModificationState();
        try
        {
            if(!_renpyDataBackups.ContainsKey(connection))
            {
                if(string.IsNullOrWhiteSpace(_renpyDataSaveDirectory))throw new IOException("请先刷新数据。");
                string savedir=_renpyDataSaveDirectory,backup=Path.Combine(RenpyGameAdapter.Storage(game.ExePath),"save-backups",DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
                await Task.Run(()=>BackupRenpySaveLocations(game.ExePath,savedir,backup));_renpyDataBackups[connection]=backup;
            }
            if(!IsCurrentRenpyConnection(game.ExePath,connection))return;
            await connection.RequestAsync(new(){["op"]="dataSetMany",["changes"]=changes});
            if(!IsCurrentRenpyConnection(game.ExePath,connection))return;
            await LoadRenpySnapshotAsync(game.ExePath,connection);
            _renpyDataHint.Text=$"已{(value?"开启":"关闭")} {rows.Length} 项，可一次撤销本批修改。画廊请重新打开查看。";
        }
        catch(Exception ex){if(IsCurrentRenpyConnection(game.ExePath,connection))_renpyDataHint.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{_renpyDataBusy=false;RenderRenpyModificationState();}
    }
}
