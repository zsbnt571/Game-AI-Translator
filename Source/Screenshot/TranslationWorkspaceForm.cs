using System.Text.Json;
namespace ScreenshotTranslationUiTester;

internal sealed partial class TranslationWorkspaceForm:Form
{
    private sealed record Row(string Source,string Text,string State,string Problem);
    private readonly GameInfo game;
    private readonly ApiSettings settings;
    private readonly TranslationWorkspaceStore store;
    private readonly EmbeddedTextSyntax syntax;
    private readonly bool unreal;
    private readonly WorkspaceDataGrid grid=TranslationWorkspaceUi.Table("译文列表"),terms=TranslationWorkspaceUi.Table("术语列表"),history=TranslationWorkspaceUi.Table("修改记录");
    private readonly TextBox search=new(){Width=240,PlaceholderText=UiLanguage.Text("原文或译文"),AccessibleName="搜索原文或译文"};
    private readonly GamePlanBox filter=new(){Width=150,Margin=new(0,2,12,2),DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="筛选译文"};
    private readonly TextBox original=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill};
    private readonly TextBox translated=new(){Multiline=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,AcceptsReturn=true};
    private readonly TextBox termSource=new(){Width=220,PlaceholderText=UiLanguage.Text("术语原文"),AccessibleName="术语原文"};
    private readonly TextBox termText=new(){Width=220,PlaceholderText=UiLanguage.Text("统一译名"),AccessibleName="统一译名"};
    private readonly Label feedback=new(){AutoSize=true,Padding=new(6),Text="正在读取译文…"};
    private readonly Label pageLabel=new(){AutoSize=true,Padding=new(8)};
    private readonly Label entryLabel=new(){AutoSize=true,Padding=new(4),Text="选择一条译文进行更正"};
    private readonly System.Windows.Forms.Timer debounce=new(){Interval=250};
    private Dictionary<string,string> automatic=new(StringComparer.Ordinal);
    private Row[] rows=[],filtered=[];
    private int page;
    private bool busy,selecting,refreshing;
    private string selectedSource="",savedEditor="";
    private const int PageSize=200;
    internal TranslationWorkspaceForm(GameInfo game,ApiSettings settings)
    {
        this.game=game;this.settings=ApiSettingsSnapshot.Copy(settings);store=TranslationWorkspaceStore.For(game.ExePath,settings);
        syntax=EmbeddedGameAdapters.TextSyntax(game);
        unreal=game.AdapterId=="unreal";
        BuildUi();
    }
    private async Task Run(Func<Task> work)
    {
        if(busy)return;busy=true;UseWaitCursor=true;
        List<Control> controls=[];void Pause(Control p){foreach(Control c in p.Controls){if(c is System.Windows.Forms.Button or TextBox or ComboBox or DataGridView){if(c.Enabled){controls.Add(c);c.Enabled=false;}}else Pause(c);}}Pause(this);
        try{await work();}catch(Exception ex){feedback.Text="未完成："+SafeDiagnosticOutput.ExceptionSummary(ex);entryLabel.Text=feedback.Text;}
        finally{foreach(var control in controls)if(!control.IsDisposed)control.Enabled=true;busy=false;UseWaitCursor=false;UpdateActions();}
    }
    private bool DiscardEdit()
    {
        if(translated.Text==savedEditor)return true;
        if(AppDialog.Show(this,"尚未保存","当前译文尚未保存。确定放弃这次编辑？",AppDialogKind.Confirmation)!=DialogResult.Yes)return false;
        translated.Text=savedEditor;return true;
    }
    private async Task RefreshAsync()
    {
        if(!DiscardEdit())return;string remember=selectedSource;feedback.Text="正在读取译文…";
        var loaded=await Task.Run(()=>
        {
            var native=RpgTranslationCache.Load(game.ExePath,settings);var a=native.Entries;
            foreach(var p in RpgTranslationCache.LoadDisplay(game.ExePath,settings).Entries)a.TryAdd(p.Key,p.Value);
            var d=store.Snapshot(true);
            if(syntax==EmbeddedTextSyntax.Renpy)
                foreach(var p in GameDialogueContextCache.Load(native.Path+".dialogue-context-v1.json").ForEditor(settings,d.Terms))a[p.Key]=p.Value;
            var issues=store.Issues().GroupBy(i=>i.Source,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.First().Reason,StringComparer.Ordinal);
            var values=a.Keys.Concat(d.Manual.Keys).Concat(issues.Keys).Concat(d.Requests.Keys).Distinct(StringComparer.Ordinal).Select(s=>
            {
                var value=d.Manual.GetValueOrDefault(s)??a.GetValueOrDefault(s,"");string problem=issues.GetValueOrDefault(s,"");
                if(value.Length>0&&!TranslationWorkspaceStore.Valid(s,value,syntax,unreal))problem="变量、控制符或内容校验未通过；请更正后保存。";
                return new Row(s,value,problem.Length>0?"待处理":d.Manual.ContainsKey(s)?"手动更正":d.Requests.ContainsKey(s)?"等待重译":"自动译文",problem);
            }).OrderBy(r=>r.Source,StringComparer.Ordinal).ToArray();return(a,d,values);
        });
        if(IsDisposed)return;automatic=loaded.a;rows=loaded.values;refreshing=true;
        try
        {
            terms.Rows.Clear();foreach(var p in loaded.d.Terms.OrderBy(p=>p.Key,StringComparer.Ordinal)){int i=terms.Rows.Add(p.Key,p.Value);terms.Rows[i].Tag=p;}
            history.Rows.Clear();foreach(var e in loaded.d.History.AsEnumerable().Reverse())history.Rows.Add(e.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),e.Action,e.Changes.Length);
            selectedSource="";savedEditor="";translated.Text="";original.Text="";
        }
        finally{refreshing=false;}
        foreach(var table in new[]{grid,terms,history})TranslationWorkspaceUi.GridPalette(table);
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
            pageLabel.Text=$"{page+1} / {Math.Max(1,(filtered.Length+PageSize-1)/PageSize)} 页 · {filtered.Length:N0} 条";if(grid.Rows.Count>0)grid.CurrentCell=grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r=>(r.Tag as Row)?.Source==preferred)?.Cells[0]??grid.Rows[0].Cells[0];}
        finally{selecting=false;}SelectRow();UpdateActions();
    }
    private static string Preview(string text)=>text.Length>220?text[..220].Replace('\n',' ')+'…':text.Replace('\n',' ');
    private void SelectRow()
    {
        if(selecting||refreshing)return;var row=grid.CurrentRow?.Tag as Row;if(row?.Source==selectedSource)return;
        if(!DiscardEdit()){selecting=true;foreach(DataGridViewRow r in grid.Rows)if((r.Tag as Row)?.Source==selectedSource){grid.CurrentCell=r.Cells[0];break;}selecting=false;return;}
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
    {string s=termSource.Text.Trim(),t=termText.Text.Trim();await Task.Run(()=>{GameTerminology.For(store).Set(s,t);store.RequestRetry(rows.Where(r=>TranslationGlossary.Contains(r.Source,s)).Select(r=>r.Source));});await RefreshAsync();feedback.Text="统一译名已保存，相关自动译文会重新准备；人工更正保留。原词和译名相同表示保留原文。";}
    private async Task DeleteTermAsync()
    {string s=termSource.Text;await Task.Run(()=>GameTerminology.For(store).Remove(s));await RefreshAsync();}
    private Task RetryTermAsync()
    {string s=termSource.Text.Trim();return RequestAsync(s.Length==0?[]:rows.Where(r=>TranslationGlossary.Contains(r.Source,s)).Select(r=>r.Source).Take(1000).ToArray());}
    private async Task ExportAsync()
    {using var dialog=new SaveFileDialog{Filter=UiLanguage.Text("Fusion 译文文件 (*.json)|*.json"),FileName="Fusion-译文-"+store.Language+".json"};if(dialog.ShowDialog(this)!=DialogResult.OK)return;await Task.Run(()=>store.Export(dialog.FileName,automatic));feedback.Text="译文与术语已导出；文件不含接口密钥、存档或游戏资源。";}
    private async Task ImportAsync()
    {
        if(!DiscardEdit())return;using var dialog=new OpenFileDialog{Filter=UiLanguage.Text("Fusion 译文文件 (*.json)|*.json")};if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        var preview=await Task.Run(()=>store.Preview(TranslationWorkspaceStore.ReadTransfer(dialog.FileName),automatic,syntax,unreal));
        using var review=new TranslationImportReviewForm(preview,settings);
        var answer=ModalSafety.Show(this,review);if(answer is not (DialogResult.Yes or DialogResult.No))return;
        int count=await Task.Run(()=>store.Import(preview.Data,automatic,syntax,answer==DialogResult.Yes,unreal));await RefreshAsync();feedback.Text=$"导入完成：保存 {count} 条译文变更。尚未在游戏读取到的原句不会直接获得写回权限。";
    }
}
