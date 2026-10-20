using System.Diagnostics;
using W=System.Windows;
using WC=System.Windows.Controls;
namespace ScreenshotTranslationUiTester;
internal sealed class NativeModelManagerWindow : NativeDialogWindow
{
    private readonly OcrRuntimeManager runtime;
    private readonly NativeRecordGrid grid=new(("组件",1),("检查状态",1),("位置 / 详情",3));
    private readonly WC.TextBlock summary=NativeDialogControls.Text("检查尚未开始。"),notice=NativeDialogControls.Text(FusionRuntime.ReadOnlyMessage);
    private readonly CancellationTokenSource lifetime=new();private bool busy;
    internal NativeModelManagerWindow(OcrRuntimeManager runtime):base("模型与运行环境管理",900,560)
    {
        grid.LocalizeColumns(0,1,2);
        this.runtime=runtime;var root=new WC.Grid{Margin=new(20)};root.RowDefinitions.Add(new(){Height=W.GridLength.Auto});root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        root.Children.Add(NativeDialogControls.Column(summary,notice));WC.Grid.SetRow(grid.View,1);root.Children.Add(grid.View);
        var buttons=NativeDialogControls.Row(NativeDialogControls.Button("重新检查",async()=>await RefreshAsync()),NativeDialogControls.Button("打开目录",OpenSelected),ResultButton("关闭",System.Windows.Forms.DialogResult.Cancel));buttons.Margin=new(0,12,0,0);WC.Grid.SetRow(buttons,2);root.Children.Add(buttons);Content=root;
        Loaded+=async(_,_)=>await RefreshAsync();Closed+=(_,_)=>lifetime.Cancel();
    }
    private async Task RefreshAsync()
    {
        if(busy||IsDisposed)return;busy=true;
        try
        {
            string? selected=grid.CurrentRow?.Tag as string;grid.Rows.Clear();
            void Add(string name,string status,string path){var index=grid.Rows.Add(name,status,path);grid.Rows[index].Tag=path;}
            summary.Text=UiLanguage.Text("检查：正在验证组件、模型及小图实际识别；不会联网下载…");
            Add("RapidOCR","检查中",runtime.RuntimeRoot);var vision=Path.Combine(ModelManagerOperations.ResolveVisionRoot(AppContext.BaseDirectory),"paddlex","official_models","PP-DocLayout-S");Add("PP-DocLayout-S（实验）",Directory.Exists(vision)?"产品关闭":"未提供（非必需）",vision);
            var python=ModelManagerOperations.ResolveRapidPython(AppContext.BaseDirectory);Add("捆绑 Python",File.Exists(python)?"存在，待加载验证":"缺失",python);
            grid.View.SelectedItem=grid.Rows.FirstOrDefault(x=>(string?)x.Tag==selected)??grid.Rows[0];
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(50));var status=await runtime.CheckAsync(OcrEngineKind.Rapid,timeout.Token);if(IsDisposed)return;
            grid.Rows[0].Cells[1].Value=status.Ready?"实际识别通过":"未就绪";grid.Rows[0].Cells[2].Value=status.Detail;summary.Text="检查："+status.Detail;
        }
        catch(OperationCanceledException){if(!IsDisposed)summary.Text=UiLanguage.Text("检查：已取消，未标记就绪。");}
        catch(Exception ex){if(!IsDisposed)summary.Text="检查或修复未完成："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{busy=false;}
    }
    private void OpenSelected(){if(grid.CurrentRow?.Tag is not string path)return;var target=Directory.Exists(path)?path:Path.GetDirectoryName(path);if(target is not null&&Directory.Exists(target))Process.Start(new ProcessStartInfo(target){UseShellExecute=true});}
}

public sealed partial class MainForm
{
    internal void NativeOpenModelManager()=>new NativeModelManagerWindow(_ocrRuntimeManager).Show(this);
    internal void NativeOpenOcrComparison(bool experimental)=>new OcrComparisonForm(_ocrRuntimeManager,experimental?OcrComparisonMode.ExperimentalWindows:OcrComparisonMode.Standard).Show(this);
}
