namespace ScreenshotTranslationUiTester;

// Navigation history is independent of windows and input devices. A host only
// supplies the current location and an operation that can accept or reject it.
internal sealed class PageHistory<T> where T:notnull
{
    private readonly List<T> pages=[];
    private int index=-1;
    internal void Visit(T page)
    {
        if(index>=0&&EqualityComparer<T>.Default.Equals(pages[index],page))return;
        if(index+1<pages.Count)pages.RemoveRange(index+1,pages.Count-index-1);
        pages.Add(page);index++;
        if(pages.Count>100){pages.RemoveAt(0);index--;}
    }
    internal bool TryPeek(int direction,out T page)
    {int next=index+direction;if(direction is -1 or 1&&next>=0&&next<pages.Count){page=pages[next];return true;}page=default!;return false;}
    internal void Moved(int direction){if(TryPeek(direction,out _))index+=direction;}
}

internal sealed class PageNavigationController<T> : IDisposable where T : notnull
{
    private readonly PageHistory<T> history = new();
    private readonly Func<T> current;
    private readonly Func<T, Task<bool>> restore;
    private readonly Action<Action> post;
    private readonly Action<Exception> reportError;
    private bool pending, moving, disposed;

    internal PageNavigationController(Func<T> current, Func<T, Task<bool>> restore,
        Action<Action> post, Action<Exception> reportError)
    {
        this.current = current; this.restore = restore;
        this.post = post; this.reportError = reportError;
    }
    internal bool IsMoving => moving;
    internal void Record(bool deferred = true)
    {
        if (disposed || moving || pending) return;
        if (!deferred) { history.Visit(current()); return; }
        pending = true;
        try
        {
            post(() =>
            {
                pending = false;
                if (!disposed && !moving) history.Visit(current());
            });
        }
        catch { pending = false; throw; }
    }
    internal async Task MoveAsync(int direction)
    {
        if (disposed || moving || direction is not (-1 or 1)) return;
        history.Visit(current());
        if (!history.TryPeek(direction, out var target)) return;
        moving = true;
        try { if (await restore(target) && !disposed) history.Moved(direction); }
        catch (Exception error) { if (!disposed) reportError(error); }
        finally { moving = false; }
    }
    public void Dispose() { disposed = true; }
}
