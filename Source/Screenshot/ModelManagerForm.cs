using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

internal sealed class ModelManagerForm : Form
{
    private readonly OcrRuntimeManager _ocr;
    private readonly string _applicationRoot;
    private readonly ListView _list=new(){Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true};
    private readonly Label _summary=new(){AutoSize=true,Dock=DockStyle.Top,Padding=new Padding(12)};
    private readonly Label _repairStatus=new(){AutoSize=true,Dock=DockStyle.Top,Padding=new Padding(12),Text="修复：尚未执行。检查不会联网下载。"};
    private readonly DialogActionBar _actions=new();
    private readonly CancellationTokenSource _lifetime=new();
    private OcrRuntimeStatus? _lastCheck;
    private bool _busy;

    internal ModelManagerForm(string applicationRoot,OcrRuntimeManager ocr,ApiSettings? themeSettings=null)
    {
        SuspendLayout();AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new SizeF(96,96);
        _applicationRoot=applicationRoot;_ocr=ocr;Text="模型与运行环境管理";StartPosition=FormStartPosition.CenterParent;Size=new Size(820,520);MinimumSize=new Size(680,420);
        _list.Columns.Add("组件",220);_list.Columns.Add("检查状态",140);_list.Columns.Add("位置 / 详情",450);
        void Add(string text,EventHandler click){var b=new Button{Text=text};b.Click+=click;_actions.Controls.Add(b);}
        Add("重新检查",async(_,_)=>await RefreshAsync());
        Add("打开目录",(_,_)=>OpenSelectedFolder());
        _repairStatus.Text = FusionRuntime.ReadOnlyMessage;
        var status=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1};
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));status.Controls.Add(_summary);status.Controls.Add(_repairStatus);
        Controls.Add(_list);Controls.Add(status);Controls.Add(_actions);
        Shown+=async(_,_)=>await RefreshAsync();
        var appearance=themeSettings??new ApiSettings();FontManager.ApplyUi(this,appearance);UiTheme.Apply(this,appearance);
        Shown+=(_,_)=>ScaleColumns();DpiChanged+=(_,_)=>ScaleColumns();
        SizeChanged+=(_,_)=>{_summary.MaximumSize=_repairStatus.MaximumSize=new Size(Math.Max(1,ClientSize.Width-24),0);};
        FormClosing+=(_,_)=>_lifetime.Cancel();Disposed+=(_,_)=>{_lifetime.Cancel();_lifetime.Dispose();};
        DialogLayout.Attach(this);ResumeLayout(true);
    }

    private void ScaleColumns(){int[] widths=[220,140,450];for(int i=0;i<widths.Length;i++)_list.Columns[i].Width=(int)Math.Round(widths[i]*DeviceDpi/96f);}

    private async Task RunOperationAsync(Func<Task> operation)
    {
        if(_busy||IsDisposed)return;_busy=true;_actions.Enabled=false;
        try{await operation();}
        catch(OperationCanceledException){if(!IsDisposed)_summary.Text="检查：已取消，未标记就绪。";}
        catch(Exception ex){if(!IsDisposed)_summary.Text="检查或修复未完成："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{_busy=false;if(!IsDisposed)_actions.Enabled=true;}
    }

    private Task RefreshAsync()=>RunOperationAsync(InspectAsync);

    private async Task InspectAsync()
    {
        _summary.Text="检查：正在验证组件、模型及小图实际识别；不会联网下载…";
        var selected=_list.SelectedItems.Count>0?_list.SelectedItems[0].Tag as string:"rapid";
        _list.Items.Clear();
        AddRow("RapidOCR","检查中","","rapid");
        var model=Path.Combine(ResolveVisionRoot(),"paddlex","official_models","PP-DocLayout-S");
        AddRow("PP-DocLayout-S（实验）",Directory.Exists(model)?"产品关闭":"未提供（非必需）",model,"pp-s");
        var python=ModelManagerOperations.ResolveRapidPython(_applicationRoot);
        AddRow("本地提供的 Python",File.Exists(python)?"存在，待版本与加载验证":"Missing",python,"rapid-runtime");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(50));
        var check=await _ocr.CheckAsync(OcrEngineKind.Rapid,timeout.Token);
        if(IsDisposed||_lifetime.IsCancellationRequested)return;
        _lastCheck=check;_list.Items[0].SubItems[1].Text=check.Ready?"实际识别通过":"未就绪";
        _list.Items[0].SubItems[2].Text=check.Detail;
        _summary.Text="检查："+check.Detail;
        foreach(ListViewItem item in _list.Items)if((string?)item.Tag==selected)item.Selected=true;
    }

    private void AddRow(string name,string status,string detail,string key){var item=new ListViewItem(name){Tag=key};item.SubItems.Add(status);item.SubItems.Add(detail);_list.Items.Add(item);}
    private string ResolveVisionRoot()=>ModelManagerOperations.ResolveVisionRoot(_applicationRoot);
    private string? SelectedPath()=>_list.SelectedItems.Count==0?null:_list.SelectedItems[0].Tag switch{"rapid"=>_ocr.RuntimeRoot,"pp-s"=>Path.Combine(ResolveVisionRoot(),"paddlex","official_models","PP-DocLayout-S"),"rapid-runtime"=>_ocr.RuntimeRoot,_=>null};

    private void DeleteSelected()
    {
        if(_busy)return;var path=SelectedPath();if(path is null||!Directory.Exists(path))return;
        if(AppDialog.Show(this,"删除组件",$"确认移除 {Path.GetFileName(path)}？组件将先移动到可恢复隔离区。",AppDialogKind.Confirmation)!=DialogResult.Yes)return;
        if(!ModelManagerOperations.MoveToQuarantine(path)){AppDialog.Show(this,"无法删除","组件被占用、不存在或隔离区已存在，请先结束识别再处理。",AppDialogKind.Warning);return;}
        _repairStatus.Text="修复：组件已移至本地隔离区，可使用修复按钮还原。";_=RefreshAsync();
    }

    private Task RepairSelectedAsync()=>RunOperationAsync(async()=>
    {
        var path=SelectedPath();if(path is null){_repairStatus.Text="修复：请先选择组件。";return;}
        if(ModelManagerOperations.RapidNativePathTooLong(_applicationRoot)){_repairStatus.Text="修复："+ModelManagerOperations.LongPathMessage;return;}
        var restored=ModelManagerOperations.RestoreFromQuarantine(path);
        if(_list.SelectedItems[0].Tag as string=="pp-s")
        {_repairStatus.Text=restored?"修复：实验组件已从本地隔离区还原；产品功能仍关闭。":"修复：实验组件仅支持本地隔离恢复，不属于基础 OCR，也不在此下载。";return;}
        _repairStatus.Text=restored?"修复：已还原本地隔离组件；正在重新检查。":"修复：正在检查可用的本地恢复材料。";
        await InspectAsync();if(IsDisposed||_lifetime.IsCancellationRequested)return;
        if(_lastCheck?.Ready==true){_repairStatus.Text=restored?"修复：本地组件还原完成，修复后实际识别通过。":"修复：检查已通过，无需下载或重新安装。";return;}
        if(_lastCheck?.Code is not ("MODEL_MISSING" or "MODEL_INVALID"))
        {
            _repairStatus.Text="修复：没有可用的本地组件恢复材料，或组件无法加载。请重新解压完整程序包；仅下载模型不能修复此故障。";
            return;
        }
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var repair=await _ocr.RepairModelsAsync(false,timeout.Token);
        if(IsDisposed||_lifetime.IsCancellationRequested)return;
        if(repair.Code=="NETWORK_REQUIRED")
        {
            _repairStatus.Text="修复：本地模型恢复材料不足；等待联网修复确认。";
            using var prompt=DialogLayout.NetworkRepairPrompt(this);
            if(ModalSafety.Show(this,prompt)!=DialogResult.Yes)
            {_repairStatus.Text="修复：未执行联网下载。请联网后重试，或重新解压完整程序包。";return;}
            _repairStatus.Text="修复：已获准联网下载模型，正在执行（限时、无无限重试）…";
            repair=await _ocr.RepairModelsAsync(true,timeout.Token);
        }
        if(IsDisposed||_lifetime.IsCancellationRequested)return;
        _repairStatus.Text="修复："+repair.Detail;
        await InspectAsync();
        if(!IsDisposed&&repair.Code=="REPAIRED")_repairStatus.Text="修复："+repair.Detail+(_lastCheck?.Ready==true?" 修复后实际识别通过。":" 修复后检查仍未通过，不能标记就绪。");
    });

    private void OpenSelectedFolder(){var path=SelectedPath();if(path is null)return;var target=Directory.Exists(path)?path:Path.GetDirectoryName(path);if(target is not null&&Directory.Exists(target))Process.Start(new ProcessStartInfo("explorer.exe",$"\"{target}\""){UseShellExecute=true});}
}
