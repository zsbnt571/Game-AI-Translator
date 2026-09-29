namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private Task<RecentGameStore>? _recentStoreTask;
    private readonly CancellationTokenSource _desktopLifetime=new();
    private CancellationTokenSource? _mediaSelectionCancellation,_coverCancellation;
    private readonly PictureBox _selectedGameIcon=new(){Size=new(48,48),SizeMode=PictureBoxSizeMode.Zoom,AccessibleName="游戏图标"};
    private readonly Label _gameBadge=new(){AutoSize=true};
    private readonly Label _mediaStatus=new(){AutoSize=true};
    private readonly Label _coverDetails=new(){AutoSize=true};
    private readonly GameActionButton _recentButton=new("选择游戏");
    private readonly GameActionButton _coverButton=new("更新封面");
    private readonly GameActionButton _folderButton=new("打开文件夹");
    private readonly GameActionButton _saveGameButton=new("保存游戏");
    private RecentGame? _currentRecent;
    private Task _rememberSelectedTask=Task.CompletedTask;
    private bool _recentOpening,_manualCoverActive;
    private long _coverRound;
    private int _lastGameTab=1;
    private readonly int[] _lastGamePages=new int[2];
    private Task<RecentGameStore> RecentStore()=>_recentStoreTask??=Task.Run(()=>new RecentGameStore());
    private void InitializeRecentUi()
    {
        GameIconPlaceholder.Attach(_selectedGameIcon);
        _mediaStatus.Visible=false;_mediaStatus.TextChanged+=(_,_)=>_mediaStatus.Visible=_mediaStatus.Text.Length>0;
        _recentButton.Click+=async(_,_)=>await OpenRecentGamesAsync();
        _coverButton.Text="更换图片";_coverButton.Click+=async(_,_)=>await EditGameArtworkAsync();
        _saveGameButton.Click+=async(_,_)=>{
            var game=_selectedGame;var epoch=_gameSelectionEpoch;if(game is null)return;
            try{
                await _rememberSelectedTask;var store=await RecentStore();
                if(IsDisposed||epoch!=_gameSelectionEpoch)return;
                var record=store.Find(game.ExePath)??await Task.Run(()=>store.Select(game.Name,game.ExePath,usage:false));
                await Task.Run(()=>store.SetSaved(record.Id,!record.Saved));
                if(!IsDisposed&&epoch==_gameSelectionEpoch){_currentRecent=store.Find(game.ExePath);UpdateSavedButton();}
            }catch(Exception ex){if(!IsDisposed)SetLabelText(_mediaStatus,SafeDiagnosticOutput.ExceptionSummary(ex));}
        };
        _folderButton.Click+=async(_,_)=>{
            var path=_selectedGame?.ExePath;if(path is null)return;
            try{var folder=Path.GetDirectoryName(path)!;if(!await Task.Run(()=>Directory.Exists(folder)))throw new IOException("游戏目录不可访问，请重新定位。");
                if(!IsDisposed)System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder){UseShellExecute=true});}
            catch(Exception ex){if(!IsDisposed)SetLabelText(_mediaStatus,SafeDiagnosticOutput.ExceptionSummary(ex));}
        };
        FormClosed+=(_,_)=>{_desktopLifetime.Cancel();_mediaSelectionCancellation?.Cancel();_coverCancellation?.Cancel();ReplaceGameIcon(null);};
    }
    private void UpdateSavedButton()=>_saveGameButton.Text=_currentRecent?.Saved==true?"已保存 · 取消":"保存游戏";
    private void CancelSelectedMedia()
    {
        _mediaSelectionCancellation?.Cancel();_mediaSelectionCancellation?.Dispose();
        _mediaSelectionCancellation=CancellationTokenSource.CreateLinkedTokenSource(_desktopLifetime.Token);
        _libraryDetailEpoch++;var oldCover=_libraryCover.Image;_libraryCover.Image=null;oldCover?.Dispose();_libraryChineseName.Text="";_libraryNote.Text="";
        Interlocked.Increment(ref _coverRound);_coverCancellation?.Cancel();_manualCoverActive=false;_currentRecent=null;ReplaceGameIcon(null);SetLabelText(_mediaStatus,"");SetLabelText(_coverDetails,"");UpdateSavedButton();
    }
    private void ReplaceGameIcon(Bitmap? image)
    {
        var old=_selectedGameIcon.Image;_selectedGameIcon.Image=image;old?.Dispose();
        _selectedGameIcon.BackColor=image is null?UiTheme.Current.Secondary:UiTheme.Current.Main;
    }
    private async Task RememberSelectedGameAsync(GameInfo game,int epoch,string? relinkId,bool recordSelection)
    {
        var token=_mediaSelectionCancellation!.Token;Bitmap? icon=null;
        try
        {
            var store=await RecentStore();token.ThrowIfCancellationRequested();
            var record=await Task.Run(()=>store.Writable?store.Select(game.Name,game.ExePath,relinkId,recordSelection):store.Find(game.ExePath),token);
            if(IsDisposed||epoch!=_gameSelectionEpoch||token.IsCancellationRequested)return;
            _currentRecent=record;UpdateSavedButton();SetLabelText(_mediaStatus,store.Notice);
            if(record is null)return;
            var usable=await Task.Run(()=>{using var cover=store.ReadImage(record,true);return cover is not null;},token);
            if(IsDisposed||epoch!=_gameSelectionEpoch||token.IsCancellationRequested)return;
            if(store.Notice.Length==0)SetLabelText(_mediaStatus,usable?"已有封面 · 可在游戏标题页更新":"尚未取得有效封面 · 游戏窗口可用时可更新");
            icon=await Task.Run(()=>store.IconFor(record,token),token);
            if(IsDisposed||epoch!=_gameSelectionEpoch||token.IsCancellationRequested)return;
            ReplaceGameIcon(icon);icon=null;_=FillDefaultArtworkAsync([game.ExePath]);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!IsDisposed&&epoch==_gameSelectionEpoch)SetLabelText(_mediaStatus,"游戏记录 / 图标："+SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally{icon?.Dispose();if(!IsDisposed)ScheduleLibraryRefresh();}
    }
    private async Task RememberLaunchAsync(GameInfo game)
    {
        try{
            await _rememberSelectedTask;var store=await RecentStore();
            if(store.Writable){var record=await Task.Run(()=>store.Select(game.Name,game.ExePath));if(SameGame(game.ExePath,_selectedGame?.ExePath))_currentRecent=record;}
        }catch(Exception ex){if(!IsDisposed)SetLabelText(_mediaStatus,"启动已发起，最近记录保存失败："+SafeDiagnosticOutput.ExceptionSummary(ex));}
    }
    private async Task UpdateGameCoverAsync(bool automatic)
    {
        var game=_selectedGame;if(game is null||automatic&&_manualCoverActive)return;
        if(!automatic&&_automaticGameCovers.Remove(RecentGameStore.Normalize(game.ExePath),out var background))background.Cancel();
        var epoch=_gameSelectionEpoch;var round=Interlocked.Increment(ref _coverRound);
        _coverCancellation?.Cancel();
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(_desktopLifetime.Token);_coverCancellation=cancel;
        _manualCoverActive=!automatic;
        var hadCover=false;var stage="记录";string recordId="pending";
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        bool Current()=>!cancel.IsCancellationRequested&&round==Volatile.Read(ref _coverRound)&&epoch==_gameSelectionEpoch;
        void Log(string value)=>AppLog.Write("game-cover",$"mode={(automatic?"auto":"manual")} round={round} record={recordId} totalMs={elapsed.ElapsedMilliseconds} oldUsable={hadCover} "+value);
        try
        {
            Log("event=start");
            await _rememberSelectedTask;
            if(!Current())return;
            var store=await RecentStore();var record=store.Find(game.ExePath);
            if(record is null||!store.Writable){Log("event=failed stage=record");if(Current())SetLabelText(_mediaStatus,"封面获取失败 · 游戏记录不可写，请查看详情。");return;}
            recordId=record.Id;
            var identityVersion=store.CoverIdentityVersion(record);
            if(automatic&&record.CoverManual){Log("event=manual-cover-protected");return;}
            if(automatic&&(record.CoverPreference??_libraryPreferences.CoverSource)==ArtworkSource.Online){await FillDefaultArtworkAsync([game.ExePath]);record=store.Find(game.ExePath)??record;}
            hadCover=await Task.Run(()=>{using var b=store.ReadImage(record,true);return b is not null;},cancel.Token);
            if(!Current())return;
            Log("event=cache-check "+store.CoverDiagnostic(record));
            if(automatic&&hadCover){Log("event=reuse-valid-cover");return;}
            if(record.Cover is not null&&!hadCover)Log("event=invalid-cache-reacquire");
            SetLabelText(_mediaStatus,automatic?"正在等待有效游戏封面…":"正在更新封面…");stage="捕获";
            var result=await GameWindowCover.CaptureAsync(game.ExePath,automatic,cancel.Token,Log);
            using var image=result.Image;
            if(!Current()){Log("event=stale-result-rejected");return;}
            SetLabelText(_coverDetails,result.Diagnostic);
            if(image is null){Log("event=failed stage=capture "+result.Diagnostic);SetLabelText(_mediaStatus,(hadCover?"更新未成功，保留原封面 · ":"尚未取得有效封面 · ")+result.Message);return;}
            stage="写入";
            // The store checks generation, expected cover version and window identity under
            // its commit lock. A later manual result can never be overwritten by this one.
            bool CanCommit()=>Current()&&result.Target is not null&&GameLibraryWindowIdentity.Matches(result.Target);
            if(!await Task.Run(()=>store.SaveImage(record,image,true,null,cancel.Token,CanCommit,identityVersion,manual:!automatic,origin:automatic?"automatic":"screenshot"),cancel.Token))
            {
                Log("event=commit-rejected reason=stale-record-round-or-window");
                if(Current())SetLabelText(_mediaStatus,hadCover?"封面未更新，已保留原封面。":"尚未取得有效封面 · 游戏窗口或记录已改变。");
                return;
            }
            stage="读取";
            using var decoded=await Task.Run(()=>store.ReadImage(record,true),cancel.Token);
            if(decoded is null)throw new IOException("已写入文件，但无法读取有效封面");
            Log("event=committed "+store.CoverDiagnostic(record));
            if(Current())
            {
                _currentRecent=store.Find(game.ExePath);
                SetLabelText(_mediaStatus,"封面已保存 · 可在“选择游戏”中查看");
                SetLabelText(_coverDetails,result.Diagnostic+"；有效封面已保存并绑定；实际显示请查看卡片。");
            }
        }
        catch(OperationCanceledException){Log("event=cancelled");}
        catch(Exception ex){
            var reason=SafeDiagnosticOutput.ExceptionSummary(ex);Log("event=failed stage="+stage+" reason="+reason);
            if(Current()){
                SetLabelText(_mediaStatus,(hadCover?"更新未成功，保留原封面":"尚未取得有效封面")+" · "+stage+"失败，可重试。");
                SetLabelText(_coverDetails,stage+"："+reason);
            }
        }
        finally{
            if(!Current())Log("event=ended-superseded-or-cancelled");
            if(ReferenceEquals(_coverCancellation,cancel)){_coverCancellation=null;_manualCoverActive=false;}
        }
    }
    private async Task OpenRecentGamesAsync()
    {
        if(_gameActionRunning||_recentOpening)return;
        _recentOpening=true;_recentButton.Enabled=false;
        try
        {
            var store=await RecentStore();if(IsDisposed)return;
            using var dialog=new RecentGamesDialog(store,_selectedGame?.ExePath,_appliedDesktopSettings,_lastGameTab,_lastGamePages);
            var result=dialog.ShowDialog(this);_lastGameTab=dialog.CurrentTab;
            if(result==DialogResult.OK&&dialog.SelectedPath is { } path)SelectFusionGame(path);
            else if(_selectedGame is { } game){_currentRecent=store.Find(game.ExePath);UpdateSavedButton();}
            if(store.Notice.Length>0)SetLabelText(_mediaStatus,store.Notice);
        }
        catch(Exception ex){if(!IsDisposed)SetLabelText(_mediaStatus,"游戏选择："+SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally{_recentOpening=false;if(!IsDisposed)_recentButton.Enabled=!_gameActionRunning;}
    }
}

internal sealed class RecentGamesDialog:Form
{
    private const int PerPage=6;
    private readonly RecentGameStore store;
    private readonly string? current;
    private readonly FlowLayoutPanel cards=new(){Dock=DockStyle.Fill,AutoScroll=true,WrapContents=true,Padding=new(8)};
    private readonly GameLibraryTabs tabs=new(){Dock=DockStyle.Top};
    private OwnedRetentionPopup? retentionPopup;
    private NumericUpDown? retentionValue;
    private Label? retentionFeedback;
    private bool retentionSaving;
    private volatile bool retentionClosing;
    private long retentionGeneration;
    private readonly Label notice=new(){AutoSize=true};
    private readonly GameActionButton previous=new("上一页"),next=new("下一页");
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource? pageCancellation;
    private readonly ApiSettings appearance;
    private readonly Font logicalFont=new("Microsoft YaHei UI",10);
    private readonly ToolTip tips=new();
    private readonly List<(Panel Card,PictureBox Icon,PictureBox Cover,Label State,RecentGame Record)> visibleCards=[];
    private readonly HashSet<string> loaded=[];
    private bool loading,building,refreshQueued;
    private readonly int[] pages;
    internal string? SelectedPath {get;private set;}
    internal int CurrentTab=>tabs.SelectedIndex;
    private bool Saved=>CurrentTab==0;
    internal RecentGamesDialog(RecentGameStore store,string? current,ApiSettings appearance,int tab,int[] pages)
    {
        this.store=store;this.current=current;this.appearance=appearance;this.pages=pages;
        Text="选择游戏";StartPosition=FormStartPosition.CenterParent;ClientSize=new(960,650);MinimumSize=new(460,420);
        AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new(96,96);Font=logicalFont;
        tabs.SelectedIndex=Math.Clamp(tab,0,1);tabs.RecentLimit=store.RecentLimit;
        var footer=new TableLayoutPanel{Dock=DockStyle.Bottom,AutoSize=false,ColumnCount=3,RowCount=1,Padding=new(8)};
        footer.ColumnStyles.Add(new(SizeType.AutoSize));footer.ColumnStyles.Add(new(SizeType.AutoSize));footer.ColumnStyles.Add(new(SizeType.Percent,100));
        notice.AutoSize=false;notice.AutoEllipsis=true;notice.Dock=DockStyle.Fill;notice.TextAlign=ContentAlignment.MiddleLeft;
        footer.Controls.Add(previous,0,0);footer.Controls.Add(next,1,0);footer.Controls.Add(notice,2,0);
        footer.Layout+=(_,_)=>{var height=Math.Max(previous.Height,next.Height)+footer.Padding.Vertical+previous.Margin.Vertical;if(footer.Height!=height)footer.Height=height;};
        Controls.Add(cards);Controls.Add(tabs);Controls.Add(footer);
        previous.Click+=(_,_)=>{pages[CurrentTab]--;BuildCards();};next.Click+=(_,_)=>{pages[CurrentTab]++;BuildCards();};
        tabs.RetentionRequested+=(_,_)=>OpenRetentionPopup();
        tabs.SelectedIndexChanged+=(_,_)=>{retentionPopup?.Close();if(Visible)BuildCards();};
        cards.Scroll+=(_,_)=>LoadVisible();cards.Resize+=(_,_)=>{ResizeCards();LoadVisible();};
        Shown+=(_,_)=>BuildCards();
        store.Changed+=StoreChanged;
        FormClosed+=(_,_)=>{retentionClosing=true;retentionGeneration++;retentionPopup?.Dispose();store.Changed-=StoreChanged;lifetime.Cancel();pageCancellation?.Cancel();DisposeCards();};
        Disposed+=(_,_)=>{retentionClosing=true;retentionGeneration++;retentionPopup?.Dispose();store.Changed-=StoreChanged;lifetime.Cancel();pageCancellation?.Cancel();logicalFont.Dispose();tips.Dispose();pageCancellation?.Dispose();lifetime.Dispose();};
        UiTheme.Apply(this,appearance);FontManager.ApplyUi(this,appearance);
    }
    private void OpenRetentionPopup()
    {
        if(Saved||!store.Writable||retentionClosing||IsDisposed||Disposing)return;
        if(retentionSaving){notice.Text="数量正在保存，请稍候。";return;}
        if(retentionPopup is {IsDisposed:false} existing){
            if(existing.Visible){existing.Close();return;}
            retentionValue!.Value=store.RecentLimit;retentionFeedback!.Text="";
            existing.Show(tabs,tabs.RetentionAnchor);retentionValue.Focus();retentionValue.Select(0,retentionValue.Text.Length);return;
        }
        var scale=DeviceDpi/96f;
        var panel=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,Padding=new Padding((int)(12*scale)),Font=Font};
        var label=new Label{Text="最近游戏保留数量",AutoSize=true};
        var value=new NumericUpDown{Minimum=1,Maximum=1000,Value=store.RecentLimit,Width=(int)(100*scale),AccessibleName="最近游戏保留数量"};
        var description=new Label{Text="仅影响最近记录，不删除已保存游戏或游戏文件。再次选择或启动会重新加入。",AutoSize=true,MaximumSize=new((int)(300*scale),0)};
        var feedback=new Label{AutoSize=true,MaximumSize=description.MaximumSize};
        var save=new GameActionButton("保存");var cancel=new GameActionButton("取消");
        var actions=new FlowLayoutPanel{AutoSize=true,WrapContents=false};actions.Controls.AddRange([save,cancel]);
        panel.Controls.Add(label);panel.Controls.Add(value);panel.Controls.Add(description);panel.Controls.Add(feedback);panel.Controls.Add(actions);
        UiTheme.Apply(panel,appearance);FontManager.ApplyUi(panel,appearance);
        var host=new ToolStripControlHost(panel){AutoSize=false,Margin=Padding.Empty,Padding=Padding.Empty,Size=panel.GetPreferredSize(Size.Empty)};
        var popup=new OwnedRetentionPopup{AutoClose=true,Padding=Padding.Empty};popup.Items.Add(host);retentionPopup=popup;
        retentionValue=value;retentionFeedback=feedback;var generation=++retentionGeneration;
        bool Current()=>!retentionClosing&&!IsDisposed&&!Disposing&&generation==retentionGeneration&&ReferenceEquals(retentionPopup,popup)&&!popup.IsDisposed;
        popup.Opened+=(_,_)=>{panel.PerformLayout();host.Size=panel.GetPreferredSize(Size.Empty);};
        popup.Closed+=(_,e)=>AppLog.Write("retention-popup",$"Closed instance={generation} reason={e.CloseReason} saving={retentionSaving}");
        popup.Opened+=(_,_)=>AppLog.Write("retention-popup",$"Opened instance={generation}");
        popup.Disposed+=(_,_)=>{if(ReferenceEquals(retentionPopup,popup))retentionPopup=null;AppLog.Write("retention-popup",$"Disposed instance={generation}");};
        cancel.Click+=(_,_)=>{if(Current()&&!retentionSaving)popup.Close();};
        // No value changes reach the store until Save; outside click / Escape just close.
        save.Click+=async(_,_)=>{
            if(!Current()||retentionSaving)return;
            var selected=(int)value.Value;retentionSaving=true;
            save.Enabled=cancel.Enabled=value.Enabled=false;
            // Closing after this point only dismisses UI; the explicit save stays committed.
            AppLog.Write("retention-popup",$"Save started instance={generation}");
            try{
                await Task.Run(()=>store.SetRecentLimit(selected));
                if(Current()){tabs.RecentLimit=store.RecentLimit;if(popup.Visible)popup.Close();}
                AppLog.Write("retention-popup",$"Save completed instance={generation}");
            }catch(Exception ex){
                AppLog.Write("retention-popup","Save failed",ex);
                if(Current()){
                    feedback.Text="保存失败，请重试。";notice.Text=feedback.Text;
                    host.Size=panel.GetPreferredSize(Size.Empty);
                }
            }finally{
                retentionSaving=false;
                if(Current())save.Enabled=cancel.Enabled=value.Enabled=true;
            }
        };
        popup.Show(tabs,tabs.RetentionAnchor);value.Focus();value.Select(0,value.Text.Length);
    }
    private void StoreChanged()
    {
        if(retentionClosing||IsDisposed||!IsHandleCreated||refreshQueued)return;
        refreshQueued=true;
        try{BeginInvoke((Action)(()=>{refreshQueued=false;if(!retentionClosing&&!IsDisposed&&Visible)BuildCards();}));}catch(InvalidOperationException){refreshQueued=false;}
    }
    private void DisposeCards()
    {
        foreach(var c in visibleCards){c.Icon.Image?.Dispose();c.Cover.Image?.Dispose();c.Icon.Image=null;c.Cover.Image=null;c.Card.Dispose();}
        visibleCards.Clear();loaded.Clear();
        foreach(Control c in cards.Controls.Cast<Control>().ToArray())c.Dispose();
    }
    private void BuildCards()
    {
        if(building||retentionClosing||IsDisposed)return;building=true;
        try{
            pageCancellation?.Cancel();pageCancellation?.Dispose();pageCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            cards.SuspendLayout();DisposeCards();tabs.RecentLimit=store.RecentLimit;
            var all=store.Snapshot(Saved);var page=pages[CurrentTab]=Math.Clamp(pages[CurrentTab],0,Math.Max(0,(all.Length-1)/PerPage));
            previous.Enabled=page>0;next.Enabled=(page+1)*PerPage<all.Length;
            notice.Text=store.Notice.Length>0?store.Notice:$"{page+1} / {Math.Max(1,(all.Length+PerPage-1)/PerPage)} 页 · 共 {all.Length} 项 · 每页最多 {PerPage} 项";
            if(all.Length==0)cards.Controls.Add(new Label{Text=Saved?"还没有已保存游戏。在主页或最近游戏中点“保存游戏”即可长期保留。":"还没有最近游戏。完成选择或发起启动后会记录在此。",AutoSize=true,MaximumSize=new(560,0),Margin=new(16)});
            foreach(var record in all.Skip(page*PerPage).Take(PerPage))BuildCard(record);
            cards.ResumeLayout();ResizeCards();LoadVisible();
        }finally{building=false;}
    }
    private void BuildCard(RecentGame record)
    {
        var selected=StringComparer.OrdinalIgnoreCase.Equals(current,record.ExePath);
        var card=new Panel{Width=300,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new(8),BorderStyle=BorderStyle.FixedSingle,Font=logicalFont,Margin=new(6)};
        var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1};layout.ColumnStyles.Add(new(SizeType.Percent,100));
        var icon=new PictureBox{Size=new(28,28),SizeMode=PictureBoxSizeMode.Zoom};GameIconPlaceholder.Attach(icon);
        var title=new Label{Text=record.Name,AutoSize=false,AutoEllipsis=true,Width=235,Height=28,TextAlign=ContentAlignment.MiddleLeft};tips.SetToolTip(title,record.Name);
        var head=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top,WrapContents=false};head.Controls.AddRange([icon,title]);layout.Controls.Add(head);
        if(selected)layout.Controls.Add(new Label{Text="当前选择",AutoSize=true});
        var path=new GamePathLabel{Text=record.ExePath,AutoSize=false,Dock=DockStyle.Top,Height=24};tips.SetToolTip(path,record.ExePath);layout.Controls.Add(path);
        var menu=new ContextMenuStrip();menu.Items.Add("复制完整路径",null,(_,_)=>{try{Clipboard.SetText(record.ExePath);}catch(Exception ex){notice.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}});
        menu.Items.Add("打开游戏文件夹",null,(_,_)=>{try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(record.ExePath)!){UseShellExecute=true});}catch(Exception ex){notice.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}});
        path.ContextMenuStrip=menu;path.Disposed+=(_,_)=>menu.Dispose();path.Cursor=Cursors.Hand;
        path.Click+=(_,_)=>menu.Show(path,new Point(0,path.Height));
        var cover=new PictureBox{Dock=DockStyle.Top,Height=150,SizeMode=PictureBoxSizeMode.Zoom,BackColor=UiTheme.Current.Secondary,AccessibleName="游戏封面"};
        cover.Paint+=(_,e)=>{if(cover.Image is null)TextRenderer.DrawText(e.Graphics,"暂无封面",cover.Font,cover.ClientRectangle,UiTheme.Current.SecondaryText,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);};
        layout.Controls.Add(cover);
        var state=new Label{Text=record.Cover is null?"暂无封面":"封面按需加载",AutoSize=true,MaximumSize=new(280,0)};layout.Controls.Add(state);
        var choose=new GameActionButton("选择");var saved=new GameActionButton(record.Saved?"取消保存":"保存游戏");
        var relink=new GameActionButton("重新定位");var remove=new GameActionButton("从最近移除");
        choose.Click+=async(_,_)=>{choose.Enabled=false;var exists=await Task.Run(()=>File.Exists(record.ExePath));if(IsDisposed||card.IsDisposed)return;choose.Enabled=true;
            if(!exists){state.Text="路径失效，请重新定位";return;}SelectedPath=record.ExePath;DialogResult=DialogResult.OK;};
        saved.Enabled=relink.Enabled=remove.Enabled=store.Writable;
        saved.Click+=async(_,_)=>{try{await Task.Run(()=>store.SetSaved(record.Id,!record.Saved));}catch(Exception ex){if(!IsDisposed)notice.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}};
        relink.Click+=async(_,_)=>{using var d=new OpenFileDialog{Filter="游戏程序|*.exe",Title="重新定位 "+record.Name};if(d.ShowDialog(this)!=DialogResult.OK)return;
            try{await Task.Run(()=>store.Select(record.Name,d.FileName,record.Id,usage:false));}catch(Exception ex){if(!IsDisposed)notice.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}};
        remove.Click+=async(_,_)=>{try{await Task.Run(()=>store.RemoveRecent(record.Id));}catch(Exception ex){if(!IsDisposed)notice.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}};
        var actions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top};actions.Controls.AddRange(Saved?[choose,saved,relink]:[choose,saved,remove,relink]);layout.Controls.Add(actions);card.Controls.Add(layout);
        UiTheme.Apply(card,appearance);FontManager.ApplyUi(card,appearance);card.Scale(new SizeF(DeviceDpi/96f,DeviceDpi/96f));
        foreach(var button in actions.Controls.OfType<GameActionButton>())button.RefreshMetrics();
        path.Height=path.Font.Height+8*DeviceDpi/96;title.Height=Math.Max(icon.Height,title.Font.Height+6*DeviceDpi/96);
        if(selected)card.Paint+=(_,e)=>{using var pen=new Pen(UiTheme.Current.Accent,2*DeviceDpi/96f);e.Graphics.DrawRectangle(pen,1,1,card.ClientSize.Width-3,card.ClientSize.Height-3);};
        cards.Controls.Add(card);
        foreach(var button in actions.Controls.OfType<GameActionButton>())button.RefreshMetrics();
        visibleCards.Add((card,icon,cover,state,record));
    }
    private void ResizeCards()
    {
        if(IsDisposed)return;
        var width=Math.Max(240*DeviceDpi/96,Math.Min(320*DeviceDpi/96,cards.ClientSize.Width-40*DeviceDpi/96));
        foreach(var item in visibleCards)
        {
            item.Card.MinimumSize=new(width,0);item.Card.MaximumSize=new(width,0);item.Card.Width=width;
            item.Cover.Height=Math.Max(72*DeviceDpi/96,(width-item.Card.Padding.Horizontal)*9/16);
            item.State.MaximumSize=new(width-item.Card.Padding.Horizontal-8,0);
        }
    }
    private async void LoadVisible()
    {
        if(loading||!Visible||IsDisposed||pageCancellation is null)return;
        loading=true;var token=pageCancellation.Token;
        try
        {
            foreach(var item in visibleCards.ToArray())
            {
                if(token.IsCancellationRequested)break;
                if(item.Card.IsDisposed||!item.Card.Bounds.IntersectsWith(cards.ClientRectangle)||!loaded.Add(item.Record.Id))continue;
                Bitmap? icon=null,cover=null;
                try
                {
                    var exists=await Task.Run(()=>File.Exists(item.Record.ExePath),token);
                    icon=await Task.Run(()=>store.IconFor(item.Record,token),token);
                    cover=await Task.Run(()=>store.ReadImage(item.Record,true),token);
                    if(IsDisposed||token.IsCancellationRequested||item.Card.IsDisposed)continue;
                    item.Icon.Image=icon;icon=null;item.Cover.Image=cover;cover=null;
                    item.State.Text=!exists?"路径失效 · 请重新定位":item.Cover.Image is not null?"": "尚未取得有效封面 · 可返回主页更新";
                }
                catch(OperationCanceledException){break;}
                catch(Exception ex){if(!IsDisposed&&!item.Card.IsDisposed&&!token.IsCancellationRequested)item.State.Text="缩略图不可用："+SafeDiagnosticOutput.ExceptionSummary(ex);}
                finally{icon?.Dispose();cover?.Dispose();}
            }
        }
        finally{loading=false;if(!IsDisposed&&Visible&&(pageCancellation?.Token!=token||visibleCards.Any(x=>!x.Card.IsDisposed&&!loaded.Contains(x.Record.Id)&&x.Card.Bounds.IntersectsWith(cards.ClientRectangle))))LoadVisible();}
    }
}

internal sealed class GamePathLabel:Label
{
    protected override void OnPaint(PaintEventArgs e)=>TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,ForeColor,TextFormatFlags.PathEllipsis|TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
}
internal static class GameIconPlaceholder
{
    internal static void Attach(PictureBox box)=>box.Paint+=(_,e)=>{
        if(box.Image is not null)return;
        var r=Rectangle.Inflate(box.ClientRectangle,-Math.Max(3,box.Width/8),-Math.Max(3,box.Height/4));
        using var pen=new Pen(UiTheme.Current.SecondaryText,Math.Max(1,box.Width/28f));
        e.Graphics.DrawRectangle(pen,r);var x=r.Left+r.Width/3;var y=r.Top+r.Height/2;
        e.Graphics.DrawLine(pen,x-3,y,x+3,y);e.Graphics.DrawLine(pen,x,y-3,x,y+3);
        e.Graphics.DrawEllipse(pen,r.Right-r.Width/3,y-2,3,3);
    };
}


