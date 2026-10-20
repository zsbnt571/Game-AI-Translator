using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

internal sealed class NativeRpgCell(NativeRpgGridRow row)
{
    private object? value;
    internal object? Value { get => value; set { if (Equals(this.value, value)) return; this.value = value; row.Changed(); } }
    internal string ToolTipText { get; set; } = "";
    internal NativeRpgGridRow Row => row;
}
internal sealed class NativeRpgGridRow : INotifyPropertyChanged
{
    private readonly NativeRpgGrid owner;
    private object? tag;
    internal NativeRpgGridRow(NativeRpgGrid owner) { this.owner = owner; Cells = Enumerable.Range(0, 4).Select(_ => new NativeRpgCell(this)).ToArray(); }
    internal NativeRpgCell[] Cells { get; }
    internal object? Tag { get => tag; set { tag = value; Changed(); } }
    internal bool Selected { get => ReferenceEquals(owner.Grid.SelectedItem, this); set { if (value) owner.Grid.SelectedItem = this; else if (Selected) owner.Grid.SelectedItem = null; } }
    public string Title => Cells[1].Value?.ToString() is { Length: > 0 } chinese && chinese != "—" ? chinese : Cells[0].Value?.ToString() ?? "";
    public string Original => Cells[1].Value?.ToString() is { Length: > 0 } chinese && chinese != "—" ? Cells[0].Value?.ToString() ?? "" : ReadableModificationKey.Format(Cells[0].ToolTipText);
    public string Value => Tag is JsonElement item&&item.TryGetProperty("type",out var type)&&type.GetString()=="string"
        ?Cells[2].Value?.ToString()??"":UiLanguage.Text(Cells[2].Value?.ToString()??"");
    public string Lock => UiLanguage.Text(Cells[3].Value?.ToString() ?? "");
    public WM.ImageSource? Icon
    {
        get
        {
            if (owner.IconAtlas is not { } atlas || Tag is not JsonElement item || !item.TryGetProperty("icon", out var icon) || !icon.TryGetInt32(out int index) || index < 0 || index >= 10000 || (index / 16 + 1) * 32 > atlas.Height) return null;
            var crop = new System.Windows.Media.Imaging.CroppedBitmap(WpfDrawing.Image(atlas), new(index % 16 * 32, index / 16 * 32, 32, 32)); crop.Freeze(); return crop;
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Changed() => PropertyChanged?.Invoke(this, new(null));
}
internal sealed class NativeRpgGridRows(NativeRpgGrid owner) : ObservableCollection<NativeRpgGridRow>
{
    internal int Add(params object?[] values) { var row = new NativeRpgGridRow(owner); for (int i = 0; i < Math.Min(4, values.Length); i++) row.Cells[i].Value = values[i]; base.Add(row); return Count - 1; }
}
internal sealed class NativeRpgGrid : NativeRpgElement
{
    internal WC.DataGrid Grid => (WC.DataGrid)View;
    internal NativeRpgGridRows Rows { get; }
    internal Bitmap? IconAtlas;
    internal NativeRpgGrid() : base(new WC.DataGrid { Height = 360, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, SelectionMode = WC.DataGridSelectionMode.Single, SelectionUnit = WC.DataGridSelectionUnit.FullRow, RowHeight = 48, HeadersVisibility = WC.DataGridHeadersVisibility.Column, RowHeaderWidth = 0, EnableRowVirtualization = true, EnableColumnVirtualization = true, GridLinesVisibility = WC.DataGridGridLinesVisibility.Horizontal })
    {
        Rows = new(this); Grid.ItemsSource = Rows;
        Grid.SetResourceReference(WC.Control.BackgroundProperty, "Fusion.Secondary"); Grid.SetResourceReference(WC.Control.ForegroundProperty, "Fusion.Text"); Grid.SetResourceReference(WC.Control.BorderBrushProperty, "Fusion.Border");
        var row = new W.FrameworkElementFactory(typeof(WC.DockPanel));
        var image = new W.FrameworkElementFactory(typeof(WC.Image)); image.SetBinding(WC.Image.SourceProperty, new WD.Binding(nameof(NativeRpgGridRow.Icon))); image.SetValue(W.FrameworkElement.WidthProperty, 28d); image.SetValue(W.FrameworkElement.HeightProperty, 28d); image.SetValue(W.FrameworkElement.MarginProperty, new W.Thickness(4, 0, 8, 0)); row.AppendChild(image);
        var labels = new W.FrameworkElementFactory(typeof(WC.StackPanel)); labels.SetValue(W.FrameworkElement.VerticalAlignmentProperty, W.VerticalAlignment.Center);
        foreach (string key in new[] { "Title", "Original" }) { var label = new W.FrameworkElementFactory(typeof(WC.TextBlock)); label.SetBinding(WC.TextBlock.TextProperty, new WD.Binding(key)); label.SetValue(WC.TextBlock.TextTrimmingProperty, W.TextTrimming.CharacterEllipsis); if (key == "Title") label.SetValue(WC.TextBlock.FontWeightProperty, W.FontWeights.SemiBold); else label.SetValue(WC.TextBlock.FontSizeProperty, 11d); labels.AppendChild(label); }
        row.AppendChild(labels);
        Grid.Columns.Add(new WC.DataGridTemplateColumn { Header = UiLanguage.Text("名称 / Name"), CellTemplate = new W.DataTemplate { VisualTree = row }, Width = new(70, WC.DataGridLengthUnitType.Star) });
        Grid.Columns.Add(new WC.DataGridTextColumn { Header = UiLanguage.Text("当前值"), Binding = new WD.Binding("Value"), Width = new(18, WC.DataGridLengthUnitType.Star) });
        Grid.Columns.Add(new WC.DataGridTextColumn { Header = UiLanguage.Text("锁定"), Binding = new WD.Binding("Lock"), Width = new(12, WC.DataGridLengthUnitType.Star) });
        foreach(var column in Grid.Columns)WpfUiLanguage.Property(column,WC.DataGridColumn.HeaderProperty);
        WpfModificationLayout.Table(Grid);
        Grid.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        Grid.AddHandler(WC.ScrollViewer.ScrollChangedEvent, new WC.ScrollChangedEventHandler((_, _) => Scroll?.Invoke(this, EventArgs.Empty)));
        Grid.PreviewMouseLeftButtonUp += (_, e) => { if (Grid.CurrentColumn == Grid.Columns[2] && Grid.SelectedItem is NativeRpgGridRow selected) LockClicked?.Invoke(selected); };
    }
    internal IReadOnlyList<NativeRpgGridRow> SelectedRows => Grid.SelectedItems.Cast<NativeRpgGridRow>().ToArray();
    internal NativeRpgCell? CurrentCell { set { if (value is not null) Grid.SelectedItem = value.Row; } }
    internal int FirstDisplayedScrollingRowIndex { get => (int)(FindScroll(Grid)?.VerticalOffset ?? 0); set { if (Rows.Count > 0) Grid.ScrollIntoView(Rows[Math.Clamp(value, 0, Rows.Count - 1)]); } }
    internal int DisplayedRowCount(bool partial) => Math.Max(1, (int)(View.ActualHeight / WpfModificationLayout.RowHeight));
    internal void ClearSelection() => Grid.SelectedItem = null;
    internal void RefreshLanguage(){foreach(var row in Rows)row.Changed();}
    internal event EventHandler? SelectionChanged, Scroll;
    internal event Action<NativeRpgGridRow>? LockClicked;
    private static WC.ScrollViewer? FindScroll(W.DependencyObject node) { if (node is WC.ScrollViewer scroll) return scroll; for (int i = 0; i < WM.VisualTreeHelper.GetChildrenCount(node); i++) if (FindScroll(WM.VisualTreeHelper.GetChild(node, i)) is { } found) return found; return null; }
}
internal sealed class NativeRpgIcon : NativeRpgElement
{
    internal NativeRpgIcon() : base(new WC.Image { Width = 48, Height = 48, Margin = new(0, 0, 12, 0) }) { }
    internal Bitmap? Atlas;
    internal int Index = -1;
    internal override void Invalidate() { if (Atlas is { } atlas && Index >= 0 && (Index / 16 + 1) * 32 <= atlas.Height) { var image = new System.Windows.Media.Imaging.CroppedBitmap(WpfDrawing.Image(atlas), new(Index % 16 * 32, Index / 16 * 32, 32, 32)); image.Freeze(); ((WC.Image)View).Source = image; } else ((WC.Image)View).Source = null; }
}
