namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly Dictionary<string, (int Revision, FusionGameProcessState State, DateTime AtUtc)> _aiObservedProcesses = new(StringComparer.OrdinalIgnoreCase);
    private bool _aiProcessRefreshBusy;
    private DateTime _aiLastProcessRefreshUtc;

    // Refresh only games already known to this Fusion session. No attachment,
    // bridge traffic, translation polling, game launch or modification occurs.
    internal async Task AiRefreshGamesAsync()
    {
        if (_aiProcessRefreshBusy || IsDisposed || Disposing || DateTime.UtcNow - _aiLastProcessRefreshUtc < TimeSpan.FromSeconds(2)) return;
        _aiProcessRefreshBusy = true;
        var known = _gameSessions.Snapshot().Where(x => !x.Busy)
            .Select(x => (Session: x, Revision: x.Revision, Game: x.Game)).ToArray();
        try
        {
            var states = await Task.Run(() => known.Select(x =>
                (x.Session, x.Revision, x.Game, State: FusionAdapterInstaller.ProcessState(x.Game))).ToArray());
            if (IsDisposed || Disposing) return;
            foreach (var item in states)
                if (item.Session.Revision == item.Revision && !item.Session.Busy)
                    _aiObservedProcesses[RecentGameStore.Normalize(item.Game.ExePath)] = (item.Revision, item.State, DateTime.UtcNow);
        }
        catch (Exception) { /* Unknown state must never activate a tool or interrupt translation. */ }
        finally { _aiLastProcessRefreshUtc = DateTime.UtcNow; _aiProcessRefreshBusy = false; }
    }
}
