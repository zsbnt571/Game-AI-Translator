namespace ScreenshotTranslationUiTester;

// Owned by RecentGamesDialog, reused while the dialog lives. Closed is not Dispose:
// WinForms still reparents the HWND after raising Closed. A reentrant owner shutdown
// requests disposal; the outermost visibility operation performs it after unwinding.
internal sealed class OwnedRetentionPopup : ToolStripDropDown
{
    private int visibilityDepth;
    private bool disposeRequested, disposingNow;
    protected override void SetVisibleCore(bool visible)
    {
        if (IsDisposed || (visible && disposeRequested)) return;
        visibilityDepth++;
        try { base.SetVisibleCore(visible); }
        finally
        {
            visibilityDepth--;
            if (visibilityDepth == 0 && disposeRequested && !disposingNow) Dispose();
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed || disposingNow) return;
        disposeRequested = true;
        if (visibilityDepth != 0) return;
        disposingNow = true;
        try { base.Dispose(disposing); }
        finally { disposingNow = false; }
    }
}
