using System.Diagnostics;
using W=System.Windows;
using C=System.Windows.Controls;
using M=System.Windows.Media;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfMediaSelfTestPanel:C.StackPanel,IDisposable
{
    private readonly C.TextBlock status=NativeDialogControls.Text("尚未运行自检。"),path=NativeDialogControls.Text("");
    private readonly C.Image preview=new(){MaxHeight=220,Stretch=M.Stretch.Uniform,HorizontalAlignment=W.HorizontalAlignment.Left};
    private readonly C.Button screenshot,record,open,folder;
    private CancellationTokenSource? operation;
    private string lastFile="";
    private bool disposed;
    internal WpfMediaSelfTestPanel()
    {
        Children.Add(NativeDialogControls.Text("截图录像自检",18));
        Children.Add(NativeDialogControls.Text("打开独立的测试画面，检查能否保存截图或录制 3 秒无声视频。不会启动或修改游戏。游戏热键另在“设置 → 按键”配置。"));
        screenshot=NativeDialogControls.Button("截图自检",()=>_=Run(false));record=NativeDialogControls.Button("录制 3 秒画面",()=>_=Run(true));
        open=NativeDialogControls.Button("打开文件",()=>Open(false));folder=NativeDialogControls.Button("打开保存位置",()=>Open(true));open.IsEnabled=folder.IsEnabled=false;
        W.Automation.AutomationProperties.SetAutomationId(screenshot,"MediaSelfTestScreenshot");W.Automation.AutomationProperties.SetAutomationId(record,"MediaSelfTestRecord");
        Children.Add(NativeDialogControls.Row(screenshot,record));Children.Add(status);Children.Add(preview);Children.Add(path);Children.Add(NativeDialogControls.Row(open,folder));
    }
    private void Open(bool directory)
    {
        try{if(File.Exists(lastFile))Process.Start(new ProcessStartInfo(directory?Path.GetDirectoryName(lastFile)!:lastFile){UseShellExecute=true});}
        catch(Exception ex){status.Text=UiOperationText.Error(ex);}
    }
    private async Task Run(bool video)
    {
        if(disposed||operation is not null)return;
        using var cancel=new CancellationTokenSource();operation=cancel;
        screenshot.IsEnabled=record.IsEnabled=false;status.Text="正在打开测试画面…";
        var display=new C.TextBlock{FontSize=30,Foreground=M.Brushes.White,TextAlignment=W.TextAlignment.Center,VerticalAlignment=W.VerticalAlignment.Center};
        var surface=new W.Window{Title=UiLanguage.Text("Fusion · 截图录像自检"),Width=720,Height=420,ResizeMode=W.ResizeMode.NoResize,WindowStartupLocation=W.WindowStartupLocation.CenterOwner,Owner=W.Window.GetWindow(this),Content=display,Background=new M.SolidColorBrush(M.Color.FromRgb(22,45,77)),ShowInTaskbar=false};
        var elapsed=Stopwatch.StartNew();var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        void Frame()=>display.Text=UiLanguage.Format($"Fusion 截图录像自检\n画面计时 {elapsed.Elapsed.TotalSeconds:F1} 秒");
        timer.Tick+=(_,_)=>Frame();surface.Closed+=(_,_)=>{if(operation==cancel)cancel.Cancel();};
        GameMediaRecording? recording=null;
        try
        {
            Frame();surface.Show();timer.Start();await Task.Delay(500,cancel.Token);
            using var process=Process.GetCurrentProcess();
            var target=new CoverTarget(new System.Windows.Interop.WindowInteropHelper(surface).Handle,process.Id,GameLaunchPathIdentity.Canonical(Environment.ProcessPath!),process.StartTime.ToUniversalTime().Ticks);
            var store=new GameMediaStore("e10c103596a34e05aa78db28b6585c81",gameName:"截图录像自检");
            GameMediaEntry entry;
            var frame=await GameWindowCover.CaptureOcrFrameAsync(target,cancel.Token);
            using var bitmap=frame.Image;
            if(bitmap is null)throw new IOException(frame.Message);
            if(video)
            {
                status.Text="正在录制 3 秒测试画面…";
                var thumb=store.NewName("preview",".png");bitmap.Save(store.FilePath(thumb),System.Drawing.Imaging.ImageFormat.Png);
                recording=await GameMediaRecording.StartAsync(target,store,thumb,cancel.Token);
                await Task.Delay(3000,cancel.Token);entry=await recording.StopAsync();
            }
            else entry=store.SaveScreenshot(bitmap);
            cancel.Token.ThrowIfCancellationRequested();
            lastFile=store.FilePath(entry.FileName);preview.Source=WpfDrawing.Image(bitmap);path.Text=lastFile;
            status.Text=video?UiLanguage.Format($"已保存 {entry.DurationSeconds:F1} 秒无声录像。可打开文件检查播放效果。"):"已保存 PNG 截图。";
            open.IsEnabled=folder.IsEnabled=true;
        }
        catch(OperationCanceledException){if(!disposed)status.Text="自检已停止。";}
        catch(Exception ex){if(!disposed)status.Text=UiOperationText.Error(ex);}
        finally
        {
            if(recording is not null){try{await recording.StopAsync();}catch{}recording.Dispose();}
            timer.Stop();operation=null;surface.Close();
            if(!disposed)screenshot.IsEnabled=record.IsEnabled=true;
        }
    }
    internal void Cancel()=>operation?.Cancel();
    public void Dispose(){disposed=true;Cancel();}
}
