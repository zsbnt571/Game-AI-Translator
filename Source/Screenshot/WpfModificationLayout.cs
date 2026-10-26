using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;

namespace ScreenshotTranslationUiTester;

// Presentation only: each engine retains its own capabilities and commands.
internal static class WpfModificationLayout
{
    internal const double ListHeight = 400, DetailHeight = 460, RowHeight = 48;
    internal static void Table(WC.DataGrid grid)
    {
        grid.Height = ListHeight; grid.RowHeight = RowHeight; grid.ColumnHeaderHeight = double.NaN;
        grid.BorderThickness = new(1); grid.GridLinesVisibility = WC.DataGridGridLinesVisibility.Horizontal;
        grid.EnableRowVirtualization = grid.EnableColumnVirtualization = true;
        WC.VirtualizingPanel.SetIsVirtualizing(grid, true);
        WC.VirtualizingPanel.SetVirtualizationMode(grid, WC.VirtualizationMode.Recycling);
        grid.SetResourceReference(WC.Control.BackgroundProperty, "Fusion.Secondary");
        grid.SetResourceReference(WC.Control.ForegroundProperty, "Fusion.Text");
        grid.SetResourceReference(WC.Control.BorderBrushProperty, "Fusion.Border");
        grid.SetResourceReference(WC.DataGrid.HorizontalGridLinesBrushProperty, "Fusion.Border");
        grid.SetResourceReference(WC.DataGrid.VerticalGridLinesBrushProperty, "Fusion.Border");
        // Longer interface languages must keep the full column heading visible.
        // Header height may grow; row values and column identities stay intact.
        foreach(var column in grid.Columns.Where(x=>x.Header is string))
        {
            var header=new W.FrameworkElementFactory(typeof(WC.TextBlock));
            header.SetBinding(WC.TextBlock.TextProperty,new WD.Binding());
            header.SetBinding(W.FrameworkElement.ToolTipProperty,new WD.Binding());
            header.SetValue(WC.TextBlock.TextWrappingProperty,W.TextWrapping.Wrap);
            header.SetValue(W.FrameworkElement.MinHeightProperty,20d);
            column.HeaderTemplate=new W.DataTemplate{VisualTree=header};
        }
        foreach (var column in grid.Columns.OfType<WC.DataGridTextColumn>())
        {
            // Value/status headings grow with the UI language. Fixed star
            // shares clipped Russian headings even when the name column had
            // spare space; keep names flexible and size these to their content.
            if (column.Binding is WD.Binding valueBinding && valueBinding.Path.Path is "Value" or "Lock" or "Locked")
            {
                column.Width = WC.DataGridLength.Auto;
                column.MaxWidth = 240;
                var minimum = new WD.MultiBinding { Converter = StatusColumnMinimum.Instance };
                minimum.Bindings.Add(new WD.Binding("Header") { Source = column });
                foreach (string property in new[] { "FontFamily", "FontStyle", "FontWeight", "FontStretch", "FontSize" })
                    minimum.Bindings.Add(new WD.Binding(property) { Source = grid });
                WD.BindingOperations.SetBinding(column, WC.DataGridColumn.MinWidthProperty, minimum);
            }
            var text = new W.Style(typeof(WC.TextBlock));
            text.Setters.Add(new W.Setter(W.FrameworkElement.VerticalAlignmentProperty, W.VerticalAlignment.Center));
            text.Setters.Add(new W.Setter(W.FrameworkElement.MarginProperty, new W.Thickness(8, 0, 8, 0)));
            text.Setters.Add(new W.Setter(WC.TextBlock.TextTrimmingProperty, W.TextTrimming.CharacterEllipsis));
            if (column.Binding is WD.Binding binding) text.Setters.Add(new W.Setter(W.FrameworkElement.ToolTipProperty, new WD.Binding(binding.Path.Path)));
            column.ElementStyle = text;
        }
    }
    private sealed class StatusColumnMinimum : WD.IMultiValueConverter
    {
        internal static readonly StatusColumnMinimum Instance = new();
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (values.Length < 6 || values[1] is not System.Windows.Media.FontFamily family
                || values[2] is not W.FontStyle style || values[3] is not W.FontWeight weight
                || values[4] is not W.FontStretch stretch || values[5] is not double size)
                return 80d;
            // WPF caches an Auto column's first measured header width. A bound
            // minimum updates when the header language/font changes, without
            // recreating the grid or disturbing its current selection.
            var text = new System.Windows.Media.FormattedText(values[0]?.ToString() ?? "", culture,
                W.FlowDirection.LeftToRight, new System.Windows.Media.Typeface(family, style, weight, stretch),
                size, System.Windows.Media.Brushes.Black, 1);
            return Math.Clamp(Math.Ceiling(text.WidthIncludingTrailingWhitespace + 24), 80d, 240d);
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
    internal static WC.WrapPanel Toolbar(params W.FrameworkElement[] children)
    {
        var row = new WC.WrapPanel();
        foreach (var child in children) { child.Margin = new(0, 3, 10, 3); row.Children.Add(child); }
        return row;
    }
    internal static W.FrameworkElement Search(WC.TextBox input, string description)
    {
        input.Width = double.NaN; input.Height = 36; input.MinWidth = 120;
        input.Padding = new(32, 6, 10, 6); input.ToolTip = UiLanguage.Text(description);
        W.Automation.AutomationProperties.SetName(input, UiLanguage.Text(description));
        WpfUiLanguage.Property(input,W.FrameworkElement.ToolTipProperty);
        WpfUiLanguage.Property(input,W.Automation.AutomationProperties.NameProperty);
        var frame = new WC.Grid { HorizontalAlignment = W.HorizontalAlignment.Stretch };
        frame.Children.Add(input);
        frame.Children.Add(new WC.Image { Source = WpfDrawing.Icon("search", UiTheme.Current.SecondaryText), Width = 16, Height = 16, Margin = new(10, 0, 0, 0), HorizontalAlignment = W.HorizontalAlignment.Left, IsHitTestVisible = false });
        var placeholder=new NativeLocalizedTextBlock{Text=description,Margin=new(34,0,10,0),VerticalAlignment=W.VerticalAlignment.Center,TextTrimming=W.TextTrimming.CharacterEllipsis,IsHitTestVisible=false};
        placeholder.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Muted");
        frame.Children.Add(placeholder);
        void SyncPlaceholder()=>placeholder.Visibility=input.Text.Length==0?W.Visibility.Visible:W.Visibility.Collapsed;
        input.TextChanged+=(_,_)=>SyncPlaceholder();SyncPlaceholder();
        return frame;
    }
    internal static WC.ScrollViewer Details(W.FrameworkElement content)
        => new() { Content = content, Height = DetailHeight, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled };
    internal static WC.ScrollViewer? TableScroll(W.DependencyObject node)
    {
        if(node is WC.ScrollViewer scroll)return scroll;
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);i++)
            if(TableScroll(System.Windows.Media.VisualTreeHelper.GetChild(node,i)) is {} child)return child;
        return null;
    }
    internal static W.DataTemplate Names(string displayPath,string originalPath,string? rawTooltipPath=null)
    {
        var panel=new W.FrameworkElementFactory(typeof(WC.StackPanel));panel.SetValue(W.FrameworkElement.VerticalAlignmentProperty,W.VerticalAlignment.Center);panel.SetValue(W.FrameworkElement.MarginProperty,new W.Thickness(8,0,8,0));
        foreach(string path in new[]{displayPath,originalPath})
        {
            var label=new W.FrameworkElementFactory(typeof(WC.TextBlock));label.SetBinding(WC.TextBlock.TextProperty,new WD.Binding(path));label.SetBinding(W.FrameworkElement.ToolTipProperty,new WD.Binding(rawTooltipPath??path));label.SetValue(WC.TextBlock.TextTrimmingProperty,W.TextTrimming.CharacterEllipsis);
            if(path==displayPath)label.SetValue(WC.TextBlock.FontWeightProperty,W.FontWeights.SemiBold);else{label.SetValue(WC.TextBlock.FontSizeProperty,11d);label.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Muted");}
            panel.AppendChild(label);
        }
        return new W.DataTemplate{VisualTree=panel};
    }
}

internal sealed class WpfModificationColumns : WC.Grid
{
    internal WpfModificationColumns()
    {
        ColumnDefinitions.Add(new() { Width = new(57, W.GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = new(43, W.GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = W.GridLength.Auto }); RowDefinitions.Add(new() { Height = W.GridLength.Auto });
    }
    protected override W.Size MeasureOverride(W.Size available)
    {
        bool stack = available.Width < 700;
        if (Children.Count >= 2)
        {
            WC.Grid.SetColumnSpan(Children[0], stack || Children[1].Visibility == W.Visibility.Collapsed ? 2 : 1);
            WC.Grid.SetColumn(Children[1], stack ? 0 : 1); WC.Grid.SetRow(Children[1], stack ? 1 : 0);
            WC.Grid.SetColumnSpan(Children[1], stack ? 2 : 1);
            ((W.FrameworkElement)Children[1]).Margin = stack ? new(0, 12, 0, 0) : new(16, 0, 0, 0);
        }
        return base.MeasureOverride(available);
    }
}
