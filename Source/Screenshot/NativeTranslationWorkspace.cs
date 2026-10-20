using System.Text.Json;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
namespace ScreenshotTranslationUiTester;
internal sealed partial class NativeTranslationWorkspace : NativeDialogWindow
{
    private sealed record Row(string Source,string Text,string State,string Problem);
    private readonly GameInfo game;
    private readonly ApiSettings settings;
    private readonly TranslationWorkspaceStore store;
    private readonly EmbeddedTextSyntax syntax;
    private readonly bool unreal;
    private readonly NativeRecordGrid grid=new(("原文",43),("当前译文",43),("状态",14)),terms=new(("原词",27),("统一译名",27),("类型",14),("状态",32)),history=new(("时间",32),("操作",50),("原句数量",18));
    private readonly WC.TextBox search=NativeDialogControls.Input(),original=NativeDialogControls.Input("",true,true),translated=NativeDialogControls.Input("",true),termSource=NativeDialogControls.Input(),termText=NativeDialogControls.Input();
    private readonly WC.ComboBox filter=NativeDialogControls.Choice(["全部译文","手动更正","待处理问题","等待重译"]);
    private readonly WC.ComboBox termKind=NativeDialogControls.Choice(GameTerminologyClassification.Categories);
    private readonly WC.TextBlock termCount=NativeDialogControls.Text("");
    private readonly WC.CheckBox keepTerm=new(){Content="保留原文（不翻译这个名称）",VerticalAlignment=W.VerticalAlignment.Center};
    private readonly WC.TextBox termEvidence=NativeDialogControls.Input("选择名称可查看出现位置、原句与译名冲突。",true,true),termSearch=NativeDialogControls.Input();
    private GameTerminology.View[] termRows=[];
    private readonly WC.TextBlock feedback=NativeDialogControls.Text("正在读取译文…"),pageLabel=NativeDialogControls.Text(""),entryLabel=NativeDialogControls.Text("选择一条译文进行更正");
    private readonly System.Windows.Threading.DispatcherTimer debounce=new(){Interval=TimeSpan.FromMilliseconds(250)};
    private readonly WC.TabControl tabs=new();
    private readonly WC.Button previous,next,save,restore,retry;
    private Dictionary<string,string> automatic=new(StringComparer.Ordinal);
    private Row[] rows=[],filtered=[];
    private int page,lastTab;
    private bool busy,selecting,refreshing,changingTab;
    private string selectedSource="",savedEditor="";
    private string savedTermSource="",savedTermText="";
    private bool savedKeepTerm,selectingTerm;
    private bool TermDirty=>termSource.Text!=savedTermSource||termText.Text!=savedTermText||(keepTerm.IsChecked==true)!=savedKeepTerm;
    private void RememberTermEdit(){savedTermSource=termSource.Text;savedTermText=termText.Text;savedKeepTerm=keepTerm.IsChecked==true;}
    private const int PageSize=200;
    internal NativeTranslationWorkspace(GameInfo game,ApiSettings settings):base("译文管理 · "+game.Name,1120,820)
    {
        this.game=game;this.settings=ApiSettingsSnapshot.Copy(settings);store=TranslationWorkspaceStore.For(game.ExePath,settings);
        grid.LocalizeColumns(2);terms.LocalizeColumns(2,3);history.LocalizeColumns(1);
        syntax=EmbeddedGameAdapters.TextSyntax(game);unreal=game.AdapterId=="unreal";MinWidth=800;MinHeight=640;
        previous=Action("上一页",()=>{if(page>0&&DiscardEdit()){page--;RenderPage();}return Task.CompletedTask;},"back");
        next=Action("下一页",()=>{if((page+1)*PageSize<filtered.Length&&DiscardEdit()){page++;RenderPage();}return Task.CompletedTask;},"next");
        save=Action("保存更正",SaveAsync,"check",true);restore=Action("恢复自动译文",RestoreAsync,"refresh");retry=Action("重译所选",RetryAsync,"translate");
        BuildWorkspace();
        grid.SelectionChanged+=(_,_)=>SelectRow();terms.SelectionChanged+=(_,_)=>SelectTerm();
        termSearch.TextChanged+=(_,_)=>FilterTerms();termKind.SelectionChanged+=(_,_)=>FilterTerms();keepTerm.Checked+=(_,_)=>termText.IsEnabled=false;keepTerm.Unchecked+=(_,_)=>termText.IsEnabled=true;
        void RefreshTermLanguage(object? sender,EventArgs e)
        {
            if(IsDisposed)return;
            RefreshTermCount();RefreshTermEvidence();RefreshHeading();
        }
        UiLanguage.LanguageChanged+=RefreshTermLanguage;Closed+=(_,_)=>UiLanguage.LanguageChanged-=RefreshTermLanguage;
        search.TextChanged+=(_,_)=>{debounce.Stop();debounce.Start();};debounce.Tick+=(_,_)=>{debounce.Stop();ApplyFilter();};filter.SelectionChanged+=(_,_)=>ApplyFilter();
        translated.TextChanged+=(_,_)=>{if(!refreshing&&!selecting&&selectedSource.Length>0&&translated.Text!=savedEditor)entryLabel.Text=UiLanguage.Text("尚未保存 · Ctrl+S 保存更正");};
        tabs.SelectionChanged+=(_,e)=>{if(!ReferenceEquals(e.Source,tabs)||changingTab)return;changingTab=true;try{if(busy||!DiscardEdit())tabs.SelectedIndex=lastTab;else lastTab=tabs.SelectedIndex;}finally{changingTab=false;}};
        Loaded+=async(_,_)=>await Run(RefreshAsync);Closing+=(_,e)=>{if(busy){e.Cancel=true;feedback.Text=UiLanguage.Text("正在保存或读取，请稍候。");}else if(!DiscardEdit())e.Cancel=true;};Closed+=(_,_)=>debounce.Stop();
        PreviewKeyDown+=async(_,e)=>{if(WI.Keyboard.Modifiers!=WI.ModifierKeys.Control)return;if(e.Key==WI.Key.F){if(!busy&&DiscardEdit()){tabs.SelectedIndex=0;search.Focus();}e.Handled=true;}else if(e.Key==WI.Key.S&&tabs.SelectedIndex==0){e.Handled=true;await Run(SaveAsync);}};
    }
    private WC.Button Action(string caption,Func<Task> action,string icon="",bool primary=false) { var button=WpfThemeResources.Button(caption,icon,()=>{},primary);button.MinHeight=38;button.Margin=new(0);button.Click+=async(_,_)=>await Run(action);return button; }
    private static WC.Grid Table(params (W.UIElement View,bool Fill)[] rows){var result=new WC.Grid();for(int i=0;i<rows.Length;i++){result.RowDefinitions.Add(new(){Height=rows[i].Fill?new(1,W.GridUnitType.Star):W.GridLength.Auto});WC.Grid.SetRow(rows[i].View,i);result.Children.Add(rows[i].View);}return result;}
    private void UpdateActions(){previous.IsEnabled=!busy&&page>0;next.IsEnabled=!busy&&(page+1)*PageSize<filtered.Length;save.IsEnabled=restore.IsEnabled=retry.IsEnabled=!busy&&selectedSource.Length>0;}
    private async Task Run(Func<Task> work)
    {
        if(busy||IsDisposed)return;busy=true;tabs.IsEnabled=false;
        try{await work();}catch(Exception ex){if(!IsDisposed){feedback.Text="未完成："+SafeDiagnosticOutput.ExceptionSummary(ex);entryLabel.Text=feedback.Text;}}
        finally{busy=false;if(!IsDisposed){tabs.IsEnabled=true;UpdateActions();}}
    }
    private bool DiscardEdit()
    {
        if(translated.Text==savedEditor&&!TermDirty)return true;
        if(AppDialog.Show(this,"尚未保存","当前译文或统一名称尚未保存。确定放弃这次编辑？",AppDialogKind.Confirmation)!=System.Windows.Forms.DialogResult.Yes)return false;
        translated.Text=savedEditor;termSource.Text=savedTermSource;termText.Text=savedTermText;keepTerm.IsChecked=savedKeepTerm;return true;
    }
    private async Task RefreshAsync()
    {
        if(!DiscardEdit())return;string remember=selectedSource;feedback.Text="正在读取译文…";
        var loaded=await Task.Run(()=>
        {
            var a=RpgTranslationCache.Load(game.ExePath,settings).Entries;
            foreach(var p in RpgTranslationCache.LoadDisplay(game.ExePath,settings).Entries)a.TryAdd(p.Key,p.Value);
            var d=store.Snapshot(true);var issues=store.Issues().GroupBy(i=>i.Source,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.First().Reason,StringComparer.Ordinal);
            var values=a.Keys.Concat(d.Manual.Keys).Concat(issues.Keys).Concat(d.Requests.Keys).Distinct(StringComparer.Ordinal).Select(s=>
            {
                var value=d.Manual.GetValueOrDefault(s)??a.GetValueOrDefault(s,"");string problem=issues.GetValueOrDefault(s,"");
                if(value.Length>0&&!TranslationWorkspaceStore.Valid(s,value,syntax,unreal))problem="变量、控制符或内容校验未通过；请更正后保存。";
                return new Row(s,value,problem.Length>0?"待处理":d.Manual.ContainsKey(s)?"手动更正":d.Requests.ContainsKey(s)?"等待重译":"自动译文",problem);
            }).OrderBy(r=>r.Source,StringComparer.Ordinal).ToArray();
            var names=GameTerminology.For(store);names.ObserveSavedOrigins(game.ExePath,settings);names.Observe(values.Select(v=>v.Source));
            d=store.Snapshot();
            return(a,d,values,names:names.Rows(a));
        });
        if(IsDisposed)return;automatic=loaded.a;rows=loaded.values;refreshing=true;
        try
        {
            termRows=loaded.names;FilterTerms();
            history.Rows.Clear();foreach(var e in loaded.d.History.AsEnumerable().Reverse())history.Rows.Add(e.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),e.Action,e.Changes.Length);
            selectedSource="";savedEditor="";translated.Text="";original.Text="";
        }
        finally{refreshing=false;}
        ApplyFilter(remember);feedback.Text=$"已读取 {rows.Length:N0} 条 · 手动更正 {loaded.d.Manual.Count:N0} 条 · 术语 {loaded.d.Terms.Count:N0} 项。显示 {settings.Model} 的缓存；游戏退出或保存缓存后可刷新。";
    }
    private void ApplyFilter(string preferred="")
    {
        if(refreshing)return;if(!DiscardEdit())return;string q=search.Text.Trim();int kind=filter.SelectedIndex;
        var doc=store.Snapshot();filtered=rows.Where(r=>(q.Length==0||r.Source.Contains(q,StringComparison.OrdinalIgnoreCase)||r.Text.Contains(q,StringComparison.OrdinalIgnoreCase))&&(kind<=0||kind==1&&doc.Manual.ContainsKey(r.Source)||kind==2&&r.Problem.Length>0||kind==3&&doc.Requests.ContainsKey(r.Source))).ToArray();int index=Array.FindIndex(filtered,r=>r.Source==preferred);page=index<0?0:index/PageSize;RenderPage(preferred);
    }
    private void RenderPage(string preferred="")
    {
        if(!DiscardEdit())return;selecting=true;
        try{grid.Rows.Clear();foreach(var r in filtered.Skip(page*PageSize).Take(PageSize)){int i=grid.Rows.Add(Preview(r.Source),Preview(r.Text),r.State);grid.Rows[i].Tag=r;}
            pageLabel.Text=$"{page+1} / {Math.Max(1,(filtered.Length+PageSize-1)/PageSize)} 页 · {filtered.Length:N0} 条";if(grid.Rows.Count>0)grid.CurrentCell=grid.Rows.Cast<NativeRecordRow>().FirstOrDefault(r=>(r.Tag as Row)?.Source==preferred)?.Cells[0]??grid.Rows[0].Cells[0];}
        finally{selecting=false;}SelectRow();UpdateActions();
    }
    private static string Preview(string text)=>text.Length>220?text[..220].Replace('\n',' ')+'…':text.Replace('\n',' ');
    private void SelectRow()
    {
        if(selecting||refreshing)return;var row=grid.CurrentRow?.Tag as Row;if(row?.Source==selectedSource)return;
        if(!DiscardEdit()){selecting=true;foreach(NativeRecordRow r in grid.Rows)if((r.Tag as Row)?.Source==selectedSource){grid.CurrentCell=r.Cells[0];break;}selecting=false;return;}
        selectedSource=row?.Source??"";original.Text=selectedSource;translated.Text=savedEditor=row?.Text??"";entryLabel.Text=row is null?"没有符合筛选的译文":row.Problem.Length>0?row.Problem:row.State+" · 保存后优先使用更正译文";UpdateActions();
    }
    private async Task SaveAsync()
    {if(selectedSource.Length==0)return;string s=selectedSource,t=translated.Text;await Task.Run(()=>store.SetManual(s,t,syntax,unreal));savedEditor=t;await RefreshAsync();feedback.Text="更正已保存。运行中的通用 / RPG / Ren’Py 翻译会刷新已捕获原句；下次启动也会读取。";}
    private async Task RestoreAsync()
    {if(selectedSource.Length==0||!DiscardEdit())return;string s=selectedSource;await Task.Run(()=>store.SetManual(s,null,syntax,unreal));savedEditor=translated.Text;await RefreshAsync();feedback.Text="已恢复使用自动译文。若该原句尚无自动译文，游戏重新读取后会补译。";}
    private async Task UndoAsync()
    {if(!DiscardEdit())return;bool ok=await Task.Run(store.Undo);savedEditor=translated.Text;await RefreshAsync();feedback.Text=ok?"已撤销上次译文修改。":"没有可撤销的译文修改。";}
    private Task RetryAsync()=>RequestAsync(selectedSource.Length>0?[selectedSource]:[]);
    private Task RetryPageAsync()=>RequestAsync(filtered.Skip(page*PageSize).Take(PageSize).Where(r=>r.Problem.Length>0).Select(r=>r.Source).ToArray());
    private async Task RequestAsync(string[] sources)
    {int count=await Task.Run(()=>store.RequestRetry(sources));await RefreshAsync();feedback.Text=$"已登记重译 {count} 条。需要开启游戏翻译并读取到这些原句；原有译文在新结果通过检查前保留。手动更正不参与重译。";}
    private async Task SaveTermAsync()
    {
        string s=termSource.Text.Trim(),t=keepTerm.IsChecked==true?s:termText.Text.Trim();
        string[] affected=rows.Where(r=>TranslationGlossary.Contains(r.Source,s)).Select(r=>r.Source).ToArray();
        int count=await Task.Run(()=>{GameTerminology.For(store).Set(s,t);return store.RequestRetry(affected);});
        RememberTermEdit();
        await RefreshAsync();feedback.Text=$"统一译名已保存，已登记 {count:N0} 条相关自动译文更新。预译与实时翻译共用此名称；人工更正保留，冲突可在下方查看。";
    }
    private async Task DeleteTermAsync()
    {string s=termSource.Text;await Task.Run(()=>GameTerminology.For(store).Remove(s));RememberTermEdit();await RefreshAsync();feedback.Text="已移除统一规则；此词不再自动收录，之后按所在句子的语境翻译。";}
    private Task RetryTermAsync()
    {string s=termSource.Text.Trim();return RequestAsync(s.Length==0?[]:rows.Where(r=>TranslationGlossary.Contains(r.Source,s)).Select(r=>r.Source).Take(1000).ToArray());}
    private async Task RetryNamesAsync()
    {GameTerminology.For(store).RetryPending();await RefreshAsync();feedback.Text="已安排重试待准备名称。开启游戏翻译或读取文本后会继续准备；人工更正保留。";}
    private void FilterTerms()
    {
        if(!refreshing&&!DiscardEdit())return;
        string query=termSearch.Text.Trim(),remember=termSource.Text;
        string kind=GameTerminologyClassification.Categories[Math.Clamp(termKind.SelectedIndex,0,GameTerminologyClassification.Categories.Length-1)];
        selectingTerm=true;
        try
        {
            terms.Rows.Clear();
            foreach(var item in termRows.Where(t=>(kind=="全部"||t.Kind==kind)&&(query.Length==0||t.Source.Contains(query,StringComparison.OrdinalIgnoreCase)||t.Translation.Contains(query,StringComparison.OrdinalIgnoreCase)||t.Kind.Contains(query,StringComparison.OrdinalIgnoreCase))))
            {
                string state=item.KeepOriginal?"保留原文":item.Translation.Length==0?"待准备统一译名":"统一译名";
                if(item.Conflicts.Length>0)state+=$" · {item.Conflicts.Length} 处需核对";
                int i=terms.Rows.Add(item.Source,item.Translation,item.Kind,state);terms.Rows[i].Tag=item;
            }
            if(terms.Rows.Count>0)terms.CurrentCell=terms.Rows.Cast<NativeRecordRow>().FirstOrDefault(r=>(r.Tag as GameTerminology.View)?.Source==remember)?.Cells[0]??terms.Rows[0].Cells[0];
        }
        finally{selectingTerm=false;}
        RefreshTermCount();
        if(terms.Rows.Count==0)
        {termSource.Text=termText.Text="";keepTerm.IsChecked=false;RememberTermEdit();termEvidence.Text=UiLanguage.Text("此分类尚未发现匹配名称。读取游戏文本后会继续收录，也可以手动添加。");}
        SelectTerm();
    }
    private void SelectTerm()
    {
        if(selectingTerm)return;
        if(terms.CurrentRow?.Tag is not GameTerminology.View value)return;
        if(!refreshing&&TermDirty&&!DiscardEdit())
        {
            selectingTerm=true;try{var before=terms.Rows.Cast<NativeRecordRow>().FirstOrDefault(r=>(r.Tag as GameTerminology.View)?.Source==savedTermSource);if(before is not null)terms.CurrentCell=before.Cells[0];}finally{selectingTerm=false;}return;
        }
        termSource.Text=value.Source;termText.Text=value.KeepOriginal?"":value.Translation;keepTerm.IsChecked=value.KeepOriginal;
        RememberTermEdit();
        RefreshTermEvidence();
    }
    private void RefreshTermCount()
    {
        string kind=GameTerminologyClassification.Categories[Math.Clamp(termKind.SelectedIndex,0,GameTerminologyClassification.Categories.Length-1)];
        ((NativeLocalizedTextBlock)termCount).Localize=false;
        termCount.Text=UiLanguage.Format($"{UiLanguage.Text(kind)} · {terms.Rows.Count:N0} / {termRows.Length:N0} 项");
    }
    private void RefreshTermEvidence()
    {
        // Refresh software-owned labels only. Never reload editors on a language
        // change: an unsaved name or translation must remain exactly as typed.
        if(terms.CurrentRow?.Tag is not GameTerminology.View value)
        {
            termEvidence.Text=UiLanguage.Text("此分类尚未发现匹配名称。读取游戏文本后会继续收录，也可以手动添加。");
            return;
        }
        string Conflict(string text)
        {
            foreach(var prefix in new[]{"人工更正保留：","旧自动译名：","同一名称的其他语言："})
                if(text.StartsWith(prefix,StringComparison.Ordinal))return UiLanguage.Text(prefix)+text[prefix.Length..];
            return text;
        }
        string text=string.Join("\n\n",value.Examples.Select(e=>e.Sentence+"\n"+UiLanguage.Text("位置：")+e.Location).Concat(value.Conflicts.Select(Conflict)));
        if(text.Length==0)text=UiLanguage.Text("此项为手动添加的统一名称。普通词可不收录，避免不同句子被强行使用同一个译法。");
        int start=termEvidence.SelectionStart,length=termEvidence.SelectionLength;double offset=termEvidence.VerticalOffset;
        termEvidence.Text=text;
        termEvidence.Select(Math.Min(start,text.Length),Math.Min(length,Math.Max(0,text.Length-start)));termEvidence.ScrollToVerticalOffset(offset);
    }
    private async Task ExportAsync()
    {using var dialog=new SaveFileDialog{Filter=UiLanguage.Text("Fusion 译文文件 (*.json)|*.json"),FileName="Fusion-译文-"+store.Language+".json"};if(dialog.ShowDialog(this)!=System.Windows.Forms.DialogResult.OK)return;await Task.Run(()=>store.Export(dialog.FileName,automatic));feedback.Text="译文与术语已导出；文件不含接口密钥、存档或游戏资源。";}
    private async Task ImportAsync()
    {
        if(!DiscardEdit())return;using var dialog=new OpenFileDialog{Filter=UiLanguage.Text("Fusion 译文文件 (*.json)|*.json")};if(dialog.ShowDialog(this)!=System.Windows.Forms.DialogResult.OK)return;
        var preview=await Task.Run(()=>store.Preview(TranslationWorkspaceStore.ReadTransfer(dialog.FileName),automatic,syntax,unreal));
        using var review=new NativeTranslationImportReview(preview);
        var answer=review.ShowDialog(this);if(answer is not (System.Windows.Forms.DialogResult.Yes or System.Windows.Forms.DialogResult.No))return;
        int count=await Task.Run(()=>store.Import(preview.Data,automatic,syntax,answer==System.Windows.Forms.DialogResult.Yes,unreal));await RefreshAsync();feedback.Text=$"导入完成：保存 {count} 条译文变更。尚未在游戏读取到的原句不会直接获得写回权限。";
    }
}
internal sealed class NativeTranslationImportReview : NativeDialogWindow
{
    internal NativeTranslationImportReview(TranslationWorkspaceStore.ImportPreview preview):base("导入预览",800,560)
    {
        var text=NativeDialogControls.Input(preview.ConflictSources.Length>0?string.Join("\n\n",preview.ConflictSources):"没有冲突，可以保留现有译文并导入新增内容。",true,true);text.MinHeight=200;
        if(preview.ConflictSources.Length==0)
        {
            void RefreshEmpty(object? sender,EventArgs e)=>text.Text=UiLanguage.Text("没有冲突，可以保留现有译文并导入新增内容。");
            RefreshEmpty(null,EventArgs.Empty);UiLanguage.LanguageChanged+=RefreshEmpty;Closed+=(_,_)=>UiLanguage.LanguageChanged-=RefreshEmpty;
        }
        Content=NativeDialogControls.Layout(NativeDialogControls.Column(NativeDialogControls.Text($"新增 {preview.New:N0} · 相同 {preview.Same:N0} · 冲突 {preview.Conflicts:N0} · 无效 {preview.Invalid:N0}",18),NativeDialogControls.Text("无效条目会跳过，导入的变更作为手动更正保存。下方列出冲突原句与术语（最多 200 项）。"),text),NativeDialogControls.Row(ResultButton("保留现有冲突项",System.Windows.Forms.DialogResult.No,true),ResultButton("使用导入的冲突项",System.Windows.Forms.DialogResult.Yes),ResultButton("取消",System.Windows.Forms.DialogResult.Cancel)));
    }
}
