using W=System.Windows;
using WC=System.Windows.Controls;
using WA=System.Windows.Automation;

namespace ScreenshotTranslationUiTester;

internal sealed class NativeGameMediaSettingsDialog:NativeDialogWindow
{
    private sealed class Editor
    {
        internal readonly WC.CheckBox Enabled=new(){Content="启用",Margin=new(0,7,12,8)};
        internal readonly WC.CheckBox Ctrl=new(){Content="Ctrl",Margin=new(0,7,12,8)},Alt=new(){Content="Alt",Margin=new(0,7,12,8)},Shift=new(){Content="Shift",Margin=new(0,7,12,8)};
        private readonly Keys[] keys=Enumerable.Range((int)Keys.F1,24).Concat(Enumerable.Range((int)Keys.A,26)).Concat(Enumerable.Range((int)Keys.D0,10)).Select(v=>(Keys)v).ToArray();
        internal readonly WC.ComboBox Key;
        internal Editor(InputBinding binding,string id)
        {
            Key=NativeDialogControls.Choice(keys.Select(k=>InputDisplayNameProvider.KeyboardName(k)));Key.Width=100;
            WA.AutomationProperties.SetAutomationId(Key,id+"Key");WA.AutomationProperties.SetAutomationId(Enabled,id+"Enabled");
            void Toggle(){bool on=Enabled.IsChecked==true;Ctrl.IsEnabled=Alt.IsEnabled=Shift.IsEnabled=Key.IsEnabled=on;}
            Enabled.Checked+=(_,_)=>Toggle();Enabled.Unchecked+=(_,_)=>Toggle();Set(binding);Toggle();
        }
        internal void Set(InputBinding b){Enabled.IsChecked=!b.IsEmpty;Ctrl.IsChecked=b.Ctrl;Alt.IsChecked=b.Alt;Shift.IsChecked=b.Shift;Key.SelectedIndex=Math.Max(0,Array.IndexOf(keys,b.Key));}
        internal InputBinding Value=>Enabled.IsChecked!=true?new():new(InputBindingKind.Keyboard,keys[Math.Max(0,Key.SelectedIndex)],Ctrl:Ctrl.IsChecked==true,Alt:Alt.IsChecked==true,Shift:Shift.IsChecked==true);
        internal W.UIElement Row=>NativeDialogControls.Row(Enabled,Ctrl,Alt,Shift,Key);
    }
    internal NativeGameMediaSettingsDialog(GameMediaPreferences current,Func<GameMediaPreferences,string?> save):base("快捷键设置",620,450)
    {
        var shot=new Editor(current.Screenshot,"MediaScreenshot");var video=new Editor(current.Recording,"MediaRecording");
        var message=NativeDialogControls.Text("");WA.AutomationProperties.SetAutomationId(message,"MediaSettingsStatus");
        var body=NativeDialogControls.Column(NativeDialogControls.Text("截图",18),shot.Row,NativeDialogControls.Text("开始／停止录像",18),video.Row,
            NativeDialogControls.Text("Fusion 运行时生效，无需打开媒体窗口。截图针对前台游戏；录像键再次按下会停止并保存。"),message);
        var defaults=NativeDialogControls.Button("恢复默认",()=>{shot.Set(GameMediaPreferences.Default.Screenshot);video.Set(GameMediaPreferences.Default.Recording);message.Text="点击保存后生效。";});
        var apply=NativeDialogControls.Button("保存",()=>
        {
            try{var value=new GameMediaPreferences(shot.Value,video.Value);value.Validate();var error=save(value);if(error is not null){message.Text=error;return;}Finish(System.Windows.Forms.DialogResult.OK);}
            catch(Exception ex){message.Text=UiOperationText.Error(ex);}
        },true);WA.AutomationProperties.SetAutomationId(apply,"MediaSettingsSave");
        Content=NativeDialogControls.Layout(body,NativeDialogControls.Row(defaults,ResultButton("取消",System.Windows.Forms.DialogResult.Cancel),apply));
    }
}
