using System.Windows.Threading;

namespace ScreenshotTranslationUiTester;

// Native pages schedule work on their own dispatcher; the old message-loop
// timer is constructed only for the explicitly selected legacy application.
internal sealed class DesktopUiTimer : IDisposable
{
    private readonly DispatcherTimer? native;
    private readonly System.Windows.Forms.Timer? legacy;
    private bool disposed;
    internal DesktopUiTimer(bool? nativeHost = null)
    {
        if (nativeHost ?? WpfWorkspace.Enabled)
        {
            native = new(DispatcherPriority.Background, Dispatcher.CurrentDispatcher);
            native.Tick += OnTick;
        }
        else { legacy = new(); legacy.Tick += OnTick; }
    }
    internal int Interval
    {
        get => native is not null ? (int)native.Interval.TotalMilliseconds : legacy!.Interval;
        set { if (value < 1) throw new ArgumentOutOfRangeException(nameof(value)); if (native is not null) native.Interval = TimeSpan.FromMilliseconds(value); else legacy!.Interval = value; }
    }
    internal bool Enabled { get => native?.IsEnabled ?? legacy!.Enabled; set { if (value) Start(); else Stop(); } }
    internal event EventHandler? Tick;
    private void OnTick(object? sender, EventArgs e) { if (!disposed) Tick?.Invoke(this, e); }
    internal void Start() { ObjectDisposedException.ThrowIf(disposed, this); if (native is not null) native.Start(); else legacy!.Start(); }
    internal void Stop() { if (native is not null) native.Stop(); else legacy?.Stop(); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop();
        if (native is not null) native.Tick -= OnTick;
        if (legacy is not null) { legacy.Tick -= OnTick; legacy.Dispose(); }
        Tick = null;
    }
}
