using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

internal static class WpfNativeMenus
{
    private static readonly HashSet<ToolStripDropDown> Preparing = [];
    private static readonly Dictionary<ToolStripDropDown, WC.ContextMenu> OpenMenus = [];
    internal static bool Close(ToolStripDropDown model) { if (!OpenMenus.Remove(model, out var view)) return false; view.IsOpen = false; return true; }
    internal static bool InterceptOpening(ToolStripDropDown model, System.ComponentModel.CancelEventArgs args)
    { if (Preparing.Contains(model) || args.Cancel || !WpfWorkspace.Enabled) return false; args.Cancel = Show(model); return args.Cancel; }
    internal static WC.ContextMenu Create(ToolStripDropDown? model)
    {
        var menu = new WC.ContextMenu(); menu.Resources.MergedDictionaries.Add(WpfThemeResources.Create()); WpfThemeResources.Apply(menu.Resources);
        if (model is null) return menu;
        foreach (ToolStripItem item in model.Items) Add(menu.Items, item);
        return menu;
    }
    private static void Add(WC.ItemCollection target, ToolStripItem source)
    {
        if (!source.Available) return;
        if (source is ToolStripSeparator) { target.Add(new WC.Separator()); return; }
        var item = new WC.MenuItem { Header = UiLanguage.Text(source.Text), IsEnabled = source.Enabled };
        if(source is WorkspaceMenuItem action&&!string.IsNullOrWhiteSpace(action.IconKey))item.Icon=new WC.Image{Source=WpfDrawing.Icon(action.IconKey,UiTheme.Current.Accent),Width=18,Height=18};
        if (source is ToolStripMenuItem command)
        {
            item.IsCheckable = command.CheckOnClick || command.Checked; item.IsChecked = command.Checked;
            item.InputGestureText = command.ShortcutKeyDisplayString;
            foreach (ToolStripItem child in command.DropDownItems) Add(item.Items, child);
            item.SubmenuOpened += (_, e) => { if (!ReferenceEquals(e.Source, item)) return; typeof(ToolStripDropDownItem).GetMethod("OnDropDownShow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(command, [EventArgs.Empty]); item.Items.Clear(); foreach (ToolStripItem child in command.DropDownItems) Add(item.Items, child); };
        }
        if (source is ToolStripControlHost host)
        {
            var fields = new WpfSettingsFields(); item.Header = fields.Create(host.Control); item.StaysOpenOnClick = true;
            item.Unloaded += (_, _) => fields.Dispose();
        }
        else item.Click += (_, e) => { if (ReferenceEquals(e.OriginalSource, item)) { source.PerformClick(); e.Handled = true; } };
        target.Add(item);
    }
    internal static bool Show(ToolStripDropDown model, Control? anchor = null, Point? point = null, W.FrameworkElement? nativeAnchor = null)
    {
        if (!WpfWorkspace.Enabled || W.Application.Current is null) return false;
        if (OpenMenus.Remove(model, out var previous)) previous.IsOpen = false;
        var opening = new System.ComponentModel.CancelEventArgs(); Preparing.Add(model);
        try { model.GetType().GetMethod("OnOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(model, [opening]); }
        finally { Preparing.Remove(model); }
        if (opening.Cancel) return true;
        var menu = Create(model);
        if(nativeAnchor is not null)
        {
            menu.PlacementTarget=nativeAnchor;menu.Placement=point.HasValue?WC.Primitives.PlacementMode.RelativePoint:WC.Primitives.PlacementMode.Bottom;
            if(point is {} position){double dpi=System.Windows.Media.VisualTreeHelper.GetDpi(nativeAnchor).DpiScaleX;menu.HorizontalOffset=position.X/dpi;menu.VerticalOffset=position.Y/dpi;}
        }
        else if (anchor is not null && WpfControlCoordinates.View(anchor) is { IsVisible: true } view)
        {
            menu.PlacementTarget = view; menu.Placement = WC.Primitives.PlacementMode.Bottom;
        }
        else { menu.Placement = WC.Primitives.PlacementMode.MousePoint; }
        EventHandler disposed = (_, _) => menu.IsOpen = false; model.Disposed += disposed;
        menu.Closed += (_, _) => { model.Disposed -= disposed; OpenMenus.Remove(model); model.GetType().GetMethod("OnClosed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(model, [new ToolStripDropDownClosedEventArgs(ToolStripDropDownCloseReason.CloseCalled)]); };
        OpenMenus[model] = menu; menu.IsOpen = true; return true;
    }
}
