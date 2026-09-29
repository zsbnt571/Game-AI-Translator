using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

internal sealed class TemporaryWindowCaptureExclusion : IDisposable
{
    private sealed record WindowState(IntPtr Handle, uint OriginalAffinity, string Name);

    private readonly List<WindowState> _changedWindows = [];
    private bool _restored;

    private TemporaryWindowCaptureExclusion()
    {
    }

    public bool RestoreSucceeded { get; private set; } = true;
    public string RestoreError { get; private set; } = string.Empty;

    public static bool TryBegin(
        IEnumerable<Form> windows,
        out TemporaryWindowCaptureExclusion? scope,
        out string error)
    {
        scope = new TemporaryWindowCaptureExclusion();
        error = string.Empty;
        var candidates = windows.Distinct().ToList();
        Log($"TemporaryWDA begin window count={candidates.Count}");

        foreach (var form in candidates)
        {
            if (form.IsDisposed || !form.IsHandleCreated)
            {
                error = $"窗口句柄不可用：{form.GetType().Name}";
                Log($"TemporaryWDA failed form={form.GetType().Name} reason=invalid-handle");
                scope.Restore();
                scope = null;
                return false;
            }

            var handle = form.Handle;
            if (!NativeMethods.GetWindowDisplayAffinity(handle, out var oldAffinity))
            {
                var win32 = Marshal.GetLastWin32Error();
                error = $"读取窗口捕获状态失败：HWND={handle}，Win32={win32}";
                Log($"HWND {handle} get affinity failed win32={win32}");
                scope.Restore();
                scope = null;
                return false;
            }

            Log($"HWND {handle} oldAffinity=0x{oldAffinity:X}");
            if (!NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture))
            {
                var win32 = Marshal.GetLastWin32Error();
                error = $"临时排除窗口失败：HWND={handle}，Win32={win32}";
                Log($"HWND {handle} set EXCLUDEFROMCAPTURE failed win32={win32}");
                scope.Restore();
                scope = null;
                return false;
            }

            scope._changedWindows.Add(new WindowState(handle, oldAffinity, form.GetType().Name));
            Log($"HWND {handle} set EXCLUDEFROMCAPTURE success");
        }

        return true;
    }

    public void Restore()
    {
        if (_restored) return;
        _restored = true;
        Log("RestoreWDA begin");

        for (var i = _changedWindows.Count - 1; i >= 0; i--)
        {
            var state = _changedWindows[i];
            if (NativeMethods.SetWindowDisplayAffinity(state.Handle, state.OriginalAffinity))
            {
                Log($"HWND {state.Handle} restore 0x{state.OriginalAffinity:X} success");
                continue;
            }

            var win32 = Marshal.GetLastWin32Error();
            RestoreSucceeded = false;
            var item = $"HWND={state.Handle} ({state.Name}) Win32={win32}";
            RestoreError = string.IsNullOrEmpty(RestoreError) ? item : $"{RestoreError}; {item}";
            Log($"HWND {state.Handle} restore 0x{state.OriginalAffinity:X} failed win32={win32}");
        }

        _changedWindows.Clear();
        Log($"RestoreWDA end success={RestoreSucceeded}");
    }

    public void Dispose() => Restore();

    private static void Log(string message)
    {
        CaptureFlashDiagnosticLog.Write(message);
        TranslationService.DiagnosticLog(message);
    }
}

// A self-capture is the inverse of the normal capture policy: the selected
// application window must be capturable for exactly the duration of the copy.
// Preserve and restore the caller's affinity rather than assuming WDA_NONE.
internal sealed class TemporaryWindowCaptureAllowance : IDisposable
{
    private readonly IntPtr _handle;
    private readonly uint _originalAffinity;
    private bool _restored;

    private TemporaryWindowCaptureAllowance(IntPtr handle, uint originalAffinity)
    {
        _handle = handle;
        _originalAffinity = originalAffinity;
    }

    public bool RestoreSucceeded { get; private set; } = true;
    public string RestoreError { get; private set; } = string.Empty;

    public static bool TryBegin(Form window, out TemporaryWindowCaptureAllowance? scope, out string error)
    {
        scope = null;
        error = string.Empty;
        if (window.IsDisposed || !window.IsHandleCreated)
        {
            error = "Self-capture window handle is unavailable.";
            return false;
        }

        var handle = window.Handle;
        if (!NativeMethods.GetWindowDisplayAffinity(handle, out var oldAffinity))
        {
            var win32 = Marshal.GetLastWin32Error();
            error = $"Cannot read self-capture affinity: HWND={handle}, Win32={win32}.";
            return false;
        }

        if (oldAffinity != NativeMethods.WdaNone &&
            !NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaNone))
        {
            var win32 = Marshal.GetLastWin32Error();
            error = $"Cannot temporarily allow self-capture: HWND={handle}, Win32={win32}.";
            return false;
        }

        scope = new TemporaryWindowCaptureAllowance(handle, oldAffinity);
        TranslationService.DiagnosticLog(
            $"self-capture WDA allow begin hwnd={handle} original=0x{oldAffinity:X}");
        return true;
    }

    public void Restore()
    {
        if (_restored) return;
        _restored = true;
        if (_originalAffinity == NativeMethods.WdaNone ||
            NativeMethods.SetWindowDisplayAffinity(_handle, _originalAffinity))
        {
            TranslationService.DiagnosticLog(
                $"self-capture WDA restore hwnd={_handle} affinity=0x{_originalAffinity:X} success");
            return;
        }

        var win32 = Marshal.GetLastWin32Error();
        RestoreSucceeded = false;
        RestoreError = $"HWND={_handle}, affinity=0x{_originalAffinity:X}, Win32={win32}";
        TranslationService.DiagnosticLog($"self-capture WDA restore failed {RestoreError}");
    }

    public void Dispose() => Restore();
}
public static class CaptureForegroundPolicy{public static bool ShouldReactivate(bool wasForegroundBeforeCapture)=>wasForegroundBeforeCapture;}

public sealed class TemporaryCaptureVisibility : IDisposable
{
    private readonly List<TemporaryWindowCaptureExclusion> _excluded = [];
    private readonly List<TemporaryWindowCaptureAllowance> _allowed = [];
    private bool _restored;
    public bool RestoreSucceeded { get; private set; } = true;
    public string RestoreError { get; private set; } = "";

    public static bool TryBegin(MainForm main, IReadOnlyList<PreviewForm> previews,
        bool hideMain, bool hidePreviews, out TemporaryCaptureVisibility? scope, out string error)
    {
        scope = new TemporaryCaptureVisibility();
        try
        {
            TranslationService.DiagnosticLog($"capture visibility begin hideMain={hideMain} hidePreviews={hidePreviews} previewCount={previews.Count}");
            var excludedWindows = new List<Form>();
            if (hideMain && main.Visible) excludedWindows.Add(main);
            else if (main.Visible)
            {
                if (!TemporaryWindowCaptureAllowance.TryBegin(main, out var mainAllowance, out error))
                { scope.Dispose(); scope = null; return false; }
                if (mainAllowance is not null) scope._allowed.Add(mainAllowance);
            }

            foreach (var preview in previews.Where(x => x.Visible && !x.IsDisposed))
            {
                if (hidePreviews) { excludedWindows.Add(preview); continue; }
                if (!TemporaryWindowCaptureAllowance.TryBegin(preview, out var allowance, out error))
                { scope.Dispose(); scope = null; return false; }
                if (allowance is not null) scope._allowed.Add(allowance);
            }
            if (excludedWindows.Count > 0)
            {
                if (!TemporaryWindowCaptureExclusion.TryBegin(excludedWindows,
                        out var exclusion, out error))
                { scope.Dispose(); scope = null; return false; }
                if (exclusion is not null) scope._excluded.Add(exclusion);
            }
            NativeMethods.DwmFlush();
            TranslationService.DiagnosticLog($"capture visibility prepared before capture hiddenWindows=0 excludedWindows={excludedWindows.Count}");
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            scope?.Dispose(); scope = null; return false;
        }
    }

    public void Restore()
    {
        if (_restored) return;
        _restored = true;
        foreach (var allowance in Enumerable.Reverse(_allowed))
        {
            allowance.Dispose();
            if (!allowance.RestoreSucceeded)
            {
                RestoreSucceeded = false;
                RestoreError = allowance.RestoreError;
            }
        }
        foreach (var exclusion in Enumerable.Reverse(_excluded))
        {
            exclusion.Dispose();
            if (!exclusion.RestoreSucceeded)
            {
                RestoreSucceeded = false;
                RestoreError = string.IsNullOrEmpty(RestoreError)
                    ? exclusion.RestoreError
                    : $"{RestoreError}; {exclusion.RestoreError}";
            }
        }
        NativeMethods.DwmFlush();
        TranslationService.DiagnosticLog($"capture visibility restored success={RestoreSucceeded}");
    }

    public void Dispose() => Restore();
}
