using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

// Only the screenshot page opts in. No process-wide theme setting is changed.
internal static class ScreenshotNativeScrollbarTheme
{
    private sealed class AppliedTheme
    {
        internal IntPtr Handle;
        internal bool Dark;
        internal bool Applying;
    }
    private static readonly ConditionalWeakTable<Control, AppliedTheme> Applied = new();

    internal static void Apply(Control control, bool force = false)
    {
        if (control.IsDisposed || !control.IsHandleCreated) return;
        bool dark = ScreenshotRebuildStyle.Palette.Main.GetBrightness() < .5f;
        var state = Applied.GetValue(control, static _ => new AppliedTheme());
        if (state.Applying) return;
        if (!force && state.Handle == control.Handle && state.Dark == dark) return;
        state.Applying = true;
        try
        {
            var handle = control.Handle;
            // SetWindowTheme sends WM_THEMECHANGED synchronously. Its repaint
            // must not recursively reapply the same association.
            if (NativeMethods.SetWindowTheme(handle, dark ? "DarkMode_Explorer" : "Explorer", null) < 0) return;
            state.Handle = handle; state.Dark = dark;
            // Scrollbars occupy the non-client area; repainting only the content
            // does not publish a changed native scrollbar theme.
            // Queue the repaint. Forcing it here would enter native text/image
            // painting while an appearance/layout transaction is still active.
            RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0400);
        }
        finally { state.Applying = false; }
    }

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr window, IntPtr update, IntPtr region, uint flags);
}
