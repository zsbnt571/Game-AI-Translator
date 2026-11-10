using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ScreenshotTranslationUiTester;

// One owner per application, independent of the gallery lifetime. All operations
// are serialized on the UI dispatcher and preserve the original recording target.
internal sealed class GameMediaController : IDisposable
{
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    private readonly Func<Task<RecentGame[]>> games;
    private readonly Action<string> notify;
    private readonly Func<GameMediaPreferences,string?> conflicts;
    private readonly Dispatcher dispatcher;
    private readonly SemaphoreSlim operation=new(1,1);
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(500)};
    private GlobalHotKeyHost? screenshotKey,recordKey;
    private GameMediaRecording? recording;
    private bool disposed,exiting;
    internal bool Busy{get;private set;}
    internal bool Recording=>recording is not null;
    internal long GalleryRevision{get;private set;}
    internal string? RecordingGameId{get;private set;}
    private string status="启动并保持游戏窗口可见，即可截图或录像。";
    private Func<string>? formattedStatus;
    internal string Status{get=>formattedStatus?.Invoke()??UiLanguage.Text(status);private set{formattedStatus=null;status=UiLanguage.Original(value);}}
    internal GameMediaPreferences Preferences{get;private set;}=GameMediaPreferences.Default;
    internal event Action? Changed;
    internal string ShortcutStatus=>UiLanguage.Format($"截图：{Display(Preferences.Screenshot,screenshotKey)}    开始／停止录像：{Display(Preferences.Recording,recordKey)}");
    private static string Display(InputBinding b,GlobalHotKeyHost? host)=>b.IsEmpty?UiLanguage.Text("已关闭"):b.DisplayName+(!AppDataPaths.DisableGlobalInput&&host?.Registered!=true?UiLanguage.Text("（被占用）"):"");
    internal GameMediaController(Func<Task<RecentGame[]>> games,Action<string> notify,Func<GameMediaPreferences,string?>? conflicts=null)
    {
        this.games=games;this.notify=notify;this.conflicts=conflicts??(_=>null);dispatcher=Dispatcher.CurrentDispatcher;
        try{Preferences=GameMediaPreferences.Load();}catch(Exception ex){SetStatus("快捷键配置无法读取，原文件保留；请在设置 → 按键中重新保存。");AppLog.Write("game-media",SafeDiagnosticOutput.ExceptionSummary(ex));}
        if(!AppDataPaths.DisableGlobalInput)
        {
            screenshotKey=new(0x5410,()=>Trigger(false));recordKey=new(0x5411,()=>Trigger(true));
            if(!Apply(Preferences))SetStatus("部分截图录像快捷键被占用，请在设置 → 按键中修改。");
        }
        timer.Tick+=async(_,_)=>
        {
            if(Busy||recording is not {} current)return;
            if(current.Completion.IsCompleted)await Run(StopCoreAsync);
            else {var elapsed=(DateTimeOffset.UtcNow-current.StartedAt).ToString(@"hh\:mm\:ss");formattedStatus=()=>UiLanguage.Format($"● 正在录制 · {elapsed} · 无声音");Changed?.Invoke();}
        };
        timer.Start();
    }
    private void Trigger(bool video)
    {
        // Capture the foreground handle when the key fires, before any UI or
        // asynchronous work can change focus.
        var foreground=GetForegroundWindow();
        dispatcher.BeginInvoke(new Action(async()=>await Run(async()=>
        {
            if(video&&recording is not null){await StopCoreAsync();return;}
            var game=await ResolveForegroundAsync(foreground);
            if(video)await StartCoreAsync(game);else await CaptureCoreAsync(game);
        })));
    }
    internal async Task<RecentGame> ResolveForegroundAsync(IntPtr foreground)
    {
        var all=await games();
        var match=await Task.Run(()=>
        {
            string? path=null;
            try{GetWindowThreadProcessId(foreground,out var pid);using var process=System.Diagnostics.Process.GetProcessById((int)pid);path=process.MainModule?.FileName;}catch{}
            var direct=all.Where(g=>string.Equals(g.ExePath,path,StringComparison.OrdinalIgnoreCase)).ToArray();
            var candidates=direct.Length>0?direct:all;
            return candidates.Select(g=>(Game:g,Target:GameLibraryWindowIdentity.Find(g.ExePath).Target))
                .Where(p=>p.Target?.Window==foreground).Select(p=>p.Game).Take(2).ToArray();
        });
        if(match.Length!=1)throw new IOException("请先把游戏库中的游戏窗口切到前台，再按截图／录像快捷键。");
        return match[0];
    }
    private bool Apply(GameMediaPreferences value)
    {
        screenshotKey?.Unregister();recordKey?.Unregister();
        if(AppDataPaths.DisableGlobalInput)return true;
        bool a=value.Screenshot.IsEmpty||screenshotKey?.Register(value.Screenshot)==true;
        bool b=value.Recording.IsEmpty||recordKey?.Register(value.Recording)==true;
        return a&&b;
    }
    internal string? SavePreferences(GameMediaPreferences value)
    {
        value.Validate();if(conflicts(value) is {} error)return error;
        if(!Apply(value)){Apply(Preferences);Changed?.Invoke();return "快捷键已被其他功能或软件占用，请换一个组合。";}
        try{value.Save();Preferences=value;Changed?.Invoke();return null;}
        catch{Apply(Preferences);Changed?.Invoke();throw;}
    }
    private void SetStatus(string value){Status=value;Changed?.Invoke();notify(Status);}
    private void SetFormattedStatus(Func<string> value){formattedStatus=value;Changed?.Invoke();notify(Status);}
    private async Task Run(Func<Task> action)
    {
        if(disposed||exiting||Busy)return;
        Busy=true;Changed?.Invoke();await operation.WaitAsync();
        try{await action();}
        catch(Exception ex){SetStatus(UiOperationText.Error(ex));AppLog.Write("game-media",SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally{Busy=false;operation.Release();Changed?.Invoke();}
    }
    internal Task CaptureAsync(RecentGame game)=>Run(()=>CaptureCoreAsync(game));
    internal Task StartAsync(RecentGame game)=>Run(async()=>{if(recording is null)await StartCoreAsync(game);});
    internal Task StopAsync()=>Run(StopCoreAsync);
    private static async Task<(Bitmap Image,CoverTarget Target)> FrameAsync(RecentGame game)
    {
        var found=await Task.Run(()=>GameLibraryWindowIdentity.Find(RecentGameStore.Normalize(game.ExePath)));
        if(found.Target is null)throw new IOException(found.Message);
        var frame=await GameWindowCover.CaptureOcrFrameAsync(found.Target,CancellationToken.None);
        if(frame.Image is null)throw new IOException(frame.Message);
        return(frame.Image,found.Target);
    }
    private async Task CaptureCoreAsync(RecentGame game)
    {
        var frame=await FrameAsync(game);var store=new GameMediaStore(game.Id,gameName:game.Name);
        using(frame.Image){var entry=await Task.Run(()=>store.SaveScreenshot(frame.Image));GalleryRevision++;SetFormattedStatus(()=>UiLanguage.Format($"截图已保存 · {game.Name} · {entry.Width} × {entry.Height}"));}
    }
    private async Task StartCoreAsync(RecentGame game)
    {
        SetStatus("正在准备录像…");var frame=await FrameAsync(game);var store=new GameMediaStore(game.Id,gameName:game.Name);string thumbnail;
        using(frame.Image)using(var small=GameWindowCover.Thumbnail(frame.Image,480,270))
        {
            thumbnail=store.NewName("preview",".png");using var file=new FileStream(store.FilePath(thumbnail),FileMode.CreateNew,FileAccess.Write,FileShare.None);small.Save(file,System.Drawing.Imaging.ImageFormat.Png);
        }
        recording=await GameMediaRecording.StartAsync(frame.Target,store,thumbnail,CancellationToken.None);RecordingGameId=game.Id;SetFormattedStatus(()=>UiLanguage.Format($"● 正在录制 · {game.Name} · 无声音"));
    }
    private async Task StopCoreAsync()
    {
        var current=recording;if(current is null)return;
        try{SetStatus("正在保存录像…");var entry=await current.StopAsync();GalleryRevision++;SetFormattedStatus(()=>UiLanguage.Format($"录像已保存 · {entry.DurationSeconds:0.0} 秒")+(entry.FinishReason=="manual"?"":" · "+UiLanguage.Text(Reason(entry.FinishReason))));}
        finally{current.Dispose();recording=null;RecordingGameId=null;Changed?.Invoke();}
    }
    private static string Reason(string reason)=>reason switch {"window-unavailable"=>"游戏窗口已最小化或退出","window-resized"=>"游戏窗口尺寸已改变","disk-space"=>"磁盘剩余空间不足","duration-limit"=>"达到单段时长上限",_=>"录制结束"};
    internal async Task FinishForExitAsync()
    {
        exiting=true;timer.Stop();screenshotKey?.Unregister();recordKey?.Unregister();await operation.WaitAsync();
        try{await StopCoreAsync();}finally{operation.Release();}
    }
    public void Dispose(){if(disposed)return;disposed=true;timer.Stop();screenshotKey?.Dispose();recordKey?.Dispose();recording?.Dispose();}
}

public sealed partial class MainForm
{
    private GameMediaController? _mediaController;
    private bool _mediaClosing;
    private GameMediaController MediaController=>_mediaController??=CreateMediaController();
    private GameMediaController CreateMediaController()
    {
        var controller=new GameMediaController(async()=>(await RecentStore()).SnapshotAll(),text=>
        {
            DesktopStatus=text;
            if(!DesktopVisible&&!AppDataPaths.DisableGlobalInput)_trayIcon.ShowBalloonTip(2000,UiLanguage.Text("截图与录像"),text,ToolTipIcon.Info);
        },value=>
        {
            foreach(var action in new[]{InputActionId.StartCapture,InputActionId.CancelCapture,InputActionId.ToggleResults})
                if(!NativeBinding(action).IsEmpty&&(NativeBinding(action)==value.Screenshot||NativeBinding(action)==value.Recording))return "与截图翻译快捷键重复，请换一个组合。";
            return null;
        });
        controller.Changed+=()=>NativeShortcutsChanged?.Invoke();return controller;
    }
    GameMediaPreferences IDesktopSettingsHost.NativeMediaPreferences=>MediaController.Preferences;
    string IDesktopSettingsHost.NativeMediaShortcutStatus=>MediaController.ShortcutStatus;
    void IDesktopSettingsHost.NativeConfigureMediaKeys()
    {
        using var dialog=new NativeGameMediaSettingsDialog(MediaController.Preferences,MediaController.SavePreferences);dialog.ShowDialog(this);
    }
    private async Task FinishMediaBeforeExitAsync()
    {
        if(_mediaClosing)return;_mediaClosing=true;
        try{await MediaController.FinishForExitAsync();}
        catch(Exception ex){AppLog.Write("game-media-exit",SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally{_mediaClosing=false;_mediaController?.Dispose();_mediaController=null;Close();}
    }
}
