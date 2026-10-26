using W = System.Windows;
using WC = System.Windows.Controls;
namespace ScreenshotTranslationUiTester;

internal sealed class GameFontSettingsDialog : NativeDialogWindow
{
    private readonly GameInfo game;
    private readonly Func<CancellationToken, Task<string>>? apply;
    private readonly CancellationTokenSource applyLifetime = new();
    private bool applying, refreshingSelection;
    private readonly WC.ComboBox mode, family;
    private readonly WC.TextBox path = NativeDialogControls.Input();
    private readonly WC.TextBox previewText = NativeDialogControls.Input();
    private readonly WC.TextBlock notice = NativeDialogControls.Text("");
    private readonly WC.TextBlock sample = new() { TextWrapping = W.TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    internal GameFontSettingsDialog(GameInfo game, Func<CancellationToken, Task<string>>? apply = null, string? targetLanguage = null) : base("游戏内字体", 640, 650)
    {
        this.game = game;
        this.apply = apply;
        Closed += (_, _) => { applyLifetime.Cancel(); applyLifetime.Dispose(); };
        MinWidth = 480; MinHeight = 420;
        GameFontPreference current;
        string loadError = "";
        try { current = GameFontSettings.Read(game.ExePath); }
        catch (Exception ex) { current = new(); loadError = SafeDiagnosticOutput.ExceptionSummary(ex); }
        var body = new WC.StackPanel { Margin = new(20) };
        body.Children.Add(new WC.TextBlock { Text = game.Name, TextWrapping = W.TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        body.Children.Add(NativeDialogControls.Text("默认保持当前贴合游戏原版的字体适配；只有选择自定义才会替换。"));
        mode = NativeDialogControls.Choice(new[] { GameFontSettings.DefaultLabel, "自定义字体" }, current.Custom ? 1 : 0);
        NativeAppearanceDialog.Add(body, "字体方式", mode);
        var names = GameFontSettings.Families();
        family = NativeDialogControls.Choice(names, Math.Max(0, Array.FindIndex(names, n => n.Equals(current.Family, StringComparison.OrdinalIgnoreCase))));
        family.ItemTemplate = null;
        NativeAppearanceDialog.Add(body, "已安装字体", family);
        path.Text = current.Path; path.IsReadOnly = true;
        var choose = NativeDialogControls.Button("选择字体文件", ChooseFile);
        var fileRow = NativeDialogControls.Column(path, choose);
        if (!GameFontSettings.SystemFamilyOnly(game)) NativeAppearanceDialog.Add(body, "字体文件", fileRow);
        previewText.Text = GameFontPreview.Sample(targetLanguage);
        previewText.MaxLength = 4096; previewText.AcceptsReturn = true;
        previewText.TextWrapping = W.TextWrapping.Wrap; previewText.MaxHeight = 100;
        previewText.VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto;
        NativeAppearanceDialog.Add(body, "预览文字", previewText);
        body.Children.Add(NativeDialogControls.Text("可输入任何语言的文字检查字体；预览文字不会发送给翻译服务。"));
        sample.FontSize = 24; sample.Text = previewText.Text;
        sample.FlowDirection = GameFontPreview.RightToLeft(sample.Text) ? W.FlowDirection.RightToLeft : W.FlowDirection.LeftToRight;
        body.Children.Add(sample); body.Children.Add(notice);
        body.Children.Add(NativeDialogControls.Text(GameFontSettings.RestartHint));
        if (GameFontSettings.SystemFamilyOnly(game)) body.Children.Add(NativeDialogControls.Text("此引擎支持已安装字体，暂不直接加载外部字体文件。"));
        var save = NativeDialogControls.Button("保存并应用", Save, true);
        var cancel = NativeDialogControls.Button("关闭", () => Close());
        body.Children.Add(NativeDialogControls.Row(save, cancel));
        Content = new WC.ScrollViewer { Content = body, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
        mode.SelectionChanged += (_, _) => EnabledState();
        previewText.TextChanged += (_, _) =>
        {
            sample.Text = previewText.Text;
            sample.FlowDirection = GameFontPreview.RightToLeft(sample.Text) ? W.FlowDirection.RightToLeft : W.FlowDirection.LeftToRight;
            if (mode.SelectedIndex == 1) try { Preview(family.SelectedItem?.ToString() ?? ""); }
                catch (Exception ex) { notice.Text = UiLanguage.Text(SafeDiagnosticOutput.ExceptionSummary(ex)); }
        };
        family.SelectionChanged += (_, _) =>
        {
            if (refreshingSelection || mode.SelectedIndex != 1) return;
            try
            {
                string selected = family.SelectedItem?.ToString() ?? "";
                if (!GameFontSettings.SystemFamilyOnly(game)) path.Text = GameFontSettings.ResolveFamilyFile(selected);
                Preview(selected);
            }
            catch (Exception ex) { path.Text = ""; notice.Text = UiLanguage.Text(SafeDiagnosticOutput.ExceptionSummary(ex)); }
        };
        void EnabledState()
        {
            bool custom = mode.SelectedIndex == 1;
            if (!custom) { sample.FontFamily = FontFamily; notice.Text = UiLanguage.Text("默认预览使用界面字体；游戏继续使用现有字体适配。"); }
            family.IsEnabled = custom;
            fileRow.Visibility = GameFontSettings.SystemFamilyOnly(game) ? W.Visibility.Collapsed : W.Visibility.Visible;
            choose.IsEnabled = custom;
            if (custom && path.Text.Length == 0 && family.SelectedItem is string selected)
                try { if (!GameFontSettings.SystemFamilyOnly(game)) path.Text = GameFontSettings.ResolveFamilyFile(selected); Preview(selected); }
                catch (Exception ex) { notice.Text = UiLanguage.Text(SafeDiagnosticOutput.ExceptionSummary(ex)); }
        }
        EnabledState();
        if (current.Custom) try { Preview(current.Family); } catch (Exception ex) { loadError = SafeDiagnosticOutput.ExceptionSummary(ex); }
        if (loadError.Length > 0) notice.Text = UiLanguage.Text(loadError);
    }
    private void Preview(string name)
    {
        if (!GameFontSettings.SystemFamilyOnly(game))
        {
            if (!File.Exists(path.Text)) throw new IOException("自定义字体文件缺失或已改变，请重新选择或恢复默认。");
            var info = GameFontSettings.Inspect(path.Text, previewText.Text); name = info.Family; notice.Text = info.Coverage;
            sample.FontFamily = new System.Windows.Media.FontFamily(new Uri(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path.Text))! + System.IO.Path.DirectorySeparatorChar), "./" + Uri.EscapeDataString(System.IO.Path.GetFileName(path.Text)) + "#" + name);
        }
        else
        {
            sample.FontFamily = new System.Windows.Media.FontFamily(name);
            notice.Text = GameFontPreview.Coverage(GameFontSettings.CheckInstalledFamily(name, previewText.Text));
        }
    }
    private void ReloadSelection(GameFontPreference preference)
    {
        refreshingSelection = true;
        try
        {
            path.Text = preference.Path;
            family.SelectedItem = family.Items.Cast<object>().FirstOrDefault(value => string.Equals(value.ToString(), preference.Family, StringComparison.OrdinalIgnoreCase));
            mode.SelectedIndex = preference.Custom ? 1 : 0;
            if (preference.Custom) Preview(preference.Family);
        }
        finally { refreshingSelection = false; }
    }
    private void ChooseFile()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = UiLanguage.Text("选择字体文件"), Filter = UiLanguage.Text("字体文件") + " (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        try { var info = GameFontSettings.Inspect(picker.FileName); path.Text = picker.FileName; Preview(info.Family); }
        catch (Exception ex) { notice.Text = UiLanguage.Text(SafeDiagnosticOutput.ExceptionSummary(ex)); }
    }
    private async void Save()
    {
        if (applying) return;
        applying = true;
        GameFontPreferenceChange? saved = null;
        try
        {
            saved = GameFontSettings.SaveChange(game, mode.SelectedIndex == 1, family.SelectedItem?.ToString() ?? "", path.Text);
            notice.Text = UiLanguage.Text("正在等待游戏应用字体；若游戏暂停，请切回游戏。");
            string result = apply is null ? "字体选择已保存，下次开启翻译时应用。" : await apply(applyLifetime.Token);
            if (!IsDisposed) notice.Text = UiLanguage.Text(result);
        }
        catch (GameFontPreferenceRejectedException ex) when (saved is not null)
        {
            try
            {
                bool restored = GameFontSettings.TryRestore(game.ExePath, saved.Current, saved.Previous);
                if (!IsDisposed)
                {
                    try { ReloadSelection(GameFontSettings.Read(game.ExePath)); } catch { /* Keep the definite runtime failure visible even if preview is unavailable. */ }
                    notice.Text = UiLanguage.Text(restored ? "游戏未接受所选字体，已恢复之前的字体选择。" : "字体选择已在其他操作中改变，已保留当前选择。")
                        + " " + UiLanguage.Text(ex.Message);
                }
            }
            catch (Exception restoreError)
            {
                if (!IsDisposed) notice.Text = UiLanguage.Text("字体选择已保存，但尚未确认游戏应用：")
                    + UiLanguage.Text(ex.Message) + " " + UiLanguage.Text(SafeDiagnosticOutput.ExceptionSummary(restoreError));
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed) notice.Text = (saved is not null ? UiLanguage.Text("字体选择已保存，但尚未确认游戏应用：") : "")
                + UiLanguage.Text(SafeDiagnosticOutput.ExceptionSummary(ex));
        }
        finally { applying = false; }
    }
}

public sealed partial class MainForm
{
    private void EditGameFont()
    {
        if (_selectedGame is not { } game || !GameFontSettings.Supports(game)) return;
        using var dialog = new GameFontSettingsDialog(game, async cancellation =>
        {
            var data = new[] { _dataSessions, _scriptTranslationSessions, _embeddedTranslationSessions }
                .Select(sessions => sessions.TryGetValue(game.ExePath, out var found) ? found : null)
                .FirstOrDefault(found => found?.Connection.Connected == true);
            if (data is null) return "字体选择已保存；游戏连接尚未就绪，尚未实时应用。";
            if (data.Translation is not { Enabled: true, RestorePending: false })
                return "字体选择已保存，下次开启翻译时应用。";
            var applied = await GameFontLive.ApplyWhenAsync(game, data.Connection,
                () => data.Translation is { Enabled: true, RestorePending: false }, cancellation);
            if (applied is not { } result) return "字体选择已保存，下次开启翻译时应用。";
            if (result.TryGetProperty("deferredComponents", out var deferred) && deferred.ValueKind == System.Text.Json.JsonValueKind.Number && deferred.TryGetInt32(out int count) && count > 0)
                return "游戏已接收字体选择；部分文字暂不支持所选字体，将保留当前翻译字体。";
            if (result.TryGetProperty("messageRedrawPending", out var pending) && pending.ValueKind == System.Text.Json.JsonValueKind.True)
                return "字体选择已保存并被游戏接收；当前文字需等游戏刷新或翻页后更新。";
            bool active = result.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == System.Text.Json.JsonValueKind.True;
            return active ? "游戏已接收字体选择，正在重绘；可直接查看游戏，也可恢复默认。" : "游戏已接收字体选择，开启翻译后应用。";
        }, EmbeddedSettings.TargetLanguage);
        dialog.ShowDialog(this);
        RenderGameSessionState();
    }
}
