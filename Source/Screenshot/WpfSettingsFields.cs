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
    private static readonly MethodInfo SetState = typeof(Control).GetMethod("SetState", BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static void SetLogicalVisibility(Control model, bool visible) => SetState.Invoke(model, [VisibleState, visible]);
    internal static bool LocallyVisible(Control model) => (bool)State.Invoke(model, [VisibleState])!;
    internal static bool LocallyEnabled(Control model) => (bool)State.Invoke(model, [EnabledState])!;
    internal static bool CanDispatch(Control model) => !model.IsDisposed && !model.Disposing && model.FindForm() is not { IsDisposed: true } and not { Disposing: true };
    internal static void Raise(Control model, string name)
    {
        if (!CanDispatch(model)) return;
        var method = model.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(EventArgs)], null);
        try { method?.Invoke(model, [EventArgs.Empty]); }
        catch (TargetInvocationException e) when (e.InnerException is { } cause) { ExceptionDispatchInfo.Capture(cause).Throw(); }
    }
}

internal sealed partial class WpfSettingsFields : IDisposable
{
    private readonly List<Action> refreshers = [];
    private readonly List<Action> unsubscribe = [];
    private readonly HashSet<(Control Model,W.FrameworkElement View)> inputBindings = [];
    internal int NativeFields { get; private set; }
    internal bool UseModelDimensions { get; init; }
    internal bool RetainLayout { get; init; }
    internal IReadOnlyList<string> HostedTypes => [];
    internal void Refresh() { foreach (var refresh in refreshers.ToArray()) refresh(); }
    private void Watch(Control source, Action refresh)
    {
        EventHandler handler = (_, _) => refresh();
        bool queued = false;
        InvalidateEventHandler invalidated = (_, _) =>
        {
            if (queued || W.Application.Current is not { } application) return;
            queued = true;
            application.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)(() => { queued = false; if (!source.IsDisposed && refreshers.Contains(refresh)) refresh(); }));
        };
        source.TextChanged += handler; source.EnabledChanged += handler; source.VisibleChanged += handler; source.FontChanged += handler;
        source.Invalidated += invalidated;
        unsubscribe.Add(() => { source.TextChanged -= handler; source.EnabledChanged -= handler; source.VisibleChanged -= handler; source.FontChanged -= handler; source.Invalidated -= invalidated; });
        refreshers.Add(refresh); refresh();
    }
    internal void Bind(Control model, W.FrameworkElement view, bool visibility = true, bool interactions = true)
    {
        // Numeric wrappers deliberately hide their retained native spinner;
        // only a text area's own visibility controls this disclosure row.
        Control? field = model is SettingsTextArea area ? area.Field : null;
        void RefreshBinding()
        {
            view.IsEnabled = WpfSettingsModel.LocallyEnabled(model);
            // A styled input wrapper must not make a hidden field (and its
            // settings row) visible again during a later binding refresh.
            if (visibility) view.Visibility = WpfSettingsModel.LocallyVisible(model) && (field is null || WpfSettingsModel.LocallyVisible(field)) ? W.Visibility.Visible : W.Visibility.Collapsed;
            if (view is WC.Control control) { control.FontFamily = WpfDrawing.FontFamily(model.Font.FontFamily.Name); control.FontSize = model.Font.SizeInPoints * 96 / 72; }
            // Editable values, especially API secrets, are never control names.
            string name = model.AccessibleName ?? (model is TextBoxBase or NumericUpDown ? model.Name : model.Text);
            if (name.Length > 0) W.Automation.AutomationProperties.SetName(view, name);
        }
        Watch(model, RefreshBinding);
        if (field is not null) Watch(field, RefreshBinding);
        if (!interactions || !inputBindings.Add((model,view))) return;
        if (model.Name.Length > 0) W.Automation.AutomationProperties.SetAutomationId(view, model.Name);
        WI.KeyboardNavigation.SetTabIndex(view, model.TabIndex);
        WI.KeyboardNavigation.SetIsTabStop(view, model.TabStop);
        if (!string.IsNullOrWhiteSpace(model.AccessibleDescription)) W.Automation.AutomationProperties.SetHelpText(view, model.AccessibleDescription);
        view.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { WpfSettingsModel.Raise(model, "OnEnter"); WpfSettingsModel.Raise(model, "OnGotFocus"); }
            else
            {
                var validation=new System.ComponentModel.CancelEventArgs(); Raise(model,"OnValidating",validation);
                if (!validation.Cancel) WpfSettingsModel.Raise(model,"OnValidated");
                WpfSettingsModel.Raise(model,"OnLeave"); WpfSettingsModel.Raise(model,"OnLostFocus");
            }
        };
        WpfControlCoordinates.Register(model, view);
        if (model.ContextMenuStrip is not null)
        {
            view.ContextMenu = new WC.ContextMenu();
            view.ContextMenuOpening += (_, e) => { if (model.ContextMenuStrip is { } menu) { e.Handled = WpfNativeMenus.Show(menu); } };
        }
    }
    internal W.FrameworkElement Create(Control model)
    {
        W.FrameworkElement view;
        if (RetainLayout && model is Panel and not ScreenshotRebuildPane) return NativeLayout(model);
        switch (model)
        {
            case WorkspaceTextField frame:
                // This is one input, not a vertical content section. Mapping it
                // through Children adds 8 DIP below the editor; horizontal rows
                // then stretch neighboring buttons to that padded height.
                view = Text(frame.Field); Bind(frame.Field, view, visibility: false); break;
            case SettingsInputFrame frame:
                view = Create(frame.Field); Bind(frame, view); return view;
            case SettingsTextArea area:
                view = Create(area.Field); Bind(area, view); return view;
            case InputBindingCaptureButton or GameShortcutButton:
                view = Shortcut((Button)model); break;
            case CheckBox check: view = Check(check); break;
            case Button button: view = Button(button); break;
            case ComboBox combo: view = Combo(combo); break;
            case TextBox text: view = Text(text); break;
            case NumericUpDown number: view = Number(number); break;
            case LinkLabel link: view = Native(link); break;
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
            default: view = Native(model); break;
        }
        Bind(model, view, model is not NumericUpDown);
        return view;
    }
    private W.FrameworkElement Label(Label model)
    {
        NativeFields++;
        var view = new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap, VerticalAlignment = W.VerticalAlignment.Center };
        Watch(model, () => { view.Text = model.Text; view.FontFamily = WpfDrawing.FontFamily(model.Font.FontFamily.Name); view.FontSize = model.Font.SizeInPoints * 96 / 72;
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
            bool primary = model is GameActionButton { Primary: true, TabStyle: false, NavigationStyle: false } or ScreenshotRebuildButton { Primary: true } || Equals(model.Tag, "settings-primary");
            string? icon = model switch { GameActionButton a => a.IconKey, ScreenshotRebuildButton s => s.IconKey, _ => null };
            if (string.IsNullOrWhiteSpace(icon)) icon = null;
            WpfThemeResources.SetButton(view, model.Text, icon, primary, compact: model is ScreenshotRebuildButton { LogicalHeight: < 36 }, coverOverlay: model is GameActionButton { CoverOverlay: true });
            if (model is ScreenshotRebuildButton shot) { view.MinHeight = shot.LogicalHeight; view.Padding = shot.LogicalHeight >= 36 ? new(10, 5, 10, 5) : new(6, 2, 6, 2); }
            if (UseModelDimensions && model is GameActionButton action) view.MinHeight = action.LogicalHeight;
            if (primary) view.Background = WpfDrawing.Brush(SettingsColors.PrimaryFill);
            if (model is GameActionButton { TabStyle: true } tab)
            {
                view.SetResourceReference(WC.Control.StyleProperty, "Fusion.TabButton");
                view.Background = WpfDrawing.Brush(UiTheme.Current.Main); view.Foreground = WpfDrawing.Brush(tab.Primary ? UiTheme.Current.Accent : UiTheme.Current.Text);
                view.BorderThickness = new(0, 0, 0, 2); view.BorderBrush = tab.Primary ? WpfDrawing.Brush(UiTheme.Current.Accent) : System.Windows.Media.Brushes.Transparent;
            }
            else if (model is GameActionButton { NavigationStyle: true }) { view.SetResourceReference(WC.Control.StyleProperty, "Fusion.NavButton"); view.BorderThickness = new(0); view.Background = WpfDrawing.Brush(UiTheme.Current.Main); }
            if (model is ThemeChoiceButton choice)
            {
                bool selected = ReferenceEquals(UiTheme.Current, choice.Mode == ApplicationThemeMode.Night ? ThemePalette.Night : ThemePalette.Day);
                view.MinWidth = 96; view.Background = WpfDrawing.Brush(selected ? WorkspaceSkin.SoftAccent : UiTheme.Current.Secondary);
                view.BorderBrush = WpfDrawing.Brush(selected ? UiTheme.Current.Accent : UiTheme.Current.Border);
                view.BorderThickness = new(selected ? 2 : 1);
            }
            if (RetainLayout) view.Padding = new(4, 2, 4, 2);
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
        view.SelectionChanged += (_, _) => { if (!updating) { model.SelectedIndex = view.SelectedIndex; WpfSettingsModel.Raise(model, "OnSelectionChangeCommitted"); Refresh(); } };
        view.AddHandler(WC.Primitives.TextBoxBase.TextChangedEvent, new WC.TextChangedEventHandler((_, _) => { if (!updating && view.IsEditable) model.Text = view.Text; }));
        view.DropDownOpened += (_, _) => { WpfSettingsModel.Raise(model, "OnDropDown"); Sync(); };
        view.DropDownClosed += (_, _) => { WpfSettingsModel.Raise(model, "OnDropDownClosed"); Refresh(); };
        view.PreviewMouseWheel += (_, e) => { if (!view.IsDropDownOpen) { e.Handled = true; if (FindScroll(view) is { } scroll) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta); } };
        EventHandler handler = (_, _) => Sync(); model.SelectedIndexChanged += handler; unsubscribe.Add(() => model.SelectedIndexChanged -= handler); Watch(model, Sync);
        return view;
    }
    private static WC.ScrollViewer? FindScroll(W.DependencyObject child)
    { for (var p = System.Windows.Media.VisualTreeHelper.GetParent(child); p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p)) if (p is WC.ScrollViewer scroll) return scroll; return null; }
    internal static void ScrollEditor(WC.TextBox view, int delta, bool editing)
    {
        if (editing)
        {
            double distance = W.SystemParameters.WheelScrollLines < 0 ? view.ViewportHeight : W.SystemParameters.WheelScrollLines * view.FontSize * 1.35;
            view.ScrollToVerticalOffset(view.VerticalOffset - delta / 120d * distance);
        }
        else if (FindScroll(view) is { } page)
        {
            double distance = W.SystemParameters.WheelScrollLines < 0 ? page.ViewportHeight : W.SystemParameters.WheelScrollLines * 16d;
            page.ScrollToVerticalOffset(page.VerticalOffset - delta / 120d * distance);
        }
    }
    private W.FrameworkElement Text(TextBox model)
    {
        NativeFields++; bool updating = false;
        if (model.UseSystemPasswordChar || model.PasswordChar != '\0')
        {
            var surface = new WC.Grid();
            var password = new WC.PasswordBox { MinHeight = 36, Padding = new(10, 6, 10, 6), HorizontalAlignment = W.HorizontalAlignment.Stretch };
            var revealed = new WC.TextBox { MinHeight = 36, Visibility = W.Visibility.Collapsed };
            surface.Children.Add(password); surface.Children.Add(revealed);
            password.PasswordChanged += (_, _) => { if (!updating) model.Text = password.Password; };
            revealed.TextChanged += (_, _) => { if (!updating && revealed.IsVisible) model.Text = revealed.Text; };
            Watch(model, () => { updating = true; try {
                bool masked = model.UseSystemPasswordChar || model.PasswordChar != '\0';
                if (password.Password != model.Text) password.Password = model.Text;
                revealed.Text = masked ? "" : model.Text;
                password.Visibility = masked ? W.Visibility.Visible : W.Visibility.Collapsed; revealed.Visibility = masked ? W.Visibility.Collapsed : W.Visibility.Visible;
            } finally { updating = false; } });
            return surface;
        }
        var view = new WC.TextBox { MinHeight = 36, IsReadOnly = model.ReadOnly, AcceptsReturn = model.Multiline, TextWrapping = model.Multiline ? W.TextWrapping.Wrap : W.TextWrapping.NoWrap,
            VerticalScrollBarVisibility = model.Multiline ? WC.ScrollBarVisibility.Auto : WC.ScrollBarVisibility.Hidden, HorizontalAlignment = W.HorizontalAlignment.Stretch };
        if (model.Multiline) view.Height = Math.Max(112, model.Height * 96d / model.DeviceDpi);
        if (model.Multiline)
        {
            view.VerticalContentAlignment = W.VerticalAlignment.Top;
            // Hovering an editor must not interrupt page scrolling. Once it has
            // keyboard focus, keep wheel input inside, including at either end.
            view.PreviewMouseWheel += (_, e) =>
            {
                ScrollEditor(view, e.Delta, view.IsKeyboardFocusWithin);
                e.Handled = true;
            };
        }
        view.TextChanged += (_, _) => { if (!updating) model.Text = view.Text; };
        Watch(model, () => { updating = true; try { if (view.Text != model.Text) view.Text = model.Text; view.IsReadOnly = model.ReadOnly; } finally { updating = false; } });
        return view;
    }
    private W.FrameworkElement Number(NumericUpDown model)
    {
        NativeFields++; bool updating = false;
        var row = new WC.Grid { Width = 144, Height = 36, HorizontalAlignment = W.HorizontalAlignment.Left };
        row.ColumnDefinitions.Add(new() { Width = new(30) }); row.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new(30) });
        var edit = new WC.TextBox { TextAlignment = W.TextAlignment.Center, Padding = new(0, 6, 0, 6), BorderThickness = new(0) };
        var minus = new WC.Button { Content = "−" }; var plus = new WC.Button { Content = "+" };
        minus.Click += (_, _) => model.Value = Math.Max(model.Minimum, model.Value - model.Increment);
        plus.Click += (_, _) => model.Value = Math.Min(model.Maximum, model.Value + model.Increment);
        foreach (var step in new[] { minus, plus }) step.SetResourceReference(WC.Control.StyleProperty, "Fusion.StepperButton");
        edit.Background = System.Windows.Media.Brushes.Transparent;
        W.Automation.AutomationProperties.SetName(minus, UiLanguage.Choose("减小", "Decrease"));
        W.Automation.AutomationProperties.SetName(plus, UiLanguage.Choose("增大", "Increase"));
        WpfUiLanguage.Property(minus,W.Automation.AutomationProperties.NameProperty,"减小");WpfUiLanguage.Property(plus,W.Automation.AutomationProperties.NameProperty,"增大");
        row.Children.Add(minus); WC.Grid.SetColumn(edit, 1); row.Children.Add(edit); WC.Grid.SetColumn(plus, 2); row.Children.Add(plus);
        void Sync() { updating = true; try { edit.Text = model.Value.ToString("F" + model.DecimalPlaces, System.Globalization.CultureInfo.CurrentCulture); minus.IsEnabled = model.Value > model.Minimum; plus.IsEnabled = model.Value < model.Maximum; } finally { updating = false; } }
        edit.TextChanged += (_, _) => { if (!updating && decimal.TryParse(edit.Text, out var value) && value >= model.Minimum && value <= model.Maximum) model.Value = value; };
        edit.LostKeyboardFocus += (_, _) => Sync();
        edit.PreviewKeyDown += (_, e) => { if (e.Key is WI.Key.Up or WI.Key.Down) { model.Value = Math.Clamp(model.Value + (e.Key == WI.Key.Up ? model.Increment : -model.Increment), model.Minimum, model.Maximum); e.Handled = true; } };
        EventHandler handler = (_, _) => Sync(); model.ValueChanged += handler; unsubscribe.Add(() => model.ValueChanged -= handler); Watch(model, Sync);
        var outline = new WC.Border { Child = row, CornerRadius = new(5), BorderThickness = new(1), HorizontalAlignment = W.HorizontalAlignment.Left };
        outline.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border");
        outline.SetResourceReference(WC.Border.BackgroundProperty, "Fusion.Main");
        return outline;
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
    public void Dispose() { foreach (var detach in unsubscribe.ToArray()) detach(); unsubscribe.Clear(); refreshers.Clear(); inputBindings.Clear(); }
}
