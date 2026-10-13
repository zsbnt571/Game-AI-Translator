using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace ScreenshotTranslationUiTester;

internal sealed record DesktopInputBinding(InputActionId Id, InputBinding Binding, Action Trigger);
internal sealed record DesktopInputLease(long Id, IntPtr Window, bool BindingCapture, Rectangle Editor,
    Action<InputBinding> Commit, Action Cancel);

// One thread owns both hooks. UI supplies immutable bindings and an explicit interaction
// lease; a background ordinary dialog never creates a lease. No Control is read here.
internal sealed class DesktopInputRuntime : IDisposable
{
    private sealed record Configuration(long Revision, DesktopInputBinding[] Bindings);
    private Configuration configuration = new(0, []);
    private DesktopInputLease? lease;
    private IntPtr[] previewWindows = [];
    private SynchronizationContext? ui;
    private Thread? thread;
    private uint threadId;
    private int stopping, wakePending;
    private IntPtr keyboardHook, mouseHook;
    private readonly HookProc keyboardProc, mouseProc;
    private readonly bool[] pressed = new bool[512];
    private readonly IntPtr[] consumed = new IntPtr[512];
    private readonly bool[] mousePressed = new bool[5];
    private readonly IntPtr[] mouseConsumed = new IntPtr[5];
    private readonly int[] actionPending = new int[3];
    private long capturePending, cancelledLease;
    private long revision, leaseId;
    private (long Revision, long Lease) attemptedKeyboard = (-1, -1), attemptedMouse = (-1, -1);
    private int keyboardInstalled, mouseInstalled;
    private readonly InputEventLog log = new();
    private Action<bool, string>? readiness;
    private long lastSlowEvent;
    internal bool KeyboardInstalled => Volatile.Read(ref keyboardInstalled) != 0;
    internal bool MouseInstalled => Volatile.Read(ref mouseInstalled) != 0;
    internal bool Ready => thread is not null && Volatile.Read(ref stopping) == 0;
    internal bool HasConfiguredBindings => Volatile.Read(ref stopping) == 0 && Volatile.Read(ref configuration).Bindings.Length != 0;
    internal void ProtectPreviewMouse(IntPtr[] windows) => Volatile.Write(ref previewWindows, windows);

    internal DesktopInputRuntime() { keyboardProc = Keyboard; mouseProc = Mouse; }

    // UI thread only. Replacement does not accumulate registrations or threads.
    internal void Configure(DesktopInputBinding[] bindings, Action<bool, string> ready)
    {
        if (Volatile.Read(ref stopping) != 0) { ready(false, "输入监听已停止，请退出后重新启动软件。"); return; }
        readiness = ready;
        Volatile.Write(ref configuration, new(++revision, bindings.Where(x => !x.Binding.IsEmpty).ToArray()));
        EnsureStarted();
        Wake();
    }
    private void EnsureStarted()
    {
        ui ??= SynchronizationContext.Current ?? throw new InvalidOperationException("UI context is not ready.");
        if (thread is null)
        {
            thread = new Thread(Run) { Name = "Fusion input message loop", IsBackground = true };
            thread.Start();
        }
    }

    internal InputBinding? RegisteredBinding(InputActionId action)
    {
        var binding = Volatile.Read(ref configuration).Bindings.FirstOrDefault(x => x.Id == action)?.Binding;
        return binding is null ? null : binding.Kind == InputBindingKind.Keyboard
            ? (KeyboardInstalled ? binding : null) : (KeyboardInstalled && MouseInstalled ? binding : null);
    }
    internal void Disable()
    {
        readiness = null;
        EndLease();
        Volatile.Write(ref configuration, new(++revision, []));
        Wake();
    }

    internal void BeginLease(IntPtr window, bool bindingCapture, Rectangle editor, Action<InputBinding> commit, Action cancel)
    {
        if (AppDataPaths.DisableGlobalInput || window == IntPtr.Zero || Volatile.Read(ref stopping) != 0) return;
        var value = new DesktopInputLease(++leaseId, window, bindingCapture, editor, commit, cancel);
        Volatile.Write(ref lease, value);
        log.Write($"Lease opened id={value.Id} mode={(bindingCapture ? "binding" : "selection")}");
        EnsureStarted();
        Wake();
    }
    internal void EndLease()
    {
        var old = Interlocked.Exchange(ref lease, null);
        if (old is not null) log.Write($"Lease ended id={old.Id}");
        Wake();
    }

    private void Wake()
    {
        var id = Volatile.Read(ref threadId);
        if (id == 0 || Interlocked.Exchange(ref wakePending, 1) != 0) return;
        if (!PostThreadMessage(id, 0x8001, IntPtr.Zero, IntPtr.Zero))
        { Interlocked.Exchange(ref wakePending, 0); log.Write("Input wake could not be posted"); }
    }
    private void Run()
    {
        try
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Establish queue before publishing thread id.
            Volatile.Write(ref threadId, GetCurrentThreadId());
            log.Write($"Input thread ready managed={Environment.CurrentManagedThreadId} native={threadId}");
            ApplyHooks();
            while (Volatile.Read(ref stopping) == 0)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result <= 0) { if (result < 0) log.Write("Input message loop failed"); break; }
                if (message.Message == 0x8001)
                { Interlocked.Exchange(ref wakePending, 0); if (Volatile.Read(ref stopping) == 0) ApplyHooks(); }
                else { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
        }
        catch (Exception ex) { log.Write("Input loop failed type=" + ex.GetType().Name); }
        finally
        {
            var unexpected = Interlocked.Exchange(ref stopping, 1) == 0;
            Remove(ref keyboardHook, ref keyboardInstalled, "keyboard");
            Remove(ref mouseHook, ref mouseInstalled, "mouse");
            Array.Clear(pressed); Array.Clear(consumed); Array.Clear(mousePressed); Array.Clear(mouseConsumed);
            Volatile.Write(ref threadId, 0);
            log.Write("Input thread stopped; owned hooks released");
            if (unexpected)
            {
                // Report terminal failure without using Post, which deliberately
                // rejects ordinary queued actions after stopping.
                try { ui?.Post(_ => readiness?.Invoke(false, "输入监听已停止，请退出后重新启动软件。"), null); }
                catch (InvalidOperationException) { log.CountDropped(); }
            }
            log.Complete();
        }
    }
    private void ApplyHooks()
    {
        var config = Volatile.Read(ref configuration);
        var active = Volatile.Read(ref lease);
        var attempt = (config.Revision, active?.Id ?? 0);
        var keyboardNeeded = active is not null || config.Bindings.Length != 0;
        var mouseNeeded = active?.BindingCapture == true || config.Bindings.Any(x => x.Binding.Kind is InputBindingKind.MouseButton or InputBindingKind.MouseWheel);
        // Keep a hook until its consumed downs have corresponding releases. New events
        // already see the new configuration and pass through when no longer applicable.
        keyboardNeeded |= consumed.Any(x => x != IntPtr.Zero);
        mouseNeeded |= mouseConsumed.Any(x => x != IntPtr.Zero);
        if (!keyboardNeeded) Remove(ref keyboardHook, ref keyboardInstalled, "keyboard");
        else if (keyboardHook == IntPtr.Zero && attemptedKeyboard != attempt)
        {
            attemptedKeyboard = attempt;
            // Initial state only: never poll the full keyboard in a callback or timer.
            Array.Clear(pressed); Array.Clear(consumed);
            for (var i = 0; i < 256; i++) pressed[i] = (GetAsyncKeyState(i) & 0x8000) != 0;
            pressed[(int)Keys.Enter + 256] = pressed[(int)Keys.Enter];
            keyboardHook = SetWindowsHookEx(13, keyboardProc, GetModuleHandle(null), 0);
            Volatile.Write(ref keyboardInstalled, keyboardHook != IntPtr.Zero ? 1 : 0);
            log.Write($"Keyboard hook installed={KeyboardInstalled} error={(KeyboardInstalled ? 0 : Marshal.GetLastWin32Error())}");
        }
        if (!mouseNeeded) Remove(ref mouseHook, ref mouseInstalled, "mouse");
        else if (mouseHook == IntPtr.Zero && attemptedMouse != attempt)
        {
            attemptedMouse = attempt;
            Array.Clear(mousePressed); Array.Clear(mouseConsumed);
            var buttons = new[] { 1, 2, 4, 5, 6 };
            for (var i = 0; i < buttons.Length; i++) mousePressed[i] = (GetAsyncKeyState(buttons[i]) & 0x8000) != 0;
            mouseHook = SetWindowsHookEx(14, mouseProc, GetModuleHandle(null), 0);
            Volatile.Write(ref mouseInstalled, mouseHook != IntPtr.Zero ? 1 : 0);
            log.Write($"Mouse hook installed={MouseInstalled} error={(MouseInstalled ? 0 : Marshal.GetLastWin32Error())}");
        }
        var ok = (!keyboardNeeded || KeyboardInstalled) && (!mouseNeeded || MouseInstalled);
        Notify(ok, ok ? "输入监听已就绪。" : "输入监听未能完整启用，请查看输入日志。");
    }
    private void Remove(ref IntPtr hook, ref int installed, string kind)
    {
        if (hook == IntPtr.Zero) return;
        var ok = UnhookWindowsHookEx(hook);
        log.Write($"{kind} hook removed={ok}");
        hook = IntPtr.Zero; Volatile.Write(ref installed, 0);
        if (kind == "keyboard") attemptedKeyboard = (-1, -1); else attemptedMouse = (-1, -1);
    }
    private void Notify(bool ok, string message)
    {
        var expected = Volatile.Read(ref configuration);
        Post(() => { if (ReferenceEquals(expected, Volatile.Read(ref configuration))) readiness?.Invoke(ok, message); });
    }
    private bool Post(Action action)
    {
        if (Volatile.Read(ref stopping) != 0 || ui is null) return false;
        try { ui.Post(_ => { if (Volatile.Read(ref stopping) == 0) action(); }, null); return true; }
        catch (InvalidOperationException) { log.CountDropped(); return false; }
    }
    private bool Owns(DesktopInputLease? value, IntPtr foreground) => value is not null && foreground != IntPtr.Zero
        && foreground == value.Window && IsWindowVisible(value.Window) && ReferenceEquals(value, Volatile.Read(ref lease));
    private bool Capture(DesktopInputLease value, InputBinding? binding, bool cancel)
    {
        if (Volatile.Read(ref cancelledLease) == value.Id || Interlocked.CompareExchange(ref capturePending, value.Id, 0) != 0) return false;
        var posted = Post(() =>
        {
            try
            {
                if (!Owns(value, GetForegroundWindow())) return;
                if (cancel) value.Cancel(); else if (binding is not null) value.Commit(binding);
            }
            finally { Interlocked.CompareExchange(ref capturePending, 0, value.Id); }
        });
        if (!posted) Interlocked.CompareExchange(ref capturePending, 0, value.Id);
        else { Volatile.Write(ref cancelledLease, value.Id); log.Write($"Lease action queued id={value.Id} cancel={cancel}"); }
        return posted;
    }
    private bool Trigger(Configuration config, DesktopInputBinding entry, DesktopInputLease? active, IntPtr foreground)
    {
        // Cancel is meaningful only for an active selection belonging to the foreground.
        if (entry.Id == InputActionId.CancelCapture && (!Owns(active, foreground) || active!.BindingCapture)) return false;
        var index = (int)entry.Id;
        if (index is < 0 or > 2 || Interlocked.CompareExchange(ref actionPending[index], 1, 0) != 0) return false;
        var posted = Post(() =>
        {
            try
            {
                if (!ReferenceEquals(config, Volatile.Read(ref configuration)) || GetForegroundWindow() != foreground) return;
                if (entry.Id == InputActionId.CancelCapture && !Owns(active, foreground)) return;
                if (entry.Id == InputActionId.CancelCapture) active!.Cancel();
                else entry.Trigger();
            }
            finally { Interlocked.Exchange(ref actionPending[index], 0); }
        });
        if (!posted) Interlocked.Exchange(ref actionPending[index], 0);
        else log.Action(entry.Id);
        return posted;
    }
    private IntPtr Keyboard(int code, IntPtr wp, IntPtr lp)
    {
        if (code < 0 || Volatile.Read(ref stopping) != 0) return CallNextHookEx(keyboardHook, code, wp, lp);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var msg = wp.ToInt32();
            if (msg is not (0x100 or 0x101 or 0x104 or 0x105)) return CallNextHookEx(keyboardHook, code, wp, lp);
            var data = Marshal.PtrToStructure<KeyboardData>(lp);
            var key = (Keys)data.Key; var numpad = key == Keys.Enter && (data.Flags & 1) != 0;
            var index = (int)data.Key + (numpad ? 256 : 0);
            if (index is < 0 or >= 512) return CallNextHookEx(keyboardHook, code, wp, lp);
            var down = msg is 0x100 or 0x104; var repeated = pressed[index]; pressed[index] = down;
            var foreground = GetForegroundWindow();
            var priorOwner = consumed[index];
            if (!down)
            {
                consumed[index] = IntPtr.Zero;
                if (priorOwner != IntPtr.Zero) Wake();
                return priorOwner != IntPtr.Zero && priorOwner == foreground ? new IntPtr(1) : CallNextHookEx(keyboardHook, code, wp, lp);
            }
            if (repeated) return priorOwner != IntPtr.Zero && priorOwner == foreground ? new IntPtr(1) : CallNextHookEx(keyboardHook, code, wp, lp);
            // Modifiers are tracked on down/up, but never treated as complete shortcuts.
            if (IsModifier(key)) return CallNextHookEx(keyboardHook, code, wp, lp);
            var binding = MakeKeyboard(key, numpad);
            var active = Volatile.Read(ref lease);
            if (Owns(active, foreground) && active!.BindingCapture)
            {
                if (Capture(active, binding, key == Keys.Escape)) { consumed[index] = foreground; return new IntPtr(1); }
                return CallNextHookEx(keyboardHook, code, wp, lp);
            }
            var config = Volatile.Read(ref configuration);
            var entry = config.Bindings.FirstOrDefault(x => x.Binding == binding);
            if (entry is not null && Trigger(config, entry, active, foreground)
                && entry.Id == InputActionId.CancelCapture && Owns(active, foreground))
            { consumed[index] = foreground; log.Write("Consumed selection cancel; foreground lease matched"); return new IntPtr(1); }
            return CallNextHookEx(keyboardHook, code, wp, lp);
        }
        catch (Exception ex) { log.Error(ex.GetType().Name); return CallNextHookEx(keyboardHook, code, wp, lp); }
        finally { RecordSlow(started); }
    }
    private InputBinding MakeKeyboard(Keys key, bool numpad) => new(InputBindingKind.Keyboard, key,
        Ctrl: Down(Keys.LControlKey) || Down(Keys.RControlKey), Alt: Down(Keys.LMenu) || Down(Keys.RMenu),
        Shift: Down(Keys.LShiftKey) || Down(Keys.RShiftKey), Win: Down(Keys.LWin) || Down(Keys.RWin), Numpad: numpad);
    private bool Down(Keys key) => pressed[(int)key];
    private static bool IsModifier(Keys key) => key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
        or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    private IntPtr Mouse(int code, IntPtr wp, IntPtr lp)
    {
        if (code < 0 || Volatile.Read(ref stopping) != 0) return CallNextHookEx(mouseHook, code, wp, lp);
        var msg = wp.ToInt32();
        if (msg is not (0x201 or 0x202 or 0x204 or 0x205 or 0x207 or 0x208 or 0x20B or 0x20C or 0x20A))
            return CallNextHookEx(mouseHook, code, wp, lp); // Includes all movement.
        var started = Stopwatch.GetTimestamp();
        try
        {
            var data = Marshal.PtrToStructure<MouseData>(lp);
            var foreground = GetForegroundWindow(); var wheel = msg == 0x20A;
            var index = msg switch { 0x201 or 0x202 => 0, 0x204 or 0x205 => 1, 0x207 or 0x208 => 2, _ => (data.ButtonData >> 16) == 1 ? 3 : 4 };
            if (!wheel)
            {
                var down = msg is 0x201 or 0x204 or 0x207 or 0x20B;
                var repeated = mousePressed[index]; mousePressed[index] = down;
                var priorOwner = mouseConsumed[index];
                if (!down)
                {
                    mouseConsumed[index] = IntPtr.Zero; if (priorOwner != IntPtr.Zero) Wake();
                    return priorOwner != IntPtr.Zero && priorOwner == foreground ? new IntPtr(1) : CallNextHookEx(mouseHook, code, wp, lp);
                }
                if (repeated) return priorOwner != IntPtr.Zero && priorOwner == foreground ? new IntPtr(1) : CallNextHookEx(mouseHook, code, wp, lp);
            }
            var modifiers = MakeKeyboard(Keys.None, false);
            var button = index switch { 0 => InputMouseButton.Left, 1 => InputMouseButton.Right, 2 => InputMouseButton.Middle, 3 => InputMouseButton.XButton1, _ => InputMouseButton.XButton2 };
            var binding = wheel
                ? new InputBinding(InputBindingKind.MouseWheel, Wheel: (short)(data.ButtonData >> 16) > 0 ? InputWheelDirection.Up : InputWheelDirection.Down, Ctrl: modifiers.Ctrl, Alt: modifiers.Alt, Shift: modifiers.Shift, Win: modifiers.Win)
                : new InputBinding(InputBindingKind.MouseButton, MouseButton: button, Ctrl: modifiers.Ctrl, Alt: modifiers.Alt, Shift: modifiers.Shift, Win: modifiers.Win);
            var active = Volatile.Read(ref lease);
            if (!KeyboardInstalled) return CallNextHookEx(mouseHook, code, wp, lp);
            if (Owns(active, foreground) && active!.BindingCapture)
            {
                if (!wheel && index <= 1 && !active.Editor.Contains(data.Point))
                { Capture(active, null, true); return CallNextHookEx(mouseHook, code, wp, lp); }
                if (Capture(active, binding, false))
                { if (!wheel) mouseConsumed[index] = foreground; return new IntPtr(1); }
                return CallNextHookEx(mouseHook, code, wp, lp);
            }
            // UI-local clicks in this process belong to controls. Native foreground
            // ownership replaces cross-thread Bounds/ContainsFocus enumeration.
            GetWindowThreadProcessId(foreground, out var foregroundProcess);
            var config = Volatile.Read(ref configuration);
            var entry = config.Bindings.FirstOrDefault(x => x.Binding == binding);
            if (entry?.Id == InputActionId.CancelCapture && Owns(active, foreground))
            {
                if (Trigger(config, entry, active, foreground))
                { if (!wheel) mouseConsumed[index] = foreground; return new IntPtr(1); }
                return CallNextHookEx(mouseHook, code, wp, lp);
            }
            if (foregroundProcess == (uint)Environment.ProcessId && (!wheel && index <= 1 || active is not null))
                return CallNextHookEx(mouseHook, code, wp, lp);
            if (Volatile.Read(ref previewWindows).Contains(foreground) && GetWindowRect(foreground, out var bounds)
                && data.Point.X >= bounds.Left && data.Point.X < bounds.Right && data.Point.Y >= bounds.Top && data.Point.Y < bounds.Bottom)
                return CallNextHookEx(mouseHook, code, wp, lp);
            if (entry is not null) Trigger(config, entry, active, foreground);
            return CallNextHookEx(mouseHook, code, wp, lp); // Ordinary global actions observe and pass.
        }
        catch (Exception ex) { log.Error(ex.GetType().Name); return CallNextHookEx(mouseHook, code, wp, lp); }
        finally { RecordSlow(started); }
    }
    private void RecordSlow(long start)
    {
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(start, now).TotalMilliseconds < 20 || Stopwatch.GetElapsedTime(lastSlowEvent, now).TotalSeconds < 2) return;
        lastSlowEvent = now; log.Write($"Slow input callback ms={Stopwatch.GetElapsedTime(start, now).TotalMilliseconds:F1}; thread={Environment.CurrentManagedThreadId}");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        Interlocked.Exchange(ref lease, null); Wake();
        if (thread is not null && thread != Thread.CurrentThread && !thread.Join(750)) log.Write("Input shutdown wait expired; stopping callbacks pass through; thread retains cleanup ownership");
        if (thread is null) log.Complete();
        log.FinishBounded();
    }
    private delegate IntPtr HookProc(int code, IntPtr wp, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Key, ScanCode, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public Point Point; public uint ButtonData, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MessageData { public IntPtr Window; public uint Message; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRect bounds);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out MessageData message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MessageData message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MessageData message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MessageData message);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint id, uint message, IntPtr wp, IntPtr lp);
}

// Input-only diagnostics: bounded queue, no character stream, no IO in callbacks.
internal sealed class InputEventLog
{
    private readonly Channel<string> events = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly Task worker;
    private long dropped, lastError;
    private readonly long[] lastAction = new long[3];
    internal InputEventLog() => worker = Task.Run(async () =>
    {
        await foreach (var item in events.Reader.ReadAllAsync()) AppLog.Write("input-runtime", item);
        AppLog.Write("input-runtime", $"Diagnostic queue finished dropped={Interlocked.Read(ref dropped)}");
    });
    internal void Write(string message) { if (!events.Writer.TryWrite(message)) CountDropped(); }
    internal void CountDropped() => Interlocked.Increment(ref dropped);
    internal void Action(InputActionId action)
    {
        var now = Environment.TickCount64; var i = (int)action;
        if (now - lastAction[i] < 1000) return; lastAction[i] = now; Write("Action queued=" + action);
    }
    internal void Error(string type)
    { var now = Environment.TickCount64; if (now - lastError < 1000) return; lastError = now; Write("Callback failed open type=" + type); }
    internal void Complete() => events.Writer.TryComplete();
    internal void FinishBounded() { try { worker.Wait(250); } catch (AggregateException) { } }
}
