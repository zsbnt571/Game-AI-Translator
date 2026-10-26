using System.Runtime.CompilerServices;
using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

// Only product-owned presentation slots opt in. Editable text, game dialogue,
// paths, profile names and translation cells are never traversed as UI strings.
internal static class WpfUiLanguage
{
    private sealed class Source(string text) { internal string Raw=text, Display=""; }
    private static readonly ConditionalWeakTable<W.DependencyObject,Dictionary<W.DependencyProperty,Source>> Sources=new();
    private static readonly List<WeakReference<W.DependencyObject>> Properties=[];
    private static readonly List<WeakReference<NativeLocalizedTextBlock>> Labels=[];
    internal static void Register(NativeLocalizedTextBlock label)=>Labels.Add(new(label));
    internal static void Refresh()
    {
        for(int i=Labels.Count-1;i>=0;i--)
            if(Labels[i].TryGetTarget(out var label))label.RefreshLanguage();else Labels.RemoveAt(i);
        for(int i=Properties.Count-1;i>=0;i--)
            if(Properties[i].TryGetTarget(out var target)&&Sources.TryGetValue(target,out var bindings))
                foreach(var property in bindings.Keys.ToArray())Property(target,property);
            else Properties.RemoveAt(i);
    }
    internal static void Property(W.DependencyObject target,W.DependencyProperty property)
    {
        if(target.GetValue(property) is not string value)return;
        if(!Sources.TryGetValue(target,out var properties))
        {
            properties=new();Sources.Add(target,properties);Properties.Add(new(target));
        }
        if(!properties.TryGetValue(property,out var source))properties[property]=source=new(UiLanguage.Original(value));
        else if(value!=source.Display)source.Raw=UiLanguage.Original(value);
        source.Display=UiLanguage.Text(source.Raw);
        if(value!=source.Display)target.SetCurrentValue(property,source.Display);
    }
    // Callers that own the source caption must supply it explicitly. Different
    // commands can share a translation (for example German Zurück for Back and
    // Previous); reverse lookup cannot reconstruct the intended command.
    internal static void Property(W.DependencyObject target,W.DependencyProperty property,string canonicalSource)
    {
        if(!Sources.TryGetValue(target,out var properties))
        {properties=new();Sources.Add(target,properties);Properties.Add(new(target));}
        if(!properties.TryGetValue(property,out var source))properties[property]=source=new(canonicalSource);
        source.Raw=canonicalSource;source.Display=UiLanguage.Text(canonicalSource);
        if(!Equals(target.GetValue(property),source.Display))target.SetCurrentValue(property,source.Display);
    }
    internal static void Dialog(W.DependencyObject root)
    {
        if(root is W.Window)Property(root,W.Window.TitleProperty);
        // Captions are software-owned. TextBox/PasswordBox contents and data-bound
        // cells are deliberately absent from this walk.
        if(root is WC.HeaderedContentControl)Property(root,WC.HeaderedContentControl.HeaderProperty);
        if(root is WC.HeaderedItemsControl)Property(root,WC.HeaderedItemsControl.HeaderProperty);
        if(root is WC.Button or WC.CheckBox or WC.RadioButton)Property(root,WC.ContentControl.ContentProperty);
        if(root is W.FrameworkElement)Property(root,W.FrameworkElement.ToolTipProperty);
        if(root is WC.DataGrid grid)foreach(var column in grid.Columns)Property(column,WC.DataGridColumn.HeaderProperty);
        foreach(var child in W.LogicalTreeHelper.GetChildren(root).OfType<W.DependencyObject>())Dialog(child);
    }
}

internal sealed class NativeLocalizedTextBlock : WC.TextBlock
{
    private string source="";
    private bool applying;
    internal bool Localize {get;set;}=true;
    static NativeLocalizedTextBlock()=>TextProperty.OverrideMetadata(typeof(NativeLocalizedTextBlock),new W.FrameworkPropertyMetadata("",TextChanged));
    public NativeLocalizedTextBlock()=>WpfUiLanguage.Register(this);
    private static void TextChanged(W.DependencyObject target,W.DependencyPropertyChangedEventArgs e)
    {
        var label=(NativeLocalizedTextBlock)target;if(label.applying)return;
        label.source=label.Localize?UiLanguage.Original((string?)e.NewValue??""):(string?)e.NewValue??"";label.RefreshLanguage();
    }
    internal void RefreshLanguage()
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(RefreshLanguage);return;}
        applying=true;
        try { var next=Localize?UiLanguage.Text(source):source;if(Text!=next)SetCurrentValue(TextProperty,next); }
        finally { applying=false; }
    }
}
