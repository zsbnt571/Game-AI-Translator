using System.Diagnostics;
using W=System.Windows;
using WC=System.Windows.Controls;
namespace ScreenshotTranslationUiTester;

// Neutral fixtures only. No user's roster, credentials or cache is read.
internal sealed class WpfCharacterTestPanel:WC.StackPanel,IDisposable
{
    readonly WC.Button start,open;
    readonly WC.TextBlock status=Text("尚未运行角色检查。");
    readonly WC.StackPanel rows=new();
    EngineTestResult? result;CancellationTokenSource? running;bool disposed;
    internal WpfCharacterTestPanel()
    {
        W.Automation.AutomationProperties.SetAutomationId(this,"TestCharacterPanel");
        Children.Add(Text("检查同句角色区分、手动修改与恢复自动，以及中文缓存保护；不调用翻译 API。"));
        start=NativeDialogControls.Button("检查角色与缓存",()=>_=Run(),true);
        open=NativeDialogControls.Button("打开测试结果",Open);open.Visibility=W.Visibility.Collapsed;
        Children.Add(NativeDialogControls.Row(start,open));Children.Add(status);Children.Add(rows);
        UiLanguage.LanguageChanged+=LanguageChanged;
    }
    static WC.TextBlock Text(string value){var t=NativeDialogControls.Text(value);t.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");return t;}
    async Task Run()
    {
        if(disposed||running is not null)return;
        running=new();var cancellation=running;start.IsEnabled=false;open.Visibility=W.Visibility.Collapsed;rows.Children.Clear();status.Text=UiLanguage.Text("正在检查角色与缓存…");
        try{result=await Task.Run(()=>EngineTestWorkspaceRunner.RunCharacterCheckAsync(Path.Combine(AppDataPaths.Root,"test-area"),cancellation.Token));}
        catch(Exception ex){result=new(){EngineId="shared-character",Status=EngineTestRunStatus.Failed,EvidenceKind=EngineTestEvidenceKind.ControlledAdapter,Summary="测试未完成。",Checks=[new("测试未完成",false,SafeDiagnosticOutput.ExceptionSummary(ex),EngineTestEvidenceKind.ControlledAdapter)]};}
        finally{cancellation.Dispose();running=null;if(!disposed){start.IsEnabled=true;Render();}}
    }
    void Render()
    {
        rows.Children.Clear();if(result is null)return;
        status.Text=UiLanguage.Text("共享流程检查")+" · "+UiLanguage.Text(result.Summary);
        foreach(var check in result.Checks)rows.Children.Add(Text(UiLanguage.Text(check.Passed?"通过":"未通过")+" · "+UiLanguage.Text(check.Name)));
        open.Visibility=File.Exists(result.ReportPath)?W.Visibility.Visible:W.Visibility.Collapsed;
    }
    void Open(){if(result is null||!File.Exists(result.ReportPath))return;try{Process.Start(new ProcessStartInfo(Path.GetDirectoryName(result.ReportPath)!){UseShellExecute=true});}catch(Exception ex){status.Text=UiLanguage.Text(SafeDiagnosticOutput.ExceptionSummary(ex));}}
    void LanguageChanged(object? sender,EventArgs e){if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(()=>LanguageChanged(sender,e));return;}Render();}
    internal void Cancel()=>running?.Cancel();
    public void Dispose(){if(disposed)return;disposed=true;Cancel();UiLanguage.LanguageChanged-=LanguageChanged;}
}
