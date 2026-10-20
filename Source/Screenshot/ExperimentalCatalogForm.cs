namespace ScreenshotTranslationUiTester;

internal sealed class ExperimentalCatalogForm:Form
{
    private readonly CancellationTokenSource cancellation=new();
    private bool cancellationDisposed;
    private ExperimentalRpgCatalog.Result? result;
    private readonly TaskCompletionSource<bool> preparationFinished=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<bool> PreparationFinished=>preparationFinished.Task;
    internal ExperimentalCatalogForm(GameInfo game,ApiSettings? settings=null,ApiSettings? translationSettings=null,Func<string,ApiSettings,CancellationToken,Task<string>>? translate=null,ExperimentalRpgCatalog.Result? initial=null,bool beforeLaunch=false)
    {
        Text="读取文本与准备译文 · "+game.Name;AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new(96,96);Font=new("Microsoft YaHei UI",10);Size=new(1140,790);MinimumSize=new(760,560);StartPosition=FormStartPosition.CenterParent;
        var profile=ApiSettingsSnapshot.Copy(translationSettings??settings??new ApiSettings());Dictionary<string,string> cached=new(StringComparer.Ordinal);CancellationTokenSource? preparation=null;
        var table=TranslationWorkspaceUi.Column(new(SizeType.AutoSize),new(SizeType.AutoSize),new(SizeType.Percent,100),new(SizeType.AutoSize));table.Padding=new(20,12,20,12);Controls.Add(table);
        var title=TranslationWorkspaceUi.Label("读取文本与准备译文");title.Font=new(Font.FontFamily,14,FontStyle.Bold);table.Controls.Add(title,0,0);
        var scope=TranslationWorkspaceUi.Label(game.Name+" · "+game.Engine+"\n"+UiLanguage.Text("准备译文会使用所选翻译方案并保存缓存。翻译启动将优先复用已保存译文，动态文字仍可能需要补译。"),true);table.Controls.Add(scope,0,1);
        var tabs=new TranslationWorkspaceTabs();table.Controls.Add(tabs,0,2);var files=tabs.Add("文件读取结果","folder");var texts=tabs.Add("原文与译文（前 1000 处）","list");
        WorkspaceDataGrid Grid(Panel tab,string name,string[] columns){var g=TranslationWorkspaceUi.Table(name);foreach(string c in columns)g.Columns.Add(c,c);tab.Controls.Add(g);return g;}
        var fileGrid=Grid(files,"文件读取结果",["文件","结果","原文数","说明"]);var textGrid=Grid(texts,"原文与译文",["文件","字段 / 行","原文","译文"]);
        fileGrid.Columns[0].FillWeight=28;fileGrid.Columns[1].FillWeight=14;fileGrid.Columns[2].FillWeight=10;fileGrid.Columns[3].FillWeight=48;
        textGrid.Columns[0].FillWeight=18;textGrid.Columns[1].FillWeight=18;textGrid.Columns[2].FillWeight=32;textGrid.Columns[3].FillWeight=32;
        var status=TranslationWorkspaceUi.Label("正在读取资源，请稍候…",true);
        var export=TranslationWorkspaceUi.Button("导出完整提取结果","export");export.Enabled=false;
        var prepare=TranslationWorkspaceUi.Button("准备 / 更新译文","translate");prepare.Enabled=false;
        var cancel=TranslationWorkspaceUi.Button("暂停","stop");cancel.Enabled=false;
        bool launchSavedRequested=false;var launchSaved=TranslationWorkspaceUi.Button("使用已保存译文并启动","play");
        launchSaved.Visible=beforeLaunch;
        void FinishSavedLaunch(){preparationFinished.TrySetResult(true);Close();}
        launchSaved.Click+=(_,_)=>{launchSavedRequested=true;launchSaved.Enabled=false;if(preparation is null)FinishSavedLaunch();else preparation.Cancel();};
        var footer=TranslationWorkspaceUi.Column(new(SizeType.AutoSize),new(SizeType.AutoSize));footer.AutoSize=true;footer.AutoSizeMode=AutoSizeMode.GrowAndShrink;footer.Dock=DockStyle.Top;
        footer.Controls.Add(status,0,0);footer.Controls.Add(TranslationWorkspaceUi.Bar(prepare,cancel,export,launchSaved),0,1);table.Controls.Add(footer,0,3);
        async Task RefreshTranslations()
        {
            var loaded=await Task.Run(()=>RpgTranslationCache.Effective(game.ExePath,profile,CatalogTranslationPreparation.Syntax(game.Engine)));if(IsDisposed)return;cached=loaded;
            foreach(DataGridViewRow row in textGrid.Rows)if(row.Tag is string source)row.Cells[3].Value=cached.GetValueOrDefault(source,"");
        }
        cancel.Click+=(_,_)=>{preparation?.Cancel();cancel.Enabled=false;status.Text="正在暂停；已完成的译文会保留。";};
        prepare.Click+=async(_,_)=>
        {
            if(result is null||translate is null||preparation is not null)return;
            prepare.Enabled=export.Enabled=false;cancel.Enabled=true;preparation=CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            bool finished=false;
            void Progress(CatalogTranslationPreparation.Progress p)
            {
                if(IsDisposed||!IsHandleCreated||cancellation.IsCancellationRequested)return;
                try{BeginInvoke((Action)(()=>{if(!finished&&!IsDisposed&&!cancellation.IsCancellationRequested)status.Text=$"已准备 {p.Ready:N0} / {p.Total:N0} 条 · 本次保存 {p.Added:N0} 条 · 本次失败 {p.Failed:N0} 条\n"+p.Status;}));}
                catch(InvalidOperationException)when(IsDisposed||Disposing||!IsHandleCreated){}
            }
            try
            {
                var final=await Task.Run(()=>CatalogTranslationPreparation.PrepareAsync(game.ExePath,result,profile,translate,Progress,preparation.Token));
                finished=true;
                if(!IsDisposed){await RefreshTranslations();status.Text=$"已准备 {final.Ready:N0} / {final.Total:N0} 条 · "+final.Status;}
                if(beforeLaunch&&final.Ready==final.Total&&final.Failed==0&&!cancellation.IsCancellationRequested&&!IsDisposed){preparationFinished.TrySetResult(true);Close();}
            }
            catch(OperationCanceledException){finished=true;if(!IsDisposed&&!cancellation.IsCancellationRequested){try{await RefreshTranslations();if(!IsDisposed)status.Text="已暂停。完成的译文已保存，再次准备会复用缓存。";}catch(Exception ex){if(!IsDisposed)status.Text="已暂停；刷新译文失败："+SafeDiagnosticOutput.ExceptionSummary(ex);}}}
            catch(Exception ex){finished=true;if(!IsDisposed)status.Text="准备未完成："+SafeDiagnosticOutput.ExceptionSummary(ex);}
            finally{preparation.Dispose();preparation=null;if(!IsDisposed){prepare.Enabled=export.Enabled=true;cancel.Enabled=false;if(launchSavedRequested)FinishSavedLaunch();}}
        };
        export.Click+=(_,_)=>{if(result is null)return;using var dialog=new SaveFileDialog{Filter=UiLanguage.Text("文本与译文记录 (*.json)|*.json"),FileName="Fusion-文本与译文.json"};if(dialog.ShowDialog(this)!=DialogResult.OK)return;try{PortableDataStorage.WriteJson(dialog.FileName,new{Version=1,result.Engine,result.Complete,result.Boundary,TargetLanguage=profile.TargetLanguage,Entries=result.Entries.Select(e=>new{e.File,e.Location,e.Text,Translation=cached.GetValueOrDefault(e.Text,"")}),result.Files});status.Text="已导出。"+result.Boundary;}catch(Exception ex){status.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}};
        Shown+=async(_,_)=>
        {
            try
            {
                var read=initial??await Task.Run(()=>CatalogTranslationPreparation.ReadCatalog(game,profile,cancellation.Token));if(IsDisposed)return;result=read;
                foreach(var f in read.Files)fileGrid.Rows.Add(f.File,f.Status,f.Texts,f.Detail);
                foreach(var e in read.Entries.Take(1000)){int row=textGrid.Rows.Add(e.File,e.Location,e.Text,"");textGrid.Rows[row].Tag=e.Text;}
                await RefreshTranslations();if(IsDisposed)return;
                string[] sources=CatalogTranslationPreparation.Sources(read,profile);int ready=sources.Count(cached.ContainsKey);
                status.Text=(beforeLaunch?UiLanguage.Text("准备完成后继续启动游戏；关闭此窗口会取消本次启动。")+"\n":"")+$"{read.Engine} · 提取 {read.Entries.Length:N0} 处原文 · 按当前语言选择 {sources.Length:N0} 条 · 已有译文 {ready:N0} 条\n方案：{profile.Model} → {profile.TargetLanguage} · "+(read.Complete?"已支持字段检查完成。":"仍有未读取内容，请查看文件结果。");export.Enabled=true;prepare.Enabled=translate is not null&&sources.Length>0;
                TranslationWorkspaceUi.GridPalette(fileGrid);TranslationWorkspaceUi.GridPalette(textGrid);
            }
            catch(OperationCanceledException){}catch(Exception ex){if(!IsDisposed)status.Text="读取未完成："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        };
        table.SizeChanged+=(_,_)=>{int width=Math.Max(200,table.ClientSize.Width-table.Padding.Horizontal-8);scope.MaximumSize=status.MaximumSize=new(width,0);};
        FormClosed+=(_,_)=>{if(!cancellationDisposed)cancellation.Cancel();preparationFinished.TrySetResult(false);};KeyPreview=true;KeyDown+=(_,e)=>{if(e.Control&&e.KeyCode==Keys.Tab){e.SuppressKeyPress=true;tabs.Next(e.Shift);}};
        Disposed+=(_,_)=>{if(cancellationDisposed)return;cancellationDisposed=true;cancellation.Cancel();cancellation.Dispose();};
        TranslationWorkspaceUi.Prepare(this,settings??new ApiSettings());
    }
}
