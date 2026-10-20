namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private TableLayoutPanel? _novelStatePanel;
    private static bool NovelSnapshots(GameInfo? game)=>game?.AdapterId is "web-tyrano" or "web-vnmaker";
    private readonly ComboBox _novelSnapshots=WpfWorkspace.Enabled ? null! : new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=330,AccessibleName="状态快照"};
    private readonly Label _novelSnapshotFeedback=WpfWorkspace.Enabled ? null! : WorkspaceLabel("刷新列表后可查看此游戏的快照。"),_novelSnapshotNote=WpfWorkspace.Enabled ? null! : WorkspaceLabel("保存当前状态的独立副本；载入前会自动备份当前状态。此功能仍需实机验证，重要进度请同时使用游戏自己的存档。");
    private Button? _novelSnapshotRefresh,_novelSnapshotCapture,_novelSnapshotLoad;
    private void BuildNovelStateWorkspace(TableLayoutPanel parent)
    {
        var panel=_novelStatePanel=WorkspaceColumn();WorkspaceAdd(parent,panel);
        WorkspaceAdd(panel,WorkspaceLabel("状态快照（测试）"));
        _novelSnapshotRefresh=IconButton("刷新快照","refresh",async(_,_)=>await NovelSnapshotAsync("list"),false,116);
        _novelSnapshotCapture=IconButton("保存快照","copy",async(_,_)=>await NovelSnapshotAsync("capture"),false,116);
        _novelSnapshotLoad=IconButton("载入快照","back",async(_,_)=>await NovelSnapshotAsync("load"),false,116);
        WorkspaceAdd(panel,WorkspaceRow(_novelSnapshots,_novelSnapshotRefresh,_novelSnapshotCapture,_novelSnapshotLoad));
        WorkspaceAdd(panel,_novelSnapshotFeedback);WorkspaceAdd(panel,_novelSnapshotNote);
        _novelSnapshots.SelectedIndexChanged+=(_,_)=>RenderNovelModificationState();
    }
    private void RenderNovelState(bool connected,bool changed)
    {
        if(_novelSnapshotRefresh is null)return;
        _novelStatePanel!.Visible=NovelSnapshots(_selectedGame);
        if(changed){_novelSnapshots.Items.Clear();NovelSnapshotFeedback="刷新列表后可查看此游戏的快照。";}
        bool ready=NovelSnapshots(_selectedGame)&&!_novelBusy&&!_gameActionRunning;
        _novelSnapshotRefresh.Enabled=ready;_novelSnapshots.Enabled=ready;
        _novelSnapshotCapture!.Enabled=ready&&connected;
        _novelSnapshotLoad!.Enabled=ready&&connected&&_novelSnapshots.SelectedItem is NovelStateRecord;
    }
    private async Task NovelSnapshotAsync(string operation)
    {
        if(_novelBusy||_selectedGame is not {} game||!NovelSnapshots(game))return;
        var session=NovelSession();var selected=NovelSelectedSnapshot;
        if(operation=="load"&&(selected is null||AppDialog.Show(this,"载入状态快照","载入所选快照会改变当前游戏进度。继续前将自动保存一份当前状态，变量锁定和撤销记录会清除。是否继续？",AppDialogKind.Confirmation)!=DialogResult.Yes))return;
        bool Current()=>!IsDisposed&&!Disposing&&SameGame(_selectedGame?.ExePath,game.ExePath)&&ReferenceEquals(session,NovelSession());
        _novelBusy=true;RenderNovelModificationState();NovelSnapshotFeedback=operation=="load"?"正在校验快照并备份当前状态…":operation=="capture"?"正在读取并保存当前状态…":"正在读取快照列表…";
        NovelStateRecord? backup=null;
        try
        {
            if(operation!="list")
            {
                if(session?.Connection.Connected!=true)throw new IOException("游戏未连接，请从本软件启动游戏。");
                var status=await session.Connection.RequestAsync(new(){["op"]="stateStatus"});
                if(!status.GetProperty("available").GetBoolean())throw new IOException("此游戏版本未提供匹配的状态快照接口。");
                if(status.GetProperty("busy").GetBoolean())throw new IOException("上次状态操作尚未结束。请等待游戏处理；不要重复载入。");
                if(operation=="load")
                {
                    await NovelStateSnapshots.RestoreAsync(game.ExePath,selected!,session.Connection,Current,saved=>{
                        backup=saved;if(Current()){_novelRows=[];ClearNovelGrid();NovelDataFeedback="状态载入后，请重新刷新变量。";}
                    });
                }
                else
                {
                    var identity=await Task.Run(()=>NovelStateSnapshots.Identify(game.ExePath));
                    if(!Current())throw new IOException("当前游戏选择已变化，操作取消。");
                    backup=await NovelStateSnapshots.CaptureAndSaveAsync(game.ExePath,session.Connection,identity,"manual");
                }
            }
            var records=await Task.Run(()=>NovelStateSnapshots.List(game.ExePath));
            if(!Current())return;
            SetNovelSnapshots(records);
            NovelSnapshotFeedback=operation=="load"?"游戏已接收载入请求，实际恢复请以游戏画面为准。载入前备份已保存。":operation=="capture"?"当前状态快照已保存。":$"找到 {records.Count} 份可读取的快照；载入时会核对游戏和版本。";
        }
        catch(Exception ex)
        {
            if(Current())NovelSnapshotFeedback=(ex is OperationCanceledException?"等待游戏响应超时，结果未确认。请先检查游戏画面，不要立即重复载入。":SafeDiagnosticOutput.ExceptionSummary(ex))+(operation=="load"&&backup is not null?" 载入前备份已保存，可刷新列表查看。":"");
        }
        finally{_novelBusy=false;if(!IsDisposed)RenderNovelModificationState();}
    }
}
