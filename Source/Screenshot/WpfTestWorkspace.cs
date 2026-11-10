using System.Globalization;
using System.Text;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

// An independent workbench. Network trials run only on an explicit click. It owns no game session, translation queue,
// character preference or cache. Merely visiting it cannot change a game.
internal sealed class WpfTestWorkspace : WC.UserControl, IDisposable
{
    internal static readonly (string Code, string Name)[] Languages = [
        ("zh-CN","简体中文"),("zh-TW","繁體中文"),("en","English"),("ja","日本語"),
        ("ko","한국어"),("es","Español"),("fr","Français"),("de","Deutsch"),("ru","Русский")];
    private readonly WC.ComboBox family;
    private readonly WC.ComboBox mode,defaultFont;
    private readonly WC.ComboBox scenario, sampleLanguage;
    private readonly WpfEngineTestPanel engineTestPanel;
    private readonly WpfCharacterTestPanel characterTestPanel;
    private readonly WC.TextBlock layoutStatus = NativeDialogControls.Text("");
    private readonly WC.TextBlock missingHint = NativeDialogControls.Text("缺字用 □ 标记，展开可查看详情。");
    private bool loadingSample;
    private string customText="";
    private readonly WC.Grid fontSelectionGroup=new(),previewLayout=new();
    private readonly WC.WrapPanel fontActions=new();
    private readonly WC.StackPanel inputPane=new(),previewPane=new();
    private readonly WC.Expander coverageDetails=new(),comparisonDetails=new();
    private readonly WC.TextBlock coverageDetailText=NativeDialogControls.Text("");
    private readonly WC.StackPanel comparisonDetailRows=new();
    private readonly WC.TextBlock defaultHint;
    private readonly WC.TextBlock choiceLabel;
    private readonly WC.Grid fontChoices=new();
    private readonly Dictionary<string,GameFontCoverage.Result> coverageCache=new(StringComparer.Ordinal);
    private string customPath="";
    private readonly WC.TextBox path = NativeDialogControls.Input("", readOnly:true);
    private readonly WC.TextBox input = NativeDialogControls.Input("012 Abc あいうえお 你好 Привет!\n" + GameFontPreview.Sample("zh-CN"), true);
    private readonly WC.TextBlock status = NativeDialogControls.Text("");
    private readonly WC.TextBlock inputCoverage = NativeDialogControls.Text("");
    private readonly WC.TextBlock preview = new() { TextWrapping= W.TextWrapping.Wrap, Padding=new(12), MinHeight=90 };
    private readonly WC.Grid comparisons = new();
    private readonly WC.StackPanel engineRows = new();
    private readonly WC.Slider size = new(){Minimum=10,Maximum=48,Value=24,TickFrequency=1,IsSnapToTickEnabled=true,MinWidth=120,VerticalAlignment=W.VerticalAlignment.Center};
    private readonly WC.Slider spacing = new(){Minimum=1,Maximum=2.5,Value=1.5,TickFrequency=.1,IsSnapToTickEnabled=true,MinWidth=120,VerticalAlignment=W.VerticalAlignment.Center};
    private readonly WC.CheckBox wrap = new(){Content="自动换行",IsChecked=true,Margin=new(0,12,0,0)};
    private readonly WC.ComboBox engine;
    private readonly WC.Button animate;
    private readonly System.Windows.Threading.DispatcherTimer typing = new(){Interval=TimeSpan.FromMilliseconds(45)};
    private readonly List<(WC.TextBlock Reference, WC.TextBlock Selected, WC.TextBlock Coverage, string Text)> samples=[];
    private readonly List<Action> refresh=[];
    private WM.FontFamily chosen = new("Segoe UI");
    private byte[]? fontBytes;
    private int[] textStarts=[];
    private int position;
    private bool disposed, updating, selectedFile, active;
    private string loadedFamily="", selectedFace="";
    private long loadedBytes;

    private readonly WC.StackPanel fontPage=new(),diagnosticPage=new(),enginePage=new(),characterPage=new();
    private readonly WpfTranslationTrialPanel trial;
    private readonly WpfMediaSelfTestPanel media=new();
    private readonly WC.WrapPanel navigation=new(),diagnosticNavigation=new();
    private readonly WC.Slider frameWidth=new(){Minimum=180,Maximum=720,Value=440,TickFrequency=10,IsSnapToTickEnabled=true};
    private readonly WC.ComboBox layoutStyle=NativeDialogControls.Choice(["对白框","选项","字幕"]);
    private readonly WC.Border previewFrame=new();
    private string mainPage="trial",diagnostic="character";
    private bool diagnosticsOpen;

    internal WpfTestWorkspace():this(()=>new(new(),new())){}
    internal WpfTestWorkspace(Func<TranslationTrialConfiguration> configuration)
    {
        W.Automation.AutomationProperties.SetAutomationId(this,"FusionTestArea");
        SetResourceReference(BackgroundProperty,"Fusion.Main");
        var content=new WC.StackPanel{Margin=new(28,24,28,28),MaxWidth=1240,HorizontalAlignment=W.HorizontalAlignment.Stretch};
        Content=new WC.ScrollViewer{Content=content,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=WC.ScrollBarVisibility.Disabled};
        content.Children.Add(Label("诊断与预览",28,true));
        trial=new(configuration);content.Children.Add(navigation);
        void Tab(string title,string id,Action action){var button=NativeDialogControls.Button(title,action);W.Automation.AutomationProperties.SetAutomationId(button,id);navigation.Children.Add(button);}
        Tab("翻译试用","TestTrialTab",()=>SelectPage("trial"));Tab("字体预览","TestFontTab",()=>SelectPage("font"));
        Tab("高级诊断","TestAdvancedTab",()=>{diagnosticsOpen=!diagnosticsOpen;RenderPages();});
        foreach(var page in new W.UIElement[]{trial,fontPage,diagnosticPage})content.Children.Add(page);
        diagnosticPage.Children.Add(diagnosticNavigation);
        foreach(var item in new[]{("截图录像自检","media"),("引擎检查","engine"),("角色与缓存","character")})
        {var button=NativeDialogControls.Button(item.Item1,()=>{diagnostic=item.Item2;RenderPages();});W.Automation.AutomationProperties.SetAutomationId(button,"Diagnostic_"+item.Item2);diagnosticNavigation.Children.Add(button);}
        foreach(var page in new W.UIElement[]{media,enginePage,characterPage})diagnosticPage.Children.Add(page);
        fontPage.Children.Add(Label("预览字体与对白排版；游戏中的实际显示仍需在对应游戏确认。"));
        var fonts=Card(fontPage,"选择字体");
        mode=NativeDialogControls.Choice(["默认补字方案","自定义字体"]);mode.MinWidth=230;mode.HorizontalAlignment=W.HorizontalAlignment.Left;mode.Margin=new(0,0,0,16);fonts.Children.Add(mode);
        family=NativeDialogControls.Choice(GameFontSettings.Families());family.ItemTemplate=null;family.IsEditable=true;family.IsReadOnly=true;family.MinWidth=180;family.Margin=new(0);family.HorizontalAlignment=W.HorizontalAlignment.Stretch;
        var selected=FontManager.ProductDefaultFamily; if(family.Items.Contains(selected))family.SelectedItem=selected;
        defaultFont=NativeDialogControls.Choice(["Noto Sans CJK SC · Fusion","Microsoft YaHei · Ren’Py / Godot 3","Malgun Gothic · Ren’Py"]);defaultFont.ItemTemplate=null;defaultFont.MinWidth=180;defaultFont.Margin=new(0);
        fontChoices.Children.Add(defaultFont);fontChoices.Children.Add(family);
        for(int i=0;i<5;i++)fontSelectionGroup.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        fontSelectionGroup.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});fontSelectionGroup.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});
        choiceLabel=Label("默认补字字体",null,true);choiceLabel.Margin=new(0,0,0,8);Place(fontSelectionGroup,choiceLabel,0,0,2);
        Place(fontSelectionGroup,fontChoices,1,0);
        foreach(var button in new[]{NativeDialogControls.Button("选择字体文件",SelectFile),NativeDialogControls.Button("恢复预览默认",Reset)}){button.Margin=new(8,0,0,0);fontActions.Children.Add(button);}
        Place(fontSelectionGroup,fontActions,1,1);
        path.BorderThickness=new(0);path.Background=WM.Brushes.Transparent;path.Padding=new(0);path.MinHeight=0;path.FontSize=12;path.TextWrapping=W.TextWrapping.Wrap;path.Margin=new(0,8,0,0);path.SetResourceReference(WC.Control.ForegroundProperty,"Fusion.Text");
        Place(fontSelectionGroup,path,2,0,2);status.Margin=new(0,8,0,0);status.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");Place(fontSelectionGroup,status,3,0,2);
        fonts.Children.Add(fontSelectionGroup);
        defaultHint=Label("当前仅预览默认补字字体。");defaultHint.Margin=new(0,12,0,0);fonts.Children.Add(defaultHint);
        fontSelectionGroup.SizeChanged+=(_,_)=>ArrangeFontSelection();
        var previewBody=Card(fontPage,"显示效果");
        scenario=NativeDialogControls.Choice(FontTestScenarios.Names);scenario.Width=210;scenario.Margin=new(0,0,0,0);
        sampleLanguage=NativeDialogControls.Choice(Languages.Select(x=>x.Name));sampleLanguage.ItemTemplate=null;sampleLanguage.Width=210;sampleLanguage.Margin=new(0);
        var sampleFields=new WC.WrapPanel{Margin=new(0,0,0,14)};
        foreach(var field in new[]{("显示场景",scenario),("样文语言",sampleLanguage)})
        {
            var group=new WC.StackPanel{Margin=new(0,0,20,8)};var label=Label(field.Item1,null,true);label.Margin=new(0,0,0,8);group.Children.Add(label);group.Children.Add(field.Item2);sampleFields.Children.Add(group);
        }
        previewBody.Children.Add(sampleFields);
        layoutStyle.Width=210;layoutStyle.Margin=new(0,0,0,12);previewBody.Children.Add(layoutStyle);
        previewBody.Children.Add(SliderField("文字框宽度",frameWidth,"0","TestFrameWidth"));
        W.Automation.AutomationProperties.SetAutomationId(layoutStyle,"TestLayoutStyle");
        frameWidth.ValueChanged+=(_,_)=>ArrangePreviewFrame();layoutStyle.SelectionChanged+=(_,_)=>ArrangePreviewFrame();
        var adjustments=new WC.Grid();adjustments.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});adjustments.ColumnDefinitions.Add(new(){Width=new(20)});adjustments.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});
        Place(adjustments,SliderField("预览字号",size,"0","TestFontSizeField"),0,0);Place(adjustments,SliderField("行距",spacing,"0.0","TestLineSpacingField"),0,2);previewBody.Children.Add(adjustments);previewBody.Children.Add(wrap);
        previewLayout.Margin=new(0,16,0,0);previewLayout.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});previewLayout.ColumnDefinitions.Add(new(){Width=new(20)});previewLayout.ColumnDefinitions.Add(new(){Width=new(1.4,W.GridUnitType.Star)});for(int i=0;i<3;i++)previewLayout.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        var inputLabel=Label("预览文字（可编辑）",null,true);inputLabel.Margin=new(0,0,0,8);W.Automation.AutomationProperties.SetAutomationId(inputLabel,"TestPreviewInputLabel");inputPane.Children.Add(inputLabel);
        var outputLabel=Label("测试字体",null,true);outputLabel.Margin=new(0,0,0,8);W.Automation.AutomationProperties.SetAutomationId(outputLabel,"TestPreviewOutputLabel");previewPane.Children.Add(outputLabel);
        input.MaxLength=4096;input.MinHeight=150;input.MaxHeight=200;input.Margin=new(0);input.VerticalContentAlignment=W.VerticalAlignment.Top;inputPane.Children.Add(input);
        preview.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");
        preview.MaxHeight=240;
        var frame=previewFrame;frame.Child=preview;frame.BorderThickness=new(1);frame.CornerRadius=new(6);frame.ClipToBounds=true;frame.HorizontalAlignment=W.HorizontalAlignment.Left;
        frame.SetResourceReference(WC.Border.BorderBrushProperty,"Fusion.Border");frame.SetResourceReference(WC.Border.BackgroundProperty,"Fusion.Main");previewPane.Children.Add(frame);
        animate=NativeDialogControls.Button("预览打字效果",StartTyping);
        animate.HorizontalAlignment=W.HorizontalAlignment.Left;animate.Margin=new(0,12,0,0);previewPane.Children.Add(animate);
        Place(previewLayout,inputPane,0,0);Place(previewLayout,previewPane,0,2);previewBody.Children.Add(previewLayout);previewLayout.SizeChanged+=(_,_)=>ArrangePreview();
        inputCoverage.Margin=new(0,14,0,6);inputCoverage.FontWeight=W.FontWeights.SemiBold;inputCoverage.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");previewBody.Children.Add(inputCoverage);
        layoutStatus.Margin=new(0,0,0,6);layoutStatus.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");previewBody.Children.Add(layoutStatus);
        ConfigureDetails(coverageDetails,"缺字详情",coverageDetailText);previewBody.Children.Add(coverageDetails);
        missingHint.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");previewBody.Children.Add(missingHint);
        var compareHolder=new WC.StackPanel();var compareDetails=new WC.Expander();ConfigureDetails(compareDetails,"多语言对照",compareHolder);fontPage.Children.Add(compareDetails);
        var compare=Card(compareHolder,"多语言对照");
        comparisons.ColumnDefinitions.Add(new(){Width=new(100)});
        comparisons.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});
        comparisons.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});
        comparisons.ColumnDefinitions.Add(new(){Width=new(120)});
        AddSampleRow(0,"语言","参考显示","所选字体","检查结果",true);
        int n=1;
        foreach(var lang in Languages)
        {
            string text=GameFontPreview.Sample(lang.Code);
            AddSampleRow(n++,lang.Name,text,text,"",false);
        }
        compare.Children.Add(comparisons);
        ConfigureDetails(comparisonDetails,"缺字详情",comparisonDetailRows);compare.Children.Add(comparisonDetails);
        var engineTests=Card(enginePage,"引擎显示测试");
        engineTestPanel=new(()=>new EngineTestRequest{FontPath=loadedFamily.Length>0?path.Text:"",FontFamily=loadedFamily,FontMode=mode.SelectedIndex==0?"default":"custom",SampleText=input.Text});
        engineTests.Children.Add(engineTestPanel);
        characterTestPanel=new();Card(characterPage,"角色与缓存").Children.Add(characterTestPanel);
        var engines=new WC.StackPanel();var engineDetails=new WC.Expander{Margin=new(0,20,0,0)};ConfigureDetails(engineDetails,"角色与字体适配",engines);characterPage.Children.Add(engineDetails);
        engine=NativeDialogControls.Choice(new[]{"全部引擎"}.Concat(FontTestEngines.Items.Select(x=>x.Name)));
        engine.MaxWidth=430;engine.HorizontalAlignment=W.HorizontalAlignment.Left;engine.MinWidth=240;
        engines.Children.Add(Label("选择引擎查看角色与字体的适用范围。"));engines.Children.Add(engine);
        engines.Children.Add(Label("以下为已接入范围；不代表所有游戏均已验证。"));
        engines.Children.Add(engineRows);
        foreach(var item in new (W.DependencyObject Control,string Name)[]{(mode,"测试字体"),(defaultFont,"默认补字字体"),(family,"已安装字体"),(path,"字体文件"),(input,"预览文字"),(size,"预览字号"),(spacing,"行距"),(engine,"引擎字体适配")})
            WpfUiLanguage.Property(item.Control,W.Automation.AutomationProperties.NameProperty,item.Name);
        family.SelectionChanged+=(_,_)=>{if(!updating&&mode.SelectedIndex==1){selectedFile=false;customPath="";path.Text="";LoadSelected();}};
        mode.SelectionChanged+=(_,_)=>{if(!updating){selectedFile=mode.SelectedIndex==0||customPath.Length>0;path.Text=customPath;LoadSelected();}};
        defaultFont.SelectionChanged+=(_,_)=>{if(!updating&&mode.SelectedIndex==0)LoadSelected();};
        customText=input.Text;
        scenario.SelectionChanged+=(_,_)=>LoadSample();sampleLanguage.SelectionChanged+=(_,_)=>{if(scenario.SelectedIndex!=0)LoadSample();};
        input.TextChanged+=(_,_)=>{if(!loadingSample){customText=input.Text;loadingSample=true;scenario.SelectedIndex=0;loadingSample=false;}StopTyping();Render();};
        preview.SizeChanged+=(_,_)=>RenderLayoutStatus();
        size.ValueChanged+=(_,_)=>Render();spacing.ValueChanged+=(_,_)=>Render();wrap.Checked+=(_,_)=>Render();wrap.Unchecked+=(_,_)=>Render();
        engine.SelectionChanged+=(_,_)=>RenderEngines();
        typing.Tick+=Tick;
        UiLanguage.LanguageChanged+=LanguageChanged;
        WpfUiLanguage.Dialog(this);LoadSelected();RefreshLabels();RenderPages();
    }
    private void SelectPage(string page){mainPage=page;diagnosticsOpen=false;RenderPages();}
    private void RenderPages()
    {
        trial.Visibility=!diagnosticsOpen&&mainPage=="trial"?W.Visibility.Visible:W.Visibility.Collapsed;
        fontPage.Visibility=!diagnosticsOpen&&mainPage=="font"?W.Visibility.Visible:W.Visibility.Collapsed;
        diagnosticPage.Visibility=diagnosticsOpen?W.Visibility.Visible:W.Visibility.Collapsed;
        media.Visibility=diagnostic=="media"?W.Visibility.Visible:W.Visibility.Collapsed;
        enginePage.Visibility=diagnostic=="engine"?W.Visibility.Visible:W.Visibility.Collapsed;
        characterPage.Visibility=diagnostic=="character"?W.Visibility.Visible:W.Visibility.Collapsed;
        int i=0;foreach(WC.Button button in navigation.Children){WpfThemeResources.SetButton(button,new[]{"翻译试用","字体预览","高级诊断"}[i],null,diagnosticsOpen?i==2:i==(mainPage=="trial"?0:1));i++;}
        i=0;foreach(WC.Button button in diagnosticNavigation.Children){WpfThemeResources.SetButton(button,new[]{"截图录像自检","引擎检查","角色与缓存"}[i],null,new[]{"media","engine","character"}[i]==diagnostic);i++;}
        if(trial.Visibility==W.Visibility.Visible)trial.Reload();else trial.Cancel();
        if(!diagnosticsOpen||diagnostic!="engine")engineTestPanel.Cancel();
        if(!diagnosticsOpen||diagnostic!="character")characterTestPanel.Cancel();
        if(!diagnosticsOpen||diagnostic!="media")media.Cancel();
    }
    private void ArrangePreviewFrame()
    {
        if(previewPane.ActualWidth<=0)return;
        previewFrame.Width=Math.Max(100,Math.Min(frameWidth.Value,previewPane.ActualWidth));
        previewFrame.Height=layoutStyle.SelectedIndex==2?100:layoutStyle.SelectedIndex==1?220:170;
        preview.MinHeight=0;preview.MaxHeight=previewFrame.Height-2;
        preview.TextAlignment=layoutStyle.SelectedIndex==2?W.TextAlignment.Center:W.TextAlignment.Left;
        RenderLayoutStatus();
    }
    private void LoadSample()
    {
        if(loadingSample||disposed)return;
        loadingSample=true;
        try
        {
            input.Text=scenario.SelectedIndex==0?customText:FontTestScenarios.Text(scenario.SelectedIndex,Languages[Math.Clamp(sampleLanguage.SelectedIndex,0,Languages.Length-1)].Code);
            if(scenario.SelectedIndex==5)size.Value=14;
        }
        finally{loadingSample=false;}
        StopTyping();Render();
    }
    private WC.TextBlock Label(string source,double? size=null,bool bold=false)
    {
        var value=NativeDialogControls.Text(source,size);value.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");
        if(bold)value.FontWeight=W.FontWeights.SemiBold;return value;
    }
    private static void Place(WC.Grid grid,W.UIElement element,int row,int column,int span=1)
    {WC.Grid.SetRow(element,row);WC.Grid.SetColumn(element,column);WC.Grid.SetColumnSpan(element,span);grid.Children.Add(element);}
    private WC.StackPanel SliderField(string title,WC.Slider slider,string format,string id)
    {
        var field=new WC.StackPanel();W.Automation.AutomationProperties.SetAutomationId(field,id);
        var label=Label(title,null,true);label.Margin=new(0,0,0,8);field.Children.Add(label);
        var row=new WC.Grid();row.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=new(52)});
        var number=Label("");number.Margin=new(12,0,0,0);number.VerticalAlignment=W.VerticalAlignment.Center;
        refresh.Add(()=>number.Text=slider.Value.ToString(format,UiLanguage.NumericInputCulture));Place(row,slider,0,0);Place(row,number,0,1);field.Children.Add(row);return field;
    }
    private void ConfigureDetails(WC.Expander details,string title,W.UIElement body)
    {
        details.Header=Label(title,null,true);details.Content=body;details.IsExpanded=false;details.Margin=new(0,4,0,8);details.SetResourceReference(WC.Control.ForegroundProperty,"Fusion.Text");
        WpfUiLanguage.Property(details,W.Automation.AutomationProperties.NameProperty,title);
    }
    private void ArrangeFontSelection()
    {
        bool stacked=fontSelectionGroup.ActualWidth<760;
        WC.Grid.SetRow(fontActions,stacked?2:1);WC.Grid.SetColumn(fontActions,stacked?0:1);WC.Grid.SetColumnSpan(fontActions,stacked?2:1);
        fontActions.Margin=stacked?new(0,10,0,0):new(8,0,0,0);
        foreach(W.FrameworkElement item in fontActions.Children)item.Margin=stacked?new(0,0,8,8):new(8,0,0,0);
        WC.Grid.SetColumnSpan(fontChoices,stacked?2:1);WC.Grid.SetRow(path,stacked?3:2);WC.Grid.SetRow(status,stacked?4:3);
    }
    private void ArrangePreview()
    {
        bool stacked=previewLayout.ActualWidth<840;
        WC.Grid.SetColumnSpan(inputPane,stacked?3:1);WC.Grid.SetRow(previewPane,stacked?2:0);WC.Grid.SetColumn(previewPane,stacked?0:2);WC.Grid.SetColumnSpan(previewPane,stacked?3:1);
        previewPane.Margin=stacked?new(0,18,0,0):new(0);ArrangePreviewFrame();
    }
    private WC.StackPanel Card(WC.Panel parent,string title)
    {
        var body=new WC.StackPanel();body.Children.Add(Label(title,18,true));
        var border=new WC.Border{Child=body,Padding=new(20),Margin=new(0,16,0,0),BorderThickness=new(1),CornerRadius=new(8)};
        border.SetResourceReference(WC.Border.BorderBrushProperty,"Fusion.Border");border.SetResourceReference(WC.Border.BackgroundProperty,"Fusion.Secondary");parent.Children.Add(border);return body;
    }
    private void AddSampleRow(int row,string language,string reference,string selected,string coverage,bool header)
    {
        comparisons.RowDefinitions.Add(new(){Height=W.GridLength.Auto,MinHeight=header?44:64});
        var rule=new WC.Border{BorderThickness=new(0,0,0,1),IsHitTestVisible=false};rule.SetResourceReference(WC.Border.BorderBrushProperty,"Fusion.Border");Place(comparisons,rule,row,0,4);
        WC.TextBlock? referenceBlock=null,selectedBlock=null,coverageBlock=null;
        foreach(var (text,col) in new[]{(language,0),(reference,1),(selected,2),(coverage,3)})
        {
            WC.TextBlock block=header||col==0?Label(text,null,header):new WC.TextBlock{Text=text,TextWrapping=W.TextWrapping.Wrap};
            block.Margin=new(8,12,12,12);block.VerticalAlignment=W.VerticalAlignment.Center;block.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");
            block.FontFamily=new WM.FontFamily("Global User Interface");block.FontSize=14;block.FontWeight=W.FontWeights.Normal;
            if(header)block.FontWeight=W.FontWeights.SemiBold;
            WC.Grid.SetRow(block,row);WC.Grid.SetColumn(block,col);comparisons.Children.Add(block);
            if(col==1)referenceBlock=block;if(col==2)selectedBlock=block;if(col==3)coverageBlock=block;
        }
        if(!header)samples.Add((referenceBlock!,selectedBlock!,coverageBlock!,selected));
    }
    private void SelectFile()
    {
        var picker=new Microsoft.Win32.OpenFileDialog{Title=UiLanguage.Text("选择字体文件"),Filter=UiLanguage.Text("字体文件")+" (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc",CheckFileExists=true};
        if(picker.ShowDialog(W.Window.GetWindow(this))!=true)return;
        updating=true;mode.SelectedIndex=1;selectedFile=true;customPath=picker.FileName;path.Text=picker.FileName;updating=false;LoadSelected();
    }
    private void Reset()
    {
        updating=true;mode.SelectedIndex=0;defaultFont.SelectedIndex=0;family.SelectedItem=FontManager.ProductDefaultFamily;customPath="";selectedFile=true;path.Text="";size.Value=24;spacing.Value=1.5;wrap.IsChecked=true;updating=false;LoadSelected();
    }
    private void LoadSelected()
    {
        if(updating||disposed)return;
        StopTyping();fontBytes=null;loadedFamily="";selectedFace="";loadedBytes=0;coverageCache.Clear();
        bool automatic=mode.SelectedIndex==0;
        family.Visibility=automatic?W.Visibility.Collapsed:W.Visibility.Visible;
        fontActions.Children[0].Visibility=automatic?W.Visibility.Collapsed:W.Visibility.Visible;
        defaultFont.Visibility=defaultHint.Visibility=automatic?W.Visibility.Visible:W.Visibility.Collapsed;
        WpfUiLanguage.Property(choiceLabel,WC.TextBlock.TextProperty,automatic?"默认补字字体":"测试字体");
        if(automatic){selectedFile=true;path.Text=DefaultFontPath(defaultFont.SelectedIndex);}
        ArrangeFontSelection();
        try
        {
            string file="";
            if(selectedFile)
            {
                file=path.Text;var info=GameFontSettings.Inspect(file,input.Text);
                fontBytes=File.ReadAllBytes(file);loadedFamily=info.Family;
                chosen=new WM.FontFamily(new Uri(Path.GetDirectoryName(Path.GetFullPath(file))!+Path.DirectorySeparatorChar),"./"+Uri.EscapeDataString(Path.GetFileName(file))+"#"+loadedFamily);
            }
            else{loadedFamily=family.SelectedItem?.ToString()??"";chosen=new(loadedFamily);}
            var face=new WM.Typeface(chosen,W.FontStyles.Normal,W.FontWeights.Normal,W.FontStretches.Normal);
            // Never label a substituted face as the selected font having loaded.
            if(!face.TryGetGlyphTypeface(out var glyph)||!glyph.FontUri.IsFile||(selectedFile&&!Path.GetFullPath(glyph.FontUri.LocalPath).Equals(Path.GetFullPath(file),StringComparison.OrdinalIgnoreCase)))
                throw new IOException("预览未能载入所选字体。请选择其他字体或恢复预览默认。");
            path.Text=glyph.FontUri.LocalPath;selectedFace=glyph.FontUri.Fragment;loadedBytes=new FileInfo(path.Text).Length;
            if(selectedFile&&selectedFace.Length>0&&selectedFace!="#0")
                throw new IOException("此字体位于字体集合的非默认位置，暂不能单独选择。请改选独立字体文件或其他已安装字体。");
            status.Text=string.Format(UiLanguage.Text("已载入字体：{0}"),loadedFamily);
            status.Visibility=W.Visibility.Collapsed;
            if(!automatic)
            {
                customPath=selectedFile?path.Text:"";
                if(selectedFile){updating=true;family.SelectedItem=null;family.Text=loadedFamily;updating=false;}
            }
        }
        catch(Exception ex)
        {
            fontBytes=null;loadedFamily="";chosen=new("Global User Interface");
            status.Text=UiLanguage.Text("字体未载入，当前使用参考字体显示。")+"\n"+UiLanguage.Text(SafeDiagnosticOutput.ExceptionSummary(ex));
            status.Visibility=W.Visibility.Visible;
        }
        Render();RenderEngines();
    }
    internal static string DefaultFontPath(int selected)=>selected switch
    {
        1=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Fonts","msyh.ttc"),
        2=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Fonts","malgun.ttf"),
        _=>Path.Combine(AppContext.BaseDirectory,"adapters","gamemaker","bridge-font.otf")
    };
    private GameFontCoverage.Result MeasureCoverage(string text)
    {
        if(coverageCache.TryGetValue(text,out var cached))return cached;
        var result=fontBytes is null?GameFontSettings.CheckInstalledFamily(loadedFamily,text):GameFontCoverage.Check(fontBytes,text);
        if(coverageCache.Count>64)coverageCache.Clear();coverageCache[text]=result;return result;
    }
    private string Coverage(string text,bool detail=false)
    {
        if(loadedFamily.Length==0)return UiLanguage.Text("尚未载入字体");
        GameFontCoverage.Result result;
        try{result=MeasureCoverage(text);}
        catch(Exception ex) when(ex is IOException or ArgumentException){return UiLanguage.Text(SafeDiagnosticOutput.ExceptionSummary(ex));}
        if(result.Checked==0)return UiLanguage.Text("输入预览文字可检查所选字体是否缺字。");
        if(result.Missing.Length==0)return UiLanguage.Text("样文无缺字");
        if(!detail)return string.Format(UiLanguage.Text("缺少 {0} 个字符"),result.Missing.Length);
        var missing=string.Join(" ",result.Missing.Take(8).Select(cp=>char.ConvertFromUtf32(cp)+" (U+"+cp.ToString("X4",CultureInfo.InvariantCulture)+")"));
        if(result.Missing.Length>8)missing+=" …";
        return string.Format(UiLanguage.Text("缺少 {0} 个字符：{1}"),result.Missing.Length,missing);
    }
    private string DisplayText(string text)
    {
        if(loadedFamily.Length==0)return text;
        try{var missing=MeasureCoverage(text).Missing.ToHashSet();return string.Concat(text.EnumerateRunes().Select(r=>missing.Contains(r.Value)?"□":r.ToString()));}
        catch(Exception ex)when(ex is IOException or ArgumentException){return text;}
    }
    private void Render()
    {
        if(updating||disposed)return;
        foreach(var action in refresh)action();
        preview.FontFamily=chosen;preview.FontSize=size.Value;preview.LineHeight=size.Value*spacing.Value;
        preview.TextWrapping=wrap.IsChecked==true?W.TextWrapping.Wrap:W.TextWrapping.NoWrap;
        preview.FlowDirection=GameFontPreview.RightToLeft(input.Text)?W.FlowDirection.RightToLeft:W.FlowDirection.LeftToRight;
        if(!typing.IsEnabled)preview.Text=DisplayText(input.Text);
        comparisonDetailRows.Children.Clear();bool anyMissing=false;
        for(int i=0;i<samples.Count;i++)
        {
            var row=samples[i];row.Selected.FontFamily=chosen;row.Reference.FontSize=row.Selected.FontSize=18;row.Reference.LineHeight=row.Selected.LineHeight=26;row.Selected.Text=DisplayText(row.Text);row.Coverage.Text=Coverage(row.Text);
            if(loadedFamily.Length>0&&MeasureCoverage(row.Text).Missing.Length>0){anyMissing=true;comparisonDetailRows.Children.Add(Label(Languages[i].Name+" · "+Coverage(row.Text,true)));}
        }
        inputCoverage.Text=Coverage(input.Text);coverageDetailText.Text=Coverage(input.Text,true);preview.ToolTip=inputCoverage.Text;
        coverageDetails.Visibility=loadedFamily.Length>0&&MeasureCoverage(input.Text).Missing.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
        missingHint.Visibility=coverageDetails.Visibility;
        comparisonDetails.Visibility=anyMissing?W.Visibility.Visible:W.Visibility.Collapsed;
        RenderLayoutStatus();
    }
    private void RenderLayoutStatus()
    {
        if(disposed||preview.ActualWidth<=24||loadedFamily.Length==0){layoutStatus.Text="";return;}
        try
        {
            var text=new WM.FormattedText(DisplayText(input.Text),CultureInfo.CurrentUICulture,preview.FlowDirection,new WM.Typeface(chosen,W.FontStyles.Normal,W.FontWeights.Normal,W.FontStretches.Normal),size.Value,WM.Brushes.Black,WM.VisualTreeHelper.GetDpi(preview).PixelsPerDip){LineHeight=size.Value*spacing.Value};
            if(wrap.IsChecked==true)text.MaxTextWidth=Math.Max(1,preview.ActualWidth-24);
            bool overflow=text.WidthIncludingTrailingWhitespace>preview.ActualWidth-23||text.Height>preview.MaxHeight-23;
            layoutStatus.Text=UiLanguage.Text(overflow?"文字超出预览范围，可调整字号或换行。":"文字可在预览范围内显示。");
        }
        catch(ArgumentException){layoutStatus.Text=UiLanguage.Text("暂时无法检查排版。");}
    }
    private void StartTyping()
    {
        if(typing.IsEnabled){StopTyping();Render();return;}
        textStarts=StringInfo.ParseCombiningCharacters(input.Text);position=0;preview.Text="";
        if(textStarts.Length>0){typing.Start();WpfUiLanguage.Property(animate,WC.ContentControl.ContentProperty,"停止预览");}
    }
    private void Tick(object? sender,EventArgs e)
    {
        if(!IsVisible){StopTyping();return;}
        position++;preview.Text=DisplayText(input.Text[..(position>=textStarts.Length?input.Text.Length:textStarts[position])]);
        if(position>=textStarts.Length)StopTyping();
    }
    private void StopTyping(){typing.Stop();if(animate is not null)WpfUiLanguage.Property(animate,WC.ContentControl.ContentProperty,"预览打字效果");}
    internal void SetActive(bool active){if(active&&!this.active)trial.Reload();if(!active){trial.Cancel();media.Cancel();engineTestPanel.Cancel();characterTestPanel.Cancel();}if(this.active==active&&!typing.IsEnabled)return;this.active=active;if(!active){StopTyping();Render();}}
    private void LanguageChanged(object? sender,EventArgs e){if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(RefreshLabels);return;}RefreshLabels();}
    private void RefreshLabels(){foreach(var action in refresh)action();Render();RenderEngines();if(loadedFamily.Length>0)status.Text=string.Format(UiLanguage.Text("已载入字体：{0}"),loadedFamily);}
    private void RenderEngines()
    {
        if(engine is null)return;engineRows.Children.Clear();
        foreach(var item in FontTestEngines.Items.Where((_,i)=>engine.SelectedIndex<=0||i==engine.SelectedIndex-1))
        {
            var capability=GameFontCapabilities.ForAdapter(item.Id);
            var body=new WC.StackPanel{Margin=new(0,12,0,12)};
            var heading=Label(item.Name,16,true);heading.Margin=new(0,0,0,8);body.Children.Add(heading);
            string? sources=EngineCharacterSources.ForAdapter(item.Id);
            var roles=Label(UiLanguage.Text("角色应用")+"："+UiLanguage.Text(sources is null?"尚无可靠台词绑定":"可应用于已识别的角色台词"),14);
            roles.Margin=new(0,0,0,8);body.Children.Add(roles);
            if(sources is not null){var detail=Label(sources,14);detail.Margin=new(0,0,0,8);body.Children.Add(detail);}
            var fonts=Label(UiLanguage.Text("字体应用")+"："+UiLanguage.Text(!capability.Custom?"自定义字体适配尚未完成。":capability.Live&&capability.Restore?"可预先选择、实时应用和恢复默认":"可预先选择，下次启动时应用"),14);
            fonts.Margin=new(0,0,0,8);body.Children.Add(fonts);
            var limits=Label(capability.Limitation.Length>0?capability.Limitation:item.Requirement,14);body.Children.Add(limits);
            if(mode.SelectedIndex==1){var selected=Label(FontTestEngines.Check(item.Id,path.Text,!selectedFile,loadedFamily.Length>0,selectedFace,loadedBytes),14);selected.Margin=new(0,8,0,0);body.Children.Add(selected);}
            var row=new WC.Border{Child=body,BorderThickness=new(0,0,0,1)};row.SetResourceReference(WC.Border.BorderBrushProperty,"Fusion.Border");engineRows.Children.Add(row);
        }
    }
    public void Dispose(){if(disposed)return;disposed=true;trial.Dispose();media.Dispose();engineTestPanel.Dispose();characterTestPanel.Dispose();typing.Stop();typing.Tick-=Tick;UiLanguage.LanguageChanged-=LanguageChanged;fontBytes=null;}
}

// Native speaker contracts, not a claim that arbitrary text widgets contain
// character identities. Keep this list narrower than the engine inventory.
internal static class EngineCharacterSources
{
    internal static string? ForAdapter(string? id)=>id switch
    {
        "renpy"=>"Ren’Py · Say",
        "rpg-maker-mv"=>"RPG Maker MV · YEP Message Core",
        "rpg-maker-mz"=>"RPG Maker MZ · Speaker Name",
        "web-rpg"=>"RPG Maker MZ / MV + YEP Message Core",
        "rgss"=>"VX Ace · YEA Message System",
        "unity-mono" or "unity-il2cpp"=>"Naninovel / Dialogue System for Unity",
        "godot3"=>"Dialogic 1",
        "godot4"=>"Dialogic 2",
        "web-tyrano"=>"TyranoScript · chara_ptext",
        "web-vnmaker"=>"Visual Novel Maker · ShowMessage",
        "defold"=>"MatchaNovel · Textbox",
        "gamemaker"=>"Chatterbox · Speech",
        "yukar-bakin"=>"Bakin · Cast Nameplate",
        _=>null
    };
}

internal static class FontTestEngines
{
    internal static string Check(string id,string path,bool installed,bool loaded,string face="",long bytes=0)
    {
        if(!loaded)return "尚未载入字体";
        var capability=GameFontCapabilities.ForAdapter(id);
        if(!capability.Custom)return "自定义字体适配尚未完成。";
        if(!capability.SystemFamilyOnly&&Path.GetExtension(path).ToLowerInvariant() is not (".ttf" or ".otf" or ".ttc"))return "请选择 TTF、OTF 或 TTC 字体文件。";
        if(!capability.SystemFamilyOnly&&bytes>GameFontSettings.MaximumFileBytes)return "字体文件不能超过 32 MB。";
        if(installed&&!capability.SystemFamilyOnly&&face.Length>0&&face!="#0")return "此字体位于字体集合的非默认位置，暂不能单独选择。请改选独立字体文件或其他已安装字体。";
        if(id is "unity-mono" or "unity-il2cpp")return "字体支持取决于 Unity 版本和文字组件。";
        if(capability.SystemFamilyOnly&&!installed)return "需要选择已安装字体。";
        if(id is "gamemaker" or "wolf" or "yukar-bakin" or "defold" or "unreal" && Path.GetExtension(path).Equals(".ttc",StringComparison.OrdinalIgnoreCase))return "需要独立 TTF 或 OTF 字体文件。";
        return "字体条件可用，实际效果待验证。";
    }
    internal sealed record Entry(string Id,string Name,string Requirement);
    internal static readonly Entry[] Items=[
        new("renpy","Ren’Py","支持自定义字体；效果取决于游戏文字组件。"),
        new("rpg-maker-mv","RPG Maker MV","支持自定义字体；效果取决于游戏文字组件。"),
        new("rpg-maker-mz","RPG Maker MZ","支持自定义字体；效果取决于游戏文字组件。"),
        new("rgss","RPG Maker XP / VX / VX Ace","此引擎支持已安装字体，暂不直接加载外部字体文件。"),
        new("unity-mono","Unity Mono","字体支持取决于 Unity 版本和文字组件。"),
        new("unity-il2cpp","Unity IL2CPP","字体支持取决于 Unity 版本和文字组件。"),
        new("godot3","Godot 3","支持自定义字体；效果取决于游戏文字组件。"),
        new("godot4","Godot 4","支持自定义字体；效果取决于游戏文字组件。"),
        new("wolf","WOLF RPG Editor",GameFontCapabilities.ForAdapter("wolf").Limitation),
        new("web-rpg","RPG Maker Web","支持自定义字体；效果取决于游戏文字组件。"),
        new("web-construct","Construct / NW.js","支持自定义字体；效果取决于游戏文字组件。"),
        new("web-tyrano","TyranoScript","支持自定义字体；效果取决于游戏文字组件。"),
        new("web-choicescript","ChoiceScript","支持自定义字体；效果取决于游戏文字组件。"),
        new("gamemaker","GameMaker 2.3+ VM","需要独立 TTF 或 OTF 字体文件。"),
        new("web-vnmaker","Visual Novel Maker",GameFontCapabilities.ForAdapter("web-vnmaker").Limitation),
        new("defold","Defold",GameFontCapabilities.ForAdapter("defold").Limitation),
        new("unreal","Unreal Engine",GameFontCapabilities.ForAdapter("unreal").Limitation),
        new("yukar-bakin","Yukar / Bakin",GameFontCapabilities.ForAdapter("yukar-bakin").Limitation)];
}
