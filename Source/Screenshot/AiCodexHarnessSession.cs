using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal sealed class AiCodexHarnessSession : AiHarnessSession
{
    private Process? process;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> requests = new();
    private long sequence;
    private string threadId = "", turnId = "";
    private readonly Dictionary<string, string> texts = new(StringComparer.Ordinal);
    private readonly HashSet<string> finalTexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> progressTexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiNativeProgress> progressItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> childParents = new(StringComparer.Ordinal);
    private readonly HashSet<string> previousTurns = new(StringComparer.Ordinal);
    private readonly List<Task> reviewPrompts = [];
    private readonly HashSet<string> reviewedIds = new(StringComparer.Ordinal);
    private readonly List<JsonObject> pendingUsage = [];
    private readonly object stateGate = new();
    private JsonObject? nativeUsage, usageBaseline;
    private bool resumedThread;
    private bool turnActive;
    private TaskCompletionSource<bool>? sandboxSetup;
    private int liveTools;
    internal AiCodexHarnessSession(string executable, string home, string run, ApiSettings settings, AiAgentOptions options, JsonArray tools, AiHarnessContext? context = null)
        : base(executable, home, run, settings, options, tools, context) { }

    internal static JsonObject NativeConfig(string mode)
    {
        if (mode is not ("ask" or "auto" or "full")) throw new AiToolsException("harness_permission_mode");
        return new JsonObject
        {
            ["approval_policy"] = mode == "full" ? "never" : "on-request",
            ["approvals_reviewer"] = mode == "auto" ? "auto_review" : "user",
            ["sandbox_mode"] = mode == "full" ? "danger-full-access" : "workspace-write",
            ["sandbox_workspace_write.network_access"] = false,
            ["features.shell_tool"] = true, ["features.unified_exec"] = true,
            ["shell_environment_policy.inherit"] = "all",
            ["shell_environment_policy.ignore_default_excludes"] = false,
            ["shell_environment_policy.exclude"] = new JsonArray("*KEY*", "*TOKEN*", "*SECRET*", "*PASSWORD*", "FUSION_HARNESS_*", "OPENCODE_*"),
            ["cli_auth_credentials_store"] = "file", ["analytics.enabled"] = false
        };
    }

    internal static JsonObject RestrictedConfig() => new()
    {
        ["approval_policy"] = "never", ["sandbox_mode"] = "read-only", ["web_search"] = "disabled",
        ["project_doc_max_bytes"] = 0, ["include_environment_context"] = false, ["cli_auth_credentials_store"] = "file",
        ["features.shell_tool"] = false, ["features.unified_exec"] = false, ["features.apply_patch_freeform"] = false,
        ["features.code_mode"] = false, ["features.js_repl"] = false, ["features.view_image"] = false,
        ["features.multi_agent"] = false, ["features.apps"] = false, ["features.connectors"] = false,
        ["features.plugins"] = false, ["features.hooks"] = false, ["features.memories"] = false,
        ["features.memory_tool"] = false, ["features.browser_use"] = false, ["features.computer_use"] = false,
        ["features.request_permissions"] = false, ["features.request_permissions_tool"] = false,
        ["features.standalone_web_search"] = false, ["features.search_tool"] = false,
        ["features.skip_host_skill_discovery"] = true, ["features.skill_search"] = false,
        ["features.image_generation"] = false, ["features.goals"] = false,
        ["features.stable_environment_tools"] = false, ["tools.update_plan.enabled"] = false,
        ["features.collaboration_modes"] = false, ["features.default_mode_request_user_input"] = false,
        ["tools.experimental_request_user_input.enabled"] = false,
        ["analytics.enabled"] = false
    };

    internal override async Task StartAsync(IReadOnlyList<JsonObject> messages, CancellationToken token)
    {
        await AiHarnessEnvironment.VerifyVersionAsync(Executable, "codex", Home, token).ConfigureAwait(false);
        var info = AiHarnessEnvironment.StartInfo(Executable, Home, Workspace);
        info.ArgumentList.Add("app-server"); info.ArgumentList.Add("--listen"); info.ArgumentList.Add("stdio://");
        var config = Context is null ? RestrictedConfig() : NativeConfig(Context.PermissionMode);
        string provider;
        if (Options.AuthMode == "api")
        {
            var endpoint = AiHarnessEnvironment.ApiEndpoint(Settings, "codex");
            if (!endpoint.Responses) throw new AiToolsException("harness_responses_required");
            string url = endpoint.Url.AbsoluteUri[..^"/responses".Length];
            provider = "fusion_api";
            config["model_provider"] = provider;
            config["model_providers.fusion_api.name"] = "Fusion explicit API";
            config["model_providers.fusion_api.base_url"] = url;
            config["model_providers.fusion_api.env_key"] = "FUSION_HARNESS_API_KEY";
            config["model_providers.fusion_api.wire_api"] = "responses";
            config["model_providers.fusion_api.requires_openai_auth"] = false;
            config["model_providers.fusion_api.supports_websockets"] = false;
            config["model_providers.fusion_api.request_max_retries"] = 0;
            config["model_providers.fusion_api.stream_max_retries"] = 0;
            info.Environment["FUSION_HARNESS_API_KEY"] = Settings.ApiKey;
        }
        else
        {
            if (Options.ProviderId is not ("" or "openai")) throw new AiToolsException("harness_provider");
            provider = "openai";
            config["forced_login_method"] = "chatgpt";
            config["model_provider"] = "openai";
        }
        foreach (var pair in config)
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(pair.Key + "=" + pair.Value!.ToJsonString());
        }
        bool trustDecided = false, trustWritten = false, setupAttempted = false;
        for (;;)
        {
            process = Launch(info);
            _ = DrainErrorAsync(process.StandardError);
            _ = PumpAsync(process);
            await RpcAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "fusion_ai_tools", ["title"] = "Fusion AI Tools", ["version"] = "1.1.144" },
                ["capabilities"] = new JsonObject { ["experimentalApi"] = true }
            }, token).ConfigureAwait(false);
            await SendAsync(new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() }, token).ConfigureAwait(false);
            if (Options.AuthMode == "account")
            {
                var account = await RpcAsync("account/read", new JsonObject { ["refreshToken"] = false }, token).ConfigureAwait(false);
                if (account["account"]?["type"]?.GetValue<string>() != "chatgpt") throw new AiToolsException("harness_login_required");
            }
            if (Context is null) break;
            var projectConfig = await RpcAsync("config/read", new JsonObject { ["cwd"] = Workspace, ["includeLayers"] = true }, token).ConfigureAwait(false);
            var blocked = BlockedWorkspaceLayer(projectConfig, Workspace);
            if (trustWritten && blocked is not null) throw new AiToolsException("native_project_trust_failed");
            if (!trustDecided && blocked is not null)
            {
                trustDecided = true;
                if (await RequestProjectTrustAsync(projectConfig, blocked, token).ConfigureAwait(false))
                {
                    trustWritten = true;
                    RestartTransport();
                    continue;
                }
            }
            if (Context.PermissionMode == "full" || !OperatingSystem.IsWindows()) break;
            var readiness = await RpcAsync("windowsSandbox/readiness", null, token).ConfigureAwait(false);
            if (String(readiness, "status") == "ready") break;
            if (setupAttempted) throw new AiToolsException("harness_windows_sandbox");
            setupAttempted = true;
            await SetupWindowsSandboxAsync(token).ConfigureAwait(false);
            // Setup writes the selected backend into this Fusion-owned CODEX_HOME.
            // 0.166 readiness uses startup configuration, so reload it in a new process.
            RestartTransport();
        }
        var functions = new JsonArray();
        foreach (var tool in Tools.OfType<JsonObject>())
        {
            var function = tool["function"]!.AsObject();
            functions.Add(new JsonObject { ["type"] = "function", ["name"] = function["name"]!.DeepClone(),
                ["description"] = function["description"]?.DeepClone() ?? JsonValue.Create("Fusion controlled tool"),
                ["inputSchema"] = function["parameters"]!.DeepClone() });
        }
        var threadParameters = new JsonObject
        {
            ["model"] = Settings.Model, ["modelProvider"] = provider, ["cwd"] = Workspace
        };
        bool resume = Context is not null && Context.NativeSessionId.Length > 0;
        resumedThread = resume;
        if (Context is null)
        {
            threadParameters["ephemeral"] = true; threadParameters["approvalPolicy"] = "never";
            threadParameters["sandbox"] = "read-only"; threadParameters["environments"] = new JsonArray();
            threadParameters["runtimeWorkspaceRoots"] = new JsonArray(); threadParameters["baseInstructions"] = Instructions(messages);
        }
        else
        {
            ApplyNativeThreadPolicy(threadParameters, Context.PermissionMode, Workspace);
            threadParameters["developerInstructions"] = Instructions(messages);
            if (resume)
            {
                threadParameters["threadId"] = Context.NativeSessionId;
                threadParameters["excludeTurns"] = true;
            }
            else threadParameters["ephemeral"] = false;
        }
        // Dynamic tool definitions are durably restored by thread/resume in 0.166.
        if (!resume) { threadParameters["dynamicTools"] = functions; threadParameters["allowProviderModelFallback"] = false; }
        var started = await RpcAsync(resume ? "thread/resume" : "thread/start", threadParameters, token).ConfigureAwait(false);
        if (Context is not null) ValidateNativeThread(started, Settings.Model, provider, Context.PermissionMode);
        threadId = started["thread"]?["id"]?.GetValue<string>() ?? throw new AiToolsException("harness_protocol");
        Context?.SaveSessionId(threadId);
        Started = true;
        await StartTurnAsync(messages, resume, token).ConfigureAwait(false);
    }

    private void RestartTransport()
    {
        var previous = process; process = null;
        AiHarnessEnvironment.Kill(previous); previous?.Dispose();
    }

    internal static JsonObject? BlockedWorkspaceLayer(JsonObject configuration, string workspace)
    {
        // In pinned 0.166 project-layer disabledReason is produced exclusively by
        // ProjectTrustContext. Parse failures use startup warnings instead. Inspect
        // the structured source, never infer trust from localized warning prose.
        // Do not broaden this decision to an ancestor repository/configuration.
        string expected = Path.Combine(Path.GetFullPath(workspace), ".codex");
        return (configuration["layers"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(layer =>
            layer["name"] is JsonObject source && String(source, "type") == "project" &&
            SamePath(String(source, "dotCodexFolder"), expected) && String(layer, "disabledReason").Length > 0);
    }

    private async Task<bool> RequestProjectTrustAsync(JsonObject configuration, JsonObject blocked, CancellationToken token)
    {
        string trust = Guid.NewGuid().ToString("N"), skip = Guid.NewGuid().ToString("N"), cancel = Guid.NewGuid().ToString("N");
        string selected = await Context!.Approve(new AiNativeApproval("project-trust", "projectTrust", AiUiText.Text("native_project_trust_title"),
            AiUiText.Text("native_project_trust_detail") + "\n" + Workspace + "\n\n" + Safe(String(blocked, "disabledReason")),
            [new(trust, AiUiText.Text("native_project_trust_allow")), new(skip, AiUiText.Text("native_project_trust_skip")), new(cancel, AiUiText.Text("Cancel"))]), token).ConfigureAwait(false);
        if (selected == skip) return false;
        if (selected != trust) throw new AiToolsException("ai_cancelled");
        string file = Path.Combine(Path.GetFullPath(Home), "config.toml");
        AppDataPaths.RejectReparsePoints(file);
        var user = (configuration["layers"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault(layer =>
            layer["name"] is JsonObject source && String(source, "type") == "user" && SamePath(String(source, "file"), file));
        if (user is null || String(user, "version").Length == 0) throw new AiToolsException("native_project_trust_failed");
        var written = await RpcAsync("config/value/write", new JsonObject
        {
            ["filePath"] = file, ["expectedVersion"] = String(user, "version"), ["mergeStrategy"] = "upsert",
            ["keyPath"] = ProjectTrustKeyPath(Workspace), ["value"] = "trusted"
        }, token).ConfigureAwait(false);
        if (String(written, "status") != "ok" || !SamePath(String(written, "filePath"), file)) throw new AiToolsException("native_project_trust_failed");
        return true;
    }

    internal static string ProjectTrustKeyPath(string workspace) => "projects.\"" + Path.GetFullPath(workspace)
        .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\".trust_level";

    private static bool SamePath(string value, string expected)
    {
        try { return Path.IsPathFullyQualified(value) && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static void ApplyNativeThreadPolicy(JsonObject parameters, string mode, string workspace)
    {
        var config = NativeConfig(mode);
        parameters["approvalPolicy"] = config["approval_policy"]!.DeepClone();
        parameters["approvalsReviewer"] = config["approvals_reviewer"]!.DeepClone();
        parameters["sandbox"] = config["sandbox_mode"]!.DeepClone();
        parameters["runtimeWorkspaceRoots"] = new JsonArray(workspace);
    }

    internal static void ValidateNativeThread(JsonObject result, string model, string provider, string mode)
    {
        string reviewer = String(result, "approvalsReviewer");
        if (String(result, "model") != model || String(result, "modelProvider") != provider ||
            String(result, "approvalPolicy") != (mode == "full" ? "never" : "on-request") ||
            result["sandbox"]?["type"]?.GetValue<string>() != (mode == "full" ? "dangerFullAccess" : "workspaceWrite") ||
            (mode == "auto" ? reviewer is not ("auto_review" or "guardian_subagent") : reviewer != "user"))
            throw new AiToolsException("harness_protocol");
    }

    private async Task SetupWindowsSandboxAsync(CancellationToken token)
    {
        string install = Guid.NewGuid().ToString("N"), cancel = Guid.NewGuid().ToString("N");
        string decision = await Context!.Approve(new AiNativeApproval("windows-sandbox", "windowsSandbox", AiUiText.Text("native_windows_setup_title"),
            AiUiText.Text("native_windows_setup_detail") + "\n" + Workspace,
            [new(install, "Set up Windows sandbox"), new(cancel, "Cancel")]), token).ConfigureAwait(false);
        if (decision != install) throw new AiToolsException("ai_cancelled");
        sandboxSetup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var setupTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            setupTimeout.CancelAfter(TimeSpan.FromMinutes(5));
            Report("status", AiUiText.Text("native_windows_setup_progress"), "windows-sandbox");
            var response = await RpcAsync("windowsSandbox/setupStart", new JsonObject { ["mode"] = "elevated", ["cwd"] = Workspace }, setupTimeout.Token).ConfigureAwait(false);
            if (response["started"]?.GetValue<bool>() != true || !await sandboxSetup.Task.WaitAsync(setupTimeout.Token).ConfigureAwait(false))
                throw new AiToolsException("harness_windows_sandbox");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new AiToolsException("harness_windows_sandbox"); }
        finally { sandboxSetup = null; }
    }

    internal static string NativePrompt(IReadOnlyList<JsonObject> messages, bool continuation)
        => AiNativeConversation144.Prompt(messages, continuation);

    internal override Task ContinueAsync(IReadOnlyList<JsonObject> messages, CancellationToken token) => StartTurnAsync(messages, true, token);

    internal static JsonObject NativeCollaborationMode(string agent, string model, string effort)
    {
        if (agent is not ("build" or "plan")) throw new AiToolsException("ai_agent_options");
        return new JsonObject { ["mode"] = agent == "plan" ? "plan" : "default", ["settings"] = new JsonObject
            { ["model"] = model, ["reasoning_effort"] = effort.Length > 0 ? JsonValue.Create(effort) : null, ["developer_instructions"] = null } };
    }

    internal override async Task InterruptAsync(CancellationToken token)
    {
        string activeTurn;
        lock (stateGate) { if (!turnActive || turnId.Length == 0 || threadId.Length == 0) return; activeTurn = turnId; }
        await RpcAsync("turn/interrupt", new JsonObject { ["threadId"] = threadId, ["turnId"] = activeTurn }, token).ConfigureAwait(false);
    }

    private async Task StartTurnAsync(IReadOnlyList<JsonObject> messages, bool continuation, CancellationToken token)
    {
        lock (stateGate)
        {
            if (turnActive) throw new AiToolsException("harness_incomplete");
            if (turnId.Length > 0) previousTurns.Add(turnId);
            texts.Clear(); finalTexts.Clear(); items.Clear(); progressTexts.Clear(); progressItems.Clear(); childParents.Clear(); reviewPrompts.Clear(); reviewedIds.Clear(); pendingUsage.Clear(); turnId = ""; turnActive = true;
            usageBaseline = nativeUsage is not null ? (JsonObject)nativeUsage.DeepClone() : resumedThread ? null
                : new JsonObject { ["inputTokens"] = 0L, ["outputTokens"] = 0L, ["cachedInputTokens"] = 0L, ["reasoningOutputTokens"] = 0L };
            if (Context is not null) TotalUsage = null;
        }
        var parameters = new JsonObject { ["threadId"] = threadId, ["input"] = Context is null ? new JsonArray(new JsonObject
            { ["type"] = "text", ["text"] = Prompt(messages) }) : AiCodexActivity144.Inputs(NativePrompt(messages, continuation), Context) };
        string effort = Options.EffectiveEffort(Settings);
        if (effort.Length > 0) parameters["effort"] = effort;
        if (Context is not null) parameters["collaborationMode"] = NativeCollaborationMode(Context.Agent, Settings.Model, effort);
        var turn = await RpcAsync("turn/start", parameters, token).ConfigureAwait(false);
        SetActiveTurn(turn["turn"]?["id"]?.GetValue<string>() ?? throw new AiToolsException("harness_protocol"));
    }

    private void SetActiveTurn(string id)
    {
        JsonObject[] queued;
        lock (stateGate)
        {
            if (previousTurns.Contains(id) || (turnId.Length > 0 && turnId != id)) return;
            turnId = id; queued = pendingUsage.ToArray(); pendingUsage.Clear();
        }
        Context?.SaveTurnId(id);
        foreach (var usage in queued) UpdateNativeUsage(usage);
    }

    private async Task<JsonObject> RpcAsync(string method, JsonObject? parameters, CancellationToken token)
    {
        long id = Interlocked.Increment(ref sequence);
        var source = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously); requests[id] = source;
        try
        {
            var message = new JsonObject { ["id"] = id, ["method"] = method };
            if (parameters is not null) message["params"] = parameters;
            await SendAsync(message, token).ConfigureAwait(false);
            return await source.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally { requests.TryRemove(id, out _); }
    }
    private async Task SendAsync(JsonObject message, CancellationToken token)
    {
        await writes.WaitAsync(token).ConfigureAwait(false);
        try { await process!.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), token).ConfigureAwait(false); await process.StandardInput.FlushAsync(token).ConfigureAwait(false); }
        finally { writes.Release(); }
    }
    private async Task PumpAsync(Process child)
    {
        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                string? line = await AiHarnessWire.ReadLineAsync(child.StandardOutput, Lifetime.Token).ConfigureAwait(false);
                if (line is null) throw new AiToolsException("harness_exited");
                var message = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { MaxDepth = 40 })?.AsObject() ?? throw new AiToolsException("harness_protocol");
                string? method = message["method"]?.GetValue<string>();
                if (method is null && message["id"] is JsonValue idValue && idValue.TryGetValue<long>(out long id) && requests.TryGetValue(id, out var request))
                {
                    if (message["error"] is not null) request.TrySetException(new AiToolsException("harness_protocol"));
                    else
                    {
                        var result = message["result"]?.AsObject() ?? new JsonObject();
                        // Publish turn identity in the pump before subsequent notifications;
                        // the RPC caller's async continuation may run after a fast whole turn.
                        if (result["turn"]?["id"] is JsonValue returnedTurn) SetActiveTurn(returnedTurn.GetValue<string>());
                        request.TrySetResult(result);
                    }
                    continue;
                }
                var p = message["params"] as JsonObject ?? new JsonObject();
                if (method == "windowsSandbox/setupCompleted")
                {
                    sandboxSetup?.TrySetResult(p["success"]?.GetValue<bool>() == true);
                    continue;
                }
                if (message["id"] is not null && method is not null)
                {
                    if (Context is not null && String(p, "turnId").Length > 0 && !CurrentNotification(p))
                    {
                        await SendAsync(new JsonObject { ["id"] = message["id"]!.DeepClone(), ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "Turn is no longer active." } }, Lifetime.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (method == "item/tool/call")
                    {
                        if (Interlocked.Increment(ref liveTools) > 4) throw new AiToolsException("tool_invalid");
                        _ = HandleToolAsync(message["id"]!.DeepClone(), p);
                    }
                    else if (Context is not null)
                    {
                        if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
                        {
                            string owner = String(p, "threadId"), key = owner + ":" + String(p, "itemId"); bool known;
                            lock (stateGate) known = progressItems.ContainsKey(key);
                            if (!known && String(p, "itemId").Length > 0)
                                Snapshot(new AiNativeProgress("tool", "", key) { ActivityType = method.Contains("commandExecution", StringComparison.Ordinal) ? "command" : "file", Status = "pending", NativeTurnId = turnId, Command = String(p, "command") });
                        }
                        _ = HandleNativeRequestAsync(message["id"]!.DeepClone(), method, p);
                    }
                    else
                    {
                        await SendAsync(new JsonObject { ["id"] = message["id"]!.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Native actions unavailable in this session." } }, Lifetime.Token).ConfigureAwait(false);
                        throw new AiToolsException("harness_action_denied");
                    }
                    continue;
                }
                bool mainThread = p["threadId"] is not JsonValue thread || thread.GetValue<string>() == threadId || threadId.Length == 0;
                if (Context is not null && method == "thread/started" && p["thread"] is JsonObject spawned &&
                    spawned["source"] is JsonObject source && source["subAgent"] is JsonObject subAgent && subAgent["thread_spawn"] is JsonObject origin)
                {
                    string parentThread = String(origin, "parent_thread_id"), childThread = String(spawned, "id");
                    lock (stateGate) if (turnActive && childThread.Length > 0 && childParents.Count < 128 && (parentThread == threadId || childParents.ContainsKey(parentThread)))
                        childParents.TryAdd(childThread, "");
                }
                if (Context is not null && mainThread && method == "turn/started" && p["turn"]?["id"] is JsonValue newTurn) SetActiveTurn(newTurn.GetValue<string>());
                if (Context is not null && !CurrentNotification(p)) continue;
                if (Context is not null) ReportNotification(method ?? "", p);
                if (Context?.PermissionMode == "auto" && method == "item/autoApprovalReview/completed" && String(p["review"]?.AsObject() ?? new(), "status") == "denied")
                {
                    bool prompt; lock (stateGate) prompt = reviewedIds.Count < 64 && reviewedIds.Add(String(p, "reviewId"));
                    if (prompt) { var reviewTask = ReviewDeniedAsync((JsonObject)p.DeepClone()); lock (stateGate) reviewPrompts.Add(reviewTask); }
                }
                if (!mainThread) continue;
                if (method == "thread/tokenUsage/updated")
                {
                    if (Context is null) TotalUsage = AiHarnessUsage.Codex(p["tokenUsage"]?["total"]);
                    else UpdateNativeUsage(p);
                }
                else if (method == "turn/started" && p["turn"]?["id"] is JsonValue startedTurn) SetActiveTurn(startedTurn.GetValue<string>());
                else if (method == "item/completed" && p["item"] is JsonObject item)
                {
                    string kind = item["type"]?.GetValue<string>() ?? "";
                    if (kind == "agentMessage")
                    {
                        string text = item["text"]?.GetValue<string>() ?? "";
                        lock (stateGate)
                        {
                            string key = String(item, "id");
                            if (text.Length > AiConversationClient.MaxResponseBytes || texts.Where(x => x.Key != key).Sum(x => x.Value.Length) + text.Length > AiConversationClient.MaxResponseBytes)
                                throw new AiToolsException("response_limit");
                            if (Context is null || String(item, "phase") != "commentary") texts[key] = text;
                            if (String(item, "phase") == "final_answer") finalTexts.Add(key);
                        }
                    }
                    else if (Context is null && kind is ("commandExecution" or "fileChange" or "mcpToolCall" or "webSearch" or "imageGeneration" or "collabAgentToolCall"))
                        throw new AiToolsException("harness_action_denied");
                }
                else if (method == "turn/completed")
                {
                    string status = p["turn"]?["status"]?.GetValue<string>() ?? "";
                    if (status != "completed") throw new AiToolsException(status == "interrupted" ? "ai_cancelled" : "harness_incomplete");
                    string final;
                    lock (stateGate)
                    {
                        turnActive = false;
                        final = string.Join("\n", texts.Where(x => finalTexts.Count == 0 || finalTexts.Contains(x.Key)).Select(x => x.Value));
                    }
                    if (final.Length == 0 && Context is null) throw new AiToolsException("api_format");
                    _ = CompleteTurnAsync(final);
                }
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(process, child)) return; // Expected transport restart after approved sandbox setup.
            foreach (var request in requests.Values) request.TrySetException(new AiToolsException("harness_exited"));
            sandboxSetup?.TrySetException(new AiToolsException("harness_exited"));
            Fail(ex);
        }
    }
    internal static JsonObject UsageDifference(JsonObject total, JsonObject baseline)
    {
        var result = new JsonObject();
        foreach (string key in new[] { "inputTokens", "outputTokens", "cachedInputTokens", "reasoningOutputTokens" })
        {
            if (total[key] is JsonValue current && current.TryGetValue<long>(out long value) && value >= 0 &&
                baseline[key] is JsonValue previous && previous.TryGetValue<long>(out long prior) && prior >= 0)
            {
                if (value >= prior) result[key] = value - prior;
            }
        }
        return result;
    }

    private async Task CompleteTurnAsync(string final)
    {
        try
        {
            Task[] prompts; lock (stateGate) prompts = reviewPrompts.ToArray();
            await Task.WhenAll(prompts).WaitAsync(Lifetime.Token).ConfigureAwait(false);
            await Replies.Writer.WriteAsync(new AiModelReply(final, [], 0, 0), Lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { Fail(ex); }
    }

    // 0.166 exposes a camelCase app-server assessment but its explicit override RPC
    // expects the snake_case core GuardianAssessmentEvent. No decision is invented.
    internal static JsonObject GuardianDeniedEvent(JsonObject notification)
    {
        var review = notification["review"] as JsonObject ?? throw new AiToolsException("harness_protocol");
        if (String(review, "status") != "denied") throw new AiToolsException("harness_action_denied");
        JsonNode? ConvertKeys(JsonNode? node)
        {
            if (node is JsonArray array) return new JsonArray(array.Select(ConvertKeys).ToArray());
            if (node is not JsonObject source) return node?.DeepClone();
            var result = new JsonObject();
            foreach (var pair in source)
            {
                string key = string.Concat(pair.Key.Select(c => char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : c.ToString()));
                result[key] = ConvertKeys(pair.Value);
            }
            return result;
        }
        var action = ConvertKeys(notification["action"])?.AsObject() ?? throw new AiToolsException("harness_protocol");
        action["type"] = String(action, "type") switch
        {
            "writeStdin" => "write_stdin", "applyPatch" => "apply_patch", "networkAccess" => "network_access",
            "mcpToolCall" => "mcp_tool_call", "requestPermissions" => "request_permissions", var type => type
        };
        if (String(action, "source") == "unifiedExec") action["source"] = "unified_exec";
        return new JsonObject
        {
            ["id"] = notification["reviewId"]?.DeepClone(), ["target_item_id"] = notification["targetItemId"]?.DeepClone(),
            ["turn_id"] = notification["turnId"]?.DeepClone(), ["started_at_ms"] = notification["startedAtMs"]?.DeepClone(),
            ["completed_at_ms"] = notification["completedAtMs"]?.DeepClone(), ["status"] = "denied",
            ["risk_level"] = review["riskLevel"]?.DeepClone(), ["user_authorization"] = review["userAuthorization"]?.DeepClone(),
            ["rationale"] = review["rationale"]?.DeepClone(), ["decision_source"] = notification["decisionSource"]?.DeepClone(), ["action"] = action
        };
    }

    private async Task ReviewDeniedAsync(JsonObject notification)
    {
        try
        {
            string approve = Guid.NewGuid().ToString("N"), deny = Guid.NewGuid().ToString("N");
            string selected = await Context!.Approve(new AiNativeApproval(String(notification, "reviewId"), "autoReview",
                "Codex automatic review denied this action", Safe(notification.ToJsonString()),
                [new(approve, "Allow this action on retry"), new(deny, "Keep denied")]), Lifetime.Token).ConfigureAwait(false);
            if (selected != approve) return;
            // Native operation records the user's approval for a retry; it does not run
            // an out-of-sandbox host command and does not start a new paid model turn.
            await RpcAsync("thread/approveGuardianDeniedAction", new JsonObject
                { ["threadId"] = notification["threadId"]!.DeepClone(), ["event"] = GuardianDeniedEvent(notification) }, Lifetime.Token).ConfigureAwait(false);
            Report("status", "The approved action may be retried by Codex.", "review:" + String(notification, "reviewId"));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Report("status", "The action remains denied; its approval could not be recorded.", "review:" + String(notification, "reviewId")); }
    }

    private void UpdateNativeUsage(JsonObject parameters)
    {
        if (parameters["tokenUsage"]?["total"] is not JsonObject total) return;
        lock (stateGate)
        {
            string usageTurn = String(parameters, "turnId");
            if (turnActive && turnId.Length == 0)
            {
                if (pendingUsage.Count >= 256) throw new AiToolsException("harness_protocol");
                pendingUsage.Add((JsonObject)parameters.DeepClone());
                return;
            }
            if (!turnActive || (usageTurn.Length > 0 && turnId.Length > 0 && usageTurn != turnId))
            {
                nativeUsage = (JsonObject)total.DeepClone();
                if (usageBaseline is null) usageBaseline = (JsonObject)total.DeepClone();
                return;
            }
            // A resumed thread can emit its first usage notification only after inference.
            // `last` then supplies the current provider request; infer only the prior baseline.
            if (usageBaseline is null && parameters["tokenUsage"]?["last"] is JsonObject last)
                usageBaseline = UsageDifference(total, last);
            nativeUsage = (JsonObject)total.DeepClone();
            TotalUsage = usageBaseline is null ? null : AiHarnessUsage.Codex(UsageDifference(total, usageBaseline));
            if (TotalUsage is not null) TotalUsage.Source = "Codex app-server current-turn token usage";
        }
    }

    private async Task HandleToolAsync(JsonNode id, JsonObject parameters)
    {
        try
        {
            if ((Context is null && (parameters["threadId"]?.GetValue<string>() != threadId ||
                (turnId.Length > 0 && parameters["turnId"]?.GetValue<string>() != turnId))) || parameters["namespace"] is not null)
                throw new AiToolsException("tool_invalid");
            string result = await RequestToolAsync(parameters["tool"]?.GetValue<string>() ?? "", parameters["arguments"]?.AsObject() ?? throw new AiToolsException("tool_invalid")).ConfigureAwait(false);
            await SendAsync(new JsonObject { ["id"] = id, ["result"] = new JsonObject { ["success"] = true,
                ["contentItems"] = new JsonArray(new JsonObject { ["type"] = "inputText", ["text"] = result }) } }, Lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { Fail(ex); }
        finally { Interlocked.Decrement(ref liveTools); }
    }

    // Keep native decisions opaque: the UI returns a locally generated choice ID, never
    // a reconstructed command, permission profile or persistent policy amendment.
    internal static Dictionary<string, (string Label, JsonObject Result)> ApprovalChoices(string method, JsonObject parameters)
    {
        var choices = new Dictionary<string, (string, JsonObject)>(StringComparer.Ordinal);
        void Add(string label, JsonObject result) => choices.Add(Guid.NewGuid().ToString("N"), (label, result));
        if (method == "item/permissions/requestApproval")
        {
            var permissions = parameters["permissions"] as JsonObject ?? throw new AiToolsException("harness_protocol");
            Add("Allow once", new JsonObject { ["permissions"] = permissions.DeepClone(), ["scope"] = "turn" });
            Add("Allow for this session", new JsonObject { ["permissions"] = permissions.DeepClone(), ["scope"] = "session" });
            Add("Deny", new JsonObject { ["permissions"] = new JsonObject(), ["scope"] = "turn" });
            return choices;
        }
        var decisions = parameters["availableDecisions"] as JsonArray;
        if (decisions is null || decisions.Count == 0)
        {
            decisions = new JsonArray("accept", "acceptForSession", "decline", "cancel");
            if (method == "item/commandExecution/requestApproval")
            {
                if (parameters["proposedExecpolicyAmendment"] is JsonArray amendment)
                    decisions.Add(new JsonObject { ["acceptWithExecpolicyAmendment"] = new JsonObject { ["execpolicy_amendment"] = amendment.DeepClone() } });
                if (parameters["proposedNetworkPolicyAmendments"] is JsonArray networks)
                    foreach (var network in networks.OfType<JsonObject>())
                        decisions.Add(new JsonObject { ["applyNetworkPolicyAmendment"] = new JsonObject { ["network_policy_amendment"] = network.DeepClone() } });
            }
        }
        if (decisions.Count > 32) throw new AiToolsException("harness_protocol");
        foreach (var decision in decisions)
        {
            string label = decision is JsonValue value && value.TryGetValue<string>(out var name) ? name switch
            {
                "accept" => "Allow once", "acceptForSession" => "Allow for this session", "decline" => "Deny", "cancel" => "Cancel turn", _ => ""
            } : decision is JsonObject obj && obj["acceptWithExecpolicyAmendment"] is not null ? "Allow matching commands"
                : decision is JsonObject rule && rule["applyNetworkPolicyAmendment"] is not null ? "Apply network rule" : "";
            if (label.Length == 0) continue;
            Add(label, new JsonObject { ["decision"] = decision!.DeepClone() });
        }
        return choices;
    }

    internal static JsonObject ApprovalResult(string method, Dictionary<string, (string Label, JsonObject Result)> choices, string? selected)
        => selected is not null && choices.TryGetValue(selected, out var value) ? (JsonObject)value.Result.DeepClone()
            : method == "item/permissions/requestApproval" ? new JsonObject { ["permissions"] = new JsonObject(), ["scope"] = "turn" }
            : new JsonObject { ["decision"] = "cancel" };

    private async Task HandleNativeRequestAsync(JsonNode id, string method, JsonObject parameters)
    {
        try
        {
            JsonObject result;
            if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "item/permissions/requestApproval")
            {
                var choices = ApprovalChoices(method, parameters);
                string details = parameters.ToJsonString();
                lock (stateGate) if (items.TryGetValue(String(parameters, "itemId"), out var item)) details += "\n" + item.ToJsonString();
                string selected;
                try
                {
                    selected = await Context!.Approve(new AiNativeApproval(id.ToJsonString(), method,
                        method.Contains("commandExecution", StringComparison.Ordinal) ? "Codex command approval" : method.Contains("fileChange", StringComparison.Ordinal) ? "Codex file changes" : "Codex permissions",
                        Safe(details), choices.Select(x => new AiApprovalChoice(x.Key, x.Value.Label)).ToArray()), Lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { selected = ""; }
                result = ApprovalResult(method, choices, selected);
            }
            else if (method == "item/tool/requestUserInput")
            {
                var fields = QuestionFields(parameters);
                IReadOnlyDictionary<string, string[]> answers;
                try { answers = await Context!.Ask(new AiNativeQuestion(id.ToJsonString(), fields), Lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { answers = new Dictionary<string, string[]>(); }
                result = QuestionResult(fields, answers);
            }
            else if (method == "mcpServer/elicitation/request")
            {
                try { result = await ElicitAsync(id, parameters).ConfigureAwait(false); }
                catch (OperationCanceledException) { result = new JsonObject { ["action"] = "cancel" }; }
            }
            else if (method == "currentTime/read") result = new JsonObject { ["currentTimeAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            else
            {
                Report("status", "Codex requested an unsupported client interaction: " + method, "interaction:" + id.ToJsonString());
                await SendAsync(new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Unsupported client interaction" } }, Lifetime.Token).ConfigureAwait(false);
                return;
            }
            await SendAsync(new JsonObject { ["id"] = id, ["result"] = result }, Lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { Fail(ex); }
    }

    internal static IReadOnlyList<AiQuestionField> QuestionFields(JsonObject parameters)
    {
        var questions = parameters["questions"] as JsonArray ?? throw new AiToolsException("harness_protocol");
        if (questions.Count > 16) throw new AiToolsException("harness_protocol");
        return questions.OfType<JsonObject>().Select(q => new AiQuestionField(String(q, "id"), String(q, "header"), String(q, "question"),
            (q["options"] as JsonArray)?.OfType<JsonObject>().Select(o => String(o, "label")).ToArray() ?? [], q["isSecret"]?.GetValue<bool>() ?? false)).ToArray();
    }

    internal static JsonObject QuestionResult(IReadOnlyList<AiQuestionField> fields, IReadOnlyDictionary<string, string[]> answers)
    {
        var result = new JsonObject();
        foreach (var field in fields)
            if (answers.TryGetValue(field.Id, out var answer))
                result[field.Id] = new JsonObject { ["answers"] = new JsonArray(answer.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
        return new JsonObject { ["answers"] = result };
    }

    private async Task<JsonObject> ElicitAsync(JsonNode id, JsonObject parameters)
    {
        string mode = String(parameters, "mode");
        if (mode == "url")
        {
            string answer = await Context!.Approve(new AiNativeApproval(id.ToJsonString(), "mcp", "MCP authorization",
                Safe(String(parameters, "message") + "\n" + String(parameters, "url")),
                [new("completed", "I completed this authorization"), new("cancel", "Cancel")]), Lifetime.Token).ConfigureAwait(false);
            return new JsonObject { ["action"] = answer == "completed" ? "accept" : "cancel" };
        }
        if (mode is not ("form" or "openai/form" or "openaiForm") || parameters["requestedSchema"]?["properties"] is not JsonObject properties)
            return new JsonObject { ["action"] = "cancel" };
        var fields = new List<AiQuestionField>();
        foreach (var property in properties)
        {
            if (property.Value is not JsonObject schema || String(schema, "type") is not ("string" or "boolean" or "number" or "integer") || fields.Count >= 16)
                return new JsonObject { ["action"] = "cancel" };
            var options = schema["enum"] is JsonArray values ? values.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : x!.ToJsonString()).ToArray()
                : String(schema, "type") == "boolean" ? ["true", "false"] : Array.Empty<string>();
            fields.Add(new(property.Key, String(schema, "title"), Safe(String(parameters, "message") + "\n" + String(schema, "description")), options,
                String(schema, "format") == "password"));
        }
        var answers = await Context!.Ask(new AiNativeQuestion(id.ToJsonString(), fields), Lifetime.Token).ConfigureAwait(false);
        var content = new JsonObject();
        foreach (var field in fields)
        {
            if (!answers.TryGetValue(field.Id, out var answer) || answer.Length == 0) return new JsonObject { ["action"] = "cancel" };
            string type = String(properties[field.Id]!.AsObject(), "type");
            if (type == "string") content[field.Id] = answer[0];
            else if (type == "boolean" && bool.TryParse(answer[0], out bool flag)) content[field.Id] = flag;
            else if (type == "integer" && long.TryParse(answer[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long number)) content[field.Id] = number;
            else if (type == "number" && decimal.TryParse(answer[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal value)) content[field.Id] = value;
            else return new JsonObject { ["action"] = "cancel" };
        }
        return new JsonObject { ["action"] = "accept", ["content"] = content };
    }

    private bool CurrentNotification(JsonObject parameters)
    {
        string owner = String(parameters, "threadId"), declared = String(parameters, "turnId");
        if (declared.Length == 0 && parameters["turn"] is JsonObject turn) declared = String(turn, "id");
        lock (stateGate)
        {
            if (owner.Length > 0 && threadId.Length > 0 && owner != threadId) return turnActive && childParents.ContainsKey(owner);
            if (declared.Length > 0) return declared == turnId && !previousTurns.Contains(declared);
            if (parameters["item"] is not null || parameters["itemId"] is not null) return turnActive;
            return true;
        }
    }

    private void ReportNotification(string method, JsonObject parameters)
    {
        string nativeThread = String(parameters, "threadId");
        string itemId = String(parameters, "itemId");
        if (method == "configWarning")
        {
            string text = string.Join("\n", new[] { String(parameters, "summary"), String(parameters, "details"), String(parameters, "path") }.Where(x => x.Length > 0));
            Snapshot(new AiNativeProgress("status", text, "config-warning:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16]) { Status = "completed" });
        }
        else if (parameters["item"] is JsonObject item && method is "item/started" or "item/completed")
        {
            itemId = String(item, "id");
            string type = String(item, "type"), rootTurn, parent;
            lock (stateGate)
            {
                rootTurn = turnId; parent = childParents.GetValueOrDefault(nativeThread, "");
                if (type == "fileChange" && (items.Count < 512 || items.ContainsKey(itemId))) items[itemId] = (JsonObject)item.DeepClone();
                if (type is "collabAgentToolCall" or "subAgentActivity")
                {
                    var children = (item["receiverThreadIds"] as JsonArray ?? []).OfType<JsonValue>().Select(x => x.GetValue<string>()).ToList();
                    if (String(item, "agentThreadId").Length > 0) children.Add(String(item, "agentThreadId"));
                    foreach (string child in children.Take(32)) if (childParents.Count < 128 || childParents.ContainsKey(child)) childParents[child] = nativeThread + ":" + itemId;
                }
            }
            var activity = AiCodexActivity144.Item(item, nativeThread, rootTurn, method == "item/completed", parent);
            if (activity is not null)
            {
                if (nativeThread != threadId && activity.SubagentId.Length == 0) activity = activity with { SubagentId = nativeThread };
                lock (stateGate) if (activity.Text.Length == 0 && progressTexts.TryGetValue(activity.ItemId, out var previous)) activity = activity with { Text = previous };
                Snapshot(activity);
            }
        }
        else if (method is "item/agentMessage/delta" or "item/commandExecution/outputDelta" or "item/fileChange/outputDelta" or "item/plan/delta"
            or "item/reasoning/summaryTextDelta" or "item/reasoning/textDelta")
        {
            AiNativeProgress? previous; lock (stateGate) progressItems.TryGetValue(nativeThread + ":" + itemId, out previous);
            if (previous is not null) Snapshot(previous with { Text = previous.Text + String(parameters, "delta") });
        }
        else if (method == "turn/plan/updated")
        {
            var plan = AiCodexActivity144.Plan(parameters["plan"]);
            Snapshot(new AiNativeProgress("status", String(parameters, "explanation"), nativeThread + ":plan")
                { ActivityType = "plan", Status = plan.Count > 0 && plan.All(p => p.Status == "completed") ? "completed" : "running", NativeTurnId = turnId, Plan = plan });
        }
        else if (method == "turn/diff/updated")
            Snapshot(new AiNativeProgress("tool", String(parameters, "diff"), nativeThread + ":diff")
                { ActivityType = "file", Status = "completed", NativeTurnId = turnId });
        else if (method == "item/fileChange/patchUpdated")
        {
            AiNativeProgress? previous; lock (stateGate) progressItems.TryGetValue(nativeThread + ":" + itemId, out previous);
            if (previous is not null) Snapshot(previous with { Files = AiCodexActivity144.Files(parameters["changes"]) });
        }
        else if (method is "error" or "guardianWarning" or "autoApprovalReview/strictReviewRequired" or "item/autoApprovalReview/started" or "item/autoApprovalReview/completed")
        {
            string text = String(parameters, "message");
            if (parameters["error"] is JsonObject error) text = String(error, "message");
            if (parameters["review"] is JsonObject review) text = string.Join("\n", new[] { String(review, "status"), String(review, "rationale") }.Where(x => x.Length > 0));
            Snapshot(new AiNativeProgress("status", text, nativeThread + ":" + (String(parameters, "reviewId").Length > 0 ? "review:" + String(parameters, "reviewId") : method))
                { NativeTurnId = turnId, Status = method.EndsWith("started", StringComparison.Ordinal) ? "running" : "completed" });
        }
    }

    private void Snapshot(AiNativeProgress progress)
    {
        progress = progress with { Text = Safe(progress.Text) };
        lock (stateGate)
        {
            if (progressTexts.Count >= 1024 && !progressTexts.ContainsKey(progress.ItemId)) return;
            progressTexts[progress.ItemId] = progress.Text; progressItems[progress.ItemId] = progress;
        }
        if (!Lifetime.IsCancellationRequested) Context?.Report(progress);
    }

    private static string String(JsonObject value, string key) => value[key] is JsonValue scalar && scalar.TryGetValue<string>(out string? text) ? text : "";
    private string Safe(string text)
    {
        if (!string.IsNullOrEmpty(Settings.ApiKey)) text = text.Replace(Settings.ApiKey, "[redacted]", StringComparison.Ordinal);
        return AiExecutionStore.ActivityText(text, 48000);
    }
    private void Report(string kind, string text, string itemId = "")
    { if (text.Length > 0) Context?.Report(new AiNativeProgress(kind, Safe(text), itemId)); }

    private async Task DrainErrorAsync(StreamReader reader)
    {
        try { var buffer = new char[4096]; while (await reader.ReadAsync(buffer, Lifetime.Token).ConfigureAwait(false) > 0) { } }
        catch { }
    }
    protected override void DisposeCore()
    {
        AiHarnessEnvironment.Kill(process);
        foreach (var request in requests.Values) request.TrySetCanceled();
        process?.Dispose();
    }
}

internal static class AiHarnessWire
{
    internal static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        // StreamReader.ReadLineAsync has no size limit. Bound before allocating a hostile line.
        var result = new System.Text.StringBuilder(); var buffer = new char[1];
        while (true)
        {
            int count = await reader.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return result.Length == 0 ? null : result.ToString();
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= AiConversationClient.MaxResponseBytes) throw new AiToolsException("response_limit");
            result.Append(buffer[0]);
        }
    }
}
