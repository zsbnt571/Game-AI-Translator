using System.Collections.ObjectModel;
using System.ComponentModel;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;

namespace ScreenshotTranslationUiTester;

internal sealed partial class WpfSettingsFields
{
    private sealed class NativeDataRow(DataGridViewRow source) : INotifyPropertyChanged
    {
        internal DataGridViewRow Source => source;
        public event PropertyChangedEventHandler? PropertyChanged;
        public object? this[int index]
        {
            get => source.Cells[index].Value;
            set { if (Equals(source.Cells[index].Value, value)) return; source.Cells[index].Value = value; Changed(); }
        }
        internal void Changed() => PropertyChanged?.Invoke(this, new("Item[]"));
    }
    private W.FrameworkElement DataGrid(DataGridView model)
    {
        var view = new WC.DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserReorderColumns = model.AllowUserToOrderColumns, IsReadOnly = model.ReadOnly, EnableRowVirtualization = true, EnableColumnVirtualization = true,
            SelectionMode = model.MultiSelect ? WC.DataGridSelectionMode.Extended : WC.DataGridSelectionMode.Single,
            SelectionUnit = model.SelectionMode is DataGridViewSelectionMode.CellSelect or DataGridViewSelectionMode.RowHeaderSelect ? WC.DataGridSelectionUnit.CellOrRowHeader : WC.DataGridSelectionUnit.FullRow,
            HeadersVisibility = model.ColumnHeadersVisible ? WC.DataGridHeadersVisibility.Column : WC.DataGridHeadersVisibility.None,
            GridLinesVisibility = WC.DataGridGridLinesVisibility.Horizontal, RowHeaderWidth = 0, MinHeight = 120 };
        view.SetResourceReference(WC.Control.BackgroundProperty, "Fusion.Secondary"); view.SetResourceReference(WC.Control.ForegroundProperty, "Fusion.Text"); view.SetResourceReference(WC.Control.BorderBrushProperty, "Fusion.Border");
        var rows = new ObservableCollection<NativeDataRow>(); view.ItemsSource = rows;
        bool syncing = false, pending = false; DataGridViewColumn[] columns = [];
        void Sync()
        {
            if (model.IsDisposed || syncing) return; syncing = true;
            try
            {
                var nextColumns = model.Columns.Cast<DataGridViewColumn>().ToArray();
                if (!columns.SequenceEqual(nextColumns))
                {
                    columns = nextColumns; view.Columns.Clear();
                    foreach (var column in columns)
                    {
                        var binding = new WD.Binding($"[{column.Index}]") { Mode = WD.BindingMode.TwoWay, UpdateSourceTrigger = WD.UpdateSourceTrigger.LostFocus, StringFormat = string.IsNullOrEmpty(column.DefaultCellStyle.Format) ? null : "{0:"+column.DefaultCellStyle.Format+"}" };
                        WC.DataGridColumn native;
                        if (column is DataGridViewCheckBoxColumn) native = new WC.DataGridCheckBoxColumn { Binding = binding };
                        else if (column is DataGridViewComboBoxColumn combo) native = new WC.DataGridComboBoxColumn { SelectedItemBinding = binding, ItemsSource = combo.Items.Cast<object>().ToArray() };
                        else if (column is DataGridViewButtonColumn button)
                        {
                            var factory = new W.FrameworkElementFactory(typeof(WC.Button));
                            if (button.UseColumnTextForButtonValue) factory.SetValue(WC.ContentControl.ContentProperty, button.Text); else factory.SetBinding(WC.ContentControl.ContentProperty, new WD.Binding($"[{column.Index}]"));
                            factory.AddHandler(WC.Button.ClickEvent, new W.RoutedEventHandler((sender, _) => { if (((WC.Button)sender).DataContext is NativeDataRow row) Raise(model, "OnCellContentClick", new DataGridViewCellEventArgs(column.Index, row.Source.Index)); }));
                            native = new WC.DataGridTemplateColumn { CellTemplate = new W.DataTemplate { VisualTree = factory } };
                        }
                        else native = new WC.DataGridTextColumn { Binding = binding };
                        native.Header = column.HeaderText; native.IsReadOnly = column.ReadOnly || model.ReadOnly; native.Visibility = column.Visible ? W.Visibility.Visible : W.Visibility.Collapsed;
                        native.Width = column.InheritedAutoSizeMode == DataGridViewAutoSizeColumnMode.Fill ? new WC.DataGridLength(Math.Max(1, column.FillWeight), WC.DataGridLengthUnitType.Star) : new WC.DataGridLength(Math.Max(35, column.Width * 96d / model.DeviceDpi));
                        view.Columns.Add(native);
                    }
                }
                var nextRows = model.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow && row.Visible).ToArray();
                if (!rows.Select(row => row.Source).SequenceEqual(nextRows)) { rows.Clear(); foreach (var row in nextRows) rows.Add(new(row)); }
                foreach (var row in rows) row.Changed();
                for (int i=0;i<columns.Length;i++) { view.Columns[i].Header=columns[i].HeaderText; view.Columns[i].Visibility=columns[i].Visible?W.Visibility.Visible:W.Visibility.Collapsed; view.Columns[i].IsReadOnly=model.ReadOnly||columns[i].ReadOnly; }
                var selection=rows.Where(row=>row.Source.Selected).ToArray();
                if (view.SelectionMode == WC.DataGridSelectionMode.Single) view.SelectedItem=selection.FirstOrDefault();
                else if (!view.SelectedItems.Cast<NativeDataRow>().SequenceEqual(selection)) { view.SelectedItems.Clear(); foreach(var row in selection) view.SelectedItems.Add(row); }
                view.IsReadOnly = model.ReadOnly;
            }
            finally { syncing = false; }
        }
        void Schedule()
        {
            if (pending || model.IsDisposed) return; pending = true;
            view.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { pending = false; Sync(); }));
        }
        DataGridViewRowsAddedEventHandler added = (_, _) => Schedule(); DataGridViewRowsRemovedEventHandler removed = (_, _) => Schedule();
        DataGridViewColumnEventHandler columnChanged = (_, _) => Schedule(); DataGridViewCellEventHandler valueChanged = (_, e) => { var row = rows.FirstOrDefault(row => row.Source.Index == e.RowIndex); row?.Changed(); };
        EventHandler selectionChanged=(_,_)=>Schedule();
        model.RowsAdded += added; model.RowsRemoved += removed; model.ColumnAdded += columnChanged; model.ColumnRemoved += columnChanged; model.CellValueChanged += valueChanged; model.SelectionChanged+=selectionChanged;
        unsubscribe.Add(() => { model.RowsAdded -= added; model.RowsRemoved -= removed; model.ColumnAdded -= columnChanged; model.ColumnRemoved -= columnChanged; model.CellValueChanged -= valueChanged; model.SelectionChanged-=selectionChanged; });
        int Column(WC.DataGridColumn column) => view.Columns.IndexOf(column);
        view.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            syncing=true;try { model.ClearSelection();
            if (view.SelectedItem is NativeDataRow selected && selected.Source.Cells.Count > 0) model.CurrentCell = selected.Source.Cells[Math.Clamp(view.CurrentColumn is {} column ? Column(column) : 0, 0, selected.Source.Cells.Count - 1)];
            foreach(NativeDataRow row in view.SelectedItems) row.Source.Selected=true;
            } finally { syncing=false; }
        };
        view.BeginningEdit += (_, e) => { if (e.Row.Item is NativeDataRow row) { var args = new DataGridViewCellCancelEventArgs(Column(e.Column), row.Source.Index); Raise(model, "OnCellBeginEdit", args); e.Cancel = args.Cancel; } };
        view.CellEditEnding += (_, e) => { if (e.Row.Item is NativeDataRow row) { int col = Column(e.Column); if (e.EditAction == WC.DataGridEditAction.Commit) { var value = e.EditingElement is WC.TextBox edit ? edit.Text : row[col]; var args = (DataGridViewCellValidatingEventArgs)Activator.CreateInstance(typeof(DataGridViewCellValidatingEventArgs), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, [col, row.Source.Index, value!], null)!; Raise(model, "OnCellValidating", args); e.Cancel = args.Cancel; if (!e.Cancel) { row[col] = value; Raise(model, "OnCellEndEdit", new DataGridViewCellEventArgs(col, row.Source.Index)); } } } };
        view.MouseDoubleClick += (_, _) => { if (view.CurrentItem is NativeDataRow row && view.CurrentColumn is { } column) Raise(model, "OnCellDoubleClick", new DataGridViewCellEventArgs(Column(column), row.Source.Index)); };
        view.PreviewMouseLeftButtonUp += (_, _) => { if (view.CurrentItem is NativeDataRow row && view.CurrentColumn is { } column) Raise(model, "OnCellClick", new DataGridViewCellEventArgs(Column(column), row.Source.Index)); };
        Watch(model, Sync); return view;
    }
}
