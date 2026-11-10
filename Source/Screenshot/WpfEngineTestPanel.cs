using System.Diagnostics;
using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

// Optional neutral engine sessions, separate from every ordinary game session.
internal sealed class WpfEngineTestPanel : WC.StackPanel, IDisposable
{
    private readonly Func<EngineTestRequest> selection;
    private readonly WC.ComboBox engine;
    private readonly WC.TextBlock availability = Text("");
    private readonly WC.TextBlock summary = Text("");
    private readonly WC.TextBlock testedFont = Text("");
    private readonly WC.TextBox runtimePath = NativeDialogControls.Input("", readOnly: true);
    private readonly WC.StackPanel checkRows = new();
    private readonly WC.Expander details = new();
    private readonly WC.Button start, cancel, chooseRuntime, openResult;
    private readonly Dictionary<string,string> runtimePaths = new(StringComparer.Ordinal);
    private CancellationTokenSource? running;
    private EngineTestResult? result;
    private EngineTestRequest? tested;
    private bool disposed;

    internal WpfEngineTestPanel(Func<EngineTestRequest> selection)
    {
        this.selection=selection;
        Children.Add(Text("使用独立样例检查引擎显示，不需要选择游戏。"));
        engine=NativeDialogControls.Choice(FontTestEngines.Items.Select(x=>x.Name));
        engine.ItemTemplate=null;engine.MinWidth=230;engine.MaxWidth=460;engine.HorizontalAlignment=W.HorizontalAlignment.Left;
        W.Automation.AutomationProperties.SetAutomationId(engine,"TestEngineSelector");
        WpfUiLanguage.Property(engine,W.Automation.AutomationProperties.NameProperty,"测试引擎");
        engine.SelectedIndex=Array.FindIndex(FontTestEngines.Items,x=>x.Id=="godot4");
        Children.Add(engine);Children.Add(availability);
        var runtimeBody=new WC.StackPanel();
        chooseRuntime=NativeDialogControls.Button("选择引擎运行程序",ChooseRuntime);
        runtimeBody.Children.Add(chooseRuntime);runtimePath.BorderThickness=new(0);runtimePath.TextWrapping=W.TextWrapping.Wrap;runtimePath.MinHeight=0;runtimePath.Padding=new(0);runtimePath.FontSize=12;runtimeBody.Children.Add(runtimePath);
        runtimeBody.Children.Add(Text("运行环境仅用于独立样例，不选择游戏程序。"));
        var runtimeDetails=new WC.Expander{Header=Text("运行环境"),Content=runtimeBody,Margin=new(0,0,0,12)};
        Children.Add(runtimeDetails);
        start=NativeDialogControls.Button("开始引擎测试",()=>_ = Run(),true);
        cancel=NativeDialogControls.Button("取消测试",Cancel);cancel.Visibility=W.Visibility.Collapsed;
        openResult=NativeDialogControls.Button("打开测试结果",OpenResult);openResult.Visibility=W.Visibility.Collapsed;
        Children.Add(NativeDialogControls.Row(start,cancel,openResult));
        summary.FontWeight=W.FontWeights.SemiBold;Children.Add(summary);Children.Add(testedFont);
        details.Header=Text("测试详情");details.Content=checkRows;details.Visibility=W.Visibility.Collapsed;Children.Add(details);
        engine.SelectionChanged+=(_,_)=>
        {
            if(running is null){result=null;tested=null;details.Visibility=openResult.Visibility=W.Visibility.Collapsed;RenderResult();}
            RefreshAvailability();
        };
        UiLanguage.LanguageChanged+=LanguageChanged;
        RefreshAvailability();RenderResult();
    }

    private string EngineId=>FontTestEngines.Items[Math.Clamp(engine.SelectedIndex,0,FontTestEngines.Items.Length-1)].Id;
    private static WC.TextBlock Text(string value)
    {var block=NativeDialogControls.Text(value);block.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");return block;}
    private void ChooseRuntime()
    {
        var picker=new Microsoft.Win32.OpenFileDialog{Title=UiLanguage.Text("选择引擎运行程序"),Filter=UiLanguage.Text("程序文件")+" (*.exe)|*.exe",CheckFileExists=true};
        if(picker.ShowDialog(W.Window.GetWindow(this))!=true)return;
        runtimePaths[EngineId]=picker.FileName;
        result=null;tested=null;details.Visibility=openResult.Visibility=W.Visibility.Collapsed;
        RenderResult();RefreshAvailability();
    }
    private void RefreshAvailability()
    {
        var item=EngineTestWorkspaceRunner.GetCatalog(runtimePaths).FirstOrDefault(x=>x.EngineId==EngineId);
        availability.Text=UiLanguage.Text(item?.Message??"此引擎的独立样例尚未准备。");
        runtimePath.Text=runtimePaths.GetValueOrDefault(EngineId,EngineTestWorkspaceRunner.FindDefaultRuntime(EngineId));
        start.IsEnabled=running is null&&item?.Ready==true;
        chooseRuntime.IsEnabled=running is null;
    }
    private async Task Run()
    {
        if(disposed||running is not null)return;
        EngineTestRequest request=selection() with{EngineId=EngineId,RuntimePath=runtimePaths.GetValueOrDefault(EngineId,""),OutputRoot=Path.Combine(AppDataPaths.Root,"test-area"),TimeoutSeconds=45};
        if(request.FontFamily.Length==0||request.FontPath.Length==0||!File.Exists(request.FontPath))
        {
            result=null;tested=null;details.Visibility=openResult.Visibility=W.Visibility.Collapsed;
            summary.Text=UiLanguage.Text("请先选择可读取的字体。");testedFont.Text="";return;
        }
        tested=request;result=null;running=new();var cancellation=running;
        start.IsEnabled=engine.IsEnabled=chooseRuntime.IsEnabled=false;cancel.Visibility=W.Visibility.Visible;openResult.Visibility=details.Visibility=W.Visibility.Collapsed;
        summary.Text=UiLanguage.Text("正在运行独立引擎样例…");testedFont.Text=string.Format(UiLanguage.Text("本次测试字体：{0}"),request.FontFamily);
        try { result=await Task.Run(()=>EngineTestWorkspaceRunner.RunAsync(request,cancellation.Token)); }
        catch(OperationCanceledException) { result=new(){EngineId=request.EngineId,Status=EngineTestRunStatus.Cancelled,Summary="测试已取消。"}; }
        catch(Exception ex) { result=new(){EngineId=request.EngineId,Status=EngineTestRunStatus.Failed,Summary="测试未完成。",Checks=[new("测试未完成",false,SafeDiagnosticOutput.ExceptionSummary(ex),EngineTestEvidenceKind.Unavailable)]}; }
        finally
        {
            cancellation.Dispose();running=null;
            if(!disposed){engine.IsEnabled=chooseRuntime.IsEnabled=true;cancel.Visibility=W.Visibility.Collapsed;RefreshAvailability();RenderResult();}
        }
    }
    private void RenderResult()
    {
        checkRows.Children.Clear();
        if(result is null)
        {
            if(running is null){summary.Text=UiLanguage.Text("尚未运行引擎样例。");testedFont.Text="";}
            return;
        }
        string layer=result.EvidenceKind switch{EngineTestEvidenceKind.ActualEngine=>"实际引擎样例",EngineTestEvidenceKind.ControlledAdapter=>"受控适配检查",EngineTestEvidenceKind.FontFile=>"字体文件检查",_=>"尚未验证"};
        summary.Text=UiLanguage.Text(layer)+" · "+UiLanguage.Text(result.Summary);
        testedFont.Text=string.Format(UiLanguage.Text("本次测试字体：{0}"),tested?.FontFamily??"");
        foreach(var check in result.Checks)
        {
            string checkLayer=check.EvidenceKind switch{EngineTestEvidenceKind.ActualEngine=>"实际引擎样例",EngineTestEvidenceKind.FontFile=>"字体文件检查",EngineTestEvidenceKind.ControlledAdapter=>"受控适配检查",_=>"尚未验证"};
            checkRows.Children.Add(Text(UiLanguage.Text(check.Passed?"通过":"未通过")+" · "+UiLanguage.Text(check.Name)+" · "+UiLanguage.Text(checkLayer)));
            if(check.Detail.Length>0)checkRows.Children.Add(Text(UiLanguage.Text(check.Detail)));
        }
        checkRows.Children.Add(Text(UiLanguage.Text(result.CharacterBindingVerified?"本次样例已验证角色绑定。":"本次未验证角色绑定。")));
        details.Visibility=W.Visibility.Visible;
        openResult.Visibility=File.Exists(result.ReportPath)?W.Visibility.Visible:W.Visibility.Collapsed;
    }
    private void OpenResult()
    {
        if(result is null||!File.Exists(result.ReportPath))return;
        try{Process.Start(new ProcessStartInfo(Path.GetDirectoryName(result.ReportPath)!){UseShellExecute=true});}
        catch(Exception ex){summary.Text=UiLanguage.Text(SafeDiagnosticOutput.ExceptionSummary(ex));}
    }
    private void LanguageChanged(object? sender,EventArgs args)
    {if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(()=>LanguageChanged(sender,args));return;}RefreshAvailability();RenderResult();}
    internal void Cancel()=>running?.Cancel();
    public void Dispose(){if(disposed)return;disposed=true;Cancel();UiLanguage.LanguageChanged-=LanguageChanged;}
}
