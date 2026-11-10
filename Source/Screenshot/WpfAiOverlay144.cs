using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using M = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

// This overlay is part of the application visual tree, never a separate HWND.
// Its bounds are the workspace client area, independent of Windows menu alignment.
internal sealed class WpfAiOverlay144 : C.Grid
{
    private readonly C.Canvas canvas=new(){ClipToBounds=true};
    private readonly C.Border backdrop=new(){Background=M.Brushes.Transparent};
    private W.UIElement? child;
    internal W.FrameworkElement? PlacementTarget { get; set; }
    internal W.UIElement? Child { get=>child; set {canvas.Children.Clear();child=value;if(value is not null)canvas.Children.Add(value);} }
    internal bool IsOpen { get=>Visibility==W.Visibility.Visible; set {if(value==IsOpen)return;Visibility=value?W.Visibility.Visible:W.Visibility.Collapsed;if(value){UpdatePosition();Opened?.Invoke(this,EventArgs.Empty);}else Closed?.Invoke(this,EventArgs.Empty);} }
    internal event EventHandler? Opened;
    internal event EventHandler? Closed;
    internal void AllowOutsideInput()=>backdrop.IsHitTestVisible=false;
    internal WpfAiOverlay144()
    {
        Visibility=W.Visibility.Collapsed;ClipToBounds=true;Focusable=false;
        C.Grid.SetRowSpan(this,99);C.Grid.SetColumnSpan(this,99);C.Panel.SetZIndex(this,1000);
        Children.Add(backdrop);Children.Add(canvas);backdrop.MouseLeftButtonDown+=(_,e)=>{e.Handled=true;IsOpen=false;};
        SizeChanged+=(_,_)=>UpdatePosition();LayoutUpdated+=(_,_)=>{if(IsOpen)UpdatePosition();};
        PreviewKeyDown+=(_,e)=>{if(e.Key==I.Key.Escape){e.Handled=true;IsOpen=false;}};
        I.KeyboardNavigation.SetTabNavigation(this,I.KeyboardNavigationMode.Cycle);
    }
    internal void UpdatePosition()
    {
        if(!IsOpen||child is not W.FrameworkElement surface||ActualWidth<1||ActualHeight<1)return;
        double width=Math.Min(420,Math.Max(1,ActualWidth-16)),height=Math.Max(1,ActualHeight-16);
        if(surface.Tag is double requested)width=Math.Min(requested,width);
        surface.Width=width;surface.MaxWidth=width;surface.MaxHeight=height;
        surface.Measure(new W.Size(width,height));double measuredHeight=Math.Min(height,surface.DesiredSize.Height);
        W.Point anchor=new(8,ActualHeight-8);double anchorHeight=0;
        try{if(PlacementTarget is {IsVisible:true} target){anchor=target.TransformToVisual(this).Transform(new W.Point());anchorHeight=target.ActualHeight;}}catch(InvalidOperationException){}
        double x=Math.Clamp(anchor.X,8,Math.Max(8,ActualWidth-width-8));
        double above=anchor.Y-measuredHeight-8,below=anchor.Y+anchorHeight+8;
        double y=above>=8?above:below+measuredHeight<=ActualHeight-8?below:Math.Clamp(anchor.Y,8,Math.Max(8,ActualHeight-measuredHeight-8));
        C.Canvas.SetLeft(surface,x);C.Canvas.SetTop(surface,y);
    }
}

// ComboBox retains its selection API and accessible editor, while all choices
// are shown in an in-window list. Editing filters; it never changes selection.
internal sealed class WpfAiSelection144 : IDisposable
{
    private readonly C.Grid host;
    private readonly C.ComboBox combo;
    private readonly WpfAiOverlay144 overlay;
    private readonly C.ListBox list=new(){BorderThickness=new(0),MaxHeight=300,HorizontalContentAlignment=W.HorizontalAlignment.Stretch};
    private readonly C.TextBlock empty=new(){TextWrapping=W.TextWrapping.Wrap,Margin=new(12)};
    private readonly Func<IEnumerable<object>> source;
    private readonly Func<object,string> label;
    private readonly Func<string,IEnumerable<object>>? filter;
    private bool selecting,filtering,openingFromTyping,resettingNativeDropdown;
    private readonly Func<bool> editingAllowed;
    private readonly C.Border surface;
    private object[] displayed=[];
    private string[] displayedLabels=[];
    private C.ListBoxItem? pressedItem;
    internal WpfAiSelection144(C.Grid host,C.ComboBox combo,Func<IEnumerable<object>> source,Func<object,string> label,Func<string,IEnumerable<object>>? filter=null,Func<bool>? editingAllowed=null)
    {
        this.host=host;this.combo=combo;this.source=source;this.label=label;this.filter=filter;this.editingAllowed=editingAllowed??(()=>true);
        overlay=new(){PlacementTarget=combo};C.Panel.SetZIndex(overlay,1100);host.Children.Add(overlay);
        overlay.AllowOutsideInput();host.PreviewMouseDown+=OutsideDown;host.Unloaded+=HostUnloaded;host.Loaded+=HostLoaded;
        surface=new(){CornerRadius=new(10),Padding=new(5),BorderThickness=new(1),Tag=420d};surface.SetResourceReference(C.Border.BackgroundProperty,"Fusion.Secondary");surface.SetResourceReference(C.Border.BorderBrushProperty,"Fusion.Border");
        var body=new C.Grid();body.Children.Add(list);body.Children.Add(empty);surface.Child=body;overlay.Child=surface;
        list.SetResourceReference(C.Control.BackgroundProperty,"Fusion.Secondary");list.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");empty.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");
        W.Automation.AutomationProperties.SetAutomationId(surface,W.Automation.AutomationProperties.GetAutomationId(combo)+"Choices");
        combo.Template=(C.ControlTemplate)W.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
          <Border Background="{DynamicResource Fusion.Control}" BorderBrush="{DynamicResource Fusion.Border}" BorderThickness="1" CornerRadius="7"><Grid>
            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="30"/></Grid.ColumnDefinitions>
            <ContentPresenter x:Name="Display" Margin="10,0,0,0" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" IsHitTestVisible="False"/>
            <TextBox x:Name="PART_EditableTextBox" Visibility="Collapsed" Background="Transparent" BorderThickness="0" Margin="5,0,0,0" VerticalContentAlignment="Center" Foreground="{DynamicResource Fusion.Text}"/>
            <ToggleButton Grid.Column="1" Focusable="False" ><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent"><Path Data="M 0,0 L 4,4 L 8,0" Stroke="{DynamicResource Fusion.Text}" StrokeThickness="1.4" Width="8" Height="4" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border></ControlTemplate></ToggleButton.Template></ToggleButton>
          </Grid></Border>
          <ControlTemplate.Triggers><Trigger Property="IsEditable" Value="True"><Setter TargetName="Display" Property="Visibility" Value="Collapsed"/><Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """);
        combo.DropDownOpened+=Opened;combo.DropDownClosed+=Closed;combo.PreviewKeyDown+=Key;
        combo.PreviewMouseLeftButtonDown+=(_,e)=>{filtering=false;bool arrow=Ancestor<C.Primitives.ToggleButton>(e.OriginalSource as W.DependencyObject) is not null;if(!combo.IsEditable||arrow){if(overlay.IsOpen)overlay.IsOpen=false;else OpenChoices();e.Handled=true;}};
        combo.AddHandler(C.TextBox.TextChangedEvent,new C.TextChangedEventHandler(TextChanged));
        overlay.Closed+=(_,_)=>{bool restore=list.IsKeyboardFocusWithin;if(combo.IsDropDownOpen)combo.IsDropDownOpen=false;if(restore&&combo.IsVisible&&combo.IsEnabled)combo.Focus();};
        list.PreviewMouseLeftButtonDown+=(_,e)=>pressedItem=RowAt(e.OriginalSource as W.DependencyObject);
        list.PreviewMouseLeftButtonUp+=(_,e)=>{var row=RowAt(e.OriginalSource as W.DependencyObject);if(row is not null&&ReferenceEquals(row,pressedItem)&&row.Tag is {} item){Commit(item);e.Handled=true;}pressedItem=null;};
        list.PreviewKeyDown+=(_,e)=>{if(e.Key==I.Key.Enter&&list.SelectedItem is C.ListBoxItem{Tag:{} item}){Commit(item);e.Handled=true;}};
    }
    private C.ListBoxItem? RowAt(W.DependencyObject? source)
    {
        for(var node=source;node is not null&&node!=list;node=Parent(node))
        {if(node is C.Primitives.ScrollBar or C.Primitives.Thumb)return null;if(node is C.ListBoxItem row)return row;}
        return null;
    }
    private void OutsideDown(object sender,I.MouseButtonEventArgs e)
    {
        if(!overlay.IsOpen)return;
        for(var node=e.OriginalSource as W.DependencyObject;node is not null;node=Parent(node))if(ReferenceEquals(node,surface)||ReferenceEquals(node,combo))return;
        // Dismiss the list without swallowing the same gesture aimed at the
        // underlying slider or button. No event replay or synthetic click.
        overlay.IsOpen=false;
    }
    private void HostUnloaded(object sender,W.RoutedEventArgs e){overlay.IsOpen=false;host.PreviewMouseDown-=OutsideDown;}
    private void HostLoaded(object sender,W.RoutedEventArgs e){host.PreviewMouseDown-=OutsideDown;host.PreviewMouseDown+=OutsideDown;}
    private static W.DependencyObject? Parent(W.DependencyObject node)=>node is M.Visual or System.Windows.Media.Media3D.Visual3D?M.VisualTreeHelper.GetParent(node):W.LogicalTreeHelper.GetParent(node);
    private static T? Ancestor<T>(W.DependencyObject? node) where T:W.DependencyObject{for(;node is not null;node=Parent(node))if(node is T value)return value;return null;}
    private void Commit(object item){selecting=true;try{combo.SelectedItem=item;combo.Text=label(item);overlay.IsOpen=false;combo.Focus();}finally{selecting=false;}}
    private void OpenChoices(){Refresh();overlay.IsOpen=true;}
    private void Opened(object? sender,EventArgs e)
    {
        // The in-window choices are siblings, not ComboBox descendants. Native dropdown
        // capture would redirect their thumb/scrollbar input back to this ComboBox.
        resettingNativeDropdown=true;try{combo.IsDropDownOpen=false;}finally{resettingNativeDropdown=false;}
        if(!openingFromTyping)filtering=false;OpenChoices();
    }
    private void Closed(object? sender,EventArgs e){if(!resettingNativeDropdown)overlay.IsOpen=false;}
    private void TextChanged(object sender,C.TextChangedEventArgs e){if(!selecting&&editingAllowed()&&combo.IsKeyboardFocusWithin&&e.OriginalSource is C.TextBox){filtering=true;Refresh();if(combo.IsEditable)OpenChoices();}}
    private void Key(object sender,I.KeyEventArgs e){if(e.Key==I.Key.Escape){overlay.IsOpen=false;e.Handled=true;}else if(e.Key==I.Key.Down){OpenChoices();if(list.Items.Count>0){list.SelectedIndex=0;list.Focus();}e.Handled=true;}}
    internal void Close()=>overlay.IsOpen=false;
    internal void Refresh()
    {
        string query=combo.IsEditable&&filtering?combo.Text.Trim():"";IEnumerable<object> values=filter is not null?filter(query):source().Where(x=>query.Length==0||label(x).Contains(query,StringComparison.OrdinalIgnoreCase));
        var next=values.Take(500).ToArray();var labels=next.Select(label).ToArray();
        if(!displayed.SequenceEqual(next)||!displayedLabels.SequenceEqual(labels)){pressedItem=null;displayed=next;displayedLabels=labels;list.Items.Clear();foreach(var item in next){var row=new C.ListBoxItem{Content=label(item),Tag=item,Padding=new(9,7,9,7)};row.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");list.Items.Add(row);}}
        empty.Text=AiWorkspaceText144.Text("NoChoices");empty.Visibility=list.Items.Count==0?W.Visibility.Visible:W.Visibility.Collapsed;list.Visibility=list.Items.Count==0?W.Visibility.Collapsed:W.Visibility.Visible;overlay.UpdatePosition();
    }
    internal bool IsOpen=>overlay.IsOpen;
    public void Dispose(){overlay.IsOpen=false;host.PreviewMouseDown-=OutsideDown;host.Unloaded-=HostUnloaded;host.Loaded-=HostLoaded;if(overlay.Parent is C.Panel owner)owner.Children.Remove(overlay);combo.DropDownOpened-=Opened;combo.DropDownClosed-=Closed;combo.PreviewKeyDown-=Key;}
}
