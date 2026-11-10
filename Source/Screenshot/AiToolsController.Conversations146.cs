namespace ScreenshotTranslationUiTester;

internal sealed partial class AiToolsController
{
    internal bool IsTaskRunning(string taskId) => Busy && Current?.Id == taskId;

    private AiTaskRecord Conversation146(string taskId)
    {
        if (disposed) throw new ObjectDisposedException(nameof(AiToolsController));
        return tasks.FirstOrDefault(x => x.Id == taskId && !x.IsDeleted)
            ?? throw new AiToolsException("session_expired");
    }

    internal void RenameTask(string taskId, string title)
    {
        var task = Conversation146(taskId);
        title = title.Trim();
        if (title.Length is 0 or > 200 || title.Any(char.IsControl))
            throw new AiToolsException("ai_task_title_invalid");
        string previous = task.Title; bool custom = task.TitleIsCustom;
        task.Title = title; task.TitleIsCustom = true;
        try { store.Save(task); }
        catch { task.Title = previous; task.TitleIsCustom = custom; throw; }
        Notify();
    }

    internal void SetTaskPinned(string taskId, bool pinned)
    {
        var task = Conversation146(taskId);
        if (task.IsPinned == pinned) return;
        bool previous = task.IsPinned; task.IsPinned = pinned;
        try { store.Save(task); }
        catch { task.IsPinned = previous; throw; }
        Notify();
    }

    internal void DeleteTask(string taskId)
    {
        var task = Conversation146(taskId);
        if (IsTaskRunning(taskId)) throw new AiToolsException("busy");
        // Removing a conversation from the list does not remove a project,
        // attached files, execution records, or either harness's native database.
        // Persist the marker first so a failed save leaves the live list intact.
        task.IsDeleted = true;
        try { store.Save(task); }
        catch { task.IsDeleted = false; throw; }
        if (ReferenceEquals(Current, task))
        {
            selectedHarness = task.Harness;
            ResetLocal(); Current = null; StatusCode = "ready";
        }
        tasks.Remove(task);
        Notify();
    }
}
