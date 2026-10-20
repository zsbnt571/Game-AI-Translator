using System.IO.Compression;
using System.Text;
using WC=System.Windows.Controls;
namespace ScreenshotTranslationUiTester;

internal sealed class NativeDiagnosticDialog : NativeDialogWindow
{
    private readonly WC.TextBox body=NativeDialogControls.Input("",true,true);
    private (byte[] Text,byte[] Json) snapshot;
    private readonly string textName,jsonName,archiveName;
    private readonly Func<Task<(byte[] Text,byte[] Json)>> capture;
    internal NativeDiagnosticDialog(string title,(byte[] Text,byte[] Json) initial,Func<Task<(byte[] Text,byte[] Json)>> capture,string textName,string jsonName,string archiveName,Func<bool>? canRetry=null,Action? retryAction=null,Func<byte[],byte[]>? localizeText=null):base(title,840,650)
    {
        this.capture=capture;this.textName=textName;this.jsonName=jsonName;this.archiveName=archiveName;snapshot=initial;body.Text=Encoding.UTF8.GetString(initial.Text);
        body.VerticalContentAlignment=System.Windows.VerticalAlignment.Top;
        System.Windows.Automation.AutomationProperties.SetAutomationId(body,"DiagnosticReportBody");
        var refresh=NativeDialogControls.Button("刷新状态",()=>{});var export=NativeDialogControls.Button("导出报告包…",()=>{});var retry=NativeDialogControls.Button("重试失败",()=>{});retry.IsEnabled=canRetry?.Invoke()==true;
        var close=ResultButton("关闭",System.Windows.Forms.DialogResult.Cancel);
        string retryLabel="重试失败",exportLabel="导出报告包…";
        System.Windows.Automation.AutomationProperties.SetAutomationId(refresh,"RefreshDiagnosticReport");
        refresh.Click+=async(_,_)=>{refresh.IsEnabled=false;try{var updated=await capture();if(!IsDisposed){snapshot=updated;body.Text=Encoding.UTF8.GetString(updated.Text);retryLabel="重试失败";retry.Content=UiLanguage.Text(retryLabel);retry.IsEnabled=canRetry?.Invoke()==true;}}catch(Exception ex){if(!IsDisposed)AppDialog.Show(this,"读取失败",SafeDiagnosticOutput.ExceptionSummary(ex),AppDialogKind.Error);}finally{if(!IsDisposed)refresh.IsEnabled=true;}};
        retry.Click+=(_,_)=>{retryAction?.Invoke();retry.IsEnabled=false;retryLabel="已安排重试";retry.Content=UiLanguage.Text(retryLabel);};
        export.Click+=(_,_)=>{using var save=new SaveFileDialog{Filter=UiLanguage.Text("诊断报告包 (*.zip)|*.zip"),FileName=archiveName+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip",OverwritePrompt=true};if(save.ShowDialog(this)!=System.Windows.Forms.DialogResult.OK)return;
            try{ExportSnapshot(save.FileName);exportLabel="报告已导出";export.Content=UiLanguage.Text(exportLabel);}catch(Exception ex){AppDialog.Show(this,"导出失败",SafeDiagnosticOutput.ExceptionSummary(ex),AppDialogKind.Error);}};
        void Localize(object? sender,EventArgs args)
        {
            if(IsDisposed)return;
            Title=UiLanguage.Text(title);refresh.Content=UiLanguage.Text("刷新状态");retry.Content=UiLanguage.Text(retryLabel);export.Content=UiLanguage.Text(exportLabel);close.Content=UiLanguage.Text("关闭");
            if(localizeText is not null){snapshot.Text=localizeText(snapshot.Json);body.Text=Encoding.UTF8.GetString(snapshot.Text);}
        }
        UiLanguage.LanguageChanged+=Localize;Closed+=(_,_)=>UiLanguage.LanguageChanged-=Localize;
        var actions=NativeDialogControls.Row(refresh);if(retryAction is not null)actions.Children.Add(retry);actions.Children.Add(export);actions.Children.Add(close);Content=NativeDialogControls.Layout(body,actions,false);
    }
    internal void ExportSnapshot(string path)
    {
        using var file=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None);
        using var zip=new ZipArchive(file,ZipArchiveMode.Create);
        using(var entry=zip.CreateEntry(textName).Open())entry.Write(snapshot.Text);
        using(var entry=zip.CreateEntry(jsonName).Open())entry.Write(snapshot.Json);
    }
}

public sealed partial class MainForm
{
    private void ShowNativeTranslationDiagnostics(Func<TranslationDiagnosticReport> capture,Action retryAction)
    {
        using var dialog=BuildNativeTranslationDiagnostics(capture,retryAction);dialog.ShowDialog(this);
    }
    internal NativeDiagnosticDialog BuildNativeTranslationDiagnostics(Func<TranslationDiagnosticReport> capture,Action retryAction)
    {
        var report=capture();(byte[] Text,byte[] Json) Bytes()=>(Encoding.UTF8.GetBytes(report.Text()),Encoding.UTF8.GetBytes(report.Json()));
        return new NativeDiagnosticDialog("翻译状态与排查",Bytes(),()=>{report=capture();return Task.FromResult(Bytes());},"translation-status.txt","translation-status.json","Fusion-translation-status",()=>report.Connected&&report.Enabled&&report.Sample is {} sample&&sample.RetryScheduledTexts+sample.RetryStoppedTexts>0,retryAction,_=>Encoding.UTF8.GetBytes(report.Text()));
    }
}
