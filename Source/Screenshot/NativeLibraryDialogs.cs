using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

internal sealed class NativeLibraryNoteDialog : NativeDialogWindow
{
    internal WC.TextBox OriginalName { get; }
    internal WC.TextBox ChineseName { get; }
    internal ApplicationLanguage NameLanguage { get; } = UiLanguage.Current;
    internal WC.TextBox Note { get; }
    internal WC.TextBox Tags { get; }
    internal WC.ComboBox NoteColor { get; }
    internal WC.CheckBox Bookmark { get; }
    private readonly CancellationTokenSource lifetime;
    internal NativeLibraryNoteDialog(RecentGame game, LibraryAnnotation annotation, Func<string, CancellationToken, Task<string>> translate, CancellationToken cancellation) : base("名称、标签与便签", 600, 620)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        OriginalName = NativeDialogControls.Input(game.Name); OriginalName.MaxLength = 160;
        ChineseName = NativeDialogControls.Input(annotation.NameFor(NameLanguage).Text); ChineseName.MaxLength = 120;
        Note = NativeDialogControls.Input(annotation.Note); Note.MaxLength = 120;
        Tags = NativeDialogControls.Input(annotation.Tags); Tags.MaxLength = 160;
        Bookmark = new() { Content = UiLanguage.Text("在卡片上放置书签"), IsChecked = annotation.Bookmark, VerticalAlignment = W.VerticalAlignment.Center, Margin = new(0, 0, 16, 12) };
        NoteColor = NativeDialogControls.Choice(["浅黄", "淡紫", "薄荷"], annotation.Color == "lavender" ? 1 : annotation.Color == "mint" ? 2 : 0); NoteColor.MinWidth = 120;
        var feedback = NativeDialogControls.Text(""); var save = ResultButton("保存", System.Windows.Forms.DialogResult.OK, true);
        var generate = NativeDialogControls.Button("生成译名", () => { });
        generate.Click += async (_, _) =>
        {
            generate.IsEnabled = save.IsEnabled = false; ChineseName.IsReadOnly = true; feedback.Text = UiLanguage.Text("正在用当前游戏翻译方案生成…");
            try { var value = await translate(OriginalName.Text.Trim(), lifetime.Token); if (!IsDisposed && !lifetime.IsCancellationRequested) { ChineseName.Text = LibraryNameTranslation.Validate(value,NameLanguage); feedback.Text = UiLanguage.Text("机器译名，可修改后保存。"); } }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) feedback.Text = SafeDiagnosticOutput.ExceptionSummary(ex); }
            finally { if (!IsDisposed) { generate.IsEnabled = save.IsEnabled = true; ChineseName.IsReadOnly = false; } }
        };
        Content = NativeDialogControls.Layout(NativeDialogControls.Column(NativeDialogControls.Text("游戏名称"), OriginalName, NativeDialogControls.Text("本地化名称"), ChineseName, NativeDialogControls.Row(generate), feedback, NativeDialogControls.Text("便签"), Note, NativeDialogControls.Text("标签"), Tags, NativeDialogControls.Row(Bookmark, NoteColor)), NativeDialogControls.Row(save, ResultButton("取消", System.Windows.Forms.DialogResult.Cancel)));
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }
}

public sealed partial class MainForm
{
    private string? AskNativeLibraryName(string title, string initial, string label, int maxLength = 64)
    {
        using var dialog = new NativeDialogWindow(title, 460, 230); var input = NativeDialogControls.Input(initial); input.MaxLength = maxLength;
        var save = dialog.ResultButton("保存", System.Windows.Forms.DialogResult.OK, true); save.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        input.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        dialog.Content = NativeDialogControls.Layout(NativeDialogControls.Column(NativeDialogControls.Text(label), input), NativeDialogControls.Row(save, dialog.ResultButton("取消", System.Windows.Forms.DialogResult.Cancel)));
        dialog.ContentRendered += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog(this) == System.Windows.Forms.DialogResult.OK ? input.Text.Trim() : null;
    }
    private async Task EditNativeLibraryNoteAsync(RecentGameStore store, RecentGame record, GameInfo game, LibraryAnnotation note)
    {
        var language=UiLanguage.Current;
        using var dialog = new NativeLibraryNoteDialog(record, note, async (source, cancellation) => (await _translationService.TranslatePlainAsync(source, LibraryNameTranslation.Settings(_fusion!.GameSettings(UiSettings, game.ExePath),language), cancellation)).Text, _desktopLifetime.Token);
        if (dialog.ShowDialog(this) != System.Windows.Forms.DialogResult.OK) return;
        try
        {
            if (dialog.OriginalName.Text.Trim() != record.Name)
            {
                store.SetDisplayName(record.Id, dialog.OriginalName.Text, true);
                if (SameGame(game.ExePath, _selectedGame?.ExePath)) { _selectedGame = game with { Name = dialog.OriginalName.Text.Trim() }; GameTitleText = dialog.OriginalName.Text.Trim(); }
            }
            var value = _libraryMetadata!.Get(record.Id).WithName(dialog.NameLanguage,new(dialog.ChineseName.Text.Trim(),"manual")) with { Note = dialog.Note.Text.Trim(), Color = dialog.NoteColor.SelectedIndex == 1 ? "lavender" : dialog.NoteColor.SelectedIndex == 2 ? "mint" : "yellow", Bookmark = dialog.Bookmark.IsChecked == true, Tags = dialog.Tags.Text.Trim() };
            _libraryMetadata.Save(record.Id, value); ScheduleLibraryRefresh();
        }
        catch (Exception ex) { ShowFusionError(ex); }
        await Task.CompletedTask;
    }
    private async Task EditNativeLibraryRetentionAsync(RecentGameStore store)
    {
        using var dialog = new NativeDialogWindow("最近玩过", 460, 260);
        var number = new NativeRpgNumber { Minimum = 1, Maximum = 1000, Value = store.RecentLimit }; number.View.Width = 160;
        dialog.Content = NativeDialogControls.Layout(NativeDialogControls.Column(NativeDialogControls.Text("保留最近多少项？"), number.View, NativeDialogControls.Text("只调整最近列表，游戏库中的记录仍保留。")), NativeDialogControls.Row(dialog.ResultButton("保存", System.Windows.Forms.DialogResult.OK, true), dialog.ResultButton("取消", System.Windows.Forms.DialogResult.Cancel)));
        if (dialog.ShowDialog(this) != System.Windows.Forms.DialogResult.OK) return;
        int limit = (int)number.Value; await Task.Run(() => store.SetRecentLimit(limit));
    }
}
