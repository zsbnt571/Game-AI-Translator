using W = System.Windows;
using WC = System.Windows.Controls;
namespace ScreenshotTranslationUiTester;
internal sealed class NativeExperimentalCatalog : NativeDialogWindow
{
    private readonly CancellationTokenSource cancellation=new();
    private ExperimentalRpgCatalog.Result? result;
    // Keep the source template and its opaque values, not a previously rendered
    // sentence. A language change must not reload files, cache entries or rows.
    private readonly NativeLocalizedTextBlock status = new() { Localize=false, TextWrapping=W.TextWrapping.Wrap, Margin=new(0,4,0,12) };
    private Func<string> statusText=()=>UiLanguage.Text("正在读取资源，请稍候…");
    private void SetStatus(Func<string> render){statusText=render;status.Text=render();}
    private void ShowProgress(CatalogTranslationPreparation.Progress progress,bool complete=false)
    {
        SetStatus(()=>complete
            ?UiLanguage.Format($"已准备 {progress.Ready:N0} / {progress.Total:N0} 条 · ")+UiLanguage.Text(progress.Status)
            :UiLanguage.Format($"已准备 {progress.Ready:N0} / {progress.Total:N0} 条 · 本次保存 {progress.Added:N0} 条 · 本次失败 {progress.Failed:N0} 条\n")+UiLanguage.Text(progress.Status));
    }
    private void ShowFailure(string prefix,Exception error)
    {
        string type=error.GetType().Name;
        string diagnostic=SafeDiagnosticOutput.Redact(error.Message);
        SetStatus(()=>UiLanguage.Text(prefix)+type+": "+UiLanguage.Exact(diagnostic));
    }
    private readonly TaskCompletionSource<bool> preparationFinished=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<bool> PreparationFinished=>preparationFinished.Task;
    internal NativeExperimentalCatalog(GameInfo game,ApiSettings settings,ApiSettings translationSettings,Func<string,ApiSettings,CancellationToken,Task<string>>? translate,ExperimentalRpgCatalog.Result? initial=null,bool beforeLaunch=false,CatalogPreviewLoader? previewLoader=null):base("读取文本与准备译文 · "+game.Name,1140,790)
    {
        MinWidth=760;MinHeight=560;
        var profile=ApiSettingsSnapshot.Copy(translationSettings);Dictionary<string,string> cached=new(StringComparer.Ordinal);CancellationTokenSource? preparation=null,reading=null;
        previewLoader??=new();bool started=false;int sourceCount=0;
        var fileGrid=new NativeRecordGrid(("文件",28),("结果",14),("原文数",10),("说明",48));var textGrid=new NativeRecordGrid(("文件",18),("字段 / 行",18),("原文",32),("译文",32));
        fileGrid.LocalizeColumns(1,3);
        // Longer translated headers may need two lines even at the default
        // window size. Keep column minimums below the narrow window's budget.
        var headerText=new W.FrameworkElementFactory(typeof(WC.TextBlock));
        headerText.SetBinding(WC.TextBlock.TextProperty,new System.Windows.Data.Binding());
        headerText.SetValue(WC.TextBlock.TextWrappingProperty,W.TextWrapping.Wrap);
        var headerTemplate=new W.DataTemplate{VisualTree=headerText};
        var headerStyle=new W.Style(typeof(WC.Primitives.DataGridColumnHeader),(W.Style?)TryFindResource(typeof(WC.Primitives.DataGridColumnHeader)));
        headerStyle.Setters.Add(new W.Setter(WC.Control.HorizontalContentAlignmentProperty,W.HorizontalAlignment.Stretch));
        headerStyle.Setters.Add(new W.Setter(WC.Control.VerticalContentAlignmentProperty,W.VerticalAlignment.Center));
        fileGrid.View.ColumnHeaderStyle=headerStyle;
        fileGrid.View.ColumnHeaderHeight=double.NaN;
        double[] minimumWidths=[90,120,130,140];
        for(int i=0;i<fileGrid.View.Columns.Count;i++){fileGrid.View.Columns[i].HeaderTemplate=headerTemplate;fileGrid.View.Columns[i].MinWidth=minimumWidths[i];}
        var tabs=new WC.TabControl();tabs.Items.Add(new WC.TabItem{Header="文件读取结果",Content=fileGrid.View});tabs.Items.Add(new WC.TabItem{Header="原文与译文（前 1000 处）",Content=textGrid.View});
        W.Automation.AutomationProperties.SetAutomationId(fileGrid.View,"CatalogFiles");W.Automation.AutomationProperties.SetAutomationId(textGrid.View,"CatalogTexts");W.Automation.AutomationProperties.SetAutomationId(tabs,"CatalogTabs");
        status.Text=statusText();W.Automation.AutomationProperties.SetAutomationId(status,"CatalogStatus");var export=NativeDialogControls.Button("导出完整提取结果",()=>{});export.IsEnabled=false;var prepare=NativeDialogControls.Button("准备 / 更新译文",()=>{});prepare.IsEnabled=false;var cancel=NativeDialogControls.Button("暂停",()=>{});cancel.IsEnabled=false;
        var supplement=NativeDialogControls.Button("补充读取 / 重试",()=>{});supplement.Visibility=game.Engine=="WOLF RPG Editor"?W.Visibility.Visible:W.Visibility.Collapsed;
        W.Automation.AutomationProperties.SetAutomationId(supplement,"CatalogSupplement");W.Automation.AutomationProperties.SetAutomationId(prepare,"CatalogPrepare");W.Automation.AutomationProperties.SetAutomationId(cancel,"CatalogCancel");
        supplement.ToolTip=UiLanguage.Text("补充读取可能需要较长时间；可以暂停，已有目录和译文会保留。");
        var identity=new NativeLocalizedTextBlock{Localize=false,Text=game.Name+" · "+game.Engine,TextWrapping=W.TextWrapping.Wrap,Margin=new(0,4,0,4)};
        W.Automation.AutomationProperties.SetAutomationId(identity,"CatalogIdentity");
        var root=new WC.DockPanel();var heading=NativeDialogControls.Column(NativeDialogControls.Text("读取文本与准备译文",21),identity,NativeDialogControls.Text("准备译文会使用所选翻译方案并保存缓存。翻译启动将优先复用已保存译文，动态文字仍可能需要补译。"));WC.DockPanel.SetDock(heading,WC.Dock.Top);root.Children.Add(heading);root.Children.Add(tabs);
        void RefreshLanguage(object? sender,EventArgs e)
        {
            if(IsDisposed)return;
            Title=UiLanguage.Text("读取文本与准备译文")+" · "+game.Name;
            status.Text=statusText();
            supplement.ToolTip=UiLanguage.Text("补充读取可能需要较长时间；可以暂停，已有目录和译文会保留。");
        }
        UiLanguage.LanguageChanged+=RefreshLanguage;Loaded+=RefreshLanguage;Closed+=(_,_)=>UiLanguage.LanguageChanged-=RefreshLanguage;
        RefreshLanguage(null,EventArgs.Empty);
        bool launchSavedRequested=false;var launchSaved=NativeDialogControls.Button("使用已保存译文并启动",()=>{});
        launchSaved.Visibility=beforeLaunch?W.Visibility.Visible:W.Visibility.Collapsed;
        void FinishSavedLaunch(){preparationFinished.TrySetResult(true);Close();}
        launchSaved.Click+=(_,_)=>{launchSavedRequested=true;launchSaved.IsEnabled=false;if(preparation is null)FinishSavedLaunch();else preparation.Cancel();};
        Content=NativeDialogControls.Layout(root,NativeDialogControls.Column(status,NativeDialogControls.Row(prepare,supplement,cancel,export,launchSaved)),false);
        void UpdateActions()
        {
            if(IsDisposed)return;bool busy=preparation is not null||reading is not null;
            prepare.IsEnabled=!busy&&result is not null&&translate is not null&&sourceCount>0;
            export.IsEnabled=!busy&&result is not null;supplement.IsEnabled=!busy;cancel.IsEnabled=busy;
        }
        async Task ReadPreview(bool collect,ExperimentalRpgCatalog.Result? supplied=null)
        {
            if(reading is not null||preparation is not null||IsDisposed)return;
            reading=CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);var token=reading.Token;UpdateActions();
            SetStatus(()=>UiLanguage.Text(collect?"正在补充读取资源；可暂停，已有目录和译文会保留。":"正在读取已有资源并检查译文缓存…"));
            try
            {
                var preview=await previewLoader.LoadAsync(game,profile,supplied,collect,token);
                var rows=await Task.Run(()=>
                {
                    NativeRecordRow Make(object?[] values,bool file)
                    {
                        token.ThrowIfCancellationRequested();var row=new NativeRecordRow(4);
                        for(int i=0;i<4;i++){row.Cells[i].Value=values[i];row.Cells[i].Localized=file&&(i is 1 or 3);}
                        if(file&&values[0] is "资源目录（汇总）" or "游戏目录")row.Cells[0].Localized=true;
                        if(!file)row.Tag=values[2];return row;
                    }
                    var files=preview.Catalog.Files.Select(f=>Make([f.File,f.Status,f.Texts,f.Detail],true)).ToArray();
                    var texts=preview.Catalog.Entries.Take(1000).Select(e=>Make([e.File,e.Location,e.Text,preview.Cached.GetValueOrDefault(e.Text,"")],false)).ToArray();
                    return (files,texts);
                },token);
                token.ThrowIfCancellationRequested();if(IsDisposed)return;
                result=preview.Catalog;cached=preview.Cached;sourceCount=preview.Sources.Length;
                fileGrid.Rows.Replace(rows.files);textGrid.Rows.Replace(rows.texts);
                var read=preview.Catalog;
                SetStatus(()=>(beforeLaunch?UiLanguage.Text("准备完成后继续启动游戏；关闭此窗口会取消本次启动。")+"\n":"")+UiLanguage.Format($"{read.Engine} · 原文出现 {read.Entries.Length:N0} 处 · 去重 {preview.Unique:N0} 条 · 本方案待翻译范围 {preview.Sources.Length:N0} 条\n本页精确匹配的可用缓存 {preview.Ready:N0} 条；运行时还可复用模板和名称，数量与运行页口径不同。\n方案：{profile.Model} → {profile.TargetLanguage} · ")+UiLanguage.Text(read.Complete?"已支持字段检查完成。":"仍有未读取内容，请查看文件结果。")+
                    (game.Engine=="WOLF RPG Editor"&&!collect?"\n"+UiLanguage.Text("初次读取不会自动启动补充收集；未读取内容可点击“补充读取 / 重试”。"):""));
            }
            catch(OperationCanceledException){if(!IsDisposed&&!cancellation.IsCancellationRequested)SetStatus(()=>UiLanguage.Text("已暂停读取。已有目录和译文保留，可继续准备或重试。"));}
            catch(Exception ex){if(!IsDisposed)ShowFailure("读取未完成：",ex);}
            finally{reading.Dispose();reading=null;UpdateActions();}
        }
        supplement.Click+=async(_,_)=>await ReadPreview(true);
        async Task RefreshTranslations()
        {
            var loaded=await Task.Run(()=>RpgTranslationCache.Effective(game.ExePath,profile,CatalogTranslationPreparation.Syntax(game.Engine)));if(IsDisposed)return;cached=loaded;
            foreach(NativeRecordRow row in textGrid.Rows)if(row.Tag is string source)row.Cells[3].Value=cached.GetValueOrDefault(source,"");
        }
        cancel.Click+=(_,_)=>{preparation?.Cancel();reading?.Cancel();cancel.IsEnabled=false;SetStatus(()=>UiLanguage.Text("正在暂停；已完成的译文会保留。"));};
        prepare.Click+=async(_,_)=>
        {
            if(result is null||translate is null||preparation is not null||reading is not null)return;
            preparation=CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);UpdateActions();
            bool finished=false;
            void Progress(CatalogTranslationPreparation.Progress p)
            {
                // Reports arrive from a worker. IsLoaded and other dependency
                // properties belong to the UI dispatcher, including in guards.
                if(IsDisposed||cancellation.IsCancellationRequested||Dispatcher.HasShutdownStarted)return;
                try{Dispatcher.BeginInvoke((Action)(()=>{if(!finished&&!IsDisposed&&!cancellation.IsCancellationRequested)ShowProgress(p);}));}
                catch(InvalidOperationException)when(IsDisposed||cancellation.IsCancellationRequested||Dispatcher.HasShutdownStarted){}
            }
            try
            {
                var final=await Task.Run(()=>CatalogTranslationPreparation.PrepareAsync(game.ExePath,result,profile,translate,Progress,preparation.Token));
                finished=true;
                if(!IsDisposed){await RefreshTranslations();ShowProgress(final,true);}
                if(beforeLaunch&&final.Ready==final.Total&&final.Failed==0&&!cancellation.IsCancellationRequested&&!IsDisposed){preparationFinished.TrySetResult(true);Close();}
            }
            catch(OperationCanceledException){finished=true;if(!IsDisposed&&!cancellation.IsCancellationRequested){try{await RefreshTranslations();if(!IsDisposed)SetStatus(()=>UiLanguage.Text("已暂停。完成的译文已保存，再次准备会复用缓存。"));}catch(Exception ex){if(!IsDisposed)ShowFailure("已暂停；刷新译文失败：",ex);}}}
            catch(Exception ex){finished=true;if(!IsDisposed)ShowFailure("准备未完成：",ex);}
            finally{preparation.Dispose();preparation=null;UpdateActions();if(!IsDisposed&&launchSavedRequested)FinishSavedLaunch();}
        };
        export.Click+=(_,_)=>{if(result is null)return;using var dialog=new SaveFileDialog{Filter=UiLanguage.Text("文本与译文记录 (*.json)|*.json"),FileName="Fusion-文本与译文.json"};if(dialog.ShowDialog(this)!=System.Windows.Forms.DialogResult.OK)return;try{PortableDataStorage.WriteJson(dialog.FileName,new{Version=1,result.Engine,result.Complete,result.Boundary,TargetLanguage=profile.TargetLanguage,Entries=result.Entries.Select(e=>new{e.File,e.Location,e.Text,Translation=cached.GetValueOrDefault(e.Text,"")}),result.Files});string boundary=result.Boundary;SetStatus(()=>UiLanguage.Text("已导出。")+UiLanguage.Text(boundary));}catch(Exception ex){ShowFailure("",ex);}};
        Loaded+=async(_,_)=>{if(started)return;started=true;await ReadPreview(false,initial);};
        Closed+=(_,_)=>{cancellation.Cancel();preparationFinished.TrySetResult(false);};
    }
}
