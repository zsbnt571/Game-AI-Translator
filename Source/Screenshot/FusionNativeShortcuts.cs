using WI = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private Func<Rectangle>? _nativeBindingBounds;
    private Action? _nativeBindingRefresh;
    private System.Windows.Threading.DispatcherTimer? _nativeBindingArmTimer;
    private string _nativeEmbeddedShortcut = "F8";
    private bool _nativeGameKeyCapturing;
    private string? _nativeGameKeyHint;
    private WI.MouseButton? _nativeRecordedMouseButton;
    internal event Action? NativeShortcutsChanged;
    internal string NativeEmbeddedShortcut => _nativeEmbeddedShortcut;
    internal string NativeGameShortcutText => _nativeGameKeyCapturing ? _nativeGameKeyHint ?? "按下快捷键，Esc 取消" : _nativeEmbeddedShortcut;
    internal InputBinding NativeBinding(InputActionId action) => BindingForAction(action);
    internal string NativeBindingText(InputActionId action) => _activeBindingAction == action && _bindingCaptureState != BindingCaptureState.Idle ? "按下快捷键，Esc 取消" : BindingForAction(action).DisplayName;
    internal static InputBinding NativeDefaultBinding(InputActionId action) => action switch
    {
        InputActionId.StartCapture => InputBindingDefaults.StartCapture, InputActionId.CancelCapture => InputBindingDefaults.CancelCapture,
        InputActionId.ToggleResults => InputBindingDefaults.ToggleResults, InputActionId.PreviewZoomIn => InputBindingDefaults.ZoomIn,
        InputActionId.PreviewZoomOut => InputBindingDefaults.ZoomOut, _ => InputBindingDefaults.ResetFit
    };
    internal void NativeSetBinding(InputActionId action, InputBinding binding)
    {
        CancelBindingCapture("NativeShortcutEdit");
        switch (action)
        {
            case InputActionId.StartCapture: _translationOptions.StartCaptureBinding = binding; break;
            case InputActionId.CancelCapture: _translationOptions.CancelCaptureBinding = binding; break;
            case InputActionId.ToggleResults: _translationOptions.ToggleResultsBinding = binding; break;
            case InputActionId.PreviewZoomIn: _translationOptions.PreviewZoomInBinding = binding; break;
            case InputActionId.PreviewZoomOut: _translationOptions.PreviewZoomOutBinding = binding; break;
            case InputActionId.PreviewResetFit: _translationOptions.PreviewResetFitBinding = binding; break;
        }
        _settingsDirty = true; ShowBindingWarnings(); UpdateSettingsSaveState(); ApplyHotKey(false); SaveSettings(false); NativeShortcutsChanged?.Invoke();
    }
    internal void NativeBeginBinding(InputActionId action, Func<Rectangle> bounds)
    {
        CancelBindingCapture("NewNativeCapture"); _activeBindingAction = action; _nativeBindingBounds = bounds;
        _nativeBindingRefresh = () => NativeShortcutsChanged?.Invoke(); _bindingCaptureState = BindingCaptureState.Arming;
        _nativeBindingArmTimer ??= new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _nativeBindingArmTimer.Tick -= NativeArmTick; _nativeBindingArmTimer.Tick += NativeArmTick; _nativeBindingArmTimer.Start();
        NativeShortcutsChanged?.Invoke();
    }
    private void NativeArmTick(object? sender, EventArgs e)
    {
        if (_bindingCaptureState != BindingCaptureState.Arming) { _nativeBindingArmTimer?.Stop(); return; }
        if (!AllMouseButtonsReleased()) return;
        _bindingCaptureState = BindingCaptureState.Capturing; _nativeBindingArmTimer?.Stop(); PublishBindingInputLease(); NativeShortcutsChanged?.Invoke();
    }
    private void NativeFinishBinding(InputBinding binding)
    {
        if (_nativeBindingBounds is null || _activeBindingAction is not { } action || _bindingCaptureState != BindingCaptureState.Capturing) return;
        // Close the input lease before displaying any confirmation window.
        _inputRuntime.EndLease(); CancelBindingCapture("NativeCommit");
        if (IsHighRiskGlobalMouseBinding(action, binding) && AppDialog.Show(this, "确认高风险全局快捷键", "鼠标左键或右键是常用系统操作，设为全局快捷键可能造成频繁触发。确定保存？", AppDialogKind.Confirmation) != DialogResult.Yes) return;
        NativeSetBinding(action, binding);
    }
    internal void NativeBeginGameShortcut()
    { CancelBindingCapture("GameKeyCapture"); _nativeGameKeyCapturing = true; _nativeGameKeyHint = null; NativeShortcutsChanged?.Invoke(); }
    internal void NativeSetGameShortcut(string value)
    { _nativeEmbeddedShortcut = GameShortcut.Normalize(value); _nativeGameKeyCapturing = false; _nativeGameKeyHint = null; _settingsDirty = true; UpdateSettingsSaveState(); NativeShortcutsChanged?.Invoke(); }
    internal bool NativeBindingKey(WI.Key key, WI.ModifierKeys modifiers)
    {
        if (_nativeBindingBounds is null && !_nativeGameKeyCapturing) return false;
        var windowsKey = (Keys)WI.KeyInterop.VirtualKeyFromKey(key);
        if (windowsKey == Keys.Escape) { CancelBindingCapture("NativeEscape"); return true; }
        if (IsModifierTrigger(windowsKey)) return true;
        if (_nativeGameKeyCapturing)
        {
            var name = windowsKey switch { >= Keys.D0 and <= Keys.D9 => ((int)windowsKey - (int)Keys.D0).ToString(), Keys.Back => "Backspace", Keys.Prior => "PageUp", Keys.Next => "PageDown", _ => windowsKey.ToString() };
            var value = (modifiers.HasFlag(WI.ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(WI.ModifierKeys.Alt) ? "Alt+" : "") + (modifiers.HasFlag(WI.ModifierKeys.Shift) ? "Shift+" : "") + name;
            if (!modifiers.HasFlag(WI.ModifierKeys.Windows) && GameShortcut.TryNormalize(value, out var normalized)) NativeSetGameShortcut(normalized);
            else { _nativeGameKeyHint = "此按键不可用，请重新按键"; NativeShortcutsChanged?.Invoke(); }
            return true;
        }
        if (_bindingCaptureState == BindingCaptureState.Capturing)
            NativeFinishBinding(new(InputBindingKind.Keyboard, windowsKey, Ctrl: modifiers.HasFlag(WI.ModifierKeys.Control), Alt: modifiers.HasFlag(WI.ModifierKeys.Alt), Shift: modifiers.HasFlag(WI.ModifierKeys.Shift), Win: modifiers.HasFlag(WI.ModifierKeys.Windows)));
        return true;
    }
    internal bool NativeBindingMouse(WI.MouseButton? button, int wheel, WI.ModifierKeys modifiers, System.Windows.Point screen)
    {
        if (_nativeGameKeyCapturing) { CancelBindingCapture("GameKeyOutsideClick"); return false; }
        if (_nativeBindingBounds is null) return false;
        if (_bindingCaptureState != BindingCaptureState.Capturing) return true;
        if (button is WI.MouseButton.Left or WI.MouseButton.Right && !_nativeBindingBounds().Contains((int)screen.X, (int)screen.Y))
        { CancelBindingCapture("NativeOutsideClick"); return false; }
        var binding = button is null ? new InputBinding(InputBindingKind.MouseWheel, Wheel: wheel > 0 ? InputWheelDirection.Up : InputWheelDirection.Down) : new InputBinding(InputBindingKind.MouseButton, MouseButton: button switch
        { WI.MouseButton.Left => InputMouseButton.Left, WI.MouseButton.Right => InputMouseButton.Right, WI.MouseButton.Middle => InputMouseButton.Middle, WI.MouseButton.XButton1 => InputMouseButton.XButton1, _ => InputMouseButton.XButton2 });
        _nativeRecordedMouseButton = button;
        NativeFinishBinding(binding with { Ctrl = modifiers.HasFlag(WI.ModifierKeys.Control), Alt = modifiers.HasFlag(WI.ModifierKeys.Alt), Shift = modifiers.HasFlag(WI.ModifierKeys.Shift), Win = modifiers.HasFlag(WI.ModifierKeys.Windows) }); return true;
    }
    internal bool NativeBindingMouseReleased(WI.MouseButton button)
    { if (_nativeRecordedMouseButton != button) return false; _nativeRecordedMouseButton = null; return true; }
    private void CancelNativeBinding()
    {
        _nativeBindingArmTimer?.Stop(); _nativeBindingBounds = null; _nativeGameKeyCapturing = false; _nativeGameKeyHint = null;
        var refresh = _nativeBindingRefresh; _nativeBindingRefresh = null; refresh?.Invoke(); NativeShortcutsChanged?.Invoke();
    }
}
