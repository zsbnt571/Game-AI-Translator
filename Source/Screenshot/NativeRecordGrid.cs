using System.Collections.ObjectModel;
using System.ComponentModel;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
namespace ScreenshotTranslationUiTester;

internal sealed class NativeRecordCell : INotifyPropertyChanged
{
    private object? value;
    internal NativeRecordRow Row { get; }
    internal NativeRecordCell(NativeRecordRow row) => Row = row;
    internal object? Value { get => value; set { this.value = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
    internal bool Localized;
    public string Text => Localized?UiLanguage.Text(value?.ToString()??""):value?.ToString()??"";
    internal void RefreshLanguage()=>PropertyChanged?.Invoke(this,new(nameof(Text)));
    public event PropertyChangedEventHandler? PropertyChanged;
}
internal sealed class NativeRecordRow
{
    public NativeRecordCell[] Cells { get; }
    internal object? Tag { get; set; }
    internal NativeRecordRow(int count) => Cells = Enumerable.Range(0, count).Select(_ => new NativeRecordCell(this)).ToArray();
}
internal sealed class NativeRecordRows(int columns) : ObservableCollection<NativeRecordRow>
{
    internal HashSet<int> LocalizedColumns {get;}=[];
    internal int Add(params object?[] values) { var row = new NativeRecordRow(columns);for(int i=0;i<columns;i++)row.Cells[i].Localized=LocalizedColumns.Contains(i); for (int i = 0; i < Math.Min(columns, values.Length); i++) row.Cells[i].Value = values[i]; base.Add(row); return Count - 1; }
    internal void Replace(IEnumerable<NativeRecordRow> rows)
    {
        // Publish one complete snapshot. A reset avoids thousands of per-row
        // collection notifications and preserves the prior view until ready.
        CheckReentrancy();Items.Clear();foreach(var row in rows)Items.Add(row);
        OnPropertyChanged(new(nameof(Count)));OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
internal sealed class NativeRecordGrid
{
    internal WC.DataGrid View { get; }
    internal NativeRecordRows Rows { get; }
    internal NativeRecordRow? CurrentRow => View.SelectedItem as NativeRecordRow;
    internal NativeRecordCell? CurrentCell { set => View.SelectedItem = value?.Row; }
    internal IReadOnlyList<NativeRecordRow> SelectedRows => View.SelectedItems.Cast<NativeRecordRow>().ToArray();
    internal event EventHandler? SelectionChanged;
    internal void LocalizeColumns(params int[] columns)
    {
        Rows.LocalizedColumns.UnionWith(columns);
        foreach(var row in Rows)for(int i=0;i<row.Cells.Length;i++){row.Cells[i].Localized=Rows.LocalizedColumns.Contains(i);row.Cells[i].RefreshLanguage();}
    }
    internal NativeRecordGrid(params (string Label, double Weight)[] columns)
    {
        Rows = new(columns.Length);
        View = new() { ItemsSource = Rows, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = WC.DataGridSelectionMode.Single, SelectionUnit = WC.DataGridSelectionUnit.FullRow, EnableRowVirtualization = true, EnableColumnVirtualization = true, RowHeight = 42, HeadersVisibility = WC.DataGridHeadersVisibility.Column, GridLinesVisibility = WC.DataGridGridLinesVisibility.Horizontal, RowHeaderWidth = 0, MinHeight = 80 };
        View.SetResourceReference(WC.Control.BackgroundProperty, "Fusion.Secondary"); View.SetResourceReference(WC.Control.ForegroundProperty, "Fusion.Text"); View.SetResourceReference(WC.Control.BorderBrushProperty, "Fusion.Border");
        var textStyle = new W.Style(typeof(WC.TextBlock)); textStyle.Setters.Add(new W.Setter(WC.TextBlock.TextTrimmingProperty, W.TextTrimming.CharacterEllipsis)); textStyle.Setters.Add(new W.Setter(W.FrameworkElement.VerticalAlignmentProperty, W.VerticalAlignment.Center)); textStyle.Setters.Add(new W.Setter(W.FrameworkElement.MarginProperty, new W.Thickness(6, 0, 6, 0)));
        for (int i = 0; i < columns.Length; i++) View.Columns.Add(new WC.DataGridTextColumn { Header = UiLanguage.Text(columns[i].Label), Binding = new WD.Binding($"Cells[{i}].Text"), Width = new(columns[i].Weight, WC.DataGridLengthUnitType.Star), ElementStyle = textStyle });
        View.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        void RefreshLanguage(object? sender,EventArgs e)
        {
            foreach(var column in View.Columns)WpfUiLanguage.Property(column,WC.DataGridColumn.HeaderProperty);
            foreach(var row in Rows)foreach(var cell in row.Cells)if(cell.Localized)cell.RefreshLanguage();
        }
        bool listening=false;
        View.Loaded+=(_,_)=>{if(!listening){listening=true;UiLanguage.LanguageChanged+=RefreshLanguage;}RefreshLanguage(null,EventArgs.Empty);};
        View.Unloaded+=(_,_)=>{if(listening){listening=false;UiLanguage.LanguageChanged-=RefreshLanguage;}};
    }
}
