namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly ComboBox _interfaceLanguageBox=new GamePlanBox(){Name="InterfaceLanguage",DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly Label _settingsSaveState=new(){AutoSize=true,Name="SettingsSaveState",Text="设置已保存",Margin=new(0,10,12,0)};
    private readonly GameActionButton _settingsSaveButton=new("保存设置"){Primary=true,Name="SaveDesktopSettings",MinimumLogicalWidth=112};
    private Control? _settingsFooterActions;

    private static readonly string[] SettingsDescriptions =
    [
        "选择舒适的主题、字体和游戏封面。",
        "设置界面语言、游戏启动方式和关闭行为。",
        "管理截图、预览和游戏内扫描的快捷键。",
        "管理 API 连接，以及独立保存的翻译内容。",
        "设置文字识别与翻译图片的处理方式。",
        "调整截图结果的默认内容、窗口和文字。",
        "管理截图记录的保留方式与会话缓存。",
        "设置游戏图片来源、最近记录和中文名称。",
        "查看数据位置，或选择新的存储文件夹。",
        "查看使用说明、版本信息和诊断入口。"
    ];

    private Control BuildSettingsFooter()
    {
        var footer=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Padding=new(28,12,28,8),Margin=Padding.Empty,Name="SettingsFooter"};
        footer.ColumnStyles.Add(new(SizeType.Percent,100));footer.ColumnStyles.Add(new(SizeType.AutoSize));
        _settingsSaveState.Anchor=AnchorStyles.Left;footer.Controls.Add(_settingsSaveState,0,0);
        _settingsSaveButton.Click+=(_,_)=>SaveSettings(true);
        _settingsFooterActions=Stack(_settingsSaveButton);_settingsFooterActions.Anchor=AnchorStyles.Right;
        footer.Controls.Add(_settingsFooterActions,1,0);return footer;
    }
    private void UpdateSettingsSaveState(string? message=null)
    {
        _settingsSaveState.Text=message??(_settingsNavigation.SelectedIndex==3?"API 方案与翻译内容分别保存":_settingsDirty?"有未保存的修改":"设置已保存");
        _settingsSaveButton.Enabled=_settingsLoadSucceeded;
        if(_settingsFooterActions is not null)_settingsFooterActions.Visible=_settingsNavigation.SelectedIndex!=3;
        _settingsSaveState.ForeColor=SettingsColors.Muted;
    }
    private TableLayoutPanel BuildGeneralSettings()
    {
        var table=SettingsTable();
        if(_interfaceLanguageBox.Items.Count==0)_interfaceLanguageBox.Items.AddRange(["简体中文","English","Русский"]);
        _interfaceLanguageBox.SelectedIndex=(int)_translationOptions.InterfaceLanguage;
        _interfaceLanguageBox.SelectedIndexChanged+=SettingsControlChanged;
        AddSettingRow(table,"界面语言",_interfaceLanguageBox);
        AddSettingRow(table,"",Info("保存后切换整个软件的界面语言。游戏译文语言在“翻译方案”中设置。"));
        var launch=new CheckBox{Text="默认翻译并启动游戏",AutoSize=true,Checked=_fusion?.State.DefaultGameLaunchWithTranslation==true};
        launch.CheckedChanged+=(_,_)=>{if(_fusion is null)return;_fusion.Change(next=>next.DefaultGameLaunchWithTranslation=launch.Checked);RenderGameState();UpdateSettingsSaveState("启动方式已保存");};
        AddSettingRow(table,"游戏启动方式",launch);AddSettingRow(table,"关闭主窗口时",_closeBehaviorBox);
        AddSettingRow(table,"",Info("启动方式立即保存。切换游戏时，已经运行的游戏会继续运行。"));return table;
    }

    private SettingsContent ComposeSettingsPage(TableLayoutPanel original,string title,int index)
    {
        var content=new SettingsContent{Name="SettingsContent"+index};content.SuspendLayout();
        var header=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,Margin=Padding.Empty};
        header.ColumnStyles.Add(new(SizeType.Percent,100));
        header.Controls.Add(new Label{Text=title,AutoSize=true,Font=new Font("Microsoft YaHei UI",21,FontStyle.Bold),Margin=new(0,0,0,8),Name="SettingsPageTitle"},0,0);
        header.Controls.Add(new SettingsNote(SettingsDescriptions[index]),0,1);content.AddBlock(header);
        var starts=SectionStarts(index);SettingsSection? section=null;
        var rows=new List<(string title,Control value)>();
        for(int r=0;r<original.RowCount;r++)
        {
            // GetControlFromPosition can omit controls that do not participate
            // in layout. Preserve collapsed editors and their event handlers.
            var caption=original.Controls.Cast<Control>().FirstOrDefault(c=>original.GetColumn(c)==0&&original.GetRow(c)==r);
            var field=original.Controls.Cast<Control>().FirstOrDefault(c=>original.GetColumn(c)==1&&original.GetRow(c)==r);
            if(caption is null||field is null||original.GetColumnSpan(caption)>1)continue;
            rows.Add((caption.Text,field));
        }
        original.SuspendLayout();
        foreach(var (caption,value) in rows)
        {
            if(section is null||starts.TryGetValue(caption,out _))
            {
                section=new SettingsSection(starts.GetValueOrDefault(caption,starts.Values.First())){Name="SettingsGroup"+content.RowCount};section.SuspendLayout();content.AddBlock(section);
            }
            original.Controls.Remove(value);
            var field=PrepareSettingsField(value);
            // Long explanations remain available without dominating the form.
            if(caption=="来源与使用范围")field=new SettingsDisclosure("查看来源与使用范围",field);
            section.AddField(caption,field);
        }
        original.Dispose();
        content.SizeChanged+=(_,_)=>{
            foreach(var label in header.Controls.OfType<Label>())label.MaximumSize=new(Math.Max(1,content.ClientSize.Width-content.Padding.Horizontal),0);
        };
        UiLanguage.BindTree(content);return content;
    }
    private static Dictionary<string,string> SectionStarts(int index)=>index switch
    {
        0=>new(){{"主题","主题与文字"},{"封面样式","游戏库外观"}},
        1=>new(){{"界面语言","语言"},{"游戏启动方式","启动与关闭"}},
        2=>new(){{"开始截图","截图快捷键"},{"预览放大","预览操作"},{"游戏内扫描开关","游戏内快捷键"}},
        3=>new(){{"公共默认方案","默认方案"},{"正在编辑的方案","方案与连接"},{"目标语言","翻译内容"}},
        4=>new(){{"截图时隐藏","截图与识别"},{"翻译图片","翻译图片"},{"译图字体模式","译图文字与背景"}},
        5=>new(){{"默认图片","默认内容"},{"窗口大小","窗口尺寸"},{"文本栏字体模式","文本栏外观"}},
        6=>new(){{"截图历史缩略图","历史列表"},{"数量清理","自动清理"},{"截图会话缓存","缓存"}},
        7=>new(){{"默认图标来源","游戏图片"},{"最近记录","记录与名称"}},
        8=>new(){{"当前数据目录","软件数据"},{"默认位置","存储说明"}},
        _=>new(){{"软件版本","关于 Fusion"},{"首次使用","使用帮助"},{"游戏退出诊断","诊断与数据"}}
    };

    private static Control PrepareSettingsField(Control control)
    {
        // Reparent and style the whole row before asking its layout engine to
        // measure it. Intermediate widths are never displayed.
        control.SuspendLayout();
        try{return PrepareSettingsFieldCore(control);}
        finally{if(!control.IsDisposed)control.ResumeLayout(false);}
    }
    private static Control PrepareSettingsFieldCore(Control control)
    {
        if(control is SourceLanguageEditor editor)
        {
            foreach(Control child in editor.Controls)PrepareSettingsField(child);
            return editor;
        }
        if(control is TextBox text&&!text.Multiline)
        {
            text.MinimumSize=Size.Empty;text.MaximumSize=Size.Empty;
            return new SettingsInputFrame(text){MaximumSize=new(620,0)};
        }
        if(control is NumericUpDown number){number.MinimumSize=Size.Empty;number.MaximumSize=Size.Empty;return new SettingsInputFrame(number);}
        if(control is TextBox multi)return new SettingsTextArea(multi);
        if(control is ComboBox box){box.MaximumSize=new(320,0);box.MinimumSize=new(100,0);if(box.Name.EndsWith("FontFamily",StringComparison.Ordinal))box.Width=240;}
        if(control is Button button&&button is not GameActionButton&&button is not ThemeChoiceButton)
        {
            button.AutoSize=true;button.AutoSizeMode=AutoSizeMode.GrowAndShrink;button.MinimumSize=new(64,36);button.Padding=new(12,3,12,3);
            button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderSize=1;button.UseVisualStyleBackColor=false;
            if(button.Name=="SaveProfile")button.Tag="settings-primary";
        }
        if(control is Label label){label.Margin=new(0,3,0,4);label.Name=string.IsNullOrEmpty(label.Name)?"SettingsHelp":label.Name;}
        if(control is FlowLayoutPanel pair&&pair.Controls.Count==2&&pair.Controls[0] is CheckBox check&&pair.Controls[1] is NumericUpDown numberField)
        {
            pair.Controls.Clear();pair.Dispose();check.Text="启用";return new SettingsValueRow(check,PrepareSettingsField(numberField));
        }
        if(control is FlowLayoutPanel flow)
        {
            flow.AutoSizeMode=AutoSizeMode.GrowAndShrink;flow.WrapContents=true;flow.Margin=Padding.Empty;
            foreach(Control child in flow.Controls.Cast<Control>().ToArray())
            {
                var index=flow.Controls.GetChildIndex(child);var revised=PrepareSettingsField(child);
                if(revised!=child){flow.Controls.Remove(child);flow.Controls.Add(revised);flow.Controls.SetChildIndex(revised,index);}
                revised.Margin=new(0,0,12,0);
            }
        }
        if(control is TableLayoutPanel table&&control is not WorkspaceAlignedRow)
        {
            foreach(Control child in table.Controls.Cast<Control>().ToArray())
            {
                var cell=table.GetCellPosition(child);var columns=table.GetColumnSpan(child);var rows=table.GetRowSpan(child);
                var revised=PrepareSettingsField(child);
                if(revised!=child){table.Controls.Remove(child);table.Controls.Add(revised,cell.Column,cell.Row);table.SetColumnSpan(revised,columns);table.SetRowSpan(revised,rows);}
            }
        }
        return control;
    }
}

internal static class SettingsColors
{
    // Existing day-mode SecondaryText is too pale for small form descriptions.
    internal static Color Muted=>UiTheme.Current.Main.GetBrightness()>.5f?Color.FromArgb(77,94,119):Color.FromArgb(185,199,217);
    internal static double Contrast(Color a,Color b)
    {
        static double L(Color c){static double V(byte n){double v=n/255d;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}return .2126*V(c.R)+.7152*V(c.G)+.0722*V(c.B);}
        double x=L(a),y=L(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);
    }
    internal static Color PrimaryFill
    {
        get{var c=UiTheme.Current.Accent;while(Contrast(Color.White,c)<5)c=Color.FromArgb((int)(c.R*.9),(int)(c.G*.9),(int)(c.B*.9));return c;}
    }
    internal static Color AccentText=>UiTheme.Current.Main.GetBrightness()>.5f?PrimaryFill:UiTheme.Current.Accent;
    internal static void Apply(Control root)
    {
        foreach(var c in UiLayoutBatch.Walk(root))
        {
            var section=c is SettingsSection||HasSectionParent(c);var p=UiTheme.Current;
            if(c is FusionNavigation)c.BackColor=p.Main;
            if(c is SettingsPage or TextBoxBase or ComboBox)SettingsNativeTheme.Apply(c);
            if(section&&c is not Button&&c is not ComboBox&&c is not TextBoxBase&&c is not NumericUpDown)c.BackColor=p.Secondary;
            if(c is SettingsInputFrame frame){frame.BackColor=p.Secondary;frame.Field.BackColor=p.Secondary;}
            if(c is Label label&&(c is SettingsNote||c.Name=="SettingsHelp"||c.Name=="SettingsSaveState"))label.ForeColor=Muted;
            if(c is GameActionButton action&&action.Name=="SaveDesktopSettings")action.PrimaryFill=PrimaryFill;
            if(c is Button b&&Equals(b.Tag,"settings-primary")){b.BackColor=PrimaryFill;b.ForeColor=Color.White;b.FlatAppearance.MouseOverBackColor=ControlPaint.Dark(PrimaryFill,.08f);}
        }
    }
    private static bool HasSectionParent(Control c){for(var p=c.Parent;p is not null;p=p.Parent)if(p is SettingsSection)return true;return false;}
}
