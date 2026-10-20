using System.Collections.ObjectModel;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

// Retained presenters for the RPG editor. They own WPF elements and lightweight
// editing state; no WinForms control, handle, or layout pass is involved.
internal class NativeRpgElement : IDisposable
{
    internal W.FrameworkElement View { get; }
    internal NativeRpgElement(W.FrameworkElement view) { View = view; Controls = new(this); }
    internal NativeRpgChildren Controls { get; }
    internal NativeRpgElement? Parent { get; set; }
    internal virtual string Text { get; set; } = "";
    internal string Name { get => W.Automation.AutomationProperties.GetAutomationId(View); set => W.Automation.AutomationProperties.SetAutomationId(View, value); }
    internal string AccessibleName { get => W.Automation.AutomationProperties.GetName(View); set => W.Automation.AutomationProperties.SetName(View, value); }
    internal object? Tag { get; set; }
    internal bool Visible { get => View.Visibility == W.Visibility.Visible; set => View.Visibility = value ? W.Visibility.Visible : W.Visibility.Collapsed; }
    internal bool Enabled { get => View.IsEnabled; set => View.IsEnabled = value; }
    internal bool ContainsFocus => View.IsKeyboardFocusWithin;
    internal bool IsDisposed { get; private set; }
    internal bool IsHandleCreated => View.IsLoaded;
    internal double Width { get => double.IsFinite(View.Width) ? View.Width : View.ActualWidth; set => View.Width = value; }
    internal double Height { get => double.IsFinite(View.Height) ? View.Height : View.ActualHeight; set => View.Height = value; }
    internal W.Thickness Margin { get => View.Margin; set => View.Margin = value; }
    internal event EventHandler? Disposed;
    internal virtual void Invalidate() => View.InvalidateVisual();
    public virtual void Dispose() { if (IsDisposed) return; IsDisposed = true; foreach (var child in Controls.ToArray()) child.Dispose(); Parent?.Controls.Remove(this); Disposed?.Invoke(this, EventArgs.Empty); }
}
internal sealed class NativeRpgChildren(NativeRpgElement owner) : Collection<NativeRpgElement>
{
    protected override void InsertItem(int index, NativeRpgElement item) { base.InsertItem(index, item); item.Parent = owner; if (owner.View is WC.Panel panel) panel.Children.Insert(index, item.View); else if (owner.View is WC.ScrollViewer scroll) scroll.Content = item.View; }
    protected override void RemoveItem(int index) { var item = this[index]; base.RemoveItem(index); item.Parent = null; if (owner.View is WC.Panel panel) panel.Children.Remove(item.View); else if (owner.View is WC.ScrollViewer scroll && ReferenceEquals(scroll.Content, item.View)) scroll.Content = null; }
    protected override void ClearItems() { foreach (var child in this) child.Parent = null; base.ClearItems(); if (owner.View is WC.Panel panel) panel.Children.Clear(); else if (owner.View is WC.ScrollViewer scroll) scroll.Content = null; }
    internal void AddRange(IEnumerable<NativeRpgElement> nodes) { foreach (var node in nodes) Add(node); }
}
internal class NativeRpgColumn : NativeRpgElement
{
    internal NativeRpgColumn() : this(new WC.StackPanel()) { }
    protected NativeRpgColumn(WC.Panel panel) : base(panel) { }
}
internal sealed class NativeRpgRow : NativeRpgColumn { internal NativeRpgRow() : base(new WC.WrapPanel()) { } }
internal sealed class NativeRpgScroll : NativeRpgElement
{
    internal NativeRpgScroll() : base(new WC.ScrollViewer { VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled }) { }
    internal int ScrollOffset { get => (int)((WC.ScrollViewer)View).VerticalOffset; set => ((WC.ScrollViewer)View).ScrollToVerticalOffset(value); }
}
internal sealed class NativeRpgLabel : NativeRpgElement
{
    internal NativeRpgLabel() : base(new NativeLocalizedTextBlock { TextWrapping = W.TextWrapping.Wrap, VerticalAlignment = W.VerticalAlignment.Center }) { }
    private string source = "";
    internal bool Localize {get=>((NativeLocalizedTextBlock)View).Localize;set=>((NativeLocalizedTextBlock)View).Localize=value;}
    internal override string Text { get => source; set { source = value ?? ""; ((WC.TextBlock)View).Text = Localize?UiLanguage.Text(source):source; } }
}
internal sealed class NativeRpgButton : NativeRpgElement
{
    private string caption, icon;
    private bool primary;
    internal NativeRpgButton(string text, string icon, EventHandler action, bool primary = false) : base(WpfThemeResources.Button(text, icon, () => { }, primary))
    { caption = text; this.icon = icon; this.primary = primary; Height = 36; ((WC.Button)View).Click += (_, _) => action(this, EventArgs.Empty); }
    internal override string Text { get => caption; set { caption = value; Update(); } }
    internal bool Primary { get => primary; set { primary = value; Update(); } }
    internal bool TabStyle { get; set; }
    private void Update() => WpfThemeResources.SetButton((WC.Button)View, caption, icon, primary);
    internal override void Invalidate() => Update();
}
internal sealed class NativeRpgText : NativeRpgElement
{
    internal NativeRpgText() : base(new WC.TextBox { Height = 36, MinWidth = 80 })
    { ((WC.TextBox)View).TextChanged += (_, _) => TextChanged?.Invoke(this, EventArgs.Empty); View.PreviewKeyDown += (_, e) => { var args = new KeyEventArgs((Keys)WI.KeyInterop.VirtualKeyFromKey(e.Key)); KeyDown?.Invoke(this, args); e.Handled |= args.Handled || args.SuppressKeyPress; }; }
    internal override string Text { get => ((WC.TextBox)View).Text; set { if (Text != value) ((WC.TextBox)View).Text = value ?? ""; } }
    internal string PlaceholderText { set { View.ToolTip=value;WpfUiLanguage.Property(View,W.FrameworkElement.ToolTipProperty); } }
    internal bool Multiline { set { ((WC.TextBox)View).AcceptsReturn = value; ((WC.TextBox)View).TextWrapping = value ? W.TextWrapping.Wrap : W.TextWrapping.NoWrap; ((WC.TextBox)View).VerticalScrollBarVisibility = value ? WC.ScrollBarVisibility.Auto : WC.ScrollBarVisibility.Hidden; } }
    internal int MaxLength { set => ((WC.TextBox)View).MaxLength = value; }
    internal event EventHandler? TextChanged;
    internal event KeyEventHandler? KeyDown;
    internal void Clear() => Text = "";
}
internal sealed class NativeRpgToggle : NativeRpgElement
{
    internal NativeRpgToggle() : base(new WC.CheckBox { VerticalAlignment = W.VerticalAlignment.Center, MinHeight = 36 })
    { var view = (WC.CheckBox)View; view.Checked += (_, _) => CheckedChanged?.Invoke(this, EventArgs.Empty); view.Unchecked += (_, _) => CheckedChanged?.Invoke(this, EventArgs.Empty); }
    internal override string Text { get => base.Text; set { base.Text = value; ((WC.CheckBox)View).Content=value;WpfUiLanguage.Property(View,WC.ContentControl.ContentProperty); } }
    internal bool Checked { get => ((WC.CheckBox)View).IsChecked == true; set => ((WC.CheckBox)View).IsChecked = value; }
    internal event EventHandler? CheckedChanged;
}
internal sealed class NativeRpgItems : ObservableCollection<object>
{
    internal void AddRange(IEnumerable<object> values) { foreach (var value in values) Add(value); }
}
internal sealed class NativeRpgChoice : NativeRpgElement
{
    internal NativeRpgItems Items { get; } = new();
    internal NativeRpgChoice() : base(new WC.ComboBox { MinWidth = 80, Height = 36 })
    { var view = (WC.ComboBox)View; view.ItemsSource = Items; view.SelectionChanged += (_, _) => SelectedIndexChanged?.Invoke(this, EventArgs.Empty); view.PreviewMouseWheel += (_, e) => { if (!view.IsDropDownOpen) WpfScrollRouting.RouteToParent(view, e); }; }
    internal int SelectedIndex { get => ((WC.ComboBox)View).SelectedIndex; set => ((WC.ComboBox)View).SelectedIndex = value; }
    internal object? SelectedItem { get => ((WC.ComboBox)View).SelectedItem; set => ((WC.ComboBox)View).SelectedItem = value; }
    internal bool DroppedDown => ((WC.ComboBox)View).IsDropDownOpen;
    internal event EventHandler? SelectedIndexChanged;
}
internal sealed class NativeRpgNumber : NativeRpgElement
{
    private readonly WC.TextBox input;
    private decimal value, minimum = int.MinValue, maximum = int.MaxValue;
    private bool syncing;
    internal NativeRpgNumber() : base(new WC.Grid { Height = 36, MinWidth = 110 })
    {
        var grid = (WC.Grid)View; grid.ColumnDefinitions.Add(new() { Width = new(28) }); grid.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new(28) });
        input = new WC.TextBox { TextAlignment = W.TextAlignment.Center, MinWidth = 40 }; WC.Grid.SetColumn(input, 1); grid.Children.Add(input);
        var less = WpfThemeResources.Button("−", null, () => Value -= 1); var more = WpfThemeResources.Button("+", null, () => Value += 1);
        foreach (var button in new[] { less, more }) { button.MinWidth = 0; button.Padding = new(0); button.BorderThickness = new(0); }
        grid.Children.Add(less); WC.Grid.SetColumn(more, 2); grid.Children.Add(more);
        input.TextChanged += (_, _) => { if (!syncing && decimal.TryParse(input.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var next) && next >= minimum && next <= maximum && next != value) { value = next; ValueChanged?.Invoke(this, EventArgs.Empty); } };
        input.LostKeyboardFocus += (_, _) => Sync();
        input.PreviewKeyDown += (_, e) => { var args = new KeyEventArgs((Keys)WI.KeyInterop.VirtualKeyFromKey(e.Key)); KeyDown?.Invoke(this, args); e.Handled |= args.Handled || args.SuppressKeyPress; };
        input.PreviewMouseWheel += (_, e) => WpfScrollRouting.RouteToParent(View, e); Sync();
    }
    internal decimal Value { get => value; set { decimal next = Math.Clamp(value, minimum, maximum); if (this.value == next) { Sync(); return; } this.value = next; Sync(); ValueChanged?.Invoke(this, EventArgs.Empty); } }
    internal decimal Minimum { get => minimum; set { minimum = value; if (maximum < value) maximum = value; Value = this.value; } }
    internal decimal Maximum { get => maximum; set { maximum = value; if (minimum > value) minimum = value; Value = this.value; } }
    internal int DecimalPlaces { get; set; }
    internal event EventHandler? ValueChanged;
    internal event KeyEventHandler? KeyDown;
    private void Sync() { syncing = true; input.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture); syncing = false; }
}
internal sealed class NativeRpgCommandList : NativeRpgElement
{
    internal NativeRpgCommandList() : base(new WC.ListBox { MaxHeight = 340, MinHeight = 78, HorizontalContentAlignment = W.HorizontalAlignment.Stretch })
    {
        var factory = new W.FrameworkElementFactory(typeof(WC.TextBlock)); factory.SetBinding(WC.TextBlock.TextProperty, new System.Windows.Data.Binding()); factory.SetValue(WC.TextBlock.TextWrappingProperty, W.TextWrapping.Wrap); ((WC.ListBox)View).ItemTemplate = new W.DataTemplate { VisualTree = factory };
        ((WC.ListBox)View).SelectionChanged += (_, _) => SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }
    internal int SelectedIndex { get => ((WC.ListBox)View).SelectedIndex; set => ((WC.ListBox)View).SelectedIndex = value; }
    internal void SetItems(string[] items) => ((WC.ListBox)View).ItemsSource = items;
    internal event EventHandler? SelectedIndexChanged;
}
