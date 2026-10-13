using System.Text.Json;
namespace ScreenshotTranslationUiTester;

internal sealed record ScreenshotWorkspaceLayout
{
    public int Version { get; init; } = 1;
    public bool HistoryExpanded { get; init; }
    public double SplitPercent { get; init; } = 50;
    public string SelectedRecord { get; init; } = "";
    public ScreenshotPaneViewState Left { get; init; } = new(ScreenshotPaneView.OriginalImage);
    public ScreenshotPaneViewState Right { get; init; } = new(ScreenshotPaneView.TranslatedImage);
    internal ScreenshotWorkspaceLayout Normalize()
    {
        static ScreenshotPaneViewState Pane(ScreenshotPaneViewState? value, ScreenshotPaneView fallback)
        {
            value ??= new(fallback);
            return value with
            {
                View = Enum.IsDefined(value.View) ? value.View : fallback,
                FontSize = Math.Clamp(value.FontSize, 12, 28),
                FitMode = Enum.IsDefined(value.FitMode) ? value.FitMode : ScreenshotPaneFitMode.Window,
                Zoom = float.IsFinite(value.Zoom) ? Math.Clamp(value.Zoom, .2f, 5f) : 1,
                ScrollX = Math.Clamp(value.ScrollX, 0, 1000000), ScrollY = Math.Clamp(value.ScrollY, 0, 1000000),
                PanX = Math.Clamp(value.PanX, -1000000, 1000000), PanY = Math.Clamp(value.PanY, -1000000, 1000000)
            };
        }
        return this with { Version = 1, SplitPercent = double.IsFinite(SplitPercent) ? Math.Clamp(SplitPercent, 25, 75) : 50, SelectedRecord = ScreenshotRebuildHistory.NormalizeKey(SelectedRecord), Left = Pane(Left, ScreenshotPaneView.OriginalImage), Right = Pane(Right, ScreenshotPaneView.TranslatedImage) };
    }
}
internal static class ScreenshotWorkspaceLayoutStore
{
    internal static ScreenshotWorkspaceLayout Load(string path)
    {
        if (!File.Exists(path)) return new();
        try { return (JsonSerializer.Deserialize<ScreenshotWorkspaceLayout>(File.ReadAllText(path)) ?? new()).Normalize(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { AppLog.Write("screenshot-layout", SafeDiagnosticOutput.ExceptionSummary(ex)); return new(); }
    }
    internal static void Save(string path, ScreenshotWorkspaceLayout value)
        => PortableDataStorage.WriteJson(path, value.Normalize());
}

