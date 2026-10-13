namespace ScreenshotTranslationUiTester;

internal sealed partial class TranslationWorkspaceForm
{
    private readonly TranslationWorkspaceTabs tabs=new();
    private GameActionButton previous=null!,next=null!,save=null!,restore=null!,retry=null!;
    private void BuildUi()
    {
        SuspendLayout();AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new(96,96);
        UiLanguage.Preserve(this);Text=UiLanguage.Text("译文管理")+" · "+game.Name;StartPosition=FormStartPosition.CenterParent;Size=new(1120,820);MinimumSize=new(800,640);
        Font=new Font("Microsoft YaHei UI",10);
        var root=TranslationWorkspaceUi.Column(new(SizeType.AutoSize),new(SizeType.AutoSize),new(SizeType.Percent,100),new(SizeType.AutoSize));
        root.Padding=new(20,12,20,12);Controls.Add(root);
        var title=TranslationWorkspaceUi.Label("译文管理");title.Font=new(Font.FontFamily,14,FontStyle.Bold);title.Margin=new(0,0,0,4);root.Controls.Add(title,0,0);
        var context=TranslationWorkspaceUi.Label(game.Name+"  ·  "+UiTargetLanguageDisplay.DisplayFromCode(settings.TargetLanguage)+"  ·  "+(TranslationRoute.GenericTest(game.ExePath)?"通用测试独立缓存":"当前游戏译文"),true);root.Controls.Add(context,0,1);
        UiLanguage.Preserve(context);
        void RefreshContext(object? sender,EventArgs e)
        {
            Text=UiLanguage.Text("译文管理")+" · "+game.Name;
            context.Text=game.Name+"  ·  "+UiLanguage.Text(UiTargetLanguageDisplay.DisplayFromCode(settings.TargetLanguage))+"  ·  "+UiLanguage.Text(TranslationRoute.GenericTest(game.ExePath)?"通用测试独立缓存":"当前游戏译文");
        }
        UiLanguage.LanguageChanged+=RefreshContext;Disposed+=(_,_)=>UiLanguage.LanguageChanged-=RefreshContext;RefreshContext(null,EventArgs.Empty);
        root.Controls.Add(tabs,0,2);feedback.Tag="translation-muted";feedback.Padding=new(0,8,0,0);feedback.Margin=Padding.Empty;root.Controls.Add(feedback,0,3);
        var texts=tabs.Add("译文与问题","translate");var termTab=tabs.Add("术语","tag");var historyTab=tabs.Add("译文修改记录","list");tabs.CanLeave=()=>!busy&&DiscardEdit();
        var content=TranslationWorkspaceUi.Column(new(SizeType.AutoSize),new(SizeType.Percent,100),new(SizeType.AutoSize),new(SizeType.Absolute,140),new(SizeType.AutoSize),new(SizeType.AutoSize));texts.Controls.Add(content);
        content.SizeChanged+=(_,_)=>{float wanted=Math.Clamp(content.Height*.26f,(content.Height<450*DeviceDpi/96f?70:90)*DeviceDpi/96f,146*DeviceDpi/96f);if(Math.Abs(content.RowStyles[3].Height-wanted)>1)content.RowStyles[3].Height=wanted;};
        filter.Items.AddRange(["全部译文","手动更正","待处理问题","等待重译"]);filter.SelectedIndex=0;
        filter.DrawMode=DrawMode.OwnerDrawFixed;
        filter.DrawItem+=(_,e)=>{if(e.Index<0)return;var p=UiTheme.Current;bool selected=(e.State&DrawItemState.Selected)!=0;using var brush=new SolidBrush(selected?TranslationWorkspaceUi.SelectionColor:p.Secondary);e.Graphics.FillRectangle(brush,e.Bounds);TextRenderer.DrawText(e.Graphics,UiLanguage.Text(filter.GetItemText(filter.Items[e.Index])),filter.Font,Rectangle.Inflate(e.Bounds,-6,0),p.Text,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);e.DrawFocusRectangle();};
        var actions=TranslationWorkspaceUi.Bar(InlineLabel("搜索"),TranslationWorkspaceUi.Field(search,240,"search"),filter,Button("刷新",RefreshAsync,"refresh"),Button("导入",ImportAsync,"folder"),Button("导出",ExportAsync,"export"));content.Controls.Add(actions,0,0);
        grid.Columns.Add("source","原文");grid.Columns.Add("text","当前译文");grid.Columns.Add("state","状态");grid.Columns[0].FillWeight=43;grid.Columns[1].FillWeight=43;grid.Columns[2].FillWeight=14;
        grid.Columns[2].MinimumWidth=84;content.Controls.Add(grid,0,1);
        previous=Button("上一页",()=>{if(page>0&&DiscardEdit()){page--;RenderPage();}return Task.CompletedTask;},"back");
        next=Button("下一页",()=>{if((page+1)*PageSize<filtered.Length&&DiscardEdit()){page++;RenderPage();}return Task.CompletedTask;},"next");
        pageLabel.Tag="translation-muted";pageLabel.Padding=new(2,10,14,0);content.Controls.Add(TranslationWorkspaceUi.Bar(previous,pageLabel,next),0,2);
        var editor=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Margin=new(0,4,0,0)};
        editor.ColumnStyles.Add(new(SizeType.Percent,50));editor.ColumnStyles.Add(new(SizeType.Percent,50));editor.RowStyles.Add(new(SizeType.AutoSize));editor.RowStyles.Add(new(SizeType.Percent,100));
        editor.Controls.Add(TranslationWorkspaceUi.Label("完整原文 · 只读",true),0,0);editor.Controls.Add(TranslationWorkspaceUi.Label("更正译文 · 保留变量和控制符",true),1,0);
        editor.Controls.Add(TranslationWorkspaceUi.Editor(original,"完整原文"),0,1);editor.Controls.Add(TranslationWorkspaceUi.Editor(translated,"更正译文"),1,1);content.Controls.Add(editor,0,3);
        entryLabel.Tag="translation-muted";entryLabel.Margin=new(0,6,0,2);entryLabel.Padding=Padding.Empty;content.Controls.Add(entryLabel,0,4);
        save=Button("保存更正",SaveAsync,"check");save.Font=new(Font,FontStyle.Bold);
        restore=Button("恢复自动译文",RestoreAsync);retry=Button("重译所选",RetryAsync);
        content.Controls.Add(TranslationWorkspaceUi.Bar(save,restore,retry,Button("重译本页问题",RetryPageAsync),Button("撤销上次译文修改",UndoAsync)),0,5);
        var termLayout=TranslationWorkspaceUi.Column(new(SizeType.AutoSize),new(SizeType.AutoSize),new(SizeType.Percent,100));termTab.Controls.Add(termLayout);
        var termHelp=TranslationWorkspaceUi.Label("术语用于后续翻译，旧缓存会保留。可重译含该术语的原句（最多 1000 条）；手动更正不受影响。匹配不区分大小写。",true);termLayout.Controls.Add(termHelp,0,0);
        termLayout.Controls.Add(TranslationWorkspaceUi.Bar(InlineLabel("原词"),TranslationWorkspaceUi.Field(termSource,180,"tag"),InlineLabel("译名"),TranslationWorkspaceUi.Field(termText,180,"translate"),Button("保存术语",SaveTermAsync,"check"),Button("删除术语",DeleteTermAsync,"trash"),Button("重译相关原句",RetryTermAsync,"refresh")),0,1);
        terms.Columns.Add("source","术语原文");terms.Columns.Add("text","统一译名");termLayout.Controls.Add(terms,0,2);
        terms.SelectionChanged+=(_,_)=>{if(terms.CurrentRow?.Tag is KeyValuePair<string,string> p){termSource.Text=p.Key;termText.Text=p.Value;}};
        var historyLayout=TranslationWorkspaceUi.Column(new(SizeType.AutoSize),new(SizeType.Percent,100));historyTab.Controls.Add(historyLayout);
        var historyHelp=TranslationWorkspaceUi.Label("保留最近 100 次译文修改。撤销针对译文更正和导入，不包含术语；自动翻译不覆盖人工记录。",true);historyLayout.Controls.Add(historyHelp,0,0);
        history.Columns.Add("time","时间");history.Columns.Add("action","操作");history.Columns.Add("count","原句数量");history.Columns[0].FillWeight=32;history.Columns[1].FillWeight=50;history.Columns[2].FillWeight=18;historyLayout.Controls.Add(history,0,1);
        grid.SelectionChanged+=(_,_)=>SelectRow();search.TextChanged+=(_,_)=>{debounce.Stop();debounce.Start();};debounce.Tick+=(_,_)=>{debounce.Stop();ApplyFilter();};filter.SelectedIndexChanged+=(_,_)=>ApplyFilter();
        translated.TextChanged+=(_,_)=>{if(!refreshing&&!selecting&&selectedSource.Length>0&&translated.Text!=savedEditor)entryLabel.Text="尚未保存 · Ctrl+S 保存更正";};
        Shown+=async(_,_)=>await Run(RefreshAsync);
        FormClosing+=(_,e)=>{if(busy){e.Cancel=true;feedback.Text="正在保存或读取，请稍候。";}else if(!DiscardEdit())e.Cancel=true;};
        FormClosed+=(_,_)=>debounce.Dispose();
        KeyPreview=true;KeyDown+=async(_,e)=>
        {
            if(e.Control&&e.KeyCode==Keys.Tab){e.SuppressKeyPress=true;tabs.Next(e.Shift);}
            else if(e.Control&&e.KeyCode==Keys.F){e.SuppressKeyPress=true;if(tabs.Select(0))search.Parent!.Focus();}
            else if(e.Control&&e.KeyCode==Keys.S&&tabs.SelectedIndex==0){e.SuppressKeyPress=true;await Run(SaveAsync);}
        };
        void Wrap(){title.Visible=ClientSize.Height>=600*DeviceDpi/96;int width=Math.Max(200,root.ClientSize.Width-root.Padding.Horizontal-8);foreach(var l in new[]{context,feedback,entryLabel,termHelp,historyHelp})l.MaximumSize=new(width,0);}
        root.SizeChanged+=(_,_)=>Wrap();TranslationWorkspaceUi.Prepare(this,settings);Wrap();ResumeLayout(true);
    }
    private static Label InlineLabel(string text)=>new(){Text=text,AutoSize=true,Padding=new(0,10,8,0),Margin=Padding.Empty};
    private GameActionButton Button(string text,Func<Task> action,string icon="")
    {var b=TranslationWorkspaceUi.Button(text,icon);b.Click+=async(_,_)=>await Run(action);return b;}
    private void UpdateActions()
    {
        previous.Enabled=!busy&&page>0;next.Enabled=!busy&&(page+1)*PageSize<filtered.Length;
        save.Enabled=restore.Enabled=retry.Enabled=!busy&&selectedSource.Length>0;
    }
}

internal sealed class TranslationImportReviewForm:Form
{
    internal TranslationImportReviewForm(TranslationWorkspaceStore.ImportPreview preview,ApiSettings settings)
    {
        AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new(96,96);Text="导入预览";Size=new(800,560);MinimumSize=new(700,450);StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;MinimizeBox=false;Font=new("Microsoft YaHei UI",10);
        var root=TranslationWorkspaceUi.Column(new(SizeType.AutoSize),new(SizeType.AutoSize),new(SizeType.Percent,100),new(SizeType.AutoSize));root.Padding=new(20);Controls.Add(root);
        var counts=TranslationWorkspaceUi.Label($"新增 {preview.New:N0}   ·   相同 {preview.Same:N0}   ·   冲突 {preview.Conflicts:N0}   ·   无效 {preview.Invalid:N0}");counts.Font=new(Font,FontStyle.Bold);root.Controls.Add(counts,0,0);
        var hint=TranslationWorkspaceUi.Label("无效条目会跳过，导入的变更作为手动更正保存。下方列出冲突原句与术语（最多 200 项）。",true);root.Controls.Add(hint,0,1);
        var text=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Text=preview.ConflictSources.Length>0?string.Join("\r\n\r\n",preview.ConflictSources):"没有冲突，可以保留现有译文并导入新增内容。"};root.Controls.Add(TranslationWorkspaceUi.Editor(text,"导入冲突明细"),0,2);
        var keep=TranslationWorkspaceUi.Button("保留现有冲突项","check");keep.DialogResult=DialogResult.No;
        var replace=TranslationWorkspaceUi.Button("使用导入的冲突项");replace.DialogResult=DialogResult.Yes;
        var cancel=TranslationWorkspaceUi.Button("取消");cancel.DialogResult=DialogResult.Cancel;
        root.Controls.Add(TranslationWorkspaceUi.Bar(keep,replace,cancel),0,3);CancelButton=cancel;
        root.SizeChanged+=(_,_)=>hint.MaximumSize=new(Math.Max(200,root.ClientSize.Width-root.Padding.Horizontal-8),0);
        TranslationWorkspaceUi.Prepare(this,settings);
    }
}
