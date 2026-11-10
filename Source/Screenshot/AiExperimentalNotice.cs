using System.Text.Json;
using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

// This preference only controls introductory UI; it grants no execution permissions.
internal sealed class AiExperimentalNoticeState
{
    private readonly string path;
    private bool acceptedThisRun;
    private bool dismissed;
    internal AiExperimentalNoticeState(string dataRoot)
    {
        path = Path.Combine(dataRoot, "ai-experimental-notice.json");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length <= 8192)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                dismissed = root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("Schema", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version == 1 &&
                    root.TryGetProperty("DoNotRemind", out var preference) && preference.ValueKind == JsonValueKind.True;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    internal bool ShouldPrompt => !acceptedThisRun && !dismissed;
    internal void Accept(bool doNotRemind)
    {
        if (doNotRemind)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, "{\n  \"Schema\": 1,\n  \"DoNotRemind\": true\n}\n");
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            dismissed = true;
        }
        acceptedThisRun = true;
    }
}

internal sealed class AiExperimentalNoticeDialog : NativeDialogWindow
{
    internal AiExperimentalNoticeDialog(AiExperimentalNoticeState preference)
        : base(AiUiText.Text("ExperimentalNoticeTitle"), 600, 390)
    {
        var remember = new WC.CheckBox { Content = AiUiText.Text("ExperimentalNoticeRemember"), Margin = new(0, 6, 0, 14) };
        W.Automation.AutomationProperties.SetAutomationId(remember, "AiExperimentalDoNotRemind");
        var error = NativeDialogControls.Text(""); error.Visibility = W.Visibility.Collapsed;
        var body = NativeDialogControls.Column(NativeDialogControls.Text(AiUiText.Text("ExperimentalNoticeBody")), remember, error);
        var close = ResultButton(AiUiText.Text("ExperimentalNoticeClose"), System.Windows.Forms.DialogResult.Cancel);
        close.IsCancel = true; W.Automation.AutomationProperties.SetAutomationId(close, "AiExperimentalClose");
        var enter = NativeDialogControls.Button(AiUiText.Text("ExperimentalNoticeContinue"), () =>
        {
            try { preference.Accept(remember.IsChecked == true); Finish(System.Windows.Forms.DialogResult.OK); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.Text = AiUiText.Text("ExperimentalNoticeSaveFailed"); error.Visibility = W.Visibility.Visible;
            }
        }, true);
        enter.IsDefault = true; W.Automation.AutomationProperties.SetAutomationId(enter, "AiExperimentalContinue");
        Content = NativeDialogControls.Layout(body, NativeDialogControls.Row(close, enter));
    }
}

public sealed partial class MainForm
{
    private AiExperimentalNoticeState? _aiExperimentalNotice;
    private bool ConfirmAiExperimentalEntry()
    {
        _aiExperimentalNotice ??= new(AppDataPaths.Root);
        if (!_aiExperimentalNotice.ShouldPrompt) return true;
        using var dialog = new AiExperimentalNoticeDialog(_aiExperimentalNotice);
        return dialog.ShowDialog(this) == DialogResult.OK;
    }
}
