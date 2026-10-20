using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

internal sealed partial class WpfSettingsFields
{
    internal static readonly HashSet<string> UnsupportedTypes = new(StringComparer.Ordinal);
    private static void Raise(Control model, string method, EventArgs args)
    {
        if (!WpfSettingsModel.CanDispatch(model)) return;
        var member = model.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic, null, [args.GetType()], null);
        try { member?.Invoke(model, [args]); }
        catch (TargetInvocationException error) when (error.InnerException is { } cause) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw(); }
    }
    private W.FrameworkElement Native(Control model)
    {
        NativeFields++;
        return model switch
        {
            RadioButton radio => Radio(radio), RichTextBox text => RichText(text),
            DataGridView table => DataGrid(table), TreeView tree => Tree(tree),
            CheckedListBox list => CheckedList(list), ListBox list => List(list), ListView list => ListView(list),
            TabControl tabs => Tabs(tabs), SplitContainer split => Split(split), PictureBox image => Picture(image),
            TrackBar slider => Slider(slider), ProgressBar progress => Progress(progress),
            DateTimePicker date => Date(date), LinkLabel link => Link(link),
            RpgMapGrid map => map.CreateWpfView(), GameEventCommandList commands => commands.CreateWpfView(),
            ArtworkCropPreview crop => crop.CreateWpfView(), GameDataIcon icon => icon.CreateWpfView(),
            GameLibraryGrid library => library.NativeView,
            Control container when container.Controls.Count > 0 => NativeLayout(container),
            _ => Missing(model)
        };
    }
    private static W.FrameworkElement Missing(Control model)
    {
        UnsupportedTypes.Add(model.GetType().FullName ?? model.GetType().Name);
        // Fail visibly during migration/acceptance instead of silently dropping
        // an action or embedding a legacy-rendered bitmap behind a WPF shell.
        return new WC.TextBlock { Text = "尚未迁移：" + model.GetType().Name, TextWrapping = W.TextWrapping.Wrap, Foreground = WM.Brushes.IndianRed };
    }
    private W.FrameworkElement Shortcut(Button model)
    {
        var view = (WC.Button)Button(model); view.MinWidth = model is GameShortcutButton ? 220 : 190;
        view.PreviewKeyDown += (_, e) =>
        {
            if (model is not GameShortcutButton { Capturing: true } shortcut) return;
            var key = e.Key == WI.Key.System ? e.SystemKey : e.Key;
            var data = (Keys)WI.KeyInterop.VirtualKeyFromKey(key);
            if (WI.Keyboard.Modifiers.HasFlag(WI.ModifierKeys.Control)) data |= Keys.Control;
            if (WI.Keyboard.Modifiers.HasFlag(WI.ModifierKeys.Alt)) data |= Keys.Alt;
            if (WI.Keyboard.Modifiers.HasFlag(WI.ModifierKeys.Shift)) data |= Keys.Shift;
            shortcut.ApplyKey(data); e.Handled = true;
        };
        return view;
    }
    private W.FrameworkElement Radio(RadioButton model)
    {
        var view = new WC.RadioButton { MinHeight = 30 }; bool syncing = false;
        void Sync() { syncing = true; view.Content = model.Text; view.IsChecked = model.Checked; syncing = false; }
        view.Checked += (_, _) => { if (!syncing) model.Checked = true; };
        view.Unchecked += (_, _) => { if (!syncing) model.Checked = false; };
        EventHandler changed = (_, _) => Sync(); model.CheckedChanged += changed; unsubscribe.Add(() => model.CheckedChanged -= changed); Watch(model, Sync); return view;
    }
    private W.FrameworkElement RichText(RichTextBox model)
    {
        var view = new WC.RichTextBox { AcceptsTab = model.AcceptsTab, IsReadOnly = model.ReadOnly, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = model.WordWrap ? WC.ScrollBarVisibility.Disabled : WC.ScrollBarVisibility.Auto, MinHeight = 30, BorderThickness = new(0), Padding = new(8) };
        view.Document.PagePadding = new(0); bool syncing = false; string lastRtf = "";
        int Index(System.Windows.Documents.TextPointer point) => new System.Windows.Documents.TextRange(view.Document.ContentStart, point).Text.Replace("\r\n", "\n").Length;
        System.Windows.Documents.TextPointer Position(int index)
        {
            var start = view.Document.ContentStart; int low = 0, high = start.GetOffsetToPosition(view.Document.ContentEnd);
            while (low < high) { int mid = (low + high) / 2; var p = start.GetPositionAtOffset(mid)!; if (Index(p) < index) low = mid + 1; else high = mid; }
            return start.GetPositionAtOffset(low)!;
        }
        void Sync()
        {
            if (syncing) return; syncing = true;
            try
            {
                string rtf = model.Rtf ?? "";
                if (rtf != lastRtf)
                {
                    var scroll = WpfRichTextBridge.Position(model);
                    using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(rtf));
                    new System.Windows.Documents.TextRange(view.Document.ContentStart, view.Document.ContentEnd).Load(stream, W.DataFormats.Rtf);
                    lastRtf = rtf; view.Document.PagePadding = new(0);
                    if (scroll is { } point) WpfRichTextBridge.Restore(model, point);
                }
                view.IsReadOnly = model.ReadOnly; view.Foreground = WpfDrawing.Brush(model.ForeColor); view.Background = WpfDrawing.Brush(model.BackColor);
                if (!view.IsKeyboardFocusWithin) view.Selection.Select(Position(model.SelectionStart), Position(model.SelectionStart + model.SelectionLength));
            }
            finally { syncing = false; }
        }
        view.TextChanged += (_, _) => { if (syncing || model.ReadOnly) return; syncing = true; try { using var stream = new MemoryStream(); new System.Windows.Documents.TextRange(view.Document.ContentStart, view.Document.ContentEnd).Save(stream, W.DataFormats.Rtf); model.Rtf = System.Text.Encoding.ASCII.GetString(stream.ToArray()); lastRtf = model.Rtf; } finally { syncing = false; } };
        view.SelectionChanged += (_, _) => { if (!syncing) { syncing = true; try { int start = Math.Min(model.TextLength, Index(view.Selection.Start)); model.Select(start, Math.Clamp(Index(view.Selection.End) - start, 0, model.TextLength - start)); } finally { syncing = false; } } };
        EventHandler selected = (_, _) => Sync();
        model.SelectionChanged += selected; unsubscribe.Add(() => model.SelectionChanged -= selected);
        MouseEventArgs MouseArgs(WI.MouseEventArgs e, MouseButtons button) { var p = e.GetPosition(view); return new(button, 1, (int)Math.Round(p.X * model.DeviceDpi / 96), (int)Math.Round(p.Y * model.DeviceDpi / 96), 0); }
        view.AddHandler(WI.Mouse.MouseMoveEvent, new WI.MouseEventHandler((_, e) => Raise(model, "OnMouseMove", MouseArgs(e, MouseButtons.None))), true);
        view.AddHandler(WI.Mouse.MouseDownEvent, new WI.MouseButtonEventHandler((_, e) => Raise(model, "OnMouseDown", MouseArgs(e, e.ChangedButton == WI.MouseButton.Right ? MouseButtons.Right : MouseButtons.Left))), true);
        view.AddHandler(WI.Mouse.MouseUpEvent, new WI.MouseButtonEventHandler((_, e) => {
            var args = MouseArgs(e, e.ChangedButton == WI.MouseButton.Right ? MouseButtons.Right : MouseButtons.Left);
            Raise(model, "OnMouseClick", args);
            if (!model.DetectUrls || args.Button != MouseButtons.Left || view.Selection.Text.Length > 0) return;
            int index = WpfRichTextBridge.CharacterAt(model, args.Location);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(model.Text, "https?://[^\\s<>\\\"']+"))
                if (index >= match.Index && index < match.Index + match.Length) { Raise(model, "OnLinkClicked", new LinkClickedEventArgs(match.Value)); break; }
        }), true);
        view.MouseLeave += (_, _) => WpfSettingsModel.Raise(model, "OnMouseLeave");
        Watch(model, Sync);
        return view;
    }
    private W.FrameworkElement Picture(PictureBox model)
    {
        if (WpfNativeVisuals.Create(model) is { } custom) return custom;
        var image = new WC.Image { Stretch = model.SizeMode == PictureBoxSizeMode.StretchImage ? WM.Stretch.Fill : model.SizeMode == PictureBoxSizeMode.Normal ? WM.Stretch.None : WM.Stretch.Uniform };
        System.Drawing.Image? previous = null;
        void Sync() { if (ReferenceEquals(previous, model.Image)) return; previous = model.Image; image.Source = previous is Bitmap bitmap ? WpfDrawing.Image(bitmap) : null; }
        InvalidateEventHandler changed = (_, _) => Sync(); model.Invalidated += changed; unsubscribe.Add(() => model.Invalidated -= changed); Watch(model, Sync);
        NativePointer(model, image); return image;
    }
    private W.FrameworkElement Progress(ProgressBar model)
    {
        var view = new WC.ProgressBar { MinHeight = 12 };
        Watch(model, () => { view.Minimum = model.Minimum; view.Maximum = model.Maximum; view.Value = model.Value; view.IsIndeterminate = model.Style == ProgressBarStyle.Marquee; }); return view;
    }
    private W.FrameworkElement Slider(TrackBar model)
    {
        var view = new WC.Slider { Minimum = model.Minimum, Maximum = model.Maximum, SmallChange = model.SmallChange, LargeChange = model.LargeChange, TickFrequency = model.TickFrequency, IsSnapToTickEnabled = true };
        bool syncing = false; view.ValueChanged += (_, _) => { if (!syncing) { model.Value = (int)view.Value; WpfSettingsModel.Raise(model, "OnScroll"); } };
        EventHandler change = (_, _) => { syncing = true; view.Value = model.Value; syncing = false; }; model.ValueChanged += change; unsubscribe.Add(() => model.ValueChanged -= change); Watch(model, () => change(null, EventArgs.Empty)); return view;
    }
    private W.FrameworkElement Date(DateTimePicker model)
    {
        var view = new WC.DatePicker { SelectedDate = model.Value, DisplayDateStart = model.MinDate, DisplayDateEnd = model.MaxDate }; bool syncing = false;
        view.SelectedDateChanged += (_, _) => { if (!syncing && view.SelectedDate is { } value) model.Value = value; };
        EventHandler change = (_, _) => { syncing = true; view.SelectedDate = model.Value; syncing = false; }; model.ValueChanged += change; unsubscribe.Add(() => model.ValueChanged -= change); return view;
    }
    private W.FrameworkElement Link(LinkLabel model)
    {
        var view = new WC.Button { BorderThickness = new(0), HorizontalContentAlignment = W.HorizontalAlignment.Left };
        Watch(model, () => view.Content = model.Text); view.Click += (_, _) => Raise(model, "OnLinkClicked", new LinkLabelLinkClickedEventArgs(model.Links.Count > 0 ? model.Links[0] : new LinkLabel.Link())); return view;
    }
    private W.FrameworkElement Tabs(TabControl model)
    {
        var view = new WC.TabControl(); bool syncing = false;
        foreach (TabPage page in model.TabPages) { var item = new WC.TabItem { Header = page.Text, Content = NativeLayout(page) }; view.Items.Add(item); Watch(page, () => item.Header = page.Text); }
        view.SelectedIndex = model.SelectedIndex;
        view.SelectionChanged += (_, e) => { if (!syncing && ReferenceEquals(e.Source, view)) model.SelectedIndex = view.SelectedIndex; };
        EventHandler change = (_, _) => { syncing = true; view.SelectedIndex = model.SelectedIndex; syncing = false; }; model.SelectedIndexChanged += change; unsubscribe.Add(() => model.SelectedIndexChanged -= change); return view;
    }
    private W.FrameworkElement Split(SplitContainer model)
    {
        var view = new WC.Grid(); bool horizontal = model.Orientation == Orientation.Horizontal, syncing = false;
        model.SplitterWidth = Math.Max(1, (int)Math.Round(6 * model.DeviceDpi / 96d));
        double available = horizontal ? model.Height : model.Width; double fraction = Math.Clamp(model.SplitterDistance / Math.Max(1, available), .05, .95);
        if (horizontal) foreach (double x in new[] { fraction, 0, 1 - fraction }) view.RowDefinitions.Add(new() { Height = x == 0 ? new(6) : new(x, W.GridUnitType.Star) });
        else foreach (double x in new[] { fraction, 0, 1 - fraction }) view.ColumnDefinitions.Add(new() { Width = x == 0 ? new(6) : new(x, W.GridUnitType.Star) });
        var first = NativeLayout(model.Panel1); var second = NativeLayout(model.Panel2); var split = new WC.GridSplitter { Background = WpfDrawing.Brush(UiTheme.Current.Border), HorizontalAlignment = W.HorizontalAlignment.Stretch, VerticalAlignment = W.VerticalAlignment.Stretch, ResizeDirection = horizontal ? WC.GridResizeDirection.Rows : WC.GridResizeDirection.Columns, ResizeBehavior = WC.GridResizeBehavior.PreviousAndNext };
        if (horizontal) { WC.Grid.SetRow(split, 1); WC.Grid.SetRow(second, 2); } else { WC.Grid.SetColumn(split, 1); WC.Grid.SetColumn(second, 2); }
        view.Children.Add(first); view.Children.Add(split); view.Children.Add(second);
        void SyncRatio()
        {
            if (syncing || model.Panel1Collapsed || model.Panel2Collapsed) return;
            double extent = horizontal ? model.ClientSize.Height : model.ClientSize.Width;
            fraction = Math.Clamp(model.SplitterDistance / Math.Max(1, extent - model.SplitterWidth), .05, .95);
            if (horizontal) { view.RowDefinitions[0].Height = new(fraction, W.GridUnitType.Star); view.RowDefinitions[2].Height = new(1-fraction, W.GridUnitType.Star); }
            else { view.ColumnDefinitions[0].Width = new(fraction, W.GridUnitType.Star); view.ColumnDefinitions[2].Width = new(1-fraction, W.GridUnitType.Star); }
        }
        SplitterEventHandler moved = (_, _) => SyncRatio(); model.SplitterMoved += moved; unsubscribe.Add(() => model.SplitterMoved -= moved);
        EventHandler resized = (_, _) => SyncRatio(); model.SizeChanged += resized; unsubscribe.Add(() => model.SizeChanged -= resized);
        view.SizeChanged += (_, _) => { if (syncing) return; syncing = true; try { model.Size = new((int)Math.Round(view.ActualWidth*model.DeviceDpi/96), (int)Math.Round(view.ActualHeight*model.DeviceDpi/96)); } finally { syncing = false; } SyncRatio(); };
        split.DragCompleted += (_, _) => { var value = (int)Math.Round((horizontal ? view.RowDefinitions[0].ActualHeight : view.ColumnDefinitions[0].ActualWidth) * model.DeviceDpi / 96); try { model.SplitterDistance = value; } catch (ArgumentOutOfRangeException) { } };
        bool firstCollapsed = false, secondCollapsed = false;
        Watch(model, () => { SyncRatio();
            first.Visibility = model.Panel1Collapsed ? W.Visibility.Collapsed : W.Visibility.Visible; second.Visibility = model.Panel2Collapsed ? W.Visibility.Collapsed : W.Visibility.Visible; split.Visibility = model.Panel1Collapsed || model.Panel2Collapsed ? W.Visibility.Collapsed : W.Visibility.Visible;
            if (firstCollapsed == model.Panel1Collapsed && secondCollapsed == model.Panel2Collapsed) return;
            firstCollapsed = model.Panel1Collapsed; secondCollapsed = model.Panel2Collapsed;
            W.GridLength a = firstCollapsed ? new(0) : new(fraction, W.GridUnitType.Star), b = secondCollapsed ? new(0) : new(1-fraction, W.GridUnitType.Star), divider = new(firstCollapsed || secondCollapsed ? 0 : 6);
            if (horizontal) { view.RowDefinitions[0].Height = a; view.RowDefinitions[1].Height = divider; view.RowDefinitions[2].Height = b; }
            else { view.ColumnDefinitions[0].Width = a; view.ColumnDefinitions[1].Width = divider; view.ColumnDefinitions[2].Width = b; }
        }); return view;
    }
    private W.FrameworkElement List(ListBox model)
    {
        var view = new WC.ListBox { SelectionMode = model.SelectionMode == SelectionMode.One ? WC.SelectionMode.Single : WC.SelectionMode.Extended }; bool syncing = false;
        void Sync() { if (syncing) return; syncing = true; try { var items = model.Items.Cast<object>().Select(MainForm.InterfaceItemText).ToArray(); if (!view.Items.Cast<object>().SequenceEqual(items)) view.ItemsSource = items; if (model.SelectionMode == SelectionMode.One) view.SelectedIndex = model.SelectedIndex; else { view.SelectedItems.Clear(); foreach (int index in model.SelectedIndices) if (index < view.Items.Count) view.SelectedItems.Add(view.Items[index]); } } finally { syncing = false; } }
        view.SelectionChanged += (_, _) => { if (syncing) return; syncing = true; try { if (model.SelectionMode == SelectionMode.One) model.SelectedIndex = view.SelectedIndex; else { var indices = view.SelectedItems.Cast<object>().Select(x => view.Items.IndexOf(x)).ToHashSet(); for (int i=0;i<model.Items.Count;i++) model.SetSelected(i, indices.Contains(i)); } } finally { syncing = false; } };
        view.MouseDoubleClick += (_, _) => WpfSettingsModel.Raise(model, "OnDoubleClick");
        EventHandler change = (_, _) => Sync(); model.SelectedIndexChanged += change; unsubscribe.Add(() => model.SelectedIndexChanged -= change); Watch(model, Sync); return view;
    }
    private W.FrameworkElement CheckedList(CheckedListBox model)
    {
        var view = new WC.ListBox(); bool syncing = false;
        void Sync() { if (syncing) return; syncing = true; try { if (view.Items.Count != model.Items.Count) { view.Items.Clear(); for (int i = 0; i < model.Items.Count; i++) { int index = i; var check = new WC.CheckBox(); check.Checked += (_, _) => { if (!syncing) model.SetItemChecked(index, true); }; check.Unchecked += (_, _) => { if (!syncing) model.SetItemChecked(index, false); }; view.Items.Add(check); } } for (int i = 0; i < model.Items.Count; i++) { var check = (WC.CheckBox)view.Items[i]; check.Content = MainForm.InterfaceItemText(model.Items[i]); check.IsChecked = model.GetItemChecked(i); } view.SelectedIndex = model.SelectedIndex; } finally { syncing = false; } }
        ItemCheckEventHandler changed = (_, _) => view.Dispatcher.BeginInvoke((Action)Sync); model.ItemCheck += changed; unsubscribe.Add(() => model.ItemCheck -= changed);
        view.SelectionChanged += (_, _) => { if (!syncing) model.SelectedIndex = view.SelectedIndex; };
        Watch(model, Sync); return view;
    }
    private W.FrameworkElement ListView(ListView model)
    {
        var view = new WC.ListView { SelectionMode=model.MultiSelect ? WC.SelectionMode.Extended : WC.SelectionMode.Single }; var columns = new WC.GridView(); bool syncing=false; string[][] last=[];
        for (int i = 0; i < model.Columns.Count; i++) columns.Columns.Add(new() { Header = model.Columns[i].Text, Width = Math.Max(60, model.Columns[i].Width * 96d / model.DeviceDpi), DisplayMemberBinding = new WD.Binding($"[{i}]") });
        if (model.View == System.Windows.Forms.View.Details) view.View = columns;
        void Sync() { if(syncing)return;syncing=true;try { var next=model.Items.Cast<ListViewItem>().Select(item=>item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(x=>x.Text).ToArray()).ToArray(); if(last.Length!=next.Length || last.Where((row,i)=>!row.SequenceEqual(next[i])).Any()) { last=next;view.ItemsSource=next; } for(int i=0;i<columns.Columns.Count;i++){columns.Columns[i].Header=model.Columns[i].Text;columns.Columns[i].Width=Math.Max(60,model.Columns[i].Width*96d/model.DeviceDpi);} if(model.MultiSelect){view.SelectedItems.Clear();foreach(int i in model.SelectedIndices)if(i<view.Items.Count)view.SelectedItems.Add(view.Items[i]);}else view.SelectedIndex=model.SelectedIndices.Count>0?model.SelectedIndices[0]:-1; }finally{syncing=false;} }
        EventHandler selected=(_,_)=>Sync();model.SelectedIndexChanged+=selected;unsubscribe.Add(()=>model.SelectedIndexChanged-=selected);Watch(model,Sync);
        view.SelectionChanged += (_, _) => { if(syncing)return;syncing=true;try { var selectedRows=view.SelectedItems.Cast<object>().Select(x=>view.Items.IndexOf(x)).ToHashSet();foreach(ListViewItem item in model.Items)item.Selected=selectedRows.Contains(item.Index); }finally{syncing=false;} };
        view.MouseDoubleClick += (_, _) => WpfSettingsModel.Raise(model, "OnDoubleClick"); return view;
    }
    private W.FrameworkElement Tree(TreeView model)
    {
        var view = new WC.TreeView(); TreeNode[] last = [];
        WC.TreeViewItem Node(TreeNode node) { var item = new WC.TreeViewItem { Header = node.Text, IsExpanded = node.IsExpanded, Tag = node }; foreach (TreeNode child in node.Nodes) item.Items.Add(Node(child)); item.Expanded += (_, e) => { if (e.Source == item) node.Expand(); }; item.Collapsed += (_, e) => { if (e.Source == item) node.Collapse(); }; return item; }
        Watch(model, () => { var nodes = model.Nodes.Cast<TreeNode>().ToArray(); if (last.SequenceEqual(nodes)) return; last = nodes; view.Items.Clear(); foreach (var node in nodes) view.Items.Add(Node(node)); });
        view.SelectedItemChanged += (_, _) => { if (view.SelectedItem is WC.TreeViewItem { Tag: TreeNode node }) model.SelectedNode = node; }; return view;
    }
    private void NativePointer(Control model, W.FrameworkElement view)
    {
        Point Point(W.Point p) => new((int)Math.Round(p.X * model.DeviceDpi / 96), (int)Math.Round(p.Y * model.DeviceDpi / 96));
        view.MouseDown += (_, e) => { var p = Point(e.GetPosition(view)); Raise(model, "OnMouseDown", new MouseEventArgs(e.ChangedButton == WI.MouseButton.Right ? MouseButtons.Right : MouseButtons.Left, e.ClickCount, p.X, p.Y, 0)); view.CaptureMouse(); };
        view.MouseMove += (_, e) => { var p = Point(e.GetPosition(view)); Raise(model, "OnMouseMove", new MouseEventArgs(e.LeftButton == WI.MouseButtonState.Pressed ? MouseButtons.Left : e.RightButton == WI.MouseButtonState.Pressed ? MouseButtons.Right : MouseButtons.None, 0, p.X, p.Y, 0)); };
        view.MouseUp += (_, e) => { var p = Point(e.GetPosition(view)); var args = new MouseEventArgs(e.ChangedButton == WI.MouseButton.Right ? MouseButtons.Right : MouseButtons.Left, 1, p.X, p.Y, 0); Raise(model, "OnMouseUp", args); Raise(model, "OnMouseClick", args); WpfSettingsModel.Raise(model, "OnClick"); view.ReleaseMouseCapture(); };
        view.MouseWheel += (_, e) => { var p = Point(e.GetPosition(view)); Raise(model, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, e.Delta)); };
    }
    internal W.FrameworkElement NativeLayout(Control model)
    {
        var panel = new WpfModelLayout(model, this); Bind(model, panel, model is not Form); unsubscribe.Add(panel.Dispose);
        if (model is ScrollableControl { AutoScroll: true } scrolling)
        {
            var viewer = new WC.ScrollViewer { Content = panel, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, CanContentScroll = false };
            panel.ScrollHost = viewer;
            viewer.SizeChanged += (_, _) => panel.InvalidateMeasure();
            viewer.ScrollChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, viewer)) { scrolling.AutoScrollPosition = new((int)Math.Round(viewer.HorizontalOffset * model.DeviceDpi / 96), (int)Math.Round(viewer.VerticalOffset * model.DeviceDpi / 96)); if(e.ViewportWidthChange!=0 || e.ViewportHeightChange!=0) panel.InvalidateMeasure(); } };
            ScrollEventHandler scroll = (_, _) => { viewer.ScrollToHorizontalOffset(-scrolling.AutoScrollPosition.X * 96d / model.DeviceDpi); viewer.ScrollToVerticalOffset(-scrolling.AutoScrollPosition.Y * 96d / model.DeviceDpi); };
            scrolling.Scroll += scroll; unsubscribe.Add(() => scrolling.Scroll -= scroll); return viewer;
        }
        return panel;
    }
}

// Retained layout metadata is used only for legacy absolute-position dialog
// sections. Visible controls and their rendering, input and scrolling are WPF.
internal sealed class WpfModelLayout : WC.Panel, IDisposable
{
    private readonly Control model; private readonly WpfSettingsFields fields;
    private readonly Dictionary<Control, W.FrameworkElement> children = [];
    private bool arranging, disposed;
    internal WC.ScrollViewer? ScrollHost;
    internal WpfModelLayout(Control model, WpfSettingsFields fields)
    {
        this.model = model; this.fields = fields; ClipToBounds = true;
        model.ControlAdded += Changed; model.ControlRemoved += Changed; model.Layout += LayoutChanged;
        Sync();
    }
    private void Changed(object? sender, ControlEventArgs e) { Sync(); InvalidateMeasure(); }
    private void LayoutChanged(object? sender, LayoutEventArgs e) { if (!arranging) { InvalidateMeasure(); if(ScrollHost is {} viewer && model is ScrollableControl scrolling) { viewer.ScrollToHorizontalOffset(-scrolling.AutoScrollPosition.X*96d/model.DeviceDpi);viewer.ScrollToVerticalOffset(-scrolling.AutoScrollPosition.Y*96d/model.DeviceDpi); } } }
    private void Sync()
    {
        if(disposed||model.IsDisposed||model.Disposing)return;
        foreach (var key in children.Keys.Where(key => !model.Controls.Contains(key)).ToArray()) { Children.Remove(children[key]); children.Remove(key); }
        foreach (Control child in model.Controls) if (!children.ContainsKey(child)) { var view = fields.Create(child); view.MinWidth = view.MinHeight = 0; view.MaxWidth = view.MaxHeight = double.PositiveInfinity; view.Width = view.Height = double.NaN; view.HorizontalAlignment = W.HorizontalAlignment.Stretch; view.VerticalAlignment = W.VerticalAlignment.Stretch; children[child] = view; Children.Add(view); }
    }
    protected override W.Size MeasureOverride(W.Size available)
    {
        if(disposed||model.IsDisposed||model.Disposing)return new();
        double scale = model.DeviceDpi / 96d;
        double width = double.IsFinite(available.Width) ? available.Width : Math.Max(100, ScrollHost?.ViewportWidth > 0 ? ScrollHost.ViewportWidth : model.Width / scale);
        double height = double.IsFinite(available.Height) ? available.Height : Math.Max(60, ScrollHost?.ViewportHeight > 0 ? ScrollHost.ViewportHeight : model.Height / scale);
        arranging = true;
        
        try { var size = new Size(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale))); if (model is Form) model.ClientSize = size; else model.Size = size; model.PerformLayout(); }
        finally { arranging = false; }
        foreach (var (child, view) in children) view.Measure(new(Math.Max(0, child.Width / scale), Math.Max(0, child.Height / scale)));
        var offset = model is ScrollableControl sc ? sc.AutoScrollPosition : Point.Empty;
        if (double.IsPositiveInfinity(available.Height)) height = Math.Max(height, children.Where(x => WpfSettingsModel.LocallyVisible(x.Key)).Select(x => (x.Key.Bottom - offset.Y) / scale).DefaultIfEmpty(0).Max());
        if (double.IsPositiveInfinity(available.Width)) width = Math.Max(width, children.Where(x => WpfSettingsModel.LocallyVisible(x.Key)).Select(x => (x.Key.Right - offset.X) / scale).DefaultIfEmpty(0).Max());
        return new(width, height);
    }
    protected override W.Size ArrangeOverride(W.Size size)
    {
        if(disposed||model.IsDisposed||model.Disposing)return size;
        double scale = model.DeviceDpi / 96d;
        var offset = model is ScrollableControl sc ? sc.AutoScrollPosition : Point.Empty;
        foreach (var (child, view) in children) view.Arrange(new((child.Left - offset.X) / scale, (child.Top - offset.Y) / scale, Math.Max(0, child.Width / scale), Math.Max(0, child.Height / scale)));
        return size;
    }
    public void Dispose() { disposed=true; model.ControlAdded -= Changed; model.ControlRemoved -= Changed; model.Layout -= LayoutChanged; }
}
