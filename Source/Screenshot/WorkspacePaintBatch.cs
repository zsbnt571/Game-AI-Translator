namespace ScreenshotTranslationUiTester;

// Only a visible root is frozen, for one synchronous mutation. Hidden controls
// must not receive WM_SETREDRAW(true), which could change their native visibility.
internal sealed class WorkspacePaintBatch : IDisposable
{
    private readonly Control? root;
    internal WorkspacePaintBatch(Control control)
    {
        // WPF compositor descendants own their presentation surfaces. Freezing
        // their HWND ancestor can retain black/stale surfaces after navigation.
        if(WpfWorkspace.Enabled)return;
        if(control.IsHandleCreated&&control.Visible&&!control.IsDisposed)
        {root=control;WorkspaceRepaint.SetRedraw(control,false);}
    }
    public void Dispose()
    {
        if(root is null||root.IsDisposed)return;
        WorkspaceRepaint.SetRedraw(root,true);root.Invalidate(true);
    }
}
