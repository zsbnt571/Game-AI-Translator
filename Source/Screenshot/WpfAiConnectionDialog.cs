using W = System.Windows;
using C = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

// Compatibility window; the workspace opens the global Settings page instead.
internal sealed class WpfAiConnectionDialog : W.Window
{
    internal string? SelectedProfileId { get; private set; }
    internal WpfAiConnectionDialog(AiToolsController controller,string? selectedId)
    {
        Title=AiConnectionText.Text("Title");Width=700;Height=780;MinWidth=460;MinHeight=480;
        MaxWidth=W.SystemParameters.WorkArea.Width;MaxHeight=W.SystemParameters.WorkArea.Height;
        WindowStartupLocation=W.WindowStartupLocation.CenterOwner;ShowInTaskbar=false;UseLayoutRounding=true;
        WpfPresentationRecovery.Attach(this);Resources.MergedDictionaries.Add(WpfThemeResources.Create());WpfThemeResources.Apply(Resources);
        SetResourceReference(BackgroundProperty,"Fusion.Main");SetResourceReference(ForegroundProperty,"Fusion.Text");
        FontFamily=new(FontManager.ProductDefaultFamily);FontSize=14;
        var editor=new WpfAiSettingsPage(controller,selectedId);
        editor.Saved+=id=>{SelectedProfileId=id;DialogResult=true;};Closed+=(_,_)=>editor.Dispose();
        Content=new C.ScrollViewer{Content=editor,Margin=new(22),VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled};
        W.Automation.AutomationProperties.SetAutomationId(this,"AiConnectionDialog");
    }
}
