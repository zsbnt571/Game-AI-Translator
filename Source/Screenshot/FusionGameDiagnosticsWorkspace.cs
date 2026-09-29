using System.IO.Compression;
using System.Text;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private GameCrashMonitor? _gameCrashMonitor;
    private readonly Label _gameDiagnosticState=WorkspaceLabel("");
    private Button? _gameDiagnosticOpen;
    private GameCrashMonitor CrashMonitor=>_gameCrashMonitor??=new((game,start,end)=>GameDiagnosticLogs.Collect(game,start,end));

    private Control BuildGameDiagnosticRow()
    {
        _gameDiagnosticOpen=IconButton("查看诊断报告","",(_,_)=>ShowGameDiagnosticReport(),false,120);
        var row=new WorkspaceAlignedRow(_gameDiagnosticState,_gameDiagnosticOpen){Name="GameExitDiagnostics",Margin=new(0,6,0,8)};
        row.FlexibleColumn(0,4096);_gameDiagnosticState.MaximumSize=new(720,0);
        row.SizeChanged+=(_,_)=>_gameDiagnosticState.MaximumSize=new(Math.Max(160,row.ClientSize.Width-145*DeviceDpi/96),0);
        FormClosed+=(_,_)=>_gameCrashMonitor?.Dispose();
        return row;
    }

    private void TrackGameDiagnostics(GameInfo game,int pid)
    {
        // UI test launch providers return synthetic PIDs. Monitoring those must
        // not attach to an unrelated real process during an offline test.
        if(_gameLaunchOperations is not DesktopGameLaunchOperations)return;
        try{CrashMonitor.Track(game,pid,_gameSessions.Get(game).LaunchMode);UpdateGameDiagnosticSnapshots();}
        catch(Exception ex){AppLog.Write("game-diagnostics",SafeDiagnosticOutput.ExceptionSummary(ex));}
    }

    private void UpdateGameDiagnosticSnapshots()
    {
        if(_gameCrashMonitor is null||IsDisposed||Disposing)return;
        foreach(var session in _gameSessions.Snapshot())
        {
            var path=session.Game.ExePath;
            if(!_gameCrashMonitor.IsTracking(path))continue;
            try
            {
                var data=_scriptTranslationSessions.GetValueOrDefault(path)??_dataSessions.GetValueOrDefault(path)??_embeddedTranslationSessions.GetValueOrDefault(path);
                bool connected=data?.Connection.Connected==true,enabled=data?.Translation?.Enabled==true;
                // Controlled facts only: no API configuration, arbitrary error
                // bodies, game dialogue, cache contents or request arguments.
                string status=$"接入={(connected?"已连接":"未连接或无此通道")}；翻译={(enabled?"开启":"关闭")}；失败待重试={data?.Translation?.FailedCount??0}；操作进行中={(session.Busy?"是":"否")}；存在接入限制={(session.TranslationBlockedReason.Length>0?"是":"否")}";
                _gameCrashMonitor.Update(path,new(session.TranslationRequested||enabled||session.LaunchMode==GameLaunchMode.Translation,connected,status));
            }
            catch(Exception ex){AppLog.Write("game-diagnostics",SafeDiagnosticOutput.ExceptionSummary(ex));}
        }
        RenderGameDiagnosticState();
    }

    private void RenderGameDiagnosticState()
    {
        if(_gameDiagnosticOpen is null||_selectedGame is not {} game)return;
        try
        {
            var monitor=CrashMonitor;var report=monitor.Latest(game.ExePath);
            bool tracking=monitor.IsTracking(game.ExePath);
            _gameDiagnosticState.Text=(tracking?"退出监测中。 ":"")+(report is null
                ?tracking?"退出后自动保存诊断报告。":"从本软件启动游戏后，自动监测退出并保存诊断报告。"
                :"最近报告："+report.Summary);
            _gameDiagnosticOpen.Enabled=report is not null;
        }
        catch{_gameDiagnosticState.Text="诊断记录暂不可读取。";_gameDiagnosticOpen.Enabled=false;}
    }

    private void OpenGameDiagnosticsFolder(bool all=false)
    {
        try
        {
            var path=all?GameCrashMonitor.ReportsRoot:_selectedGame is {} game?CrashMonitor.GetReportsDirectory(game.ExePath):GameCrashMonitor.ReportsRoot;
            Directory.CreateDirectory(path);OpenFolder(path);
        }
        catch(Exception ex){ShowFusionError(ex);}
    }

    private Control BuildGameDiagnosticHelp()
    {
        var open=new Button{Text="打开游戏诊断目录",AutoSize=true};open.Click+=(_,_)=>OpenGameDiagnosticsFolder(true);
        return Stack(Info("从本软件启动游戏后自动监测退出，最小化后继续。\n报告保存在本机，保留最近 50 次记录；可在游戏详情查看并导出。"),open);
    }

    private async void ShowGameDiagnosticReport()
    {
        if(_selectedGame is not {} game)return;
        try
        {
            var report=CrashMonitor.Latest(game.ExePath);
            if(report is null){_statusLabel.Text="还没有此游戏的退出报告。请从本软件启动游戏，退出后会自动保存。";return;}
            string reportRoot=CrashMonitor.GetReportsDirectory(game.ExePath);
            static byte[] ReadReport(string path,string root)
            {
                string full=Path.GetFullPath(path),prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("报告路径不属于此游戏。");
                for(string? current=full;current is not null;current=Path.GetDirectoryName(current))
                    if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("报告路径包含链接，不能读取。");
                var file=new FileInfo(full);if(!file.Exists||file.Length>2*1024*1024||(file.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("诊断报告缺失或超过读取限制。");
                return File.ReadAllBytes(full);
            }
            (byte[] Text,byte[] Json) ReadContents()
            {
                var latest=CrashMonitor.Latest(game.ExePath);
                if(latest?.SessionId==report.SessionId)report=latest;
                return report.ReportSaveFailed
                    ?(Encoding.UTF8.GetBytes(GameCrashMonitor.Markdown(report)),System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}))
                    :(ReadReport(report.MarkdownPath,reportRoot),ReadReport(report.JsonPath,reportRoot));
            }
            var contents=await Task.Run(ReadContents);
            if(IsDisposed)return;
            using var viewer=new Form{Text=game.Name+" · 游戏退出诊断",Size=new(820,620),MinimumSize=new(520,360),StartPosition=FormStartPosition.CenterParent,Padding=new(14)};
            var body=new RichTextBox{Dock=DockStyle.Fill,ReadOnly=true,DetectUrls=false,WordWrap=true,BorderStyle=BorderStyle.None,Text=Encoding.UTF8.GetString(contents.Text)};
            var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=48,Padding=new(0,8,0,0)};
            var export=new Button{Text="导出报告包…",AutoSize=true,Height=32};
            var refresh=new Button{Text="刷新报告",AutoSize=true,Height=32};
            refresh.Click+=async(_,_)=>
            {
                refresh.Enabled=false;
                try{contents=await Task.Run(ReadContents);if(!viewer.IsDisposed)body.Text=Encoding.UTF8.GetString(contents.Text);}
                catch(Exception ex){if(!viewer.IsDisposed)AppDialog.Show(viewer,"读取失败",SafeDiagnosticOutput.ExceptionSummary(ex),AppDialogKind.Error);}
                finally{if(!refresh.IsDisposed)refresh.Enabled=true;}
            };
            export.Click+=(_,_)=>
            {
                using var save=new SaveFileDialog{Filter="诊断报告包 (*.zip)|*.zip",FileName="Fusion-游戏退出诊断-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip",OverwritePrompt=true};
                if(save.ShowDialog(viewer)!=DialogResult.OK)return;
                try
                {
                    contents=ReadContents();
                    body.Text=Encoding.UTF8.GetString(contents.Text);
                    // Export exactly the two inspected report files. The game
                    // directory and user settings never enter the archive.
                    using(var stream=new FileStream(save.FileName,FileMode.Create,FileAccess.Write,FileShare.None))
                    using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
                    {
                        using(var entry=zip.CreateEntry("诊断报告.md").Open())entry.Write(contents.Text);
                        using(var entry=zip.CreateEntry("diagnostic.json").Open())entry.Write(contents.Json);
                    }
                    export.Text="报告包已导出";
                }
                catch(Exception ex){AppDialog.Show(viewer,"导出失败",SafeDiagnosticOutput.ExceptionSummary(ex),AppDialogKind.Error);}
            };
            var close=new Button{Text="关闭",AutoSize=true,Height=32};close.Click+=(_,_)=>viewer.Close();
            footer.Controls.Add(refresh);footer.Controls.Add(export);footer.Controls.Add(close);viewer.Controls.Add(body);viewer.Controls.Add(footer);
            FontManager.ApplyUi(viewer,UiSettings);UiTheme.Apply(viewer,UiSettings);viewer.ShowDialog(this);
        }
        catch(Exception ex){if(!IsDisposed)ShowFusionError(ex);}
    }
}
