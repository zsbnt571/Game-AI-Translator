using System.Security.Cryptography;
using System.Text;

namespace ScreenshotTranslationUiTester;

internal sealed partial class AiToolsController
{
    private string selectedHarness = "codex";
    private readonly SynchronizationContext? nativeUi = SynchronizationContext.Current;
    private readonly SemaphoreSlim nativeInteractions = new(1, 1);
    private TaskCompletionSource<string>? nativeApprovalReply;
    private TaskCompletionSource<IReadOnlyDictionary<string, string[]>>? nativeQuestionReply;
    internal event Action<AiNativeProgress>? NativeProgressChanged;
    internal AiNativeApproval? PendingNativeApproval { get; private set; }
    internal AiNativeQuestion? PendingNativeQuestion { get; private set; }
    internal string SelectedHarness => Current?.Harness ?? selectedHarness;
    internal string CurrentModel => Current is { Model.Length: > 0 } ? Current.Model
        : Current is { ProfileId.Length: > 0 } ? GetProfileSettings(Current.ProfileId).Model : "";
    internal string PermissionMode => Current?.PermissionMode ?? "ask";
    internal string NativeAgent => Current?.NativeAgent ?? "build";
    internal string CurrentWorkspace => Current is null ? "" : Current.Workspace.Length > 0 ? Current.Workspace
        : Path.Combine(harnessDataDirectory, "projects", Current.Id);

    private static string ModelConnectionIdentity(ApiSettings settings, AiAgentOptions options) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            options.Backend, options.AuthMode, options.ProviderId, options.HarnessPath, settings.ApiUrl, settings.ApiKey))));
    private void SynchronizeEditedProfile(string id, string previousIdentity)
    {
        balances.Remove(id);
        if (Current?.ProfileId == id) Stop();
        var options = GetProfileOptions(id);
        bool changed = previousIdentity != ModelConnectionIdentity(GetProfileSettings(id), options);
        foreach (var task in tasks.Where(x => x.ProfileId == id))
        {
            // Editing a shared API connection never redirects conversations to its
            // saved default assistant. Account mismatches remain explicit too.
            if (changed) { task.ModelDescriptor = null; task.ModelConnectionIdentity = ""; task.ReasoningEffort = ""; }
            store.Save(task);
        }
    }

    internal void SelectHarness(string backend)
    {
        if (Busy || disposed) return;
        if (backend is not ("codex" or "opencode")) throw new AiToolsException("harness_backend");
        selectedHarness = backend;
        if (Current is not null && Current.Harness != backend)
        {
            string model = CurrentModel;
            Stop(); Current.Harness = backend; Current.Model = model;
            Current.ModelDescriptor = null; Current.ModelConnectionIdentity = ""; Current.ReasoningEffort = "";
            if (backend == "opencode" && Current.PermissionMode == "auto") Current.PermissionMode = "ask";
            Save();
        }
        StatusCode = Current?.ProfileId is { Length: > 0 } id && ProfileHarnessError(id).Length > 0 ? ProfileHarnessError(id) : "ready";
        Notify();
    }
    internal ApiSettings GetCurrentProfileSettings()
    {
        if (Current is null || !Profiles.Any(x => x.Id == Current.ProfileId)) throw new AiToolsException("select_profile");
        var settings = GetProfileSettings(Current.ProfileId);
        if (Current.Model.Length > 0) settings.Model = Current.Model;
        return settings;
    }
    internal AiAgentOptions GetCurrentProfileOptions()
    {
        if (Current is null || !Profiles.Any(x => x.Id == Current.ProfileId)) throw new AiToolsException("select_profile");
        var settings = GetCurrentProfileSettings(); var options = GetExecutionOptions(Current.ProfileId);
        RequireHarness(options);
        if (Current.ModelDescriptor is { } descriptor && descriptor.Id == settings.Model &&
            Current.ModelConnectionIdentity == ModelConnectionIdentity(settings, options))
        {
            options.SupportedEfforts = descriptor.SupportedEfforts.ToList(); options.EffortModelId = descriptor.Id;
            options.EffortSource = descriptor.EffortSource; options.EffortEndpoint = settings.ApiUrl;
            options.EffortBackend = options.Backend;
            options.EffortDiscoveredAt = descriptor.EffortDiscoveredAt;
            options.Price = descriptor.Price;
        }
        if (Current.Model.Length > 0 || Current.ReasoningEffort.Length > 0) options.ReasoningEffort = Current.ReasoningEffort;
        return options;
    }
    internal void SetModel(string model, AiModelDescriptor? descriptor = null)
    {
        if (Busy || disposed || Current is null) return;
        if (!AiAgentOptions.Clean(model, 256) || model.Length == 0 || (descriptor is not null && descriptor.Id != model))
            throw new AiToolsException("ai_agent_options");
        string oldEffort = GetCurrentProfileOptions().EffectiveEffort(GetCurrentProfileSettings());
        bool sameModel = CurrentModel == model;
        Stop(); Current.Model = model; Current.ModelDescriptor = descriptor;
        descriptor?.Price?.BindTo(GetCurrentProfileSettings());
        Current.ModelConnectionIdentity = ModelConnectionIdentity(GetCurrentProfileSettings(), GetExecutionOptions(Current.ProfileId));
        Current.ReasoningEffort = sameModel && descriptor?.SupportedEfforts.Contains(oldEffort, StringComparer.Ordinal) == true ? oldEffort : "";
        Save(); StatusCode = "ready"; Notify();
    }
    internal void SetCurrentReasoningEffort(string effort)
    {
        if (Busy || disposed || Current is null) return;
        var options = GetCurrentProfileOptions();
        if (effort.Length > 0 && !options.EffortsFor(GetCurrentProfileSettings()).Contains(effort, StringComparer.Ordinal))
            throw new AiToolsException("ai_effort_stale");
        Stop(); Current.Model = CurrentModel; Current.ReasoningEffort = effort;
        Save(); StatusCode = "ready"; Notify();
    }
    internal async Task<AiModelCatalogResult> DiscoverModelsAsync(string profileId, CancellationToken token, bool forceRefresh = false)
    {
        RequireExecutionProfile(profileId);
        var settings = GetProfileSettings(profileId); var options = GetExecutionOptions(profileId); RequireHarness(options);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, providerLifetime.Token);
        return await CachedModelsAsync(settings, options, forceRefresh, cancel.Token);
    }
    private Task<AiModelCatalogResult> CachedModelsAsync(ApiSettings settings, AiAgentOptions options, bool forceRefresh, CancellationToken token) =>
        AiModelCatalogCache144.Shared.GetOrFetchAsync(settings, options, async fetchToken =>
        {
        if (options.Backend == "codex" && options.AuthMode == "account")
            return await AiHarnessCatalog.DiscoverCodexAsync(harnessDataDirectory, options, fetchToken);
        if (options.Backend == "opencode" && options.AuthMode == "account")
            return await AiOpenCodeCatalog.DiscoverAsync(harnessDataDirectory, options, fetchToken);
        return await (services.DiscoverModels?.Invoke(settings, options, fetchToken)
            ?? providers.DiscoverModelsAsync(settings, options, fetchToken));
        }, forceRefresh, token);
    internal void SetPermissionMode(string mode)
    {
        if (Busy || disposed || Current is null) return;
        if (mode is not ("ask" or "auto" or "full")) throw new AiToolsException("ai_agent_options");
        if (mode == "auto" && SelectedHarness == "opencode") throw new AiToolsException("harness_permission_mode_unsupported");
        Stop(); Current.PermissionMode = mode; Save(); StatusCode = "ready"; Notify();
    }
    internal void SetNativeAgent(string agent)
    {
        if (Busy || disposed || Current is null) return;
        if (agent is not ("build" or "plan")) throw new AiToolsException("harness_agent");
        Stop(); Current.NativeAgent = agent; Save(); StatusCode = "ready"; Notify();
    }
    internal void SetWorkspace(string path)
    {
        if (Busy || disposed || Current is null) return;
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new AiToolsException("harness_workspace");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        AppDataPaths.RejectReparsePoints(full);
        Stop(); Current.Workspace = full; Save(); StatusCode = "ready"; Notify();
    }

    private AiHarnessContext CreateNativeContext(AiTaskRecord task, ApiSettings settings, AiAgentOptions options, int version, AiExecutionTurn? execution = null)
    {
        string workspace = CurrentWorkspace;
        AppDataPaths.RejectReparsePoints(workspace); Directory.CreateDirectory(workspace);
        // A model/effort change keeps native history; another connection, credential,
        // engine or project gets its own history. No secrets are stored as keys.
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            task.ProfileId, options.Backend, options.AuthMode, options.ProviderId, settings.ApiUrl, settings.ApiKey, workspace,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(harnessDataDirectory)).ToUpperInvariant()))));
        bool Active() => !disposed && version == generation && ReferenceEquals(Current, task) && (execution is null || execution.CompletedAt is null);
        return new AiHarnessContext
        {
            TaskId = task.Id, Workspace = workspace, PermissionMode = task.PermissionMode, Agent = task.NativeAgent,
            TurnId = execution?.Id ?? "", Attachments = execution?.Attachments ?? [], SupportsImageInput = ImageInputCapability == true,
            SaveTurnId = id => OnNativeUi(() => { if (Active() && execution is { CompletedAt: null }) { execution.NativeTurnId = id; QueueNativeSave(task); } }),
            NativeSessionId = task.NativeSessions.GetValueOrDefault(identity, ""),
            SaveSessionId = id => OnNativeUi(() =>
            {
                if (!Active()) return;
                if (!AiAgentOptions.Clean(id, 256) || id.Length == 0) throw new AiToolsException("harness_protocol");
                task.NativeSessions[identity] = id; store.Save(task);
            }),
            Report = progress => OnNativeUi(() =>
            {
                if (!Active()) return;
                if (execution is not null) RecordNativeProgress(task, execution, progress);
                else NativeProgressChanged?.Invoke(progress with { Text = SafeDiagnosticOutput.Redact(progress.Text) });
            }),
            Approve = async (request, token) =>
            {
                await nativeInteractions.WaitAsync(token).ConfigureAwait(false);
                var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    if (!OnNativeUi(() =>
                    {
                        if (!Active() || token.IsCancellationRequested) { completion.TrySetCanceled(token); return; }
                        PendingNativeApproval = request; nativeApprovalReply = completion; Notify();
                    })) completion.TrySetCanceled();
                    return await completion.Task.WaitAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    OnNativeUi(() => { if (nativeApprovalReply == completion) { nativeApprovalReply = null; PendingNativeApproval = null; Notify(); } });
                    nativeInteractions.Release();
                }
            },
            Ask = async (request, token) =>
            {
                await nativeInteractions.WaitAsync(token).ConfigureAwait(false);
                var completion = new TaskCompletionSource<IReadOnlyDictionary<string, string[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    if (!OnNativeUi(() =>
                    {
                        if (!Active() || token.IsCancellationRequested) { completion.TrySetCanceled(token); return; }
                        PendingNativeQuestion = request; nativeQuestionReply = completion; Notify();
                    })) completion.TrySetCanceled();
                    return await completion.Task.WaitAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    OnNativeUi(() => { if (nativeQuestionReply == completion) { nativeQuestionReply = null; PendingNativeQuestion = null; Notify(); } });
                    nativeInteractions.Release();
                }
            }
        };
    }
    private bool OnNativeUi(Action action)
    {
        if (disposed) return false;
        CancellationToken cancel;
        try { cancel = providerLifetime.Token; } catch (ObjectDisposedException) { return false; }
        if (cancel.IsCancellationRequested) return false;
        if (nativeUi is null || SynchronizationContext.Current == nativeUi) { action(); return true; }
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        nativeUi.Post(_ =>
        {
            try
            {
                if (disposed || cancel.IsCancellationRequested) { completed.TrySetResult(false); return; }
                action(); completed.TrySetResult(true);
            }
            catch (Exception ex) { completed.TrySetException(ex); }
        }, null);
        try { return completed.Task.WaitAsync(cancel).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return false; }
    }
    internal void ResolveNativeApproval(string decision)
    {
        if (disposed || PendingNativeApproval is not { } request || !request.Choices.Any(x => x.Id == decision)) return;
        nativeApprovalReply?.TrySetResult(decision);
    }
    internal void ResolveNativeQuestion(IReadOnlyDictionary<string, string[]> answers)
    {
        if (disposed || PendingNativeQuestion is not { } request) return;
        foreach (var field in request.Fields)
        {
            if (!answers.TryGetValue(field.Id, out var values) || values is null || values.Length == 0 ||
                (!field.Multiple && values.Length > 1) || values.Any(x => x is null || x.Length > 16000 ||
                    (!field.AllowCustom && !field.Options.Contains(x, StringComparer.Ordinal)))) return;
        }
        nativeQuestionReply?.TrySetResult(answers.ToDictionary(x => x.Key, x => x.Value.ToArray()));
    }
    private void CancelNativeInteractions()
    {
        nativeApprovalReply?.TrySetCanceled(); nativeQuestionReply?.TrySetCanceled();
        nativeApprovalReply = null; nativeQuestionReply = null; PendingNativeApproval = null; PendingNativeQuestion = null;
    }
}
