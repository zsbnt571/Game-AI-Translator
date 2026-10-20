using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenshotTranslationUiTester;

// Recover only this HWND after WPF explicitly reports repeated failed presents.
// Idle/slow rendering is not a failure signal. No process or system setting changes.
internal sealed class WpfPresentationRecovery : IDisposable
{
    private static readonly uint FailureMessage = Native.RegisterWindowMessage("NeedsRePresentOnWake");
    private readonly Window window;
    private readonly WpfPresentFailureSequence sequence = new();
    private readonly DispatcherTimer verification;
    private HwndSource? source;
    private IntPtr hwnd;
    private bool disposed, pending, moving;
    private int generation;

    internal bool SoftwareRequested { get; private set; }
    internal bool InheritedOwnerPreference { get; private set; }
    internal int FailureNotifications { get; private set; }
    internal int RepaintRequests { get; private set; }
    internal bool IsVerificationActive => verification.IsEnabled || pending;

    internal static WpfPresentationRecovery Attach(Window window) => new(window);

    private WpfPresentationRecovery(Window window)
    {
        this.window = window;
        verification = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(300) };
        verification.Tick += Verify;
        window.SourceInitialized += SourceInitialized;
        window.Activated += EligibilityChanged;
        window.Deactivated += EligibilityChanged;
        window.StateChanged += EligibilityChanged;
        window.IsVisibleChanged += VisibilityChanged;
        window.Closing += Closing;
        window.Closed += Closed;
        if (PresentationSource.FromVisual(window) is HwndSource existing) Install(existing);
    }

    private void SourceInitialized(object? sender, EventArgs args)
    {
        if (PresentationSource.FromVisual(window) is HwndSource current) Install(current, inheritOwner: true);
    }

    private void Install(HwndSource current, bool inheritOwner = false)
    {
        if (disposed || current.IsDisposed || source is not null || FailureMessage == 0 ||
            RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly ||
            current.CompositionTarget?.RenderMode == RenderMode.SoftwareOnly) return;
        source = current;
        hwnd = current.Handle;
        // An ordinary owned dialog should use its owner's already selected
        // compatibility path from its first paint, rather than fail again.
        // Only a new WPF-owned HWND inherits; never infer an owner from a PID,
        // an interop handle, or a name, and never follow later preference changes.
        if (inheritOwner && TryInheritOwnerPreference(current)) return;
        source.AddHook(Observe);
    }

    private bool TryInheritOwnerPreference(HwndSource current)
    {
        if (window.Owner is not { } owner || ReferenceEquals(owner, window) ||
            PresentationSource.FromVisual(owner) is not HwndSource ownerSource || ownerSource.IsDisposed ||
            ownerSource.CompositionTarget is not { } ownerTarget || current.CompositionTarget is not { } target)
            return false;
        try
        {
            if (ownerTarget.RenderMode != RenderMode.SoftwareOnly) return false;
            target.RenderMode = RenderMode.SoftwareOnly;
            SoftwareRequested = InheritedOwnerPreference = true;
            window.InvalidateVisual();
            AppLog.Write("wpf-render", $"event=window-software-inherited reason=owner-preference hwnd=0x{hwnd.ToInt64():X} owner=0x{ownerSource.Handle.ToInt64():X} mode=SoftwareOnly runtime={Environment.Version}");
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            AppLog.Write("wpf-render", "event=window-preference-inheritance-failed", error);
            return SoftwareRequested;
        }
    }

    private IntPtr Observe(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Never consume a message: WPF must keep its own wake/display recovery.
        if (disposed || handle != hwnd) return IntPtr.Zero;
        if (message == 0x0231) { moving = true; Cancel(); } // WM_ENTERSIZEMOVE
        else if (message == 0x0232) { moving = false; BeginTransition(); }
        else if (message is 0x02E0 or 0x007E or 0x0218 or 0x02B1) BeginTransition(); // DPI/display/power/session
        if ((uint)message != FailureMessage || SoftwareRequested || pending) return IntPtr.Zero;
        FailureNotifications++;
        bool eligible = Eligible();
        if (!eligible) { Cancel(); return IntPtr.Zero; }
        if (sequence.Failure(Now)) RequestRecovery();
        else verification.Start();
        return IntPtr.Zero;
    }

    private void Verify(object? sender, EventArgs args)
    {
        if (!Eligible()) { Cancel(); return; }
        switch (sequence.Tick(Now))
        {
            case WpfPresentFailureAction.Wait:
                break; // Only a real failure can start this short transition grace.
            case WpfPresentFailureAction.Repaint:
                // A bounded retry is needed for a static white window: WPF itself
                // deliberately stops repainting after its first failed retry.
                RepaintRequests++;
                Native.InvalidateRect(hwnd, IntPtr.Zero, false);
                break;
            case WpfPresentFailureAction.Recover:
                RequestRecovery();
                break;
            default:
                Cancel();
                break;
        }
    }

    private void RequestRecovery()
    {
        if (pending || SoftwareRequested || disposed) return;
        verification.Stop();
        pending = true;
        int requestedGeneration = generation;
        int failures = sequence.Failures;
        double span = sequence.SpanMilliseconds;
        HwndSource? expectedSource = source;
        HwndTarget? expectedTarget = source?.CompositionTarget;
        IntPtr expectedHwnd = hwnd;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (disposed || requestedGeneration != generation || !pending) return;
            pending = false;
            if (source is null || source.IsDisposed || source.Handle != expectedHwnd || hwnd != expectedHwnd ||
                !ReferenceEquals(source, expectedSource) || !ReferenceEquals(source.CompositionTarget, expectedTarget) ||
                !ReferenceEquals(PresentationSource.FromVisual(window), expectedSource) ||
                !Eligible() || !sequence.ReadyAt(Now)) { Cancel(); return; }
            try
            {
                // Public per-window preference; keep this HWND on the recovered
                // path until it closes instead of repeatedly switching backends.
                source!.CompositionTarget!.RenderMode = RenderMode.SoftwareOnly;
                SoftwareRequested = true;
                Cancel();
                // RenderMode queues an ordinary-channel command. A Win32 paint
                // alone uses WPF's out-of-band channel; invalidate the visual too
                // so an otherwise static window commits the preference change.
                window.InvalidateVisual();
                Native.InvalidateRect(hwnd, IntPtr.Zero, false);
                Native.GetClientRect(hwnd, out var rect);
                AppLog.Write("wpf-render", $"event=window-software-requested reason=repeated-present-occluded hwnd=0x{hwnd.ToInt64():X} failures={failures} spanMs={span:F0} retries={RepaintRequests} pixels={rect.Right}x{rect.Bottom} dpi={Native.GetDpiForWindow(hwnd)} mode=SoftwareOnly runtime={Environment.Version}");
            }
            catch (Exception error) when (error is InvalidOperationException or COMException)
            {
                Cancel();
                AppLog.Write("wpf-render", "event=window-recovery-failed reason=repeated-present-occluded", error);
                Dispose(); // Do not create an exception/retry loop.
            }
        }));
    }

    private bool Eligible()
    {
        if (disposed || SoftwareRequested || moving || source is null || source.IsDisposed ||
            source.CompositionTarget is null || source.CompositionTarget.RenderMode == RenderMode.SoftwareOnly ||
            RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly || !window.IsVisible ||
            window.WindowState == WindowState.Minimized || hwnd == IntPtr.Zero ||
            Native.GetForegroundWindow() != hwnd || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) ||
            !Native.GetClientRect(hwnd, out var rect) || rect.Right <= 0 || rect.Bottom <= 0 ||
            Native.MonitorFromWindow(hwnd, 0) == IntPtr.Zero) return false;
        if (Native.DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) != 0 || cloaked != 0) return false;
        return Native.IsCurrentInputDesktop() && Native.IsActiveSession();
    }

    private static double Now => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);
    private void EligibilityChanged(object? sender, EventArgs args) => BeginTransition();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => BeginTransition();
    private void Closing(object? sender, System.ComponentModel.CancelEventArgs args) => Cancel();
    private void Closed(object? sender, EventArgs args) => Dispose();
    private void BeginTransition()
    {
        Cancel();
        sequence.BeginGrace(Now + 1000);
        // No timer here. Healthy activation/restoration stays completely idle.
    }
    private void Cancel()
    {
        generation++;
        pending = false;
        verification.Stop();
        sequence.Reset();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Cancel();
        if (source is { IsDisposed: false }) source.RemoveHook(Observe);
        source = null;
        verification.Tick -= Verify;
        window.SourceInitialized -= SourceInitialized;
        window.Activated -= EligibilityChanged;
        window.Deactivated -= EligibilityChanged;
        window.StateChanged -= EligibilityChanged;
        window.IsVisibleChanged -= VisibilityChanged;
        window.Closing -= Closing;
        window.Closed -= Closed;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(IntPtr handle, out Rect rect);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InvalidateRect(IntPtr handle, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
        [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int bytes, out int needed);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int info, out IntPtr value, out int bytes);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr value);

        internal static bool IsCurrentInputDesktop()
        {
            var input = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS only.
            if (input == IntPtr.Zero) return false;
            try
            {
                var inputName = new StringBuilder(256);
                var ownName = new StringBuilder(256);
                return GetUserObjectInformation(input, 2, inputName, 512, out _) &&
                    GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, ownName, 512, out _) &&
                    inputName.Length > 0 && string.Equals(inputName.ToString(), ownName.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseDesktop(input); }
        }

        internal static bool IsActiveSession()
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, -1, 8, out var value, out int bytes)) return false;
            try { return bytes >= sizeof(int) && Marshal.ReadInt32(value) == 0; } // WTSActive
            finally { WTSFreeMemory(value); }
        }
    }
}

internal enum WpfPresentFailureAction { Stop, Wait, Repaint, Recover }

// Deterministic timing policy; only the HWND observer may supply failure signals.
internal sealed class WpfPresentFailureSequence
{
    private double first, last, notBefore;
    private bool waitingForGrace;
    private int failuresAtLastRetry;
    internal int Failures { get; private set; }
    internal int Retries { get; private set; }
    internal double SpanMilliseconds => Failures == 0 ? 0 : last - first;
    internal bool Ready => Failures >= 4 && SpanMilliseconds >= 750;
    internal bool ReadyAt(double now) => Ready && now >= last && now - first <= 2200 && now - last <= 600;

    internal bool Failure(double now)
    {
        if (now < notBefore || waitingForGrace)
        {
            waitingForGrace = true;
            return false; // Pre-transition evidence never contributes to the threshold.
        }
        if (Failures == 0) first = now;
        else if (now - first > 2200) { Reset(); first = now; }
        last = now;
        Failures++;
        return Ready;
    }

    internal WpfPresentFailureAction Tick(double now)
    {
        if (waitingForGrace)
        {
            if (now - notBefore > 2200) { Reset(); return WpfPresentFailureAction.Stop; }
            if (now < notBefore) return WpfPresentFailureAction.Wait;
            waitingForGrace = false;
            Retries = 1;
            failuresAtLastRetry = 0;
            return WpfPresentFailureAction.Repaint;
        }
        if (Failures == 0 || now - first > 2200 || Retries >= 6 ||
            (Retries > 0 && Failures == failuresAtLastRetry))
        { Reset(); return WpfPresentFailureAction.Stop; }
        if (ReadyAt(now)) return WpfPresentFailureAction.Recover;
        failuresAtLastRetry = Failures;
        Retries++;
        return WpfPresentFailureAction.Repaint;
    }

    internal void BeginGrace(double until) { Reset(); notBefore = until; }
    internal void Reset() { first = last = notBefore = 0; waitingForGrace = false; Failures = Retries = failuresAtLastRetry = 0; }
}
