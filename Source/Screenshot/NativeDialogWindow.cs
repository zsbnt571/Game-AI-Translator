using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

// Windows owns modality, layout and focus. Only the result enum and HWND owner
// contract remain shared with the existing services and system file pickers.
internal class NativeDialogWindow : W.Window, IWin32Window, IDisposable
{
    internal DialogResult Result { get; private set; } = System.Windows.Forms.DialogResult.Cancel;
    internal bool IsDisposed { get; private set; }
    internal bool IsModal { get; private set; }
    internal NativeDialogWindow(string title, double width = 560, double height = 420)
    {
        Title = title; WpfUiLanguage.Property(this,TitleProperty,UiLanguage.Original(title)); Width = width; Height = height;
        MinWidth = Math.Min(width, 420); MinHeight = Math.Min(height, 180);
        ShowInTaskbar = false; WindowStartupLocation = W.WindowStartupLocation.CenterOwner;
        UseLayoutRounding = SnapsToDevicePixels = true;
        WpfPresentationRecovery.Attach(this);
        Resources.MergedDictionaries.Add(WpfThemeResources.Create()); WpfThemeResources.Apply(Resources);
        SetResourceReference(BackgroundProperty, "Fusion.Main"); SetResourceReference(ForegroundProperty, "Fusion.Text");
        FontFamily = new("Microsoft YaHei UI"); FontSize = 14;
        using(var icon=AppBranding.CreateIcon())
        {
            // ICO bitmaps can carry a GDI mask that Bitmap.Clone cannot convert.
            // Copy the icon through the native WPF decoder before releasing it.
            var image=System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle,W.Int32Rect.Empty,WM.Imaging.BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();Icon=image;
        }
        void LanguageChanged(object? sender,EventArgs e){if(!IsDisposed)WpfUiLanguage.Dialog(this);}
        UiLanguage.LanguageChanged+=LanguageChanged;
        Loaded+=(_,_)=>WpfUiLanguage.Dialog(this);
        Closed += (_, _) => { IsDisposed = true; UiLanguage.LanguageChanged-=LanguageChanged; };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Finish(System.Windows.Forms.DialogResult.Cancel); e.Handled = true; } };
    }
    public IntPtr Handle => new System.Windows.Interop.WindowInteropHelper(this).Handle;
    internal static W.Window? OwnerWindow(IWin32Window? owner) => owner switch
    {
        NativeDialogWindow window => window,
        MainForm main => main.WpfNativeWindow,
        DesktopForm form => form.NativeWindow,
        _ => null
    };
    private void AttachOwner(IWin32Window? owner)
    {
        var parent = OwnerWindow(owner);
        if (parent is { IsVisible: true }) { Owner = parent; Topmost = parent.Topmost; FontFamily = parent.FontFamily; FontSize = parent.FontSize; }
        else if (owner is not null && owner.Handle != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(this).Owner = owner.Handle;
        var area = W.SystemParameters.WorkArea; MaxWidth = area.Width; MaxHeight = area.Height;
        Width = Math.Min(Width, MaxWidth); Height = Math.Min(Height, MaxHeight);
        MinWidth = Math.Min(MinWidth, MaxWidth); MinHeight = Math.Min(MinHeight, MaxHeight);
    }
    internal DialogResult ShowDialog(IWin32Window? owner) { AttachOwner(owner); IsModal=true; try { base.ShowDialog(); return Result; } finally { IsModal=false; } }
    internal void Show(IWin32Window? owner) { AttachOwner(owner); base.Show(); }
    internal void Finish(DialogResult result) { Result = result; Close(); }
    internal WC.Button ResultButton(string text, DialogResult result, bool primary = false)
    {
        var button = NativeDialogControls.Button(text, () => Finish(result), primary);
        button.IsDefault = primary; return button;
    }
    public void Dispose() { if (!IsDisposed) Close(); IsDisposed = true; }
}

internal static class NativeDialogControls
{
    internal static WC.TextBlock Text(string text, double? size = null)
    {
        var block = new NativeLocalizedTextBlock { Text = text, TextWrapping = W.TextWrapping.Wrap, Margin = new(0, 4, 0, 12) };
        if(size is { } value)block.FontSize=value;return block;
    }
    internal static WC.TextBox Input(string text = "", bool multiline = false, bool readOnly = false)
    {
        var input = new WC.TextBox { Text = text, AcceptsReturn = multiline, IsReadOnly = readOnly, TextWrapping = multiline ? W.TextWrapping.Wrap : W.TextWrapping.NoWrap, MinHeight = multiline ? 110 : 36, Padding = new(10, 7, 10, 7), VerticalContentAlignment = W.VerticalAlignment.Center, Margin = new(0, 0, 0, 12), VerticalScrollBarVisibility = multiline ? WC.ScrollBarVisibility.Auto : WC.ScrollBarVisibility.Hidden };
        input.PreviewMouseWheel += (_, e) => { if (!input.IsKeyboardFocusWithin || !multiline) WpfScrollRouting.RouteToParent(input, e); };
        return input;
    }
    internal static WC.ComboBox Choice(IEnumerable<string> items, int selected = 0)
    {
        // Keep the value/order stable. Only the item template is localized, so
        // changing interface language cannot trigger a different selection.
        var combo = new WC.ComboBox { ItemsSource = items.ToArray(), SelectedIndex = selected, MinHeight = 36, Margin = new(0, 0, 0, 12) };
        var label=new W.FrameworkElementFactory(typeof(NativeLocalizedTextBlock));label.SetBinding(WC.TextBlock.TextProperty,new System.Windows.Data.Binding());
        combo.ItemTemplate=new W.DataTemplate{VisualTree=label};
        combo.PreviewMouseWheel += (_, e) => { if (!combo.IsDropDownOpen) { WpfScrollRouting.RouteToParent(combo, e); e.Handled = true; } };
        return combo;
    }
    internal static WC.Button Button(string text, Action action, bool primary = false)
    {
        var button = new WC.Button { Content = text, MinHeight = 36, MinWidth = 92, Padding = new(14, 7, 14, 7), Margin = new(0, 0, 8, 8) };
        WpfUiLanguage.Property(button,WC.ContentControl.ContentProperty,UiLanguage.Original(text));
        if (primary) button.SetResourceReference(WC.Control.BackgroundProperty, "Fusion.Accent");
        button.Click += (_, _) => action(); return button;
    }
    internal static WC.WrapPanel Row(params W.UIElement[] elements) { var row = new WC.WrapPanel(); foreach (var element in elements) row.Children.Add(element); return row; }
    internal static WC.StackPanel Column(params W.UIElement[] elements) { var column = new WC.StackPanel(); foreach (var element in elements) column.Children.Add(element); return column; }
    internal static WC.Grid Layout(W.UIElement body, W.UIElement actions, bool scroll = true)
    {
        var grid = new WC.Grid { Margin = new(24) }; grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        grid.Children.Add(scroll ? new WC.ScrollViewer { Content = body, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled, Padding = new(0, 0, 8, 0) } : body);
        WC.Grid.SetRow(actions, 1); grid.Children.Add(actions); return grid;
    }
}

internal sealed class NativeMessageDialog : NativeDialogWindow
{
    internal NativeMessageDialog(string title, string message, AppDialogKind kind, string? details) : base(title, 560, 280)
    {
        var body = NativeDialogControls.Column(NativeDialogControls.Text(message));
        var actions = NativeDialogControls.Row();
        if (!string.IsNullOrWhiteSpace(details))
        {
            var editor = NativeDialogControls.Input(UiLanguage.Text(details), true, true); editor.Visibility = W.Visibility.Collapsed; body.Children.Add(editor);
            void RefreshDetails(object? sender,EventArgs e)=>editor.Text=UiLanguage.Text(details);
            UiLanguage.LanguageChanged+=RefreshDetails;Closed+=(_,_)=>UiLanguage.LanguageChanged-=RefreshDetails;
            WC.Button? toggle = null; toggle = NativeDialogControls.Button("查看详情", () => { bool show = editor.Visibility != W.Visibility.Visible; editor.Visibility = show ? W.Visibility.Visible : W.Visibility.Collapsed; toggle!.Content = UiLanguage.Text(show ? "隐藏详情" : "查看详情"); Height = show ? 430 : 280; }); actions.Children.Add(toggle);
        }
        if (kind == AppDialogKind.Confirmation) actions.Children.Add(ResultButton("取消", System.Windows.Forms.DialogResult.No));
        actions.Children.Add(ResultButton("确定", kind == AppDialogKind.Confirmation ? System.Windows.Forms.DialogResult.Yes : System.Windows.Forms.DialogResult.OK, true));
        Content = NativeDialogControls.Layout(body, actions);
    }
}

internal sealed class NativeProfileDialog : NativeDialogWindow
{
    internal WC.ComboBox? TemplateBox { get; }
    internal NativeProfileDialog(string title, string heading, string message, bool template) : base(title, 560, 330)
    {
        var body = NativeDialogControls.Column(NativeDialogControls.Text(heading, 21));
        if (template) { TemplateBox = NativeDialogControls.Choice(["DeepSeek", "OpenAI", "自定义兼容接口（需要密钥）", "本地／无鉴权兼容接口"]); body.Children.Add(TemplateBox); }
        body.Children.Add(NativeDialogControls.Text(message));
        var actions = NativeDialogControls.Row(ResultButton("取消", System.Windows.Forms.DialogResult.Cancel));
        if (!template) actions.Children.Add(ResultButton("放弃修改", System.Windows.Forms.DialogResult.No));
        actions.Children.Add(ResultButton(template ? "继续" : "保存", template ? System.Windows.Forms.DialogResult.OK : System.Windows.Forms.DialogResult.Yes, true));
        Content = NativeDialogControls.Layout(body, actions);
    }
}

internal sealed class NativeProfileDeleteDialog : NativeDialogWindow
{
    internal sealed record Choice(string Id, string Name);
    internal WC.ComboBox Replacement { get; }
    internal WC.Button Delete { get; }
    internal string? ReplacementId => (Replacement.SelectedItem as Choice)?.Id;
    internal NativeProfileDeleteDialog(string name, string[] references, (string Id, string Name)[] choices) : base("删除方案", 620, 390)
    {
        string Description()=>UiLanguage.Format($"删除“{name}”前，请为以下位置选择替代方案：\n{string.Join("; ", references.Select(UiLanguage.Text))}\n\n确认后一起保存。游戏文件、存档和已有译文不会删除。");
        var description=NativeDialogControls.Text(Description());
        void RefreshDescription(object? sender,EventArgs e)=>description.Text=Description();
        UiLanguage.LanguageChanged+=RefreshDescription;Closed+=(_,_)=>UiLanguage.LanguageChanged-=RefreshDescription;
        Replacement = new() { ItemsSource = choices.Select(c => new Choice(c.Id, c.Name)).ToArray(), DisplayMemberPath = "Name", MinHeight = 36, SelectedIndex = -1 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(Replacement, "DeleteProfileReplacement");
        Delete = ResultButton("替换并删除", System.Windows.Forms.DialogResult.OK, true); Delete.IsEnabled = false;
        Replacement.SelectionChanged += (_, _) => Delete.IsEnabled = ReplacementId is not null;
        Content = NativeDialogControls.Layout(NativeDialogControls.Column(NativeDialogControls.Text("选择替代方案后删除", 21), description, NativeDialogControls.Text("替代方案"), Replacement), NativeDialogControls.Row(ResultButton("取消", System.Windows.Forms.DialogResult.Cancel), Delete));
    }
}
