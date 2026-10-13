using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

internal sealed class GlobalKeyboardBindingHost : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int KeyDown = 0x100;
    private const int KeyUp = 0x101;
    private const int SysKeyDown = 0x104;
    private const int SysKeyUp = 0x105;
    private readonly Func<InputContextKind> _context;
    private readonly Func<InputBinding, bool> _capture;
    private readonly Action<string> _trace;
    private readonly Dictionary<InputActionId, (InputBinding Binding, Action Trigger)> _bindings = [];
    private readonly HookProc _proc;
    private IntPtr _hook;
    private Keys _captureModifiers;

    internal GlobalKeyboardBindingHost(Func<InputContextKind> context, Func<InputBinding, bool> capture, Action<string> trace)
    { _context = context; _capture = capture; _trace = trace; _proc = Hook; }

    internal void Apply(IEnumerable<(InputActionId Action, InputBinding Binding, Action Trigger)> values)
    {
        _bindings.Clear();
        foreach (var value in values.Where(x => x.Binding.Kind == InputBindingKind.Keyboard))
            _bindings[value.Action] = (value.Binding, value.Trigger);
        EnsureHook();
    }

    internal InputBinding? RegisteredBinding(InputActionId action) =>
        _bindings.TryGetValue(action, out var entry) ? entry.Binding : null;
    internal bool IsHookInstalled => _hook != IntPtr.Zero;
    internal Keys CaptureModifiers => _captureModifiers;
    internal void ResetCaptureModifiers() => _captureModifiers = Keys.None;
    internal InputBinding? CaptureCandidateForSmoke(Keys key,bool isDown)
    {
        var modifier=ModifierFor(key);
        if(modifier!=Keys.None)
        {
            if(isDown)_captureModifiers|=modifier;else _captureModifiers&=~modifier;
            return null;
        }
        if(!isDown)return null;
        return new(InputBindingKind.Keyboard,key,
            Ctrl:_captureModifiers.HasFlag(Keys.Control),Alt:_captureModifiers.HasFlag(Keys.Alt),Shift:_captureModifiers.HasFlag(Keys.Shift),Win:_captureModifiers.HasFlag(Keys.LWin));
    }

    private void EnsureHook()
    {
        if (_hook != IntPtr.Zero) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(module.ModuleName), 0);
    }

    private IntPtr Hook(int code, IntPtr wp, IntPtr lp)
    {
        if (code < 0 || wp.ToInt32() is not (KeyDown or SysKeyDown or KeyUp or SysKeyUp)) return Next(code, wp, lp);
        var data = Marshal.PtrToStructure<KeyboardData>(lp);
        var key = (Keys)data.VirtualKey;
        var context = _context();
        var isDown = wp.ToInt32() is KeyDown or SysKeyDown;
        var modifier = ModifierFor(key);
        if (context == InputContextKind.BindingCapture && modifier != Keys.None)
        {
            if (isDown) _captureModifiers |= modifier;
            else _captureModifiers &= ~modifier;
        }
        if (!isDown)
        {
            if (context == InputContextKind.BindingCapture && modifier != Keys.None) return new IntPtr(1);
            return Next(code, wp, lp);
        }
        if (context != InputContextKind.BindingCapture && _captureModifiers != Keys.None) ResetCaptureModifiers();
        var modifiers = context == InputContextKind.BindingCapture ? _captureModifiers : CurrentModifiers(key);
        var binding = new InputBinding(InputBindingKind.Keyboard, key,
            Ctrl: modifiers.HasFlag(Keys.Control), Alt: modifiers.HasFlag(Keys.Alt), Shift: modifiers.HasFlag(Keys.Shift),
            Win: context == InputContextKind.BindingCapture ? modifiers.HasFlag(Keys.LWin) : Down(Keys.LWin)||Down(Keys.RWin)||key is Keys.LWin or Keys.RWin,
            Numpad:key==Keys.Enter&&(data.Flags&1)!=0);

        if (context == InputContextKind.BindingCapture && _capture(binding))
        {
            Trace(data, binding, context, InputConsumptionPolicy.Decide(context, true, key == Keys.Escape), "Capture");
            return new IntPtr(1);
        }

        var match = _bindings.FirstOrDefault(x => x.Value.Binding == binding);
        var matched = !match.Equals(default(KeyValuePair<InputActionId, (InputBinding Binding, Action Trigger)>));
        var decision = InputConsumptionPolicy.Decide(context, matched, key == Keys.Escape);
        if (matched) match.Value.Trigger();
        Trace(data, binding, context, decision, matched ? match.Key.ToString() : "None");
        return decision.Suppressed ? new IntPtr(1) : Next(code, wp, lp);
    }

    private void Trace(KeyboardData data, InputBinding binding, InputContextKind context, InputConsumptionDecision decision, string action) =>
        _trace($"Device=Keyboard Key={binding.DisplayName} Injected={(data.Flags & 0x10) != 0} Context={context} BindingMatch={decision.BindingMatch} Action={action} Handled={decision.Handled} Suppressed={decision.Suppressed} PassThrough={decision.PassThrough} Reason={decision.Reason}");

    private IntPtr Next(int code, IntPtr wp, IntPtr lp) => CallNextHookEx(_hook, code, wp, lp);
    private static Keys CurrentModifiers(Keys current)
    {
        var result = Keys.None;
        if (Down(Keys.ControlKey) || current is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey) result |= Keys.Control;
        if (Down(Keys.Menu) || current is Keys.Menu or Keys.LMenu or Keys.RMenu) result |= Keys.Alt;
        if (Down(Keys.ShiftKey) || current is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey) result |= Keys.Shift;
        return result;
    }
    private static Keys ModifierFor(Keys key) => key switch
    {
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => Keys.Control,
        Keys.Menu or Keys.LMenu or Keys.RMenu => Keys.Alt,
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => Keys.Shift,
        Keys.LWin or Keys.RWin => Keys.LWin,
        _ => Keys.None
    };
    private static bool Down(Keys key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _bindings.Clear();
        ResetCaptureModifiers();
    }

    private delegate IntPtr HookProc(int code, IntPtr wp, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint VirtualKey, ScanCode, Flags, Time; public IntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(Keys key);
}
