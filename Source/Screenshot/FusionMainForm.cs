using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private FusionConfiguration? _fusion;
    private string? _editingProfile;
    private bool _loadingFusion;
    private readonly ComboBox _profileEditor = new(){DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly ComboBox _commonProfile = new(){DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly ComboBox _shotProfile = new GamePlanBox();
    private readonly GamePlanBox _embeddedProfile = new(){DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly ComboBox _embeddedKey = new(){DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly Label _fusionOcrInfo = new(){AutoSize=true,MaximumSize=new(560,0)};
    private readonly Label _shotEffective = new(){AutoSize=true,MaximumSize=new(680,0)};
    private readonly Label _embeddedEffective = new(){AutoSize=true,MaximumSize=new(680,0)};
    private readonly WorkspaceThemeButton _themeIcon = new();
    private readonly ToolTip _fusionTips = new();
    private readonly FusionNavigation _settingsNavigation = new();
    private readonly Dictionary<string,Button> _navigationButtons = new();
    private readonly FusionAdapterInstaller _adapter = new();
    private GameInfo? _selectedGame;
    private readonly TextBox _gamePath = new(){ReadOnly=true};
    private readonly Label _gameInfo = new(){AutoSize=true,MaximumSize=new(680,0)};
    private readonly Label _gameStatus = new(){AutoSize=true,MaximumSize=new(680,0)};
    private Button? _installGameButton,_launchGameButton,_restoreGameButton;
    private readonly System.Windows.Forms.Timer _gameStateTimer=new(){Interval=2000};
    private bool _gameActionRunning => _selectedGame is { } game && _gameSessions.Find(game.ExePath)?.Busy==true;
    private const string FollowCommon = "跟随公共方案";
    public ApiSettings CurrentSettings => _fusion?.Effective(UiSettings,false) ?? UiSettings;
    private ApiSettings EmbeddedSettings => _selectedGame is { } game ? _fusion!.GameSettings(UiSettings,game.ExePath) : _fusion!.Effective(UiSettings,true);

    private void BuildFusionShell()
    {
        // The one-pixel frame is the only outer inset. Navigation and its
        // background reach the client edges; resizing still uses frame hit tests.
        Padding=new Padding(1);
        try{_libraryPreferences=LibraryPreferencesStore.Load();}catch(Exception ex){AppLog.Write("library-settings",SafeDiagnosticOutput.ExceptionSummary(ex));}
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
        root.ColumnStyles.Add(new(SizeType.Percent,100));
        root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.Absolute,22));
        var sidebar=new LibrarySidebar{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new(0,12,0,0),Margin=Padding.Empty};_libraryNavigationHost=sidebar;
        sidebar.ColumnStyles.Add(new(SizeType.Percent,100));sidebar.RowStyles.Add(new(SizeType.Absolute,30));sidebar.RowStyles.Add(new(SizeType.Percent,100));
        var navigationTitle=WorkspaceLabel("游戏导航");navigationTitle.Margin=new(14,4,0,4);
        _libraryNavigation.Margin=Padding.Empty;
        sidebar.Controls.Add(navigationTitle,0,0);sidebar.Controls.Add(_libraryNavigation,0,1);
        _libraryNavigation.Navigate+=NavigateLibrary;
        _libraryNavigation.ContextRequested+=ShowLibraryFolderContext;
        FormClosed+=(_,_)=>_folderContext.Dispose();
        var content=_mainContentHost;content.Padding=Padding.Empty;content.Margin=Padding.Empty;content.Name="MainPageHost";
        _workspaceCaption=new WorkspaceCaption(this){Dock=DockStyle.Fill,Margin=Padding.Empty};
        foreach(var pair in new[]{("内嵌翻译","游戏库"),("主页","截图翻译"),("设置","设置")})
        {
            var key=pair.Item1;var button=new WorkspaceTab(pair.Item2){Width=88,Height=40,Margin=Padding.Empty};
            button.Click+=(_,_)=>{ShowPage(key);if(_currentMainPage==key&&key!="设置")ClearProfileReturn();if(key=="内嵌翻译"&&_currentMainPage==key)ShowLibraryHome();};
            _navigationButtons[key]=button;
            var page=new BufferedPage{Dock=DockStyle.Fill,AutoScroll=true,Visible=true,Margin=Padding.Empty};_pages[key]=page;content.Controls.Add(page);AttachHostDiagnostics(page,"MainPage:"+key);
        }
        var tabs=new FlowLayoutPanel{AutoSize=false,Width=300,Height=40,WrapContents=false,Margin=Padding.Empty};
        foreach(var button in _navigationButtons.Values)tabs.Controls.Add(button);
        _workspaceCaption.Controls.Add(tabs);_workspaceCaption.Controls.Add(_themeIcon);
        _themeIcon.FlatAppearance.BorderSize=0;_themeIcon.Click+=(_,_)=>ToggleTheme();
        _workspaceCaption.Layout+=(_,_)=>{int d=DeviceDpi;tabs.SetBounds(238*d/96,0,Math.Min(300*d/96,Math.Max(0,_workspaceCaption.Width-416*d/96)),_workspaceCaption.Height);_themeIcon.SetBounds(_workspaceCaption.Width-178*d/96,2*d/96,36*d/96,_workspaceCaption.Height-4*d/96);};
        // The sidebar belongs to the library page. Its width stays reserved even
        // while the page is hidden, so publishing the page never first displays
        // its cards at the full-window width and then pushes them to the right.
        var libraryPage=_pages["内嵌翻译"];libraryPage.AutoScroll=false;
        var libraryLayout=new WorkspaceLibrarySplit{Dock=DockStyle.Fill,Margin=Padding.Empty,LogicalSidebarWidth=_libraryPreferences.SidebarWidth};_librarySplit=libraryLayout;
        var libraryContent=new BufferedPage{Dock=DockStyle.Fill,AutoScroll=true,Margin=Padding.Empty};
        var libraryRightFrame=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
        libraryRightFrame.ColumnStyles.Add(new(SizeType.Percent,100));libraryRightFrame.RowStyles.Add(new(SizeType.Percent,100));libraryRightFrame.RowStyles.Add(new(SizeType.Absolute,22));
        libraryRightFrame.Controls.Add(libraryContent,0,0);
        libraryLayout.Panel1.Controls.Add(sidebar);libraryLayout.Panel2.Controls.Add(libraryRightFrame);libraryPage.Controls.Add(libraryLayout);
        libraryLayout.WidthCommitted+=width=>{try{var next=LibraryPreferencesStore.Load() with{SidebarWidth=width};LibraryPreferencesStore.Save(next);_libraryPreferences=next;UpdateWorkspaceNavigation();}catch(Exception ex){ShowFusionError(ex);}};
        BuildScreenshotWorkspace(_pages["主页"]);BuildGameLibraryWorkspace(libraryContent);
        _pages["设置"].Padding=new(20,18,20,12);BuildSettingsPage(_pages["设置"]);ReorganizeFusionSettings(_pages["设置"]);
        _statusLabel.Dock=DockStyle.Fill;_statusLabel.Margin=Padding.Empty;_statusLabel.TextAlign=ContentAlignment.MiddleLeft;_statusLabel.Padding=new(20,0,0,0);_statusLabel.Font=new Font("Microsoft YaHei UI",8);_statusLabel.Text="就绪";
        root.Controls.Add(content,0,0);root.Controls.Add(_statusLabel,0,1);
        void PlaceStatus()
        {
            bool library=libraryPage.Visible;var host=library?libraryRightFrame:root;
            root.SuspendLayout();libraryRightFrame.SuspendLayout();
            try
            {
                root.RowStyles[1].Height=library?0:22*DeviceDpi/96f;
                libraryRightFrame.RowStyles[1].Height=22*DeviceDpi/96f;
                if(_statusLabel.Parent!=host)host.Controls.Add(_statusLabel,0,1);
            }
            finally{libraryRightFrame.ResumeLayout(true);root.ResumeLayout(true);}
        }
        libraryPage.VisibleChanged+=(_,_)=>PlaceStatus();DpiChanged+=(_,_)=>PlaceStatus();
        var frame=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};frame.ColumnStyles.Add(new(SizeType.Percent,100));frame.RowStyles.Add(new(SizeType.Absolute,40));frame.RowStyles.Add(new(SizeType.Percent,100));
        frame.Controls.Add(_workspaceCaption,0,0);frame.Controls.Add(root,0,1);Controls.Add(frame);ShowPage("内嵌翻译");PlaceStatus();
        _trayIcon.Text="游戏翻译 · Fusion R1";ConfigureFusionInputs(this);
        _gameStateTimer.Tick+=(_,_)=>{UpdateGameDiagnosticSnapshots();if(Visible&&!_workspaceResizing&&_currentMainPage=="内嵌翻译"){RefreshGameState();_=RefreshOtherGameSessionsAsync();_=RefreshLibraryWindowNamesAsync();_=RefreshGameDataStatusAsync();}};
        _gameStateTimer.Start();FormClosed+=(_,_)=>_gameStateTimer.Dispose();Activated+=(_,_)=>{_gameForceQuery=true;RefreshGameState();};
    }

    internal static void ConfigureFusionInputs(Control root)
    {
        if(root is ComboBox box&&box.DropDownStyle==ComboBoxStyle.DropDownList)
        {
            WorkspaceWheelGuard.Attach(box);
            box.DrawMode=DrawMode.OwnerDrawFixed;
            void Metrics(){box.MinimumSize=new(box.MinimumSize.Width,0);if(box is GamePlanBox plan)plan.RefreshMetrics();else box.ItemHeight=Math.Max(box.Font.Height+6*box.DeviceDpi/96,24*box.DeviceDpi/96);}
            Metrics();box.FontChanged+=(_,_)=>Metrics();box.DpiChangedAfterParent+=(_,_)=>Metrics();
            box.DrawItem+=(_,e)=>{
                var p=UiTheme.Current;var selected=(e.State&DrawItemState.Selected)!=0;
                using var background=new SolidBrush(selected?p.Control:p.Secondary);e.Graphics.FillRectangle(background,e.Bounds);
                var value=e.Index>=0&&e.Index<box.Items.Count?box.Items[e.Index]?.ToString():box.Text;
                TextRenderer.DrawText(e.Graphics,value,box.Font,new Rectangle(e.Bounds.X+5,e.Bounds.Y,e.Bounds.Width-8,e.Bounds.Height),box.Enabled?p.Text:p.SecondaryText,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            };
        }
        foreach(Control child in root.Controls)ConfigureFusionInputs(child);
    }
    private void BuildFusionHome(Panel page)
    {
        var table=SettingsTable();table.ColumnStyles[0].Width=150;
        var title=PageTitle("截图翻译");AddSettingRow(table,"",title);
        AddSettingRow(table,"",Info("截取屏幕上的文字，在预览中查看原图、译文与翻译图片。"));
        var capture=MakeButton("开始截图",Point.Empty,new(148,42));capture.Click+=(_,_)=>BeginCapture();
        var open=MakeButton("打开图片",Point.Empty,new(132,42));open.Click+=(_,_)=>{
            using var dialog=new OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp",Title="打开需要翻译的图片"};
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            try{using var source=new Bitmap(dialog.FileName);CreateAndShowPreview(new CaptureResult(new Bitmap(source),PreviewMode.OcrAndTranslate));}
            catch(Exception ex){ShowFusionError(ex);}
        };
        AddSettingRow(table,"",Stack(capture,open));AddSettingRow(table,"使用的翻译方案",_shotProfile);AddSettingRow(table,"实际生效",_shotEffective);AddModeEdit(table,false);
        _homeOcrStatus=Info("文字识别在本机进行，按任务需要加载。");_homeApiStatus=Info("翻译方案加载中");
        AddSettingRow(table,"识别",_homeOcrStatus);AddSettingRow(table,"翻译",_homeApiStatus);
        AddSettingRow(table,"使用方法",Info("按截图快捷键 → 框选或全屏 → 确认。Esc 取消当前截图。\n结果会在独立预览窗口中显示；历史记录保存在本候选自己的数据目录。"));
        page.Controls.Add(table);
    }
    private void BuildFusionEmbedded(Panel page)
    {
        var table=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,Padding=new(8),Name="EmbeddedWorkspace"};
        table.ColumnStyles.Add(new(SizeType.Percent,100));
        void Row(Control c){c.Anchor=AnchorStyles.Left|AnchorStyles.Right;c.Margin=new(0,3,0,6);table.Controls.Add(c,0,table.RowCount++);}
        Button Small(string text)=>new GameActionButton(text);
        void Secondary(Button button){if(button is GameActionButton action)action.RefreshMetrics();}
        foreach(var button in new[]{_recentButton,_coverButton,_folderButton})Secondary(button);
        var title=new Label{Text="内嵌翻译",AutoSize=true,Font=new Font("Microsoft YaHei UI",15,FontStyle.Bold),Margin=new(0,3,18,6)};
        Row(Stack(title,_recentButton));
        var gameHeader=new TableLayoutPanel{AutoSize=true,ColumnCount=2};
        gameHeader.ColumnStyles.Add(new(SizeType.AutoSize));gameHeader.ColumnStyles.Add(new(SizeType.Percent,100));
        _selectedGameIcon.Margin=new(0,3,12,3);gameHeader.Controls.Add(_selectedGameIcon,0,0);
        _gameTitle.Text="选择游戏";_gameTitle.Font=new Font("Microsoft YaHei UI",12,FontStyle.Bold);
        _gameTitle.Margin=new(0,2,10,3);_gameBadge.Margin=new(0,4,0,3);
        var gameText=new TableLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,ColumnCount=1};gameText.ColumnStyles.Add(new(SizeType.Percent,100));
        gameText.Controls.Add(Stack(_gameTitle,_gameBadge));
        _gamePath.AccessibleName="游戏主程序完整路径，可选择复制";_gamePath.Dock=DockStyle.Fill;_gamePath.BorderStyle=BorderStyle.None;
        gameText.Controls.Add(_gamePath);gameHeader.Controls.Add(gameText,1,0);Row(gameHeader);
        _chooseGameButton=Small("选择游戏");_chooseGameButton.Click+=(_,_)=>{
            using var dialog=new OpenFileDialog{Filter="游戏程序|*.exe",Title="选择游戏主程序"};
            if(dialog.ShowDialog(this)==DialogResult.OK)SelectFusionGame(dialog.FileName);
        };
        Row(Stack(_chooseGameButton,_folderButton,_saveGameButton,_coverButton));Row(_mediaStatus);Row(_gameSelectionHint);
        var profileRow=new FlowLayoutPanel{AutoSize=true,WrapContents=true,Margin=Padding.Empty};
        var profileLabel=new Label{Text="翻译方案",AutoSize=true,Margin=new(0,6,10,0)};
        _embeddedProfile.Width=270;_embeddedProfile.MinimumSize=new(120,0);_embeddedProfile.Margin=new(0,3,8,3);
        var edit=Small("编辑");edit.Click+=(_,_)=>EditModeProfile(true);
        var planPair=Stack(_embeddedProfile,edit);planPair.WrapContents=false;planPair.Margin=Padding.Empty;profileRow.Controls.AddRange([profileLabel,planPair]);Row(profileRow);
        void AlignPlan(){_embeddedProfile.RefreshMetrics();edit.Height=_embeddedProfile.Height;_embeddedProfile.Margin=edit.Margin;profileLabel.Margin=new(0,Math.Max(0,(_embeddedProfile.Height-profileLabel.Height)/2)+edit.Margin.Top,10,0);}
        _embeddedProfile.FontChanged+=(_,_)=>AlignPlan();_embeddedProfile.SizeChanged+=(_,_)=>{if(edit.Height!=_embeddedProfile.Height)edit.Height=_embeddedProfile.Height;};profileRow.Layout+=(_,_)=>AlignPlan();
        _embeddedEffective.Font=new Font("Microsoft YaHei UI",9);Row(_embeddedEffective);
        _installGameButton=Small("安装翻译");_launchGameButton=Small("启动游戏");_restoreGameButton=Small("恢复安装前状态");
        
        _installGameButton.Click+=async(_,_)=>await RunGameActionAsync("install");
        _launchGameButton.Click+=async(_,_)=>await RunGameActionAsync("launch");
        _restoreGameButton.Click+=async(_,_)=>await RunGameActionAsync("restore");
        var launchPair=Stack(_launchGameButton,_restoreGameButton);launchPair.WrapContents=false;launchPair.Margin=Padding.Empty;
        Row(Stack(_installGameButton,launchPair));Row(_gameActionHint);
        Row(_gameStatus);Row(_gameFeedback);
        var details=new TableLayoutPanel{AutoSize=true,ColumnCount=1,Visible=false};details.ColumnStyles.Add(new(SizeType.Percent,100));
        void Detail(Control c){c.Anchor=AnchorStyles.Left|AnchorStyles.Right;c.Margin=new(0,3,0,5);details.Controls.Add(c,0,details.RowCount++);}
        Detail(_gameConfiguration);Detail(_gameInfo);Detail(_gameQueryDetails);Detail(_coverDetails);
        Detail(Info("保存方案不代表 API 连接或游戏内翻译已验证。配置在游戏下次启动后生效。退出软件不会关闭游戏。"));
        Detail(Info("封面只取已确认的游戏窗口；不保证主菜单。加载页可手动替换，取图失败时保留旧图。"));
        var logs=Small("打开日志目录");logs.Click+=(_,_)=>{try{var folder=Path.Combine(AppDataPaths.Root,"logs");if(Directory.Exists(folder))Process.Start(new ProcessStartInfo(folder){UseShellExecute=true});}catch(Exception ex){ShowFusionError(ex);}};Detail(Stack(logs));
        var expand=Small("查看详情 ▾");expand.FlatAppearance.BorderSize=0;
        expand.Click+=(_,_)=>{details.Visible=!details.Visible;expand.Text=details.Visible?"收起详情 ▴":"查看详情 ▾";};Row(Stack(expand));Row(details);
        table.SizeChanged+=(_,_)=>{
            var width=Math.Max(120,table.ClientSize.Width-table.Padding.Horizontal-8);
            foreach(var label in new[]{_embeddedEffective,_gameStatus,_gameConfiguration,_gameActionHint,_gameInfo,_gameQueryDetails,_gameSelectionHint,_mediaStatus,_gameFeedback,_coverDetails})label.MaximumSize=new(width,0);
            foreach(var label in details.Controls.OfType<Label>())label.MaximumSize=new(width,0);
            _embeddedProfile.Width=Math.Max(120*DeviceDpi/96,Math.Min(300*DeviceDpi/96,width-profileLabel.PreferredWidth-edit.Width-32*DeviceDpi/96));
        };
        foreach(var control in new Control[]{page,table,_gamePath}){control.AllowDrop=true;
            control.DragEnter+=(_,e)=>{if(!_gameActionRunning&&e.Data?.GetDataPresent(DataFormats.FileDrop)==true)e.Effect=DragDropEffects.Copy;};
            control.DragDrop+=(_,e)=>{if(e.Data?.GetData(DataFormats.FileDrop) is string[] files&&files.Length>0)SelectFusionGame(files[0]);};}
        page.Controls.Add(table);InitializeRecentUi();RenderGameState();
    }
    private void ReorganizeFusionSettings(Panel page)
    {
        // Preserve the existing controls and their binding/capture handlers, replace only their layout.

        var tabs=new SettingsPageHost{Dock=DockStyle.Fill};
        _settingsTabs=tabs;
        void Add(string name,Func<TableLayoutPanel> build){
            var placeholder=SettingsTable();var tab=new SettingsPage(name){AutoScroll=true,Tag=placeholder};tab.Controls.Add(placeholder);tabs.TabPages.Add(tab);_settingsNavigation.Items.Add(name);
            _settingsBuilders.Add(()=>{var content=build();content.Font=new Font("Microsoft YaHei UI",10);UiTheme.Apply(content,_appliedDesktopSettings);FontManager.ApplyUi(content,_appliedDesktopSettings);
                content.Scale(new SizeF(DeviceDpi/96f,DeviceDpi/96f));tab.Controls.Remove(placeholder);placeholder.Dispose();tab.Tag=content;tab.Controls.Add(content);AttachHostDiagnostics(content,"SettingsRoot:"+name);ConfigureFusionInputs(content);});
        }
        Add("外观",BuildLibraryAppearance);
        Add("软件通用",()=>{var general=SettingsTable();
        var defaultLaunch=new CheckBox{Text="默认翻译并启动游戏",AutoSize=true,Checked=_fusion?.State.DefaultGameLaunchWithTranslation==true};
        defaultLaunch.CheckedChanged+=(_,_)=>{if(_fusion is not null){_fusion.Change(next=>next.DefaultGameLaunchWithTranslation=defaultLaunch.Checked);RenderGameState();}};
        AddSettingRow(general,"游戏启动方式",defaultLaunch);AddSettingRow(general,"关闭主窗口时",_closeBehaviorBox);
        AddSettingRow(general,"",Info("普通启动默认关闭翻译。切换到其他游戏不会终止已有游戏。"));return general;});
        Add("按键",()=>{var keys=SettingsTable();
        foreach(var pair in new[]{("开始截图",InputActionId.StartCapture),("取消当前截图",InputActionId.CancelCapture),("显示 / 隐藏截图结果",InputActionId.ToggleResults),("预览放大",InputActionId.PreviewZoomIn),("预览缩小",InputActionId.PreviewZoomOut),("预览恢复适应",InputActionId.PreviewResetFit)})
            AddSettingRow(keys,pair.Item1,BuildBindingEditor(pair.Item2));
        AddSettingRow(keys,"游戏内扫描开关",_embeddedKey);AddSettingRow(keys,"",Info("在游戏内生效，默认 F8。停止新扫描后，排队/在途请求可能完成。\n修改在下次启动游戏时生效；这不是取消所有翻译。"));return keys;});
        Add("翻译方案",()=>{var table=BuildProfileSettings();if(_draftOriginal is null)LoadEditedProfile();return table;});
        Add("截图处理",()=>{var shot=SettingsTable();AddSettingRow(shot,"截图时隐藏",Stack(_hideMainDuringCaptureBox,_hidePreviewsDuringCaptureBox));AddSettingRow(shot,"OCR 引擎",_ocrEngineBox);AddSettingRow(shot,"识别负载",_ocrLoadBox);AddSettingRow(shot,"识别语言",_ocrLanguageBox);AddSettingRow(shot,"已安装语言",_fusionOcrInfo);AddSettingRow(shot,"文字整理",_cleanupStrengthBox);AddSettingRow(shot,"翻译输入",_translationSourceBox);
        AddSettingRow(shot,"翻译图片",_imageTranslationBox);AddSettingRow(shot,"背景处理",_backgroundTreatmentBox);AddSettingRow(shot,"精细背景计算",_backgroundComputeBox);AddSettingRow(shot,"",Info("均衡不加载重型背景模型；识别负载与背景计算分别控制。"));AddSettingRow(shot,"译图字体模式",_overlayFontModeBox);AddSettingRow(shot,"译图字体",_overlayFontBox);AddSettingRow(shot,"译图字号",Stack(_overlayFontSizeBox,Info("0 表示自动适配")));AddSettingRow(shot,"译图背景策略",_overlayBackgroundBox);AddSettingRow(shot,"底板透明度",_overlayOpacityBox);return shot;});
        Add("预览窗口",()=>{var preview=SettingsTable();AddSettingRow(preview,"默认图片",_previewDefaultImageBox);AddSettingRow(preview,"默认文本",_previewDefaultTextBox);AddSettingRow(preview,"文本栏",_previewTextPanelVisibleBox);AddSettingRow(preview,"记住选择",_rememberLastPreviewBox);AddSettingRow(preview,"窗口大小",_previewSizingBox);AddSettingRow(preview,"固定宽度 / 高度",Stack(_fixedPreviewWidthBox,_fixedPreviewHeightBox));AddSettingRow(preview,"文本栏字体模式",_previewFontModeBox);AddSettingRow(preview,"文本栏字体 / 大小",Stack(_previewFontBox,_previewFontSizeBox));AddSettingRow(preview,"",Info("这些选项只影响截图预览窗口。译图中的文字样式在“截图处理”中设置。"));return preview;});
        Add("历史与缓存",()=>{var history=SettingsTable();AddSettingRow(history,"截图历史缩略图",_historyThumbnailBox);AddSettingRow(history,"历史文字大小",_historyTextSizeBox);AddSettingRow(history,"数量清理",Stack(_historyByCountBox,_historyLimitBox));AddSettingRow(history,"保存天数",Stack(_historyByAgeBox,_historyDaysBox));AddSettingRow(history,"空间上限（MB）",Stack(_historyBySpaceBox,_historySpaceBox));AddSettingRow(history,"截图会话缓存",Stack(_ocrCacheBox,_translationCacheBox));AddSettingRow(history,"",Info("这里的历史清理只处理本候选的截图记录，不清理游戏内翻译缓存。"));return history;});
        Add("游戏库与图片",BuildArtworkSettings);
        Add("存储位置",BuildStorageSettings);
        Add("帮助与诊断",()=>{var help=SettingsTable();AddSettingRow(help,"候选版本",Info(BuildIdentity.ProductVersion));AddSettingRow(help,"游戏退出诊断",BuildGameDiagnosticHelp());AddSettingRow(help,"数据配置",_configPathLabel);AddSettingRow(help,"密钥状态",_keyStatusLabel);AddSettingRow(help,"运行依赖",Info("只读复用："+FusionRuntime.Root+"\n需要保留原 R4 运行环境；本候选不是独立便携包。"));AddSettingRow(help,"内嵌边界",Info("MGI 保留原适配；Cloud Meadow / Unity Mono 为待用户验证候选。其他游戏和版本不承诺兼容。\n游戏内配置需要密钥字段；桌面配置使用当前 Windows 用户加密。"));return help;});
        page.Controls.Clear();
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2};layout.ColumnStyles.Add(new(SizeType.Absolute,174));layout.ColumnStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,48));
        _settingsNavigation.SelectedIndexChanged+=(_,_)=>SettingsCategoryChanged();
        layout.Controls.Add(_settingsNavigation,0,0);layout.Controls.Add(tabs,1,0);var footer=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new(8,4,0,0)};var save=MakeButton("保存设置",Point.Empty,new(125,36));save.Click+=(_,_)=>SaveSettings(true);footer.Controls.Add(save);_settingsSaveFooter=footer;layout.Controls.Add(footer,1,1);page.Controls.Add(layout);_loadingFusion=true;_settingsNavigation.SelectedIndex=0;_loadingFusion=false;_settingsNavigation.Commit(0);
        _profileEditor.SelectedIndexChanged+=(_,_)=>SwitchEditingProfile();
        foreach(var box in new[]{_commonProfile,_shotProfile,_embeddedProfile})box.SelectedIndexChanged+=(_,_)=>PersistChoice(box);
        _embeddedKey.SelectedIndexChanged+=(_,_)=>{if(!_loadingFusion)_settingsDirty=true;};
    }
    private void RefreshFusionChrome()
    {
        if(_fusion is null)return;
        var day=UiTheme.Current!=ThemePalette.Night;
        _themeIcon.ShowMoon=day;_themeIcon.AccessibleName=day?"切换到夜间模式":"切换到日间模式";_fusionTips.SetToolTip(_themeIcon,_themeIcon.AccessibleName);
        var p=UiTheme.Current;
        UpdateGalleryChrome();
        if(_plainLaunchButton is not null){_plainLaunchButton.BackColor=p.Accent;_plainLaunchButton.ForeColor=Color.White;}
        _gameBadge.ForeColor=_embeddedEffective.ForeColor=_gameConfiguration.ForeColor=p.SecondaryText;
        if(_launchGameButton is not null){_launchGameButton.BackColor=p.Control;_launchGameButton.ForeColor=p.Text;}
        foreach(var pair in _navigationButtons){if(pair.Value is WorkspaceTab nav){nav.Selected=pair.Key==_currentMainPage;nav.Invalidate();}}
        UpdateWorkspaceNavigation();
        string Summary(bool embedded){var id=_fusion.SelectedId(embedded);var s=_fusion.State.Profiles[id];var missing=FusionConfiguration.Missing(s);return _fusion.Name(id)+"  ·  "+s.Model+"  ·  "+UiTargetLanguageDisplay.DisplayFromCode(s.TargetLanguage)+(missing.Count>0?"\n未配置完成："+string.Join("、",missing):"\n已保存配置；连接需单独测试。")+(embedded?" 游戏下次启动时应用。":" 新截图任务使用。");}
        SetLabelText(_shotEffective,Summary(false));
        var embedded=EmbeddedSettings;
        SetLabelText(_embeddedEffective,string.IsNullOrWhiteSpace(embedded.Model)?"尚未配置翻译方案":embedded.Model+" · "+UiTargetLanguageDisplay.DisplayFromCode(embedded.TargetLanguage)+(_selectedGame is null&&_fusion.State.EmbeddedProfile is null?" · 跟随默认":""));
        var plan=FusionConfiguration.EditIdentity(embedded)+"|"+_fusion.State.EmbeddedToggleKey;
        if(_gamePlanIdentity!=plan){_gamePlanIdentity=plan;InvalidateGameState();if(_desktopReady)ScheduleLibraryRefresh();}
        if(_homeApiStatus is not null)_homeApiStatus.Text=FusionConfiguration.Missing(CurrentSettings).Count>0?"方案未配置完成，仍可仅识别文字。":"已配置 · "+CurrentSettings.Model;
    }
    private void AddModeEdit(TableLayoutPanel table,bool embedded)
    {
        var edit=MakeButton("编辑此方案",Point.Empty,new(135,36));edit.AutoSize=true;edit.MinimumSize=new(135,36);edit.Click+=(_,_)=>EditModeProfile(embedded);AddSettingRow(table,"",Stack(edit));
    }
    private string _gameOperationResult="";
    private void ShowFusionError(Exception ex){var safe=SafeDiagnosticOutput.ExceptionSummary(ex);_statusLabel.Text=safe;AppLog.Write("fusion",safe);}
}

