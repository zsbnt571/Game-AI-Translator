using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WA = System.Windows.Automation;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfGameSessionWorkspace : WC.ScrollViewer, IDisposable
{
    private readonly IGameDetailsHost host;
    private readonly WpfViewRefresh refreshQueue;
    private readonly WC.StackPanel content = new() { Margin = new(24,14,24,16) };
    private readonly List<Action> bindings = [];
    private readonly WpfNovelModificationWorkspace novel;
    private readonly WpfRenpyModificationWorkspace renpy;
    private readonly Dictionary<string,W.FrameworkElement> views = new();
    private readonly WM.ImageBrush artwork = new() { Stretch=WM.Stretch.Uniform, AlignmentX=WM.AlignmentX.Center, AlignmentY=WM.AlignmentY.Center };
    private WpfGameHeroSurface heroSurface = null!;
    private readonly WC.TextBlock title = new() { FontSize=26.667, FontWeight=W.FontWeights.Bold, Foreground=WM.Brushes.White, TextTrimming=W.TextTrimming.CharacterEllipsis, TextWrapping=W.TextWrapping.Wrap, MaxHeight=104, VerticalAlignment=W.VerticalAlignment.Center };
    private readonly WC.TextBlock subtitle = new() { FontSize=16, Foreground=WM.Brushes.Gainsboro, TextTrimming=W.TextTrimming.CharacterEllipsis, TextWrapping=W.TextWrapping.Wrap, MaxHeight=48 };
    private readonly WC.ComboBox profile = new() { MinWidth=120, MaxWidth=320, Height=36, HorizontalAlignment=W.HorizontalAlignment.Stretch };
    private readonly WC.TextBox path = new() { IsReadOnly=true, Height=36, MinWidth=120, VerticalAlignment=W.VerticalAlignment.Center };
    private WorkspaceProfileChoice[] profileChoices=[];
    private bool refreshing;
    private (string Path,bool Modifying)? displayedSection;
    private readonly Dictionary<(string Path,bool Modifying),double> sectionScroll = new();
    private int sectionRevision;
    private bool disposed;
    internal W.FrameworkElement View(string id) => views[id];

    internal WpfGameSessionWorkspace(IGameDetailsHost host)
    {
        this.host=host; refreshQueue = new(Dispatcher, Refresh);
        // Implicit ScrollViewer styles do not match this derived control.
        // Keep keyboard scrolling, but never draw a frame around the whole page.
        FocusVisualStyle = null;
        host.GameDetailChanged += refreshQueue.Request; Content=content;FontSize=13.333;
        // Keep the old coherent page visible while selecting another game,
        // without a disabled-opacity flash or actions targeting the old game.
        PreviewMouseDown+=(_,e)=>{if(host.GameDetail.SelectionPending)e.Handled=true;};
        PreviewKeyDown+=(_,e)=>{if(host.GameDetail.SelectionPending)e.Handled=true;};
        PreviewTextInput+=(_,e)=>{if(host.GameDetail.SelectionPending)e.Handled=true;};
        VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto;HorizontalScrollBarVisibility=WC.ScrollBarVisibility.Disabled;
        var state=host.GameDetail;
        var compactCapture=Action("截取游戏画面","image",GameDetailAction.CaptureCover,"GameCompactCapture");
        var compactActions=Row(Action(state.CompactLaunch,"play",GameDetailAction.Launch,"GameCompactLaunch",true),Menu("","down","launch","GameCompactLaunchArrow",true),Action(state.CompactReturn,"play",GameDetailAction.Return,"GameCompactReturn",true),Action(state.CompactRestart,"refresh",GameDetailAction.Restart,"GameCompactRestart"),Action(state.CompactExit,"close",GameDetailAction.Exit,"GameCompactExit"),Menu("···",null,"more","GameCompactMore"));
        var compact=Row(Action("返回游戏库","back",GameDetailAction.Back,"GameBack"),Label(state.CompactTitle),Action(state.CompactRename,"edit",GameDetailAction.Rename,"GameCompactRename"),compactCapture,compactActions);
        var more=views["GameCompactMore"];bindings.Add(()=>Visible(more,state.Modifying));Add(compact);
        bindings.Add(()=>{Visible(compactActions,state.Modifying);Visible(compactCapture,state.Modifying);compactCapture.IsEnabled=state.CoverEdit.Enabled;Visible(views["GameCompactLaunchArrow"],state.CompactLaunchArrow.Visible);views["GameCompactLaunchArrow"].IsEnabled=state.CompactLaunchArrow.Enabled;});
        var hero=Hero();Add(hero);bindings.Add(()=>Visible(hero,!state.Modifying));
        var status=new WC.Grid();status.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});status.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});
        var statusLabels=Row(Label(state.Status),Label(state.Badge));statusLabels.HorizontalAlignment=W.HorizontalAlignment.Right;WC.Grid.SetColumn(statusLabels,1);status.Children.Add(statusLabels);
        var launches=Row(Action(state.Launch,"play",GameDetailAction.Launch,"GameLaunch",true),Menu("","down","launch","GameLaunchArrow",true),Action(state.Return,"play",GameDetailAction.Return,"GameReturn",true),Action(state.Restart,"refresh",GameDetailAction.Restart,"GameRestart"),Action(state.Exit,"close",GameDetailAction.Exit,"GameExit"),Menu("···",null,"more","GameMore"));
        WC.Grid.SetColumn(launches,0);status.Children.Add(launches);Add(status);
        bindings.Add(()=>{Visible(status,!state.Modifying);Visible(views["GameLaunchArrow"],state.LaunchArrow.Visible);views["GameLaunchArrow"].IsEnabled=state.LaunchArrow.Enabled;});
        Add(Row(Tab(state.TranslationTab,GameDetailAction.TranslationTab,"translate","GameTranslationTab"),Tab(state.ModificationTab,GameDetailAction.ModificationTab,"tool","GameModificationTab")));
        Add(Label(state.ActionFeedback));
        var columns=Register("GameTranslationColumns",new TranslationColumns());columns.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});columns.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});columns.RowDefinitions.Add(new(){Height=W.GridLength.Auto});columns.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        var left=new WC.StackPanel();left.Children.Add(Heading("翻译设置"));
        var profileRow=new WC.Grid{Margin=new(0,8,0,8)};profileRow.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});profileRow.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star),MaxWidth=320});profileRow.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});
        var profileCaption=Text("翻译方案");profileCaption.Margin=new(0,0,12,0);profileRow.Children.Add(profileCaption);Register("GameProfile",profile);WC.Grid.SetColumn(profile,1);profileRow.Children.Add(profile);
        var edit=Action("编辑","edit",GameDetailAction.Profile,"GameProfileEdit");edit.Margin=new(12,0,0,0);WC.Grid.SetColumn(edit,2);profileRow.Children.Add(edit);left.Children.Add(profileRow);
        profile.SelectionChanged+=(_,_)=>{if(!refreshing&&!host.GameDetail.SelectionPending&&profile.SelectedItem is WorkspaceProfileChoice choice)host.NativeSelectWorkspaceProfile(true,choice.Id);};
        profile.PreviewMouseWheel+=(_,e)=>{if(profile.IsDropDownOpen)return;ScrollToVerticalOffset(VerticalOffset-e.Delta);e.Handled=true;};
        var summary=TextView("");bindings.Add(()=>summary.Text=UiLanguage.Text(host.NativeGameProfileSummary));left.Children.Add(summary);
        left.Children.Add(Row(Action(state.ToggleTranslation,"translate",GameDetailAction.Toggle,"GameTranslationToggle",true),Action(state.TranslateLaunch,"play",GameDetailAction.TranslateLaunch,"GameTranslateLaunch",true),Action(state.Retry,"refresh",GameDetailAction.Retry,"GameRetry"),Action(state.Install,"refresh",GameDetailAction.Install,"GameInstall"),Action(state.PrepareText,"translate",GameDetailAction.PrepareText,"GamePrepare")));
        left.Children.Add(Label(state.TranslationStatus));left.Children.Add(Row(Action("译文与术语","edit",GameDetailAction.Manage,"GameManage"),Action("翻译状态",null,GameDetailAction.Diagnostics,"TranslationDiagnostics")));
        var display=new WC.StackPanel();display.Children.Add(Heading("显示设置"));
        display.Children.Add(Row(Text("游戏内字体"),Label(state.FontPreference),Action(state.FontEdit,"edit",GameDetailAction.Font,"GameFontEdit")));
        display.Children.Add(Row(Text("字体大小"),Label(state.FontCapability)));display.Children.Add(Row(Text("译文显示"),Label(state.RenderingCapability)));display.Children.Add(Row(Text("扫描开关"),Label(state.ScanKey),Action(state.ScanKeyEdit,"edit",GameDetailAction.Keys,"GameScanKeyEdit")));
        display.Children.Add(Row(Action(state.ScreenTranslation,"translate",GameDetailAction.Screen,"GameScreenTranslation")));display.Children.Add(Label(state.ScreenStatus));
        columns.Children.Add(Card(left));columns.Children.Add(Card(display));Add(columns);bindings.Add(()=>Visible(columns,!state.Modifying));
        // The page owns vertical scrolling. Tables and detail panes retain
        // their bounded viewports, but an entire editor needs no second shell.
        // Speed is a common modification tool for every engine. Keep its
        // independent backend, but let each editor own its category visibility.
        novel=new(host.Novel,CommonSpeed("NovelCommonSpeed"));renpy=new(host.Renpy,CommonSpeed("RenpyCommonSpeed"));
        var rpg=host.NativeRpgView;Add(rpg);bindings.Add(()=>Visible(rpg,state.RpgModification));
        if(host.NativeRpgCommonSpeedSlot is {} speedSlot)speedSlot.Content=CommonSpeed("RpgCommonSpeed");
        Add(novel);bindings.Add(()=>Visible(novel,state.NovelModification));
        Add(renpy);bindings.Add(()=>Visible(renpy,state.RenpyModification));
        var unsupported=new WC.StackPanel();var common=WpfThemeResources.Button("常用",null,()=>{},true);common.HorizontalAlignment=W.HorizontalAlignment.Left;common.Margin=new(0,0,0,12);
        Register("CommonModificationCategories",common);
        var commonToggle=Register("CommonModificationToggle",WpfThemeResources.Button("开启修改","play",async()=>{if(!host.GameDetail.SelectionPending)await host.NativeToggleCommonModification();},true));
        commonToggle.HorizontalAlignment=W.HorizontalAlignment.Left;commonToggle.Height=36;
        unsupported.Children.Add(commonToggle);
        var commonStatus=Register("CommonModificationStatus",TextView(""));unsupported.Children.Add(commonStatus);
        unsupported.Children.Add(common);
        bindings.Add(()=>{var action=host.NativeCommonModificationAction;commonToggle.IsEnabled=action.Enabled;WpfThemeResources.SetButton(commonToggle,action.Text,"play",true);commonStatus.Text=UiLanguage.Text(host.NativeCommonModificationStatus);});
        unsupported.Children.Add(CommonSpeed("UnsupportedCommonSpeed"));unsupported.Children.Add(Text("此游戏暂未提供可读取的通用修改数据。识别到可用能力后，会在这里显示。"));
        Add(unsupported);bindings.Add(()=>Visible(unsupported,state.UnsupportedModification));
        var diagnostic=Row(Label(state.Diagnostic),Action("查看诊断报告",null,GameDetailAction.Report,"GameDiagnosticReport"));Add(diagnostic);bindings.Add(()=>{Visible(diagnostic,!state.Modifying);views["GameDiagnosticReport"].IsEnabled=state.CanOpenDiagnostic;});
        var files=CreateAction("游戏文件与存档","folder",GameDetailAction.Files,"GameFiles");files.HorizontalAlignment=W.HorizontalAlignment.Left;Add(files);
        var details=new WC.StackPanel();details.Children.Add(Text("存档位置识别尚未接入，可先打开游戏文件夹。"));foreach(var label in new[]{state.Info,state.Query,state.FileStatus,state.Media,state.CoverDetails,state.Feedback,state.Configuration,state.Hint})details.Children.Add(Label(label,true));Add(details);
        bindings.Add(()=>{Visible(files,!state.Modifying);Visible(details,!state.Modifying&&state.FileDetails);WpfThemeResources.SetButton(files,state.FileDetails?"收起游戏文件与存档":"游戏文件与存档","folder");});
        var pathRow=new WC.Grid();pathRow.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star),MaxWidth=720});pathRow.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});Register("GamePath",path);pathRow.Children.Add(path);
        var folder=Action(state.FolderOpen,"folder",GameDetailAction.Folder,"GameFolderOpen");folder.Margin=new(12,0,0,0);WC.Grid.SetColumn(folder,1);pathRow.Children.Add(folder);Add(pathRow);bindings.Add(()=>Visible(pathRow,!state.Modifying));
        Loaded+=(_,_)=>Refresh();
        // An empty grid or an exhausted inner scroller must not swallow the
        // wheel. Observe handled events too, and route only at its boundary.
        AddHandler(System.Windows.Input.Mouse.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler((_,e)=>WpfScrollRouting.BubbleAtBoundary(this,e)),true);
    }
    private T Register<T>(string id,T view) where T:W.FrameworkElement {views.Add(id,view);WA.AutomationProperties.SetAutomationId(view,id);return view;}
    private W.FrameworkElement CommonSpeed(string id)
    {
        var speed=Register(id,new WpfGameSpeedControl{Margin=new(0,8,0,12)});
        speed.Commit=async rate=>
        {
            await host.NativeSetGameSpeed(rate);
            // The page refresh is coalesced. Confirm before the control leaves
            // its pending state, instead of briefly restoring the old rate.
            speed.SetState(host.NativeGameSpeedState);
        };
        var panel=new WC.StackPanel();panel.Children.Add(speed);
        var skip=Register(id+"StorySkip",WpfThemeResources.Button("快进到下一处选择","play",async()=>await host.NativeStorySkip(false)));
        var stop=Register(id+"StorySkipStop",WpfThemeResources.Button("停止快进",null,async()=>await host.NativeStorySkip(true)));
        var story=Row(skip,stop);panel.Children.Add(story);
        var hint=Text("剧情快进会跳过未读对白，遇到选择时停止。倍速无效时可使用此功能。");panel.Children.Add(hint);
        bindings.Add(()=>{speed.SetState(host.NativeGameSpeedState);Visible(story,host.NativeStorySkipVisible);Visible(hint,host.NativeStorySkipVisible);skip.IsEnabled=stop.IsEnabled=host.NativeStorySkipEnabled;});return panel;
    }
    private void Add(W.FrameworkElement view){view.Margin=new(0,0,0,12);content.Children.Add(view);}
    private static void Visible(W.FrameworkElement view,bool show)=>view.Visibility=show?W.Visibility.Visible:W.Visibility.Collapsed;
    private static WC.TextBlock TextView(string value) => new(){Text=UiLanguage.Text(value),TextWrapping=W.TextWrapping.Wrap,VerticalAlignment=W.VerticalAlignment.Center,Margin=new(0,4,0,5)};
    private WC.TextBlock Text(string value)
    {
        var text=TextView(value);
        bindings.Add(()=>text.Text=UiLanguage.Text(value));return text;
    }
    private WC.TextBlock Heading(string value){var text=Text(value);text.FontSize=16;text.FontWeight=W.FontWeights.Bold;return text;}
    private WC.TextBlock Label(DesktopLabelState state,bool hideEmpty=false)
    {
        // Dynamic labels have one binding. A preceding Text("") binding used
        // to clear and restore every label even when the status was unchanged,
        // invalidating text measurement and the surrounding detail layout.
        var text=TextView(state.Text);bindings.Add(()=>{text.Text=UiLanguage.Text(state.Text);Visible(text,state.Visible&&(!hideEmpty||state.Text.Length>0));});return text;
    }
    private static WC.WrapPanel Row(params W.FrameworkElement[] children){var row=new WC.WrapPanel{VerticalAlignment=W.VerticalAlignment.Center};foreach(var child in children){child.Margin=new(0,3,12,3);row.Children.Add(child);}return row;}
    private static WC.Border Card(W.FrameworkElement content){var card=new WC.Border{Child=content,Padding=new(16),BorderThickness=new(1),CornerRadius=new(4)};card.SetResourceReference(WC.Border.BorderBrushProperty,"Fusion.Border");return card;}
    private WC.Button Action(string text,string? icon,GameDetailAction action,string id,bool primary=false,bool overlay=false)
    {
        var button=CreateAction(text,icon,action,id,primary);
        bindings.Add(()=>WpfThemeResources.SetButton(button,text,icon,primary,coverOverlay:overlay&&host.WpfSessionArtworkSource is not null));return button;
    }
    private WC.Button CreateAction(string text,string? icon,GameDetailAction action,string id,bool primary=false)
    {
        var button=Register(id,WpfThemeResources.Button(text,icon,()=>{if(!host.GameDetail.SelectionPending)host.NativeGameAction(action);},primary));button.Height=36;button.MinWidth=text.Length>0?72:36;return button;
    }
    private WC.Button Action(DesktopActionState state,string? icon,GameDetailAction action,string id,bool primary=false,bool overlay=false)
    {
        var button=Register(id,WpfThemeResources.Button(state.Text,icon,()=>{if(!host.GameDetail.SelectionPending)host.NativeGameAction(action);},primary));button.Height=36;button.MinWidth=state.Text.Length>0?72:36;
        // Bind a changing caption once. Replaying the initial caption before
        // the current one rebuilt the button's icon/text on every status tick.
        bindings.Add(()=>{button.IsEnabled=state.Enabled;Visible(button,state.Visible);WpfThemeResources.SetButton(button,state.Text,icon,primary,coverOverlay:overlay&&host.WpfSessionArtworkSource is not null);});return button;
    }
    private WC.Button Tab(DesktopActionState state,GameDetailAction action,string icon,string id)
    {
        var button=Action(state,icon,action,id);button.SetResourceReference(W.FrameworkElement.StyleProperty,"Fusion.TabButton");bindings.Add(()=>{button.Background=WM.Brushes.Transparent;button.BorderThickness=new(0,0,0,state.Primary?2:0);button.SetResourceReference(WC.Control.BorderBrushProperty,"Fusion.Accent");});return button;
    }
    private WC.Button Menu(string text,string? icon,string kind,string id,bool primary=false)
    {
        WC.Button? button=null;button=Register(id,WpfThemeResources.Button(text,icon,()=>{if(!host.GameDetail.SelectionPending)host.NativeGameMenu(kind,button!);},primary));button.Height=36;button.MinWidth=36;bindings.Add(()=>WpfThemeResources.SetButton(button,text,icon,primary));return button;
    }
    private W.FrameworkElement Hero()
    {
        var text=new WC.StackPanel();var row=new HeroTitleRow();row.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto,MaxWidth=680});row.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});row.Children.Add(title);
        var rename=Action("","edit",GameDetailAction.Rename,"GameRename",overlay:true);
        rename.Width=rename.MinWidth=36;rename.Padding=new(7);rename.Margin=new(10,0,0,0);rename.VerticalAlignment=W.VerticalAlignment.Center;
        bindings.Add(()=>{rename.IsEnabled=host.GameDetail.Rename.Enabled;Visible(rename,host.GameDetail.Rename.Visible);rename.ToolTip=UiLanguage.Text(host.GameDetail.Rename.Text);WA.AutomationProperties.SetName(rename,UiLanguage.Text(host.GameDetail.Rename.Text));});
        var capture=Action("截取游戏画面","image",GameDetailAction.CaptureCover,"GameCaptureCover",overlay:true);
        capture.Margin=new(8,0,0,0);capture.VerticalAlignment=W.VerticalAlignment.Center;
        bindings.Add(()=>{capture.IsEnabled=host.GameDetail.CoverEdit.Enabled;});
        var titleActions=new WC.StackPanel{Orientation=WC.Orientation.Horizontal};titleActions.Children.Add(rename);titleActions.Children.Add(capture);
        WC.Grid.SetColumn(titleActions,1);row.Children.Add(titleActions);subtitle.Margin=new(0,8,0,0);text.Children.Add(row);text.Children.Add(subtitle);
        var edit=Action(host.GameDetail.CoverEdit,"image",GameDetailAction.Cover,"GameCoverEdit",overlay:true);
        heroSurface=new(artwork,text,edit){SaveFrame=host.NativeSaveCoverFrame};
        var border=Register("GameHero",new WC.Border{Child=heroSurface,CornerRadius=new(8)});return border;
    }
    internal void Refresh()
    {
        if(refreshing||host.IsDisposed||!IsVisible)return;refreshing=true;
        try
        {
            content.IsHitTestVisible=!host.GameDetail.SelectionPending;
            if(host.GameDetail.SelectionPending)return;
            var section=(host.GamePathText.ToUpperInvariant(),host.GameDetail.Modifying);
            if(displayedSection!=section)
            {
                if(displayedSection is {} previous)sectionScroll[previous]=VerticalOffset;
                if(sectionScroll.Count>128)sectionScroll.Remove(sectionScroll.Keys.First());
                displayedSection=section;int revision=++sectionRevision;
                var saved=sectionScroll.GetValueOrDefault(section);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(()=>
                {
                    if(disposed||revision!=sectionRevision||displayedSection!=section)return;
                    ScrollToVerticalOffset(saved);
                }));
            }
            foreach(var binding in bindings)binding();title.Text=host.WpfSessionTitle;title.ToolTip=title.Text;subtitle.Text=host.WpfSessionSubtitle;path.Text=host.GamePathText;
            heroSurface.SetArtworkSource(host.WpfSessionArtworkSource,host.WpfSessionCoverFrame);subtitle.ToolTip=subtitle.Text;
            title.Foreground=host.WpfSessionArtworkSource is null?WpfDrawing.Brush(UiTheme.Current.Text):WM.Brushes.White;
            subtitle.Foreground=host.WpfSessionArtworkSource is null?WpfDrawing.Brush(UiTheme.Current.SecondaryText):WM.Brushes.Gainsboro;
            Background=WpfDrawing.Brush(UiTheme.Current.Main);var choices=host.NativeWorkspaceProfiles(true);
            if(!choices.SequenceEqual(profileChoices)){profileChoices=choices;profile.ItemsSource=choices;}
            profile.SelectedItem=profileChoices.FirstOrDefault(x=>x.Id==host.NativeWorkspaceProfileId(true));
            novel.Refresh();renpy.Refresh();
        }
        finally{refreshing=false;}
    }
    public void Dispose(){disposed=true;sectionRevision++;host.GameDetailChanged -= refreshQueue.Request;refreshQueue.Dispose();heroSurface.SetArtwork(null);novel.Dispose();renpy.Dispose();}
    private sealed class HeroTitleRow : WC.Grid
    {
        protected override W.Size MeasureOverride(W.Size availableSize)
        {
            if (double.IsFinite(availableSize.Width) && Children.Count > 1 && Children[1] is W.FrameworkElement rename)
            {
                rename.Measure(new(double.PositiveInfinity, availableSize.Height));
                // DesiredSize includes the 12 DIP rename margin. Preserve the
                // previous 16 DIP reserve without using last frame's width.
                double renameWidth = Math.Max(0, rename.DesiredSize.Width - rename.Margin.Left - rename.Margin.Right);
                double maximum = Math.Max(60, availableSize.Width - renameWidth - 16);
                if (ColumnDefinitions[0].MaxWidth != maximum) ColumnDefinitions[0].MaxWidth = maximum;
                // Auto columns can still measure a TextBlock at its untrimmed
                // natural width. Constrain the text itself before measurement.
                if (Children[0] is W.FrameworkElement text && text.MaxWidth != maximum) text.MaxWidth = maximum;
            }
            return base.MeasureOverride(availableSize);
        }
    }
    private sealed class TranslationColumns : WC.Grid
    {
        private bool? stacked;
        protected override W.Size MeasureOverride(W.Size availableSize)
        {
            bool stack = availableSize.Width < 760;
            if (stacked != stack)
            {
                stacked = stack;
                for (int i = 0; i < Children.Count; i++)
                {
                    var view = (W.FrameworkElement)Children[i];
                    WC.Grid.SetRow(view, stack ? i : 0); WC.Grid.SetColumn(view, stack ? 0 : i); WC.Grid.SetColumnSpan(view, stack ? 2 : 1);
                    view.Margin = stack ? new(0, 0, 0, 10) : i == 0 ? new(0, 0, 8, 0) : new(8, 0, 0, 0);
                }
            }
            return base.MeasureOverride(availableSize);
        }
    }
}

public sealed partial class MainForm
{
    internal event Action? GameDetailChanged;
    private void NotifyNativeGameDetailChanged() => GameDetailChanged?.Invoke();
    internal Control[] WpfSessionModules => [];
    internal string WpfSessionTitle => GameTitleText;
    internal string WpfSessionSubtitle => GameChineseNameText;
    internal Image? WpfSessionArtwork => GameCoverImage;
    internal CoverFraming WpfSessionCoverFrame => _libraryArtworkRecord?.CoverFrame??CoverFraming.Centered;
    internal async Task<bool> NativeSaveCoverFrame(CoverFraming frame)
    {
        try
        {
            var record=_libraryArtworkRecord;
            if(record is null||!SameGame(record.ExePath,GamePathText))return false;
            var store=await RecentStore();
            bool saved=await Task.Run(()=>store.SaveCoverFrame(record,frame));
            if(!saved)AppDialog.Show(this,"封面位置","封面已改变，请重新调整位置。",AppDialogKind.Warning);
            else
            {
                // Only advance the crop of the image the user actually edited.
                // A concurrent game/image change must keep its own snapshot.
                if(_libraryArtworkRecord is { } visible&&visible.Id==record.Id&&visible.Cover==record.Cover)
                    _libraryArtworkRecord=visible with{CoverFrame=frame.Normalize()};
                NotifyNativeGameDetailChanged();
            }
            return saved;
        }
        catch(Exception ex){AppDialog.Show(this,"封面位置","无法保存封面位置，请重试。",AppDialogKind.Error,SafeDiagnosticOutput.ExceptionSummary(ex));return false;}
    }
}
