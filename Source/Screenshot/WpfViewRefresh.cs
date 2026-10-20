using System.Windows.Threading;

namespace ScreenshotTranslationUiTester;

// One pending render for a burst of model changes. Layout and drawing remain
// WPF's responsibility; callers never synchronously rebuild a view per event.
internal sealed class WpfViewRefresh : IDisposable
{
    private readonly Dispatcher dispatcher;
    private Action? refresh;
    private DispatcherOperation? pending;
    private bool dirty, suspended;
    internal WpfViewRefresh(Dispatcher dispatcher, Action refresh)
    { this.dispatcher = dispatcher; this.refresh = refresh; }
    internal bool Suspended
    {
        get => suspended;
        set
        {
            if (suspended == value) return;
            suspended = value;
            if (value) { pending?.Abort(); pending = null; }
            else if (dirty) Request();
        }
    }
    internal void Request()
    {
        if (refresh is null || dispatcher.HasShutdownStarted) return;
        dirty = true;
        if (pending is null && !suspended)
            pending = dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Flush));
    }
    internal void Flush()
    {
        pending?.Abort(); pending = null;
        if (refresh is null || suspended || !dirty) return;
        dirty = false; refresh();
    }
    public void Dispose()
    { pending?.Abort(); pending = null; refresh = null; dirty = false; }
}
