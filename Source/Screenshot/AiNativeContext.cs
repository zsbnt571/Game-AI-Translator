namespace ScreenshotTranslationUiTester;

// Decisions are opaque IDs offered by the native agent. The UI must never infer
// a broader permission from a label or turn a failed review into approval.
internal sealed record AiApprovalChoice(string Id, string Label);
internal sealed record AiNativeApproval(string Id, string Kind, string Title, string Details,
    IReadOnlyList<AiApprovalChoice> Choices);
internal sealed record AiQuestionField(string Id, string Header, string Question,
    IReadOnlyList<string> Options, bool Secret = false, bool Multiple = false, bool AllowCustom = true);
internal sealed record AiNativeQuestion(string Id, IReadOnlyList<AiQuestionField> Fields);
internal sealed record AiNativeProgress(string Kind, string Text, string ItemId = "")
{
    internal string ActivityType { get; init; } = "";
    internal string Title { get; init; } = "";
    internal string Status { get; init; } = "";
    internal string Command { get; init; } = "";
    internal string WorkingDirectory { get; init; } = "";
    internal string SubagentId { get; init; } = "";
    internal string ParentItemId { get; init; } = "";
    internal string NativeTurnId { get; init; } = "";
    internal IReadOnlyList<AiActivityFile> Files { get; init; } = [];
    internal IReadOnlyList<AiActivityPlanStep> Plan { get; init; } = [];
}
internal sealed class AiHarnessContext
{
    internal string TaskId { get; init; } = "";
    internal string Workspace { get; init; } = "";
    internal string NativeSessionId { get; init; } = "";
    internal string PermissionMode { get; init; } = "ask";
    internal string Agent { get; init; } = "build";
    internal string TurnId { get; init; } = "";
    internal bool SupportsImageInput { get; init; }
    internal IReadOnlyList<AiAttachmentRecord> Attachments { get; init; } = [];
    internal Action<string> SaveTurnId { get; init; } = _ => { };
    internal Action<string> SaveSessionId { get; init; } = _ => { };
    internal Func<AiNativeApproval, CancellationToken, Task<string>> Approve { get; init; } =
        (_, _) => Task.FromException<string>(new AiToolsException("harness_approval_unavailable"));
    internal Func<AiNativeQuestion, CancellationToken, Task<IReadOnlyDictionary<string, string[]>>> Ask { get; init; } =
        (_, _) => Task.FromException<IReadOnlyDictionary<string, string[]>>(new AiToolsException("harness_question_unavailable"));
    internal Action<AiNativeProgress> Report { get; init; } = _ => { };
}
