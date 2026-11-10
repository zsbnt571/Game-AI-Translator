using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using P = System.Windows.Controls.Primitives;

namespace ScreenshotTranslationUiTester;

// Typing changes suggestions only. Choice, Enter and an explicit save commit a full wire ID.
internal sealed class WpfAiModelSearch144 : IDisposable
{
    private readonly C.ComboBox combo;
    private readonly Action<string> commit;
    private IReadOnlyList<AiModelDescriptor> catalog = [];
    private bool updating;
    private bool openingFromTyping;
    private string committed = "";
    private W.Window? owner;
    internal WpfAiModelSearch144(C.ComboBox combo, Action<string> onCommit)
    {
        this.combo = combo; commit = onCommit;
        combo.IsEditable = true; combo.IsTextSearchEnabled = false; combo.StaysOpenOnEdit = true;
        combo.AddHandler(C.TextBox.TextChangedEvent, new C.TextChangedEventHandler(TextChanged));
        combo.SelectionChanged += Selected; combo.PreviewKeyDown += KeyDown; combo.DropDownOpened += Opened;
        combo.DropDownClosed += Closed; combo.Unloaded += Unloaded;
    }
    internal void SetCatalog(IEnumerable<AiModelDescriptor> values, string text)
    {
        catalog = values.ToArray(); committed = text.Trim();
        Update(text, false);
    }
    internal bool Commit()
    {
        string value = combo.Text.Trim();
        if (updating) return false;
        if (value == committed) return true;
        // A partial search such as "6" must not silently become the saved default model.
        if (value.Length>0 && !catalog.Any(x=>x.Id==value) && AiModelSearch144.Filter(catalog,value).Count>0) return false;
        committed = value; commit(value); return true;
    }
    private void TextChanged(object sender, C.TextChangedEventArgs e)
    {
        if (updating || e.OriginalSource is not C.TextBox) return;
        Update(combo.Text, combo.IsKeyboardFocusWithin);
    }
    private void Update(string text, bool open, string? query = null)
    {
        if (updating) return;
        updating = true;
        try
        {
            var edit = combo.Template?.FindName("PART_EditableTextBox", combo) as C.TextBox;
            int caret = edit?.CaretIndex ?? text.Length;
            combo.ItemsSource = AiModelSearch144.Filter(catalog, query ?? text).Select(x => x.Id).ToArray();
            combo.Text = text;
            if (edit is not null) { edit.Text = text; edit.CaretIndex = Math.Min(caret, text.Length); }
            if (open)
            {
                openingFromTyping = !combo.IsDropDownOpen && combo.Items.Count > 0;
                combo.IsDropDownOpen = combo.Items.Count > 0;
            }
        }
        finally { updating = false; }
    }
    private void Selected(object sender, C.SelectionChangedEventArgs e)
    {
        if (updating || combo.SelectedItem is not string value || !combo.IsDropDownOpen) return;
        updating = true; combo.Text = value; updating = false;
        Commit(); combo.IsDropDownOpen = false;
    }
    private void KeyDown(object sender, I.KeyEventArgs e)
    {
        if (e.Key != I.Key.Enter) return;
        if(Commit())combo.IsDropDownOpen = false; e.Handled = true;
    }
    private void Opened(object? sender, EventArgs e)
    {
        BoundPopup();
        if(openingFromTyping){openingFromTyping=false;return;}
        if (!updating) Update(combo.Text, false, "");
        if(combo.Items.Count==0)combo.IsDropDownOpen=false;
    }
    private void BoundPopup()
    {
        DetachOwner();
        owner = W.Window.GetWindow(combo);
        if (owner?.Content is not W.FrameworkElement client || client.ActualWidth <= 0 || client.ActualHeight <= 0 ||
            combo.Template?.FindName("PART_Popup", combo) is not P.Popup popup) return;
        popup.PlacementTarget = combo; popup.Placement = P.PlacementMode.Custom;
        if (popup.Child is W.FrameworkElement child)
        {
            child.MinWidth = Math.Min(combo.ActualWidth, client.ActualWidth);
            child.MaxWidth = client.ActualWidth;
            child.MinHeight = 0; child.MaxHeight = Math.Min(360, client.ActualHeight);
        }
        popup.CustomPopupPlacementCallback = (size, target, offset) =>
        {
            var anchor = combo.TranslatePoint(new W.Point(), client);
            return [new P.CustomPopupPlacement(PlacementPoint(anchor, target, size, client.RenderSize), P.PopupPrimaryAxis.None)];
        };
        owner.LocationChanged += OwnerMoved; owner.SizeChanged += OwnerSized;
    }
    internal static W.Point PlacementPoint(W.Point anchor, W.Size target, W.Size popup, W.Size client)
    {
        double x = Math.Clamp(anchor.X, 0, Math.Max(0, client.Width - popup.Width));
        double below = anchor.Y + target.Height;
        double y = below + popup.Height <= client.Height ? below : anchor.Y - popup.Height;
        return new W.Point(x - anchor.X, Math.Clamp(y, 0, Math.Max(0, client.Height - popup.Height)) - anchor.Y);
    }
    private void OwnerMoved(object? sender, EventArgs e) => combo.IsDropDownOpen = false;
    private void OwnerSized(object sender, W.SizeChangedEventArgs e) => combo.IsDropDownOpen = false;
    private void Unloaded(object sender, W.RoutedEventArgs e) { combo.IsDropDownOpen = false; DetachOwner(); }
    private void Closed(object? sender, EventArgs e) => DetachOwner();
    private void DetachOwner()
    {
        if(owner is not null){owner.LocationChanged -= OwnerMoved;owner.SizeChanged -= OwnerSized;owner=null;}
    }
    public void Dispose()
    {
        combo.IsDropDownOpen = false; DetachOwner();
        combo.RemoveHandler(C.TextBox.TextChangedEvent, new C.TextChangedEventHandler(TextChanged));
        combo.SelectionChanged -= Selected; combo.PreviewKeyDown -= KeyDown; combo.DropDownOpened -= Opened;
        combo.DropDownClosed -= Closed; combo.Unloaded -= Unloaded;
    }
}
