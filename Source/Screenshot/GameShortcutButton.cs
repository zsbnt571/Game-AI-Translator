namespace ScreenshotTranslationUiTester;

internal static class GameShortcut
{
    internal static readonly string[] KeysSupported=Enumerable.Range(1,12).Select(n=>"F"+n)
        .Concat(Enumerable.Range('A',26).Select(c=>((char)c).ToString()))
        .Concat(Enumerable.Range(0,10).Select(n=>n.ToString()))
        .Concat(new[]{"Space","Tab","Backspace","Delete","Insert","Home","End","PageUp","PageDown","Up","Down","Left","Right"}).ToArray();
    internal static bool TryNormalize(string? source,out string value)
    {
        value="";var parts=(source??"").Split('+',StringSplitOptions.TrimEntries);
        if(parts.Length<1||parts.Length>4)return false;
        var key=KeysSupported.FirstOrDefault(k=>string.Equals(k,parts[^1],StringComparison.OrdinalIgnoreCase));
        if(key is null)return false;var modifiers=parts[..^1];
        if(modifiers.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=modifiers.Length||modifiers.Any(m=>!new[]{"Ctrl","Alt","Shift"}.Contains(m,StringComparer.OrdinalIgnoreCase)))return false;
        value=string.Concat(new[]{"Ctrl","Alt","Shift"}.Where(m=>modifiers.Contains(m,StringComparer.OrdinalIgnoreCase)).Select(m=>m+"+"))+key;return true;
    }
    internal static string Normalize(string value)=>TryNormalize(value,out var normalized)?normalized:throw new ArgumentException("无效的游戏内快捷键。");
}

internal sealed class GameShortcutButton : Button
{
    internal string Shortcut {get;private set;}="F8";
    internal bool Capturing {get;private set;}
    internal event EventHandler? ShortcutChanged;
    internal GameShortcutButton(){Text="F8";Width=220;Height=36;FlatStyle=FlatStyle.Flat;AccessibleName="游戏内扫描快捷键";}
    internal void SetShortcut(string value)
    {Shortcut=GameShortcut.Normalize(value);Capturing=false;Text=Shortcut;ShortcutChanged?.Invoke(this,EventArgs.Empty);}
    protected override void OnClick(EventArgs e){base.OnClick(e);Capturing=true;Text="按下快捷键，Esc 取消";Focus();}
    protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Capturing=false;Text=Shortcut;}
    protected override bool IsInputKey(Keys keyData)=>Capturing||base.IsInputKey(keyData);
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
    {if(Capturing){ApplyKey(keyData);return true;}return base.ProcessCmdKey(ref msg,keyData);}
    protected override void OnKeyDown(KeyEventArgs e){if(Capturing){ApplyKey(e.KeyData);e.SuppressKeyPress=true;}else base.OnKeyDown(e);}
    internal bool ApplyKey(Keys data)
    {
        var key=data&Keys.KeyCode;
        if(key==Keys.Escape){Capturing=false;Text=Shortcut;return false;}
        if(key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu)return false;
        var name=key switch{>=Keys.D0 and <=Keys.D9=>((int)key-(int)Keys.D0).ToString(),Keys.Back=>"Backspace",Keys.Prior=>"PageUp",Keys.Next=>"PageDown",_=>key.ToString()};
        var value=(data.HasFlag(Keys.Control)?"Ctrl+":"")+(data.HasFlag(Keys.Alt)?"Alt+":"")+(data.HasFlag(Keys.Shift)?"Shift+":"")+name;
        if(!GameShortcut.TryNormalize(value,out var normalized)){Text="此按键不可用，请重新按键";return false;}
        SetShortcut(normalized);return true;
    }
}
