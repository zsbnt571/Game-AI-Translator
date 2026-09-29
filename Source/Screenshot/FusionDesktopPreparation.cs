using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

internal static class DesktopStages
{
    private static readonly Stopwatch Clock=new();
    private static readonly HashSet<string> Recorded=[];
    internal static void Start()=>Clock.Start();
    internal static void Mark(string stage)
    {
        if(!Recorded.Add(stage))return;
        AppLog.Write("desktop-stages",$"{stage}: {Clock.Elapsed.TotalMilliseconds:F1} ms from managed entry; thread={Environment.CurrentManagedThreadId}; not user-perceived time");
    }
    internal static IDisposable MeasureOnce(string stage)=>new Measurement(stage);
    private sealed class Measurement(string stage):IDisposable
    {
        private readonly Stopwatch watch=Stopwatch.StartNew();
        public void Dispose(){if(Recorded.Add(stage))AppLog.Write("desktop-stages",$"{stage}: {watch.Elapsed.TotalMilliseconds:F1} ms preparation; not visual acceptance");}
    }
}

public sealed partial class MainForm
{
    private readonly List<Action?> _settingsBuilders=[];
    private readonly Label _defaultUiFont=new(){AutoSize=true};
    private readonly Label _actualUiFont=new(){AutoSize=true};
    private bool _preparingSettings;
    private void EnsureSettingsCategory(int index)
    {
        if(index<0||index>=_settingsBuilders.Count||_settingsBuilders[index] is not { } build||_preparingSettings)return;
        _preparingSettings=true;var dirty=_settingsDirty;var applying=_applyingSettings;_applyingSettings=true;
        using var timing=DesktopStages.MeasureOnce("settings-category-"+index);
        try{build();_settingsBuilders[index]=null;UpdateAppearanceEnabled();}
        catch(Exception ex){_settingsBuilders[index]=null;
            _statusLabel.Text="设置页面准备失败："+SafeDiagnosticOutput.ExceptionSummary(ex);AppLog.Write("desktop-preparation",SafeDiagnosticOutput.ExceptionSummary(ex));
            if(_settingsTabs?.TabPages[index].Tag is Control root)root.Controls.Add(new Label{AutoSize=true,Text="本页准备失败，请保留日志并重新打开软件。"});}
        finally{_applyingSettings=applying;_settingsDirty=dirty;_preparingSettings=false;}
    }
    private void PrepareFontChoices()
    {
        foreach(var box in new[]{_uiFontBox,_previewFontBox,_overlayFontBox})
        {
            box.DropDownStyle=ComboBoxStyle.DropDown;box.AutoCompleteMode=AutoCompleteMode.None;
            var loading=false;var ready=false;
            box.DropDown+=async(_,_)=>{
                if(loading||ready)return;loading=true;
                try
                {
                    var names=await Task.Run(FontSettingsPolicy.Families);
                    if(IsDisposed||box.IsDisposed)return;
                    var text=box.Text;var applying=_applyingSettings;_applyingSettings=true;
                    try{box.BeginUpdate();box.Items.AddRange(names);box.Text=text;box.SelectionLength=0;
                        box.AutoCompleteMode=AutoCompleteMode.SuggestAppend;box.AutoCompleteSource=AutoCompleteSource.ListItems;
                        // Bounded popup width; no startup measurement of every installed family.
                        box.DropDownWidth=Math.Max(box.Width,Math.Min(Screen.FromControl(box).WorkingArea.Width/2,420*DeviceDpi/96));ready=true;}
                    finally{box.EndUpdate();_applyingSettings=applying;}
                }
                catch(Exception ex){if(!IsDisposed)_statusLabel.Text="字体列表暂不可用："+SafeDiagnosticOutput.ExceptionSummary(ex);}
                finally{loading=false;}
            };
            box.Leave+=(_,_)=>box.SelectionLength=0;
        }
        _uiFontBox.TextChanged+=(_,_)=>UpdateUiFontExplanation();
    }
    private void UpdateUiFontExplanation()
    {
        var custom=_uiFontModeBox.SelectedIndex==1;
        _uiFontBox.Visible=custom;_defaultUiFont.Visible=!custom;
        if(!custom)_uiFontBox.SelectionLength=0;
        var settings=new ApiSettings{UiFontMode=custom?UiFontMode.Custom:UiFontMode.CurrentDefault,UiFontFamily=_uiFontBox.Text,UiFontSize=(float)_uiFontSizeBox.Value};
        var actual=FontManager.ResolveUiProfile(settings).PrimaryFamily;
        _defaultUiFont.Text=actual;
        _actualUiFont.Text=custom&&!string.Equals(actual,_uiFontBox.Text,StringComparison.OrdinalIgnoreCase)
            ?"当前回退："+actual+"；保留原自定义名称。":"字体来源与字号分别设置；保存后生效。";
    }
    private void AttachDesktopStages()
    {
        PaintEventHandler? first=null;
        first=(_,_)=>{Paint-=first;DesktopStages.Mark("main-root-paint-callback");};Paint+=first;
        Shown+=(_,_)=>{DesktopStages.Mark("main-shown-event");BeginInvoke((Action)(()=>{if(!IsDisposed)DesktopStages.Mark("first-ui-message-turn");}));};
    }
}

