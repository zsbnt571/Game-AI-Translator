using W = System.Windows;
using WC = System.Windows.Controls;
namespace ScreenshotTranslationUiTester;

internal sealed class NativeAppearanceDialog : NativeDialogWindow
{
    internal ApiSettings Value {get;}
    private readonly List<Action> refresh=[];
    private readonly List<(W.UIElement View,Func<bool> Enabled)> enabled=[];
    private bool syncing;
    internal NativeAppearanceDialog(ApiSettings settings):base("预览、字体与译图设置",800,650)
    {
        Value=ApiSettingsSnapshot.Copy(settings);MinWidth=680;MinHeight=500;
        var tabs=new WC.TabControl();
        WC.StackPanel Page(string label){var body=new WC.StackPanel{Margin=new(16)};tabs.Items.Add(new WC.TabItem{Header=UiLanguage.Text(label),Content=new WC.ScrollViewer{Content=body,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto}});return body;}
        var preview=Page("截图与预览");
        Add(preview,"Preview 窗口大小",Choice(nameof(Value.PreviewWindowSizingMode),Enum.GetValues<PreviewWindowSizingMode>().Select(UiStrings.PreviewSizeModeName).ToArray()));
        Add(preview,"固定宽度 / 高度",NativeDialogControls.Row(Number(nameof(Value.FixedPreviewWidth),820,10000,()=>Value.PreviewWindowSizingMode==PreviewWindowSizingMode.Fixed),Number(nameof(Value.FixedPreviewHeight),560,10000,()=>Value.PreviewWindowSizingMode==PreviewWindowSizingMode.Fixed)));
        Add(preview,"说明",NativeDialogControls.Text("自动适应：按当前图片和屏幕计算；沿用上次：继承上一 Preview 的最终位置、普通窗口大小和最大化状态；固定大小：使用指定宽高。"));
        var appearance=Page("外观");
        Add(appearance,"软件界面字体模式",Choice(nameof(Value.UiFontMode),["产品默认","自定义字体"]));Add(appearance,"界面字体 / 大小",NativeDialogControls.Column(Font(nameof(Value.UiFontFamily),()=>Value.UiFontMode==UiFontMode.Custom),Number(nameof(Value.UiFontSize),7,24,()=>Value.UiFontMode==UiFontMode.Custom)));
        Add(appearance,"Preview 右侧字体模式",Choice(nameof(Value.PreviewTextFontMode),["产品默认","跟随软件界面字体","自定义字体"]));Add(appearance,"Preview 字体 / 大小",NativeDialogControls.Column(Font(nameof(Value.PreviewTextFontFamily),()=>Value.PreviewTextFontMode==PreviewTextFontMode.Custom),Number(nameof(Value.PreviewTextFontSize),7,48,()=>Value.PreviewTextFontMode==PreviewTextFontMode.Custom)));
        Add(appearance,"字体状态",NativeDialogControls.Text(string.Join("\n",new[]{"Microsoft YaHei UI","Segoe UI","Noto Sans CJK SC"}.Select(x=>$"{x}：{(FontSettingsPolicy.IsInstalled(x)?"已安装":"未安装")}"))));
        var overlay=Page("译图");
        Add(overlay,"译图字体模式",Choice(nameof(Value.OverlayFontMode),["产品默认","自定义字体"]));Add(overlay,"译图字体",Font(nameof(Value.OverlayFontFamily),()=>Value.OverlayFontMode==OverlayFontMode.Custom));
        Add(overlay,"译图字号",NativeDialogControls.Row(Number(nameof(Value.OverlayFontSize),0,48,()=>Value.OverlayFontMode==OverlayFontMode.Custom),NativeDialogControls.Text("0 表示自动适配")));
        Add(overlay,"背景策略",Choice(nameof(Value.OverlayBackgroundStyle),Enum.GetValues<TranslationOverlayBackgroundStyle>().Select(UiStrings.BackgroundStyleName).ToArray()));Add(overlay,"轻量底板透明度",NativeDialogControls.Column(Number(nameof(Value.OverlayBackgroundOpacity),0,255,()=>Value.OverlayBackgroundStyle==TranslationOverlayBackgroundStyle.LightOverlay),NativeDialogControls.Text("0（完全透明）～255（完全不透明）")));
        Content=NativeDialogControls.Layout(tabs,NativeDialogControls.Row(NativeDialogControls.Button("恢复默认",Reset),ResultButton("取消",System.Windows.Forms.DialogResult.Cancel),ResultButton("确定",System.Windows.Forms.DialogResult.OK,true)),false);Refresh();
    }
    internal static void Add(WC.StackPanel parent,string label,W.UIElement view)
    {
        var row=new WC.Grid{Margin=new(0,0,0,16)};row.ColumnDefinitions.Add(new(){Width=new(180)});row.ColumnDefinitions.Add(new());var name=NativeDialogControls.Text(label);name.Margin=new(0,8,16,0);row.Children.Add(name);WC.Grid.SetColumn(view,1);row.Children.Add(view);parent.Children.Add(row);
    }
    private WC.ComboBox Choice(string name,string[] values)
    {
        var property=typeof(ApiSettings).GetProperty(name)!;var combo=NativeDialogControls.Choice(values);
        refresh.Add(()=>combo.SelectedIndex=Convert.ToInt32(property.GetValue(Value)));combo.SelectionChanged+=(_,_)=>{if(syncing||combo.SelectedIndex<0)return;property.SetValue(Value,Enum.ToObject(property.PropertyType,combo.SelectedIndex));RefreshEnabled();};return combo;
    }
    private W.FrameworkElement Number(string name,decimal min,decimal max,Func<bool> condition)
    {
        var property=typeof(ApiSettings).GetProperty(name)!;var number=new NativeRpgNumber{Minimum=min,Maximum=max,Width=150,Margin=new(0,0,12,10)};enabled.Add((number.View,condition));refresh.Add(()=>number.Value=Convert.ToDecimal(property.GetValue(Value)));
        number.ValueChanged+=(_,_)=>{if(!syncing)property.SetValue(Value,Convert.ChangeType(number.Value,property.PropertyType));};return number.View;
    }
    private WC.ComboBox Font(string name,Func<bool> condition)
    {
        var property=typeof(ApiSettings).GetProperty(name)!;var combo=new WC.ComboBox{ItemsSource=FontSettingsPolicy.Families(),IsEditable=true,IsTextSearchEnabled=true,MinHeight=36,Margin=new(0,0,0,12)};enabled.Add((combo,condition));refresh.Add(()=>combo.Text=(string?)property.GetValue(Value)??"");
        combo.AddHandler(WC.TextBox.TextChangedEvent,new WC.TextChangedEventHandler((_,_)=>{if(!syncing)property.SetValue(Value,combo.Text);}));return combo;
    }
    private void Refresh(){syncing=true;try{foreach(var update in refresh)update();}finally{syncing=false;}RefreshEnabled();}
    private void RefreshEnabled(){foreach(var item in enabled)item.View.IsEnabled=item.Enabled();}
    private void Reset(){Value.PreviewWindowSizingMode=PreviewWindowSizingMode.CurrentAuto;Value.FixedPreviewWidth=1220;Value.FixedPreviewHeight=800;Value.UiFontMode=UiFontMode.CurrentDefault;Value.UiFontFamily="";Value.UiFontSize=10;Value.PreviewTextFontMode=PreviewTextFontMode.CurrentDefault;Value.PreviewTextFontFamily="";Value.PreviewTextFontSize=10;Value.OverlayFontMode=OverlayFontMode.CurrentDefault;Value.OverlayFontFamily="";Value.OverlayFontSize=0;Value.OverlayBackgroundStyle=0;Value.OverlayBackgroundOpacity=105;Refresh();}
}
