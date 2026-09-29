using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace ScreenshotTranslationUiTester;

public enum InputBindingKind { None, Keyboard, MouseButton, MouseWheel }
public enum InputMouseButton { Left, Right, Middle, XButton1, XButton2 }
public enum InputWheelDirection { Up, Down }
public enum InputBindingScope { Global, PreviewLocal }
public enum InputActionId { StartCapture, CancelCapture, ToggleResults, PreviewZoomIn, PreviewZoomOut, PreviewResetFit }

public sealed record InputBinding(InputBindingKind Kind=InputBindingKind.None, Keys Key=Keys.None,
    InputMouseButton MouseButton=InputMouseButton.Middle, InputWheelDirection Wheel=InputWheelDirection.Up,
    bool Ctrl=false,bool Alt=false,bool Shift=false,bool Win=false,bool Numpad=false)
{
    [JsonIgnore] public bool IsEmpty=>Kind==InputBindingKind.None;
    [JsonIgnore] public Keys Modifiers=>(Ctrl?Keys.Control:0)|(Alt?Keys.Alt:0)|(Shift?Keys.Shift:0);
    [JsonIgnore] public string DisplayName=>InputDisplayNameProvider.GetDisplayName(this);
}

public sealed record InputBindingEntry(InputActionId Action,InputBindingScope Scope,InputBinding Binding);
public sealed record InputBindingConflict(InputActionId First,InputActionId Second,bool CrossScope);

public static class InputBindingDefaults
{
    public static InputBinding StartCapture=>new(InputBindingKind.Keyboard,Keys.Z,Ctrl:true,Alt:true);
    public static InputBinding CancelCapture=>new(InputBindingKind.Keyboard,Keys.Escape);
    public static InputBinding ToggleResults=>new(InputBindingKind.Keyboard,Keys.H,Ctrl:true,Alt:true);
    public static InputBinding ZoomIn=>new(InputBindingKind.MouseWheel,Wheel:InputWheelDirection.Up,Ctrl:true);
    public static InputBinding ZoomOut=>new(InputBindingKind.MouseWheel,Wheel:InputWheelDirection.Down,Ctrl:true);
    public static InputBinding ResetFit=>new(InputBindingKind.MouseButton,MouseButton:InputMouseButton.Middle);
}

public static class InputBindingFormatter
{
    public static string Format(InputBinding b)=>InputDisplayNameProvider.GetDisplayName(b);
    public static bool IsRiskyGlobal(InputBinding b)=>!b.Ctrl&&!b.Alt&&!b.Shift&&!b.Win&&(b.Kind==InputBindingKind.MouseWheel||b.Kind==InputBindingKind.MouseButton&&b.MouseButton is InputMouseButton.Left or InputMouseButton.Right);
}

public static class InputDisplayNameProvider
{
    public const string Unbound="未绑定";
    public static string GetDisplayName(InputBinding binding)
    {
        if(binding.IsEmpty)return Unbound;
        var parts=new List<string>(5);
        if(binding.Ctrl)parts.Add("Ctrl");
        if(binding.Alt)parts.Add("Alt");
        if(binding.Shift)parts.Add("Shift");
        if(binding.Win)parts.Add("Win");
        parts.Add(binding.Kind switch
        {
            InputBindingKind.Keyboard=>KeyboardName(binding.Key,binding.Numpad),
            InputBindingKind.MouseButton=>binding.MouseButton switch
            {
                InputMouseButton.Left=>"Left Mouse",InputMouseButton.Right=>"Right Mouse",
                InputMouseButton.Middle=>"Middle Mouse",InputMouseButton.XButton1=>"Mouse 4",
                InputMouseButton.XButton2=>"Mouse 5",_=>$"Mouse {(int)binding.MouseButton+3}"
            },
            InputBindingKind.MouseWheel=>binding.Wheel==InputWheelDirection.Up?"Wheel Up":"Wheel Down",
            _=>Unbound
        });
        return string.Join(" + ",parts);
    }

    public static string KeyboardName(Keys key,bool numpad=false)
    {
        var code=key&Keys.KeyCode;
        if(numpad&&code==Keys.Enter)return "Numpad Enter";
        if(code>=Keys.A&&code<=Keys.Z)return code.ToString();
        if(code>=Keys.D0&&code<=Keys.D9)return ((int)code-(int)Keys.D0).ToString();
        if(code>=Keys.NumPad0&&code<=Keys.NumPad9)return $"Numpad {(int)code-(int)Keys.NumPad0}";
        if(code>=Keys.F1&&code<=Keys.F24)return code.ToString();
        return code switch
        {
            Keys.Escape=>"Esc",Keys.Enter=>"Enter",Keys.Space=>"Space",Keys.Tab=>"Tab",
            Keys.Back=>"Backspace",Keys.Delete=>"Delete",Keys.Insert=>"Insert",Keys.Home=>"Home",
            Keys.End=>"End",Keys.PageUp=>"Page Up",Keys.PageDown=>"Page Down",
            Keys.Up=>"Arrow Up",Keys.Down=>"Arrow Down",Keys.Left=>"Arrow Left",Keys.Right=>"Arrow Right",
            Keys.Add=>"Numpad +",Keys.Subtract=>"Numpad -",Keys.Multiply=>"Numpad *",
            Keys.Divide=>"Numpad /",Keys.Decimal=>"Numpad .",
            Keys.CapsLock=>"Caps Lock",Keys.NumLock=>"Num Lock",Keys.Scroll=>"Scroll Lock",
            Keys.PrintScreen=>"Print Screen",Keys.Pause=>"Pause",
            Keys.LControlKey=>"Left Ctrl",Keys.RControlKey=>"Right Ctrl",
            Keys.LShiftKey=>"Left Shift",Keys.RShiftKey=>"Right Shift",
            Keys.LMenu=>"Left Alt",Keys.RMenu=>"Right Alt",Keys.LWin=>"Left Win",Keys.RWin=>"Right Win",
            Keys.Oem1=>";",Keys.Oemplus=>"=",Keys.Oemcomma=>",",Keys.OemMinus=>"-",
            Keys.OemPeriod=>".",Keys.Oem2=>"/",Keys.Oem3=>"`",Keys.Oem4=>"[",
            Keys.Oem5=>"\\",Keys.Oem6=>"]",Keys.Oem7=>"'",
            _=>code.ToString()
        };
    }
}

public static class InputBindingConflictDetector
{
    public static IReadOnlyList<InputBindingConflict> Find(IEnumerable<InputBindingEntry> entries)
    {var a=entries.Where(x=>!x.Binding.IsEmpty).ToArray();var r=new List<InputBindingConflict>();for(var i=0;i<a.Length;i++)for(var j=i+1;j<a.Length;j++)if(a[i].Binding==a[j].Binding)r.Add(new(a[i].Action,a[j].Action,a[i].Scope!=a[j].Scope));return r;}
}

public static class InputBindingConflictPresenter
{
    public static string Describe(InputBinding binding,string firstAction,string secondAction)=>
        $"{InputDisplayNameProvider.GetDisplayName(binding)} 已被 {firstAction} 与 {secondAction} 使用。";
}

internal sealed class InputBindingCaptureButton:Button
{
    public InputBinding Binding{get;private set;} public event Action<InputBinding>? BindingChanged;private bool _capturing;
    public InputBindingCaptureButton(InputBinding binding){Binding=binding;Text=binding.DisplayName;AutoSize=false;Width=190;Height=32;}
    public void BeginCapture(){_capturing=true;Text="请按下新的按键、组合键或鼠标操作";Focus();}
    public void CancelCapture(){_capturing=false;Text=Binding.DisplayName;}
    public bool IsCapturing=>_capturing;
    public void SetBinding(InputBinding value){Binding=value;_capturing=false;Text=value.DisplayName;BindingChanged?.Invoke(value);}
    protected override bool IsInputKey(Keys keyData)=>_capturing||base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e){if(!_capturing){base.OnKeyDown(e);return;}if(e.KeyCode==Keys.Escape){_capturing=false;Text=Binding.DisplayName;e.SuppressKeyPress=true;return;}if(e.KeyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey){e.SuppressKeyPress=true;return;}var mods=e.Modifiers;SetBinding(new(InputBindingKind.Keyboard,e.KeyCode,Ctrl:mods.HasFlag(Keys.Control),Alt:mods.HasFlag(Keys.Alt),Shift:mods.HasFlag(Keys.Shift)));e.SuppressKeyPress=true;}
    protected override void OnMouseDown(MouseEventArgs e){if(!_capturing){base.OnMouseDown(e);return;}var b=e.Button switch{MouseButtons.Left=>InputMouseButton.Left,MouseButtons.Right=>InputMouseButton.Right,MouseButtons.Middle=>InputMouseButton.Middle,MouseButtons.XButton1=>InputMouseButton.XButton1,_=>InputMouseButton.XButton2};var m=Control.ModifierKeys;SetBinding(new(InputBindingKind.MouseButton,MouseButton:b,Ctrl:m.HasFlag(Keys.Control),Alt:m.HasFlag(Keys.Alt),Shift:m.HasFlag(Keys.Shift)));}
    protected override void OnMouseWheel(MouseEventArgs e){if(!_capturing){base.OnMouseWheel(e);return;}var m=Control.ModifierKeys;SetBinding(new(InputBindingKind.MouseWheel,Wheel:e.Delta>0?InputWheelDirection.Up:InputWheelDirection.Down,Ctrl:m.HasFlag(Keys.Control),Alt:m.HasFlag(Keys.Alt),Shift:m.HasFlag(Keys.Shift)));}
}

public static class InputBindingMatcher
{
    public static bool Keyboard(InputBinding b,Keys key,Keys mods,bool win=false,bool numpad=false)=>b.Kind==InputBindingKind.Keyboard&&b.Key==key&&b.Modifiers==mods&&b.Win==win&&b.Numpad==numpad;
    public static bool Mouse(InputBinding b,MouseButtons button,Keys mods,bool win=false)=>b.Kind==InputBindingKind.MouseButton&&b.Modifiers==mods&&b.Win==win&&b.MouseButton==(button switch{MouseButtons.Left=>InputMouseButton.Left,MouseButtons.Right=>InputMouseButton.Right,MouseButtons.Middle=>InputMouseButton.Middle,MouseButtons.XButton1=>InputMouseButton.XButton1,_=>InputMouseButton.XButton2});
    public static bool Wheel(InputBinding b,int delta,Keys mods,bool win=false)=>b.Kind==InputBindingKind.MouseWheel&&b.Modifiers==mods&&b.Win==win&&b.Wheel==(delta>0?InputWheelDirection.Up:InputWheelDirection.Down);
}
