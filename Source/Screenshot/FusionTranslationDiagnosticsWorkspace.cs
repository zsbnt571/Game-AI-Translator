using System.IO.Compression;
using System.Text;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private void ShowTranslationDiagnostics()
    {
        if(_selectedGame is not {} game)return;
        var data=SelectedTranslationSession();
        TranslationDiagnosticReport Capture()=>new(DateTimeOffset.Now,BuildIdentity.ProductVersion,
            TranslationDiagnosticReport.EngineLabel(game.AdapterId),data?.Connection.Connected==true,
            data?.Translation?.Enabled==true,data?.Translation?.RestorePending==true,
            _fusion is null?0:FusionConfiguration.Missing(_fusion.GameSettings(UiSettings,game.ExePath)).Count,
            data?.Translation?.Snapshot);
        if(WpfWorkspace.Enabled){ShowNativeTranslationDiagnostics(Capture,()=>{if(data?.Connection.Connected==true&&data.Translation?.Enabled==true)data.Translation.RetryFailures();});return;}
        using var viewer=BuildTranslationDiagnosticViewer(Capture,()=>
        {if(data?.Connection.Connected==true&&data.Translation?.Enabled==true)data.Translation.RetryFailures();});
        viewer.ShowDialog(this);
    }

    private Form BuildTranslationDiagnosticViewer(Func<TranslationDiagnosticReport> capture,Action retryAction)
    {
        var report=capture();
        bool CanRetry()=>report.Connected&&report.Enabled&&report.Sample is {} s&&s.RetryScheduledTexts+s.RetryStoppedTexts>0;
        var viewer=new Form{Text="翻译状态与排查",Size=new(840,650),MinimumSize=new(540,380),
            StartPosition=FormStartPosition.CenterParent,Padding=new(14)};
        var body=new RichTextBox{Dock=DockStyle.Fill,ReadOnly=true,DetectUrls=false,WordWrap=true,
            BorderStyle=BorderStyle.None,Text=report.Text(),AccessibleName="当前翻译状态和处理建议"};
        var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,Padding=new(0,8,0,0),WrapContents=true};
        var refresh=new Button{Text="刷新状态",AutoSize=true};
        var retry=new Button{Text="重试失败",AutoSize=true,Enabled=CanRetry()};
        var export=new Button{Text="导出报告包…",AutoSize=true};
        var close=new Button{Text="关闭",AutoSize=true,DialogResult=DialogResult.Cancel};
        refresh.Click+=(_,_)=>{report=capture();body.Text=report.Text();retry.Text="重试失败";retry.Enabled=CanRetry();};
        retry.Click+=(_,_)=>{retryAction();retry.Enabled=false;retry.Text="已安排重试";};
        export.Click+=(_,_)=>
        {
            using var save=new SaveFileDialog{Filter=UiLanguage.Text("翻译状态报告包 (*.zip)|*.zip"),FileName="Fusion-翻译状态-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip",OverwritePrompt=true};
            if(save.ShowDialog(viewer)!=DialogResult.OK)return;
            try
            {
                // Export exactly the inspected snapshot, not a later state.
                using var file=new FileStream(save.FileName,FileMode.Create,FileAccess.Write,FileShare.None);
                using var zip=new ZipArchive(file,ZipArchiveMode.Create);
                using(var writer=new StreamWriter(zip.CreateEntry("翻译状态.txt").Open(),new UTF8Encoding(false)))writer.Write(report.Text());
                using(var writer=new StreamWriter(zip.CreateEntry("translation-status.json").Open(),new UTF8Encoding(false)))writer.Write(report.Json());
                export.Text="报告已导出";
            }
            catch(Exception ex){AppDialog.Show(viewer,"导出失败",SafeDiagnosticOutput.ExceptionSummary(ex),AppDialogKind.Error);}
        };
        footer.Controls.AddRange([refresh,retry,export,close]);viewer.Controls.Add(body);viewer.Controls.Add(footer);viewer.CancelButton=close;
        FontManager.ApplyUi(viewer,UiSettings);UiTheme.Apply(viewer,UiSettings);return viewer;
    }

    private TableLayoutPanel BuildFusionHelp()
    {
        var help=SettingsTable();
        AddSettingRow(help,"软件版本",Info(BuildIdentity.ProductVersion));
        AddSettingRow(help,"首次使用",Info("1. 将游戏启动文件加入游戏库，并打开游戏详情。\n2. 选择翻译方案；如提示未配置完成，点击旁边的“编辑”补齐接口、模型等设置。\n3. 点击“翻译并启动”。普通“启动游戏”默认不开启翻译。\n4. 进入菜单或对话页面，查看游戏内文字和本页翻译状态。"));
        AddSettingRow(help,"部分文字未译",Info("先看游戏详情中的“翻译状态”：区分未收到文字、等待翻译、接口失败、等待游戏应用和字体受阻。\n遇到失败可重试或更正单条译文；一般无需清空已有译文。反馈时提供当前画面和翻译状态报告。"));
        AddSettingRow(help,"翻译方式",Info("游戏内翻译通过对应引擎接入读取和替换文字；自动识别引擎不代表所有页面均已覆盖。\n屏幕翻译需要手动开启，在画面上显示译文；两种方式的状态分别显示。"));
        AddSettingRow(help,"游戏退出诊断",BuildGameDiagnosticHelp());
        AddSettingRow(help,"数据配置",_configPathLabel);AddSettingRow(help,"密钥状态",_keyStatusLabel);
        AddSettingRow(help,"当前运行环境",Info(FusionRuntime.Root+"\n缺少组件时会提示对应功能不可用；此测试版保持现有运行环境配置。"));
        AddSettingRow(help,"译文与设置",Info("游戏译文按游戏及翻译方案保存，可在“译文与术语”中更正、重译或导入导出。\n桌面接口密钥由当前 Windows 用户加密保存；部分旧接入方式会在游戏目录写入所需配置。"));
        return help;
    }
}
