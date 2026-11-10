namespace ScreenshotTranslationUiTester;

internal sealed partial class AiToolsController
{
    private readonly object activityGate = new();
    private readonly HashSet<AiTaskRecord> dirtyNativeTasks = [];
    private readonly HashSet<AiTaskRecord> failedNativeSaves = [];
    private AiExecutionStore executionStore = null!;
    private AiAttachmentStore attachmentStore = null!;
    private System.Threading.Timer? nativeSaveTimer;
    private AiExecutionTurn? activeExecutionTurn;
    private readonly SemaphoreSlim attachmentImports = new(1, 1);
    private string conversationDataDirectory = "";
    private string conversationRecordDirectory = "";
    internal IReadOnlyList<AiAttachmentRecord> DraftAttachments => Current?.DraftAttachments ?? [];
    internal bool HistorySaveFailed { get { lock (activityGate) return failedNativeSaves.Count > 0 || Current?.ExecutionLoadFailed == true; } }
    internal string CurrentConversationDirectory => Current is null ? "" : Path.Combine(conversationDataDirectory, Current.Id);
    internal string CurrentConversationRecordPath => Current is null ? "" : Path.Combine(conversationRecordDirectory, Current.Id + ".json");
    internal IReadOnlyList<AiExecutionTurn> CurrentExecutionSnapshot()
    {
        lock (activityGate) return (Current?.ExecutionTurns ?? []).Select(t => new AiExecutionTurn
        {
            Id = t.Id, NativeTurnId = t.NativeTurnId, Backend = t.Backend, Status = t.Status, MessageStartIndex = t.MessageStartIndex,
            MessageEndIndex = t.MessageEndIndex, StartedAt = t.StartedAt, CompletedAt = t.CompletedAt, Truncated = t.Truncated,
            Attachments = t.Attachments.Select(a => a.Copy()).ToList(), Activities = t.Activities.Select(a => new AiExecutionActivity
            {
                ItemId = a.ItemId, Kind = a.Kind, Title = a.Title, Status = a.Status, Text = a.Text, Command = a.Command,
                WorkingDirectory = a.WorkingDirectory, SubagentId = a.SubagentId, ParentItemId = a.ParentItemId,
                StartedAt = a.StartedAt, UpdatedAt = a.UpdatedAt, Truncated = a.Truncated,
                Files = a.Files.Select(f => new AiActivityFile { Path = f.Path, Action = f.Action, Diff = f.Diff }).ToList(),
                Plan = a.Plan.Select(p => new AiActivityPlanStep { Text = p.Text, Status = p.Status }).ToList()
            }).ToList()
        }).ToList();
    }
    internal bool? ImageInputCapability
    {
        get
        {
            if (Current?.ModelDescriptor is not { } model || model.Id != CurrentModel || Current.ProfileId.Length == 0) return null;
            try { return Current.ModelConnectionIdentity == ModelConnectionIdentity(GetCurrentProfileSettings(), GetExecutionOptions(Current.ProfileId)) ? model.SupportsImageInput : null; }
            catch (AiToolsException) { return null; }
        }
    }
    private void InitializeNativeHistory(string root)
    { conversationRecordDirectory = Path.Combine(root, "tasks"); conversationDataDirectory = Path.Combine(root, "conversations"); executionStore = new(conversationDataDirectory); attachmentStore = new(conversationDataDirectory); }
    private void LoadNativeHistory(AiTaskRecord task)
    {
        if (!task.ExecutionLoaded)
        {
            try { task.ExecutionTurns = executionStore.Load(task.Id, out bool recovered); if (recovered) QueueNativeSave(task); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AiToolsException or ArgumentException)
            { task.ExecutionTurns = []; task.ExecutionLoadFailed = true; StatusCode = "storage_error"; }
            task.ExecutionLoaded = true;
        }
        foreach (var attachment in task.DraftAttachments.Concat(task.ExecutionTurns.SelectMany(x => x.Attachments)))
        {
            try { attachmentStore.Bind(task.Id, attachment); }
            catch (Exception ex) when (ex is AiToolsException or ArgumentException) { attachment.StoredPath = ""; }
        }
    }
    internal void SetDraftText(string text)
    {
        if (disposed || Current is null) return;
        if (text.Length > 32000) throw new AiToolsException("context_limit");
        Current.DraftText = SafeDiagnosticOutput.Redact(text); QueueNativeSave(Current);
    }
    internal async Task AddAttachmentsAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        await attachmentImports.WaitAsync(token);
        var imported = new List<AiAttachmentRecord>(); AiTaskRecord? importedFor = null; bool committed = false;
        try
        {
        if (Busy || disposed) throw new AiToolsException("busy");
        var task = Current ?? throw new AiToolsException("session_expired"); int version = generation;
        importedFor = task;
        string[] selected = paths.ToArray(); if (selected.Length == 0) return;
        if (selected.Length + task.DraftAttachments.Count > AiAttachmentStore.MaxCount) throw new AiToolsException("attachment_count");
        long size = task.DraftAttachments.Sum(x => x.Size);
        foreach (string path in selected)
        {
            if (!File.Exists(path)) throw new AiToolsException("attachment_missing");
            long length = new FileInfo(path).Length;
            if (length > AiAttachmentStore.MaxFileBytes || size + length > AiAttachmentStore.MaxTotalBytes) throw new AiToolsException("attachment_size");
            size += length;
        }
        foreach (string path in selected) imported.Add(await attachmentStore.ImportAsync(task.Id, path, token));
        if (disposed || version != generation || !ReferenceEquals(Current, task) || Busy) throw new OperationCanceledException(token);
        task.DraftAttachments.AddRange(imported);
        try { store.Save(task); committed = true; } catch { task.DraftAttachments.RemoveAll(a => imported.Contains(a)); throw; }
        Notify();
        }
        finally
        {
            if (!committed && importedFor is not null) foreach (var item in imported)
                try { attachmentStore.RemoveOwned(importedFor.Id, item); } catch { }
            attachmentImports.Release();
        }
    }
    internal void RemoveAttachment(string id)
    {
        if (Busy || disposed || Current is null) return;
        var removed = Current.DraftAttachments.Where(x => x.Id == id).ToArray();
        Current.DraftAttachments.RemoveAll(x => x.Id == id); store.Save(Current);
        foreach (var item in removed)
            if (!Current.ExecutionTurns.Any(t => t.Attachments.Any(a => a.Id == item.Id))) attachmentStore.RemoveOwned(Current.Id, item);
        Notify();
    }
    internal void SetAttachmentAsFileReference(string id, bool value)
    {
        if (Busy || disposed || Current is null) return;
        var attachment = Current.DraftAttachments.FirstOrDefault(x => x.Id == id) ?? throw new AiToolsException("attachment_missing");
        attachment.AsFileReference = value; store.Save(Current); Notify();
    }
    private async Task<List<AiAttachmentRecord>> SnapshotAttachmentsAsync(AiTaskRecord task, CancellationToken token)
    {
        var attachments = task.DraftAttachments.Select(x => x.Copy()).ToList();
        foreach (var attachment in attachments)
        {
            await attachmentStore.ValidateAsync(task.Id, attachment, token);
            if (attachment.Kind == "image" && !attachment.AsFileReference && ImageInputCapability != true)
                throw new AiToolsException(ImageInputCapability == false ? "attachment_image_unsupported" : "attachment_image_unknown");
        }
        return attachments;
    }
    private AiExecutionTurn BeginExecutionTurn(AiTaskRecord task, AiTurnUsage usage, List<AiAttachmentRecord> attachments)
    {
        LoadNativeHistory(task);
        if (task.ExecutionLoadFailed) throw new AiToolsException("storage_error");
        if (task.ExecutionTurns.Count >= AiExecutionStore.MaxTurns) throw new AiToolsException("context_limit");
        var turn = new AiExecutionTurn { Id = usage.Id, Backend = usage.Backend, StartedAt = usage.StartedAt,
            MessageStartIndex = task.Messages.Count, MessageEndIndex = task.Messages.Count, Attachments = attachments };
        lock (activityGate) { task.ExecutionTurns.Add(turn); activeExecutionTurn = turn; }
        task.DraftAttachments.Clear(); task.DraftText = "";
        return turn;
    }
    private void RecordNativeProgress(AiTaskRecord task, AiExecutionTurn turn, AiNativeProgress progress)
    {
        lock (activityGate)
        {
            if (turn.CompletedAt is not null || (progress.NativeTurnId.Length > 0 && turn.NativeTurnId.Length > 0 && progress.NativeTurnId != turn.NativeTurnId)) return;
            string id = AiExecutionStore.Limit(progress.ItemId, 512);
            if (id.Length == 0) id = "event:" + Guid.NewGuid().ToString("N");
            var activity = turn.Activities.FirstOrDefault(x => x.ItemId == id);
            if (activity is null)
            {
                if (turn.Activities.Count >= AiExecutionStore.MaxActivitiesPerTurn) { turn.Truncated = true; QueueNativeSave(task); return; }
                activity = new() { ItemId = id }; turn.Activities.Add(activity);
            }
            string Clean(string value, int limit) => AiExecutionStore.Limit(SafeDiagnosticOutput.Redact(value), limit);
            activity.Kind = Clean(progress.ActivityType.Length > 0 ? progress.ActivityType : progress.Kind, 32);
            if (progress.Title.Length > 0) activity.Title = Clean(progress.Title, 256);
            if (progress.Status.Length > 0 && (activity.Status is "running" or "pending" || progress.Status is not ("running" or "pending"))) activity.Status = Clean(progress.Status, 32);
            activity.Truncated |= progress.Text.Length > AiExecutionStore.MaxActivityText;
            activity.Text = AiExecutionStore.ActivityText(SafeDiagnosticOutput.Redact(progress.Text), AiExecutionStore.MaxActivityText);
            if (progress.Command.Length > 0) activity.Command = Clean(progress.Command, 8192);
            if (progress.WorkingDirectory.Length > 0) activity.WorkingDirectory = Clean(progress.WorkingDirectory, 2048);
            if (progress.SubagentId.Length > 0) activity.SubagentId = Clean(progress.SubagentId, 256);
            if (progress.ParentItemId.Length > 0) activity.ParentItemId = Clean(progress.ParentItemId, 512);
            if (progress.Files.Count > 0) activity.Files = progress.Files.Take(16).Select(x => new AiActivityFile { Path = Clean(x.Path, 2048), Action = Clean(x.Action, 40), Diff = Clean(x.Diff, 6000) }).ToList();
            if (progress.Plan.Count > 0) activity.Plan = progress.Plan.Take(48).Select(x => new AiActivityPlanStep { Text = Clean(x.Text, 1024), Status = Clean(x.Status, 32) }).ToList();
            activity.UpdatedAt = DateTimeOffset.UtcNow; QueueNativeSave(task);
        }
        NativeProgressChanged?.Invoke(progress with { Text = SafeDiagnosticOutput.Redact(progress.Text) });
    }
    private void FinishExecutionTurn(AiTaskRecord task, AiExecutionTurn turn, string status)
    {
        lock (activityGate)
        {
            if (turn.CompletedAt is null)
            {
                turn.Status = status; turn.CompletedAt = DateTimeOffset.UtcNow; turn.MessageEndIndex = task.Messages.Count;
                foreach (var activity in turn.Activities.Where(x => x.Status is "running" or "pending")) activity.Status = status == "completed" ? "unknown" : status;
            }
            if (ReferenceEquals(activeExecutionTurn, turn)) activeExecutionTurn = null;
            dirtyNativeTasks.Add(task);
        }
        FlushNativeHistory();
    }
    private void StopExecutionTurn()
    {
        if (Current is { } task && activeExecutionTurn is { } turn) FinishExecutionTurn(task, turn, "stopped");
        else FlushNativeHistory();
    }
    private void QueueNativeSave(AiTaskRecord task)
    {
        lock (activityGate)
        {
            dirtyNativeTasks.Add(task);
            nativeSaveTimer ??= new System.Threading.Timer(_ => OnNativeUi(() => { if (!disposed) { FlushNativeHistory(); Notify(); } }), null, 750, Timeout.Infinite);
        }
    }
    private void FlushNativeHistory()
    {
        lock (activityGate)
        {
            nativeSaveTimer?.Dispose(); nativeSaveTimer = null;
            foreach (var task in dirtyNativeTasks.ToArray())
            {
                try
                {
                    if (task.ExecutionLoaded && !task.ExecutionLoadFailed) executionStore.Save(task.Id, task.ExecutionTurns);
                    store.Save(task); dirtyNativeTasks.Remove(task); failedNativeSaves.Remove(task);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AiToolsException or ArgumentException)
                { failedNativeSaves.Add(task); StatusCode = "storage_error"; }
            }
        }
    }
}
