using System.Reflection;
using System.Runtime.ExceptionServices;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

// The existing controls remain the settings binding model. A native view edits
// the same values and invokes the same handlers, so profiles, validation and
// save boundaries are shared with the compatibility UI.
internal static class WpfSettingsModel
{
    private static readonly MethodInfo State = typeof(Control).GetMethod("GetState", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly object VisibleState = Enum.Parse(State.GetParameters()[0].ParameterType, "Visible");
    private static readonly object EnabledState = Enum.Parse(State.GetParameters()[0].ParameterType, "Enabled");
    internal static bool LocallyVisible(Control model) => (bool)State.Invoke(model, [VisibleState])!;
    internal static bool LocallyEnabled(Control model) => (bool)State.Invoke(model, [EnabledState])!;
    internal static void Raise(Control model, string name)
    {
        var method = model.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(EventArgs)], null);
        try { method?.Invoke(model, [EventArgs.Empty]); }
        catch (TargetInvocationException e) when (e.InnerException is { } cause) { ExceptionDispatchInfo.Capture(cause).Throw(); }
    }
}

internal sealed class WpfSettingsFields : IDisposable
{
    private readonly List<Action> refreshers = [];
    private readonly List<Action> unsubscribe = [];
    private readonly List<System.Windows.Forms.Integration.WindowsFormsHost> hosted = [];
    internal int NativeFields { get; private set; }
    internal bool UseModelDimensions { get; init; }
    internal IReadOnlyList<string> HostedTypes => hosted.Select(h => h.Child?.GetType().Name ?? "").ToArray();
    internal void Refresh() { foreach (var refresh in refreshers.ToArray()) refresh(); }
    private void Watch(Control source, Action refresh)
    {
        EventHandler handler = (_, _) => refresh();
        source.TextChanged += handler; source.EnabledChanged += handler; source.VisibleChanged += handler; source.FontChanged += handler;
        unsubscribe.Add(() => { source.TextChanged -= handler; source.EnabledChanged -= handler; source.VisibleChanged -= handler; source.FontChanged -= handler; });
        refreshers.Add(refresh); refresh();
    }
    internal void Bind(Control model, W.FrameworkElement view, bool visibility = true)
    {
        Watch(model, () =>
        {
            view.IsEnabled = WpfSettingsModel.LocallyEnabled(model);
            if (visibility) view.Visibility = WpfSettingsModel.LocallyVisible(model) ? W.Visibility.Visible : W.Visibility.Collapsed;
            if (view is WC.Control control) { control.FontFamily = new(model.Font.FontFamily.Name); control.FontSize = model.Font.SizeInPoints * 96 / 72; }
            // Editable values, especially API secrets, are never control names.
            string name = model.AccessibleName ?? (model is TextBoxBase or NumericUpDown ? model.Name : model.Text);
            if (name.Length > 0) W.Automation.AutomationProperties.SetName(view, name);
        });
        if (model.Name.Length > 0) W.Automation.AutomationProperties.SetAutomationId(view, model.Name);
        WpfControlCoordinates.Register(model, view);
    }
    internal W.FrameworkElement Create(Control model)
    {
        W.FrameworkElement view;
        switch (model)
        {
            case SettingsInputFrame frame:
                view = Create(frame.Field); Bind(frame, view); return view;
            case SettingsTextArea area:
                view = Create(area.Field); Bind(area, view); return view;
            case InputBindingCaptureButton or GameShortcutButton:
                // These editors use the existing keyboard capture/message filter.
                // One leaf host avoids nested WPF/WinForms interoperation.
                view = Host(model); break;
            case CheckBox check: view = Check(check); break;
            case Button button: view = Button(button); break;
            case ComboBox combo: view = Combo(combo); break;
            case TextBox text: view = Text(text); break;
            case NumericUpDown number: view = Number(number); break;
            case Label label: view = Label(label); break;
            case FlowLayoutPanel flow:
                view = Children(flow, flow.FlowDirection is FlowDirection.TopDown or FlowDirection.BottomUp); break;
            case WorkspaceAlignedRow or SettingsValueRow: view = Children(model, false); break;
            case LanguageCombinationPicker: view = Children(model, false); break;
            case WorkspaceSurface surface:
                view = new WC.Border { Child = Children(surface, true), Padding = new(16), CornerRadius = new(6), BorderThickness = new(1) };
                view.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border"); break;
            case TableLayoutPanel table: view = Table(table); break;
            case Panel panel: view = Children(panel, true); break;
            default: view = Host(model); break;
        }
        Bind(model, view, model is not NumericUpDown);
        return view;
    }
    private W.FrameworkElement Label(Label model)
    {
        NativeFields++;
        var view = new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap, VerticalAlignment = W.VerticalAlignment.Center };
        Watch(model, () => { view.Text = model.Text; view.FontFamily = new(model.Font.FontFamily.Name); view.FontSize = model.Font.SizeInPoints * 96 / 72;
            view.FontWeight = model.Font.Bold ? W.FontWeights.Bold : W.FontWeights.Normal;
            view.Foreground = WpfDrawing.Brush(model is SettingsNote || model.Name is "SettingsHelp" or "SettingsNote" ? SettingsColors.Muted : UiTheme.Current.Text); });
        return view;
    }
    private W.FrameworkElement Button(Button model)
    {
        NativeFields++;
        var view = new WC.Button { HorizontalAlignment = W.HorizontalAlignment.Left, MinHeight = 36, Padding = new(12, 5, 12, 5) };
        view.Click += (_, _) => { if (view.IsEnabled) WpfSettingsModel.Raise(model, "OnClick"); Refresh(); };
        Watch(model, () =>
        {
            bool primary = model is GameActionButton { Primary: true } or ScreenshotRebuildButton { Primary: true } || Equals(model.Tag, "settings-primary");
            string? icon = model switch { GameActionButton a => a.IconKey, ScreenshotRebuildButton s => s.IconKey, _ => null };
            if (string.IsNullOrWhiteSpace(icon)) icon = null;
            WpfThemeResources.SetButton(view, model.Text, icon, primary, compact: model is ScreenshotRebuildButton);
            if (model is ScreenshotRebuildButton shot) { view.MinHeight = shot.LogicalHeight; view.Padding = new(6, 2, 6, 2); }
            if (UseModelDimensions && model is GameActionButton action) view.MinHeight = action.LogicalHeight;
            if (primary) view.Background = WpfDrawing.Brush(SettingsColors.PrimaryFill);
            if (model is GameActionButton { TabStyle: true } tab)
            {
                view.Background = WpfDrawing.Brush(UiTheme.Current.Main); view.Foreground = WpfDrawing.Brush(tab.Primary ? UiTheme.Current.Accent : UiTheme.Current.Text);
                view.BorderThickness = new(0, 0, 0, 2); view.BorderBrush = tab.Primary ? WpfDrawing.Brush(UiTheme.Current.Accent) : System.Windows.Media.Brushes.Transparent;
            }
            else if (model is GameActionButton { NavigationStyle: true }) { view.BorderThickness = new(0); view.Background = WpfDrawing.Brush(UiTheme.Current.Main); }
            if (model is ThemeChoiceButton choice)
            {
                bool selected = ReferenceEquals(UiTheme.Current, choice.Mode == ApplicationThemeMode.Night ? ThemePalette.Night : ThemePalette.Day);
                view.MinWidth = 96; view.Background = WpfDrawing.Brush(selected ? WorkspaceSkin.SoftAccent : UiTheme.Current.Secondary);
                view.BorderBrush = WpfDrawing.Brush(selected ? UiTheme.Current.Accent : UiTheme.Current.Border);
                view.BorderThickness = new(selected ? 2 : 1);
            }
        });
        return view;
    }
    private W.FrameworkElement Check(CheckBox model)
    {
        NativeFields++; bool updating = false;
        var view = new WC.CheckBox { VerticalContentAlignment = W.VerticalAlignment.Center, MinHeight = 30, IsThreeState = model.ThreeState };
        void Sync() { updating = true; try { view.Content = new WC.TextBlock { Text = model.Text, TextWrapping = W.TextWrapping.Wrap }; view.IsChecked = model.CheckState == CheckState.Indeterminate ? null : model.Checked; } finally { updating = false; } }
        void Changed(object? sender, W.RoutedEventArgs args) { if (!updating) { model.CheckState = view.IsChecked is null ? CheckState.Indeterminate : view.IsChecked.Value ? CheckState.Checked : CheckState.Unchecked; Refresh(); } }
        view.Checked += Changed; view.Unchecked += Changed; view.Indeterminate += Changed;
        EventHandler handler = (_, _) => Sync(); model.CheckStateChanged += handler; unsubscribe.Add(() => model.CheckStateChanged -= handler); Watch(model, Sync);
        return view;
    }
    private W.FrameworkElement Combo(ComboBox model)
    {
        NativeFields++; bool updating = false;
        var view = new WC.ComboBox { MinWidth = 100, MaxWidth = 320, MinHeight = 36, HorizontalAlignment = W.HorizontalAlignment.Left, IsEditable = model.DropDownStyle != ComboBoxStyle.DropDownList };
        view.Width = UseModelDimensions ? Math.Clamp(model.Width * 96d / model.DeviceDpi, 100, 400) : model.Name.EndsWith("FontFamily", StringComparison.Ordinal) ? 240 : 320;
        void Sync()
        {
            updating = true;
            try
            {
                // Translate only presentation labels. Selection still uses the
                // original model index, retaining profile IDs and custom values.
                var items = model.Items.Cast<object>().Select(MainForm.InterfaceItemText).ToArray();
                if (!view.Items.Cast<string>().SequenceEqual(items)) { view.ItemsSource = items; }
                view.SelectedIndex = model.SelectedIndex;
                if (view.IsEditable && view.Text != model.Text) view.Text = model.Text;
                view.Foreground = WpfDrawing.Brush(UiTheme.Current.Text);
            }
            finally { updating = false; }
        }
        view.SelectionChanged += (_, _) => { if (!updating) { model.SelectedIndex = view.SelectedIndex; Refresh(); } };
        view.AddHandler(WC.Primitives.TextBoxBase.TextChangedEvent, new WC.TextChangedEventHandler((_, _) => { if (!updating && view.IsEditable) model.Text = view.Text; }));
        view.DropDownOpened += (_, _) => { WpfSettingsModel.Raise(model, "OnDropDown"); Sync(); };
        view.DropDownClosed += (_, _) => { WpfSettingsModel.Raise(model, "OnDropDownClosed"); Refresh(); };
        view.PreviewMouseWheel += (_, e) => { if (!view.IsDropDownOpen) { e.Handled = true; if (FindScroll(view) is { } scroll) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta); } };
        EventHandler handler = (_, _) => Sync(); model.SelectedIndexChanged += handler; unsubscribe.Add(() => model.SelectedIndexChanged -= handler); Watch(model, Sync);
        return view;
    }
    private static WC.ScrollViewer? FindScroll(W.DependencyObject child)
    { for (var p = System.Windows.Media.VisualTreeHelper.GetParent(child); p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p)) if (p is WC.ScrollViewer scroll) return scroll; return null; }
    private W.FrameworkElement Text(TextBox model)
    {
        NativeFields++; bool updating = false;
        if (model.UseSystemPasswordChar || model.PasswordChar != '\0')
        {
            var surface = new WC.Grid();
            var password = new WC.PasswordBox { MinHeight = 36, MaxWidth = 620, Padding = new(10, 6, 10, 6), HorizontalAlignment = W.HorizontalAlignment.Stretch };
            var revealed = new WC.TextBox { MinHeight = 36, MaxWidth = 620, Visibility = W.Visibility.Collapsed };
            surface.Children.Add(password); surface.Children.Add(revealed);
            password.PasswordChanged += (_, _) => { if (!updating) model.Text = password.Password; };
            revealed.TextChanged += (_, _) => { if (!updating && revealed.IsVisible) model.Text = revealed.Text; };
            Watch(model, () => { updating = true; try {
                bool masked = model.UseSystemPasswordChar || model.PasswordChar != '\0';
                if (password.Password != model.Text) password.Password = model.Text;
                revealed.Text = masked ? "" : model.Text;
                password.Visibility = masked ? W.Visibility.Visible : W.Visibility.Collapsed; revealed.Visibility = masked ? W.Visibility.Collapsed : W.Visibility.Visible;
                password.Foreground = WpfDrawing.Brush(UiTheme.Current.Text); password.Background = WpfDrawing.Brush(UiTheme.Current.Secondary);
            } finally { updating = false; } });
            return surface;
        }
        var view = new WC.TextBox { MinHeight = 36, MaxWidth = 620, IsReadOnly = model.ReadOnly, AcceptsReturn = model.Multiline, TextWrapping = model.Multiline ? W.TextWrapping.Wrap : W.TextWrapping.NoWrap,
            VerticalScrollBarVisibility = model.Multiline ? WC.ScrollBarVisibility.Auto : WC.ScrollBarVisibility.Hidden, HorizontalAlignment = W.HorizontalAlignment.Stretch };
        if (model.Multiline) view.Height = Math.Max(112, model.Height * 96d / model.DeviceDpi);
        view.TextChanged += (_, _) => { if (!updating) model.Text = view.Text; };
        view.LostKeyboardFocus += (_, _) => WpfSettingsModel.Raise(model, "OnValidated");
        Watch(model, () => { updating = true; try { if (view.Text != model.Text) view.Text = model.Text; view.IsReadOnly = model.ReadOnly; } finally { updating = false; } });
        return view;
    }
    private W.FrameworkElement Number(NumericUpDown model)
    {
        NativeFields++; bool updating = false;
        var row = new WC.Grid { Width = 144, Height = 36, HorizontalAlignment = W.HorizontalAlignment.Left };
        row.ColumnDefinitions.Add(new() { Width = new(30) }); row.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new(30) });
        var edit = new WC.TextBox { TextAlignment = W.TextAlignment.Center, Padding = new(0, 6, 0, 6), BorderThickness = new(0) };
        var minus = WpfThemeResources.Button("−", null, () => model.Value = Math.Max(model.Minimum, model.Value - model.Increment));
        var plus = WpfThemeResources.Button("+", null, () => model.Value = Math.Min(model.Maximum, model.Value + model.Increment));
        minus.Padding = plus.Padding = new(0); minus.BorderThickness = plus.BorderThickness = new(0);
        row.Children.Add(minus); WC.Grid.SetColumn(edit, 1); row.Children.Add(edit); WC.Grid.SetColumn(plus, 2); row.Children.Add(plus);
        void Sync() { updating = true; try { edit.Text = model.Value.ToString("F" + model.DecimalPlaces, System.Globalization.CultureInfo.CurrentCulture); minus.IsEnabled = model.Value > model.Minimum; plus.IsEnabled = model.Value < model.Maximum; } finally { updating = false; } }
        edit.TextChanged += (_, _) => { if (!updating && decimal.TryParse(edit.Text, out var value) && value >= model.Minimum && value <= model.Maximum) model.Value = value; };
        edit.LostKeyboardFocus += (_, _) => Sync();
        edit.PreviewKeyDown += (_, e) => { if (e.Key is WI.Key.Up or WI.Key.Down) { model.Value = Math.Clamp(model.Value + (e.Key == WI.Key.Up ? model.Increment : -model.Increment), model.Minimum, model.Maximum); e.Handled = true; } };
        EventHandler handler = (_, _) => Sync(); model.ValueChanged += handler; unsubscribe.Add(() => model.ValueChanged -= handler); Watch(model, Sync);
        return new WC.Border { Child = row, CornerRadius = new(5), BorderThickness = new(1), BorderBrush = WpfDrawing.Brush(UiTheme.Current.Border), HorizontalAlignment = W.HorizontalAlignment.Left };
    }
    private W.FrameworkElement Children(Control model, bool vertical)
    {
        WC.Panel view = vertical ? new WC.StackPanel() : new WC.WrapPanel();
        foreach (Control child in model.Controls.Cast<Control>().ToArray())
        {
            var item = Create(child); item.Margin = vertical ? new(0, 0, 0, 8) : new(0, 0, 12, 4);
            view.Children.Add(item);
        }
        return view;
    }
    private W.FrameworkElement Table(TableLayoutPanel model)
    {
        if (UseModelDimensions && model.ColumnCount <= 1) return Children(model, true);
        var grid = new WC.Grid();
        for (int i = 0; i < Math.Max(1, model.ColumnCount); i++) grid.ColumnDefinitions.Add(new() { Width = i == model.ColumnCount - 1 ? new(1, W.GridUnitType.Star) : W.GridLength.Auto });
        for (int i = 0; i < Math.Max(model.RowCount, model.Controls.Count); i++) grid.RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        foreach (Control child in model.Controls.Cast<Control>().ToArray())
        {
            var item = Create(child); var cell = model.GetCellPosition(child); WC.Grid.SetRow(item, Math.Max(0, cell.Row)); WC.Grid.SetColumn(item, Math.Max(0, cell.Column));
            WC.Grid.SetRowSpan(item, model.GetRowSpan(child)); WC.Grid.SetColumnSpan(item, model.GetColumnSpan(child)); item.Margin = new(0, 0, 12, 8); grid.Children.Add(item);
        }
        return grid;
    }
    private W.FrameworkElement Host(Control model)
    {
        // Hidden legacy layout can temporarily compress these editors before
        // reparenting. Retain their designed width so complete bindings fit.
        double minimumWidth = model is InputBindingCaptureButton ? 190 : model is GameShortcutButton ? 220 : 100;
        var host = new System.Windows.Forms.Integration.WindowsFormsHost { MinHeight = 36, Height = Math.Max(36, model.Height * 96d / model.DeviceDpi), Width = Math.Max(minimumWidth, model.Width * 96d / model.DeviceDpi), HorizontalAlignment = W.HorizontalAlignment.Left };
        model.Parent?.Controls.Remove(model); model.Dock = DockStyle.Fill; host.Child = model; hosted.Add(host);
        refreshers.Add(() => { if (!model.IsDisposed) { model.BackColor = UiTheme.Current.Secondary; model.ForeColor = UiTheme.Current.Text; model.Invalidate(); } });
        return host;
    }
    public void Dispose() { foreach (var detach in unsubscribe) detach(); foreach (var host in hosted) host.Dispose(); unsubscribe.Clear(); refreshers.Clear(); hosted.Clear(); }
}
