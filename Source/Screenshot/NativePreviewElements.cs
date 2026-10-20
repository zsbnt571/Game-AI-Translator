using W=System.Windows;
using WC=System.Windows.Controls;
using WD=System.Windows.Documents;
using WM=System.Windows.Media;
using WI=System.Windows.Input;
namespace ScreenshotTranslationUiTester;

// The preview controller shares processing services with the main window. Its
// presenters own WPF controls; no hidden RichEdit, PictureBox or layout tree.
internal class NativePreviewElement : NativeRpgElement
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<W.DependencyObject,NativePreviewElement> Owners=new();
    private bool Owns(W.RoutedEventArgs e)
    {for(var node=e.OriginalSource as W.DependencyObject;node is not null;node=node is WM.Visual?WM.VisualTreeHelper.GetParent(node):W.LogicalTreeHelper.GetParent(node))if(Owners.TryGetValue(node,out var owner))return ReferenceEquals(owner,this);return false;}
    internal NativePreviewElement(W.FrameworkElement view):base(view)
    {
        Owners.Add(view,this);
        view.SizeChanged+=(_,_)=>SizeChanged?.Invoke(this,EventArgs.Empty);
        view.AddHandler(WI.Mouse.MouseMoveEvent,new WI.MouseEventHandler((_,e)=>{if(Owns(e))MouseMove?.Invoke(this,Mouse(e,MouseButtons.None));}),true);
        view.MouseLeave+=(_,_)=>MouseLeave?.Invoke(this,EventArgs.Empty);
        view.AddHandler(WI.Mouse.MouseDownEvent,new WI.MouseButtonEventHandler((_,e)=>{if(Owns(e))MouseDown?.Invoke(this,Mouse(e,Button(e.ChangedButton)));}),true);
        view.AddHandler(WI.Mouse.MouseUpEvent,new WI.MouseButtonEventHandler((_,e)=>{if(!Owns(e))return;var a=Mouse(e,Button(e.ChangedButton));MouseUp?.Invoke(this,a);if(e.ChangedButton==WI.MouseButton.Left)Click?.Invoke(this,a);MouseClick?.Invoke(this,a);}),true);
        view.PreviewMouseWheel+=(_,e)=>{if(MouseWheel is null||!Owns(e))return;MouseWheel(this,new MouseEventArgs(MouseButtons.None,0,(int)e.GetPosition(view).X,(int)e.GetPosition(view).Y,e.Delta));e.Handled=true;};
    }
    private static MouseButtons Button(WI.MouseButton button)=>button switch{WI.MouseButton.Left=>MouseButtons.Left,WI.MouseButton.Right=>MouseButtons.Right,WI.MouseButton.Middle=>MouseButtons.Middle,WI.MouseButton.XButton1=>MouseButtons.XButton1,_=>MouseButtons.XButton2};
    private MouseEventArgs Mouse(WI.MouseEventArgs e,MouseButtons button){var p=e.GetPosition(View);return new(button,e is WI.MouseButtonEventArgs b?b.ClickCount:0,(int)p.X,(int)p.Y,0);}
    internal new int Width{get=>(int)(double.IsFinite(View.Width)?View.Width:View.ActualWidth);set=>View.Width=value;}
    internal new int Height{get=>(int)(double.IsFinite(View.Height)?View.Height:View.ActualHeight);set=>View.Height=value;}
    internal Size Size{get=>new(Width,Height);set{Width=value.Width;Height=value.Height;}}
    internal Size ClientSize=>new((int)View.ActualWidth,(int)View.ActualHeight);
    internal Point Location{get=>new(Left,Top);set{WC.Canvas.SetLeft(View,value.X);WC.Canvas.SetTop(View,value.Y);}}
    internal int Left=>(int)(double.IsFinite(WC.Canvas.GetLeft(View))?WC.Canvas.GetLeft(View):0);
    internal int Top=>(int)(double.IsFinite(WC.Canvas.GetTop(View))?WC.Canvas.GetTop(View):0);
    internal int Right=>Left+Width;internal int Bottom=>Top+Height;
    internal Rectangle Bounds=>new(Location,Size);
    internal bool Focused=>View.IsKeyboardFocusWithin;
    internal Cursor Cursor{set=>View.Cursor=value==Cursors.Hand?WI.Cursors.Hand:WI.Cursors.Arrow;}
    internal Color BackColor=>UiTheme.Current.Main;
    internal event EventHandler? SizeChanged,MouseLeave,Click;
    internal event MouseEventHandler? MouseMove,MouseDown,MouseUp,MouseClick,MouseWheel;
    internal void SuspendLayout(){} internal void ResumeLayout(bool perform){if(perform)View.UpdateLayout();}
    internal void PerformLayout()=>View.UpdateLayout();internal void Refresh()=>View.InvalidateVisual();
    internal Point PointToScreen(Point p){var point=View.PointToScreen(new(p.X,p.Y));return new((int)point.X,(int)point.Y);}
    internal Point PointToClient(Point p){var point=View.PointFromScreen(new(p.X,p.Y));return new((int)point.X,(int)point.Y);}
    internal void DrawToBitmap(Bitmap target,Rectangle rectangle)
    {
        var bitmap=new WM.Imaging.RenderTargetBitmap(Math.Max(1,Width),Math.Max(1,Height),96,96,WM.PixelFormats.Pbgra32);bitmap.Render(View);
        var encoder=new WM.Imaging.PngBitmapEncoder();encoder.Frames.Add(WM.Imaging.BitmapFrame.Create(bitmap));using var stream=new MemoryStream();encoder.Save(stream);stream.Position=0;using var copy=new Bitmap(stream);using var drawing=Graphics.FromImage(target);drawing.DrawImage(copy,rectangle);
    }
}

internal sealed class NativePreviewChoice : NativeRpgChoiceBase
{
    internal NativePreviewChoice(int width):base(width){View.Width=width;}
}
internal class NativeRpgChoiceBase : NativePreviewElement
{
    private sealed class CaptionConverter:System.Windows.Data.IValueConverter
    {
        public object Convert(object value,Type type,object parameter,System.Globalization.CultureInfo culture)=>UiLanguage.Text(value?.ToString()??"");
        public object ConvertBack(object value,Type type,object parameter,System.Globalization.CultureInfo culture)=>throw new NotSupportedException();
    }
    internal NativeRpgItems Items{get;}=[];
    private WC.ComboBox Combo=>(WC.ComboBox)View;
    internal NativeRpgChoiceBase(int width):base(new WC.ComboBox{MinWidth=width,MinHeight=36,Margin=new(3)})
    {var text=new W.FrameworkElementFactory(typeof(WC.TextBlock));text.SetValue(WC.TextBlock.TextTrimmingProperty,W.TextTrimming.CharacterEllipsis);text.SetBinding(WC.TextBlock.TextProperty,new System.Windows.Data.Binding{Converter=new CaptionConverter()});Combo.ItemTemplate=new W.DataTemplate{VisualTree=text};Combo.ItemsSource=Items;Combo.SelectionChanged+=(_,_)=>SelectedIndexChanged?.Invoke(this,EventArgs.Empty);Combo.DropDownClosed+=(_,_)=>SelectionChangeCommitted?.Invoke(this,EventArgs.Empty);Combo.PreviewMouseWheel+=(_,e)=>{if(!Combo.IsDropDownOpen)WpfScrollRouting.RouteToParent(View,e);};}
    internal int SelectedIndex{get=>Combo.SelectedIndex;set=>Combo.SelectedIndex=value;}
    internal object? SelectedItem{get=>Combo.SelectedItem;set=>Combo.SelectedItem=value;}
    internal event EventHandler? SelectedIndexChanged,SelectionChangeCommitted;
    internal void BeginUpdate(){}internal void EndUpdate(){}
    internal void RefreshLanguage()
    {
        // Refresh localized bindings without changing the selected item or
        // raising selection events that would alter the user's preview mode.
        void Refresh(W.DependencyObject node)
        {
            if(node is WC.TextBlock text)text.GetBindingExpression(WC.TextBlock.TextProperty)?.UpdateTarget();
            for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);i++)Refresh(System.Windows.Media.VisualTreeHelper.GetChild(node,i));
        }
        Refresh(Combo);
    }
}

internal sealed class NativePreviewPicture : NativePreviewElement
{
    private sealed class Surface(NativePreviewPicture owner):W.FrameworkElement
    {
        protected override void OnRender(WM.DrawingContext dc)
        {
            if(owner.image is Bitmap bitmap)
            {
                double scale=owner.SizeMode==PictureBoxSizeMode.Zoom?Math.Min(RenderSize.Width/bitmap.Width,RenderSize.Height/bitmap.Height):1;
                var bounds=owner.SizeMode==PictureBoxSizeMode.StretchImage?new W.Rect(RenderSize):new((RenderSize.Width-bitmap.Width*scale)/2,(RenderSize.Height-bitmap.Height*scale)/2,bitmap.Width*scale,bitmap.Height*scale);
                dc.DrawImage(WpfDrawing.Image(bitmap),bounds);
            }
            owner.Overlay?.Invoke(new(dc));
        }
    }
    private Image? image;internal Action<WpfCanvasGraphics>? Overlay;
    internal NativePreviewPicture():base(new WC.Grid()) {var grid=(WC.Grid)View;grid.Children.Add(new Surface(this));View.ClipToBounds=true;}
    internal Image? Image{get=>image;set{image=value;((WC.Grid)View).Children[0].InvalidateVisual();}}
    internal PictureBoxSizeMode SizeMode{get;set;}=PictureBoxSizeMode.StretchImage;
    internal DockStyle Dock{get;set;}
    internal override void Invalidate()=>((WC.Grid)View).Children[0].InvalidateVisual();
}

internal sealed class NativePreviewViewport : NativePreviewElement
{
    internal WC.ScrollViewer Scroll=>(WC.ScrollViewer)View;
    private readonly WC.Canvas canvas=new();private Size extent;
    internal NativePreviewViewport():base(new WC.ScrollViewer{HorizontalScrollBarVisibility=WC.ScrollBarVisibility.Auto,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto,CanContentScroll=false})
    {Scroll.Content=canvas;Scroll.SetResourceReference(WC.Control.BackgroundProperty,"Fusion.Main");canvas.Background=WM.Brushes.Transparent;Scroll.SizeChanged+=(_,_)=>UpdateExtent();}
    internal void SetPicture(NativePreviewPicture picture)=>canvas.Children.Add(picture.View);
    internal new Size ClientSize=>new(Math.Max(1,(int)(Scroll.ViewportWidth>0?Scroll.ViewportWidth:View.ActualWidth)),Math.Max(1,(int)(Scroll.ViewportHeight>0?Scroll.ViewportHeight:View.ActualHeight)));
    internal Size AutoScrollMinSize{get=>extent;set{extent=value;UpdateExtent();}}
    private void UpdateExtent(){canvas.Width=Math.Max(ClientSize.Width,extent.Width);canvas.Height=Math.Max(ClientSize.Height,extent.Height);}
    internal Point AutoScrollPosition{get=>new(-(int)Scroll.HorizontalOffset,-(int)Scroll.VerticalOffset);set{Scroll.ScrollToHorizontalOffset(Math.Max(0,value.X));Scroll.ScrollToVerticalOffset(Math.Max(0,value.Y));}}
}

internal sealed class NativePreviewSplit : NativePreviewElement
{
    private WC.Grid Grid=>(WC.Grid)View;private readonly WC.GridSplitter splitter=new(){Width=6,HorizontalAlignment=W.HorizontalAlignment.Stretch};private bool collapsed;
    internal double ImageFraction{get;private set;}=.72;
    internal bool IsDragging{get;private set;}
    internal NativePreviewElement Panel1{get;}=new(new WC.Grid());internal NativePreviewElement Panel2{get;}=new(new WC.Grid());
    internal NativePreviewSplit():base(new WC.Grid())
    {
        splitter.SetResourceReference(WC.Control.BackgroundProperty,"Fusion.Border");
        Grid.ColumnDefinitions.Add(new(){Width=new(72,W.GridUnitType.Star)});Grid.ColumnDefinitions.Add(new(){Width=new(6)});Grid.ColumnDefinitions.Add(new(){Width=new(28,W.GridUnitType.Star)});
        Grid.Children.Add(Panel1.View);WC.Grid.SetColumn(splitter,1);Grid.Children.Add(splitter);WC.Grid.SetColumn(Panel2.View,2);Grid.Children.Add(Panel2.View);
        splitter.DragStarted+=(_,_)=>IsDragging=true;
        splitter.DragCompleted+=(_,e)=>{IsDragging=false;if(!e.Canceled){var a=Grid.ColumnDefinitions[0].Width;var b=Grid.ColumnDefinitions[2].Width;if(a.IsStar&&b.IsStar)ImageFraction=Math.Clamp(a.Value/Math.Max(1,a.Value+b.Value),.1,.9);}};
    }
    internal void Set(NativePreviewViewport image,NativePreviewText text){((WC.Grid)Panel1.View).Children.Add(image.View);((WC.Grid)Panel2.View).Children.Add(text.View);}
    internal bool Panel2Collapsed{get=>collapsed;set{if(collapsed==value)return;collapsed=value;Panel2.Visible=!value;splitter.Visibility=value?W.Visibility.Collapsed:W.Visibility.Visible;Grid.ColumnDefinitions[1].Width=new(value?0:6);Grid.ColumnDefinitions[2].Width=value?new(0):new(1-ImageFraction,W.GridUnitType.Star);Grid.ColumnDefinitions[0].Width=new(ImageFraction,W.GridUnitType.Star);}}
    internal int SplitterWidth=>6;
    internal int SplitterDistance{get=>Panel1.Width;set{if(collapsed)return;Grid.ColumnDefinitions[0].Width=new(Math.Max(1,value),W.GridUnitType.Star);Grid.ColumnDefinitions[2].Width=new(Math.Max(1,ClientSize.Width-value-6),W.GridUnitType.Star);}}
    internal int Panel1MinSize{set=>Grid.ColumnDefinitions[0].MinWidth=value;}internal int Panel2MinSize{set=>Grid.ColumnDefinitions[2].MinWidth=collapsed?0:value;}
}

internal sealed class NativePreviewText : NativePreviewElement
{
    internal WC.RichTextBox Box=>(WC.RichTextBox)View;
    private string text="";private Font font=new("Microsoft YaHei UI",10);
    internal NativePreviewText():base(new WC.RichTextBox{IsReadOnly=true,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=WC.ScrollBarVisibility.Auto,BorderThickness=new(0),Padding=new(10)})
    {Box.Document.PagePadding=new(0);Box.SetResourceReference(WC.Control.BackgroundProperty,"Fusion.Main");Box.SetResourceReference(WC.Control.ForegroundProperty,"Fusion.Text");}
    internal override string Text{get=>text;set{if(text==value)return;text=value;Box.Document=new WD.FlowDocument(new WD.Paragraph(new WD.Run(text)){Margin=new(0)}){PagePadding=new(0),FontFamily=Box.FontFamily,FontSize=Box.FontSize};}}
    internal int TextLength=>text.Length;
    private WD.TextPointer Pointer(int index)
    {
        int wanted=Math.Clamp(index,0,text.Length);var at=Box.Document.ContentStart;
        while(at is not null&&at.CompareTo(Box.Document.ContentEnd)<0){if(at.GetPointerContext(WD.LogicalDirection.Forward)==WD.TextPointerContext.Text){var part=at.GetTextInRun(WD.LogicalDirection.Forward);if(wanted<=part.Length)return at.GetPositionAtOffset(wanted)!;wanted-=part.Length;at=at.GetPositionAtOffset(part.Length);}else at=at.GetNextContextPosition(WD.LogicalDirection.Forward);}return Box.Document.ContentEnd;
    }
    private int Index(WD.TextPointer p)
    {int count=0;var at=Box.Document.ContentStart;while(at is not null&&at.CompareTo(p)<0){if(at.GetPointerContext(WD.LogicalDirection.Forward)==WD.TextPointerContext.Text){int n=at.GetTextInRun(WD.LogicalDirection.Forward).Length;var end=at.GetPositionAtOffset(n)!;if(end.CompareTo(p)>=0)return Math.Min(TextLength,count+at.GetOffsetToPosition(p));count+=n;at=end;}else at=at.GetNextContextPosition(WD.LogicalDirection.Forward);}return Math.Min(TextLength,count);}
    internal int SelectionStart=>Index(Box.Selection.Start);internal int SelectionLength=>Math.Max(0,Index(Box.Selection.End)-SelectionStart);
    internal string SelectedText=>text.Substring(SelectionStart,SelectionLength);
    internal void Select(int start,int length)=>Box.Selection.Select(Pointer(start),Pointer(start+length));
    internal Color SelectionBackColor{set=>Box.Selection.ApplyPropertyValue(WD.TextElement.BackgroundProperty,WpfDrawing.Brush(value));}
    internal Font Font{get=>font;set{var previous=font;font=value;Box.FontFamily=WpfDrawing.FontFamily(value.Name);Box.FontSize=value.SizeInPoints*96/72;Box.Document.FontFamily=Box.FontFamily;Box.Document.FontSize=Box.FontSize;if(!ReferenceEquals(previous,value))previous.Dispose();}}
    internal void ApplyFont(ApiSettings settings){var profile=FontManager.ResolvePreviewProfile(settings);Font=FontManager.Create(profile.PrimaryFamily,profile.Size);}
    internal int GetCharIndexFromPosition(Point p){var pointer=Box.GetPositionFromPoint(new(p.X,p.Y),true);return pointer is null?0:Index(pointer);}
    internal Point GetPositionFromCharIndex(int index){var rect=Pointer(index).GetCharacterRect(WD.LogicalDirection.Forward);return new((int)rect.X,(int)rect.Y);}
    internal Point ScrollPosition=>new((int)Box.HorizontalOffset,(int)Box.VerticalOffset);
    internal void RestoreScroll(Point value){Box.ScrollToHorizontalOffset(value.X);Box.ScrollToVerticalOffset(value.Y);}
    internal int FirstVisibleLine=>(int)(Box.VerticalOffset/Math.Max(1,font.Height));
    internal void RestoreLine(int line)=>Box.ScrollToVerticalOffset(Math.Max(0,line)*font.Height);
    internal void ScrollLines(int lines)=>RestoreLine(FirstVisibleLine+lines);
    internal void ScrollBottom()=>Box.ScrollToEnd();
    internal void Clear()=>Text="";internal void AppendText(string value)=>Text+=value;
    public override void Dispose(){if(IsDisposed)return;font.Dispose();base.Dispose();}
}

internal sealed class NativePreviewList : NativePreviewElement
{
    private WC.ListBox List=>(WC.ListBox)View;
    internal NativePreviewList():base(new WC.ListBox{SelectionMode=WC.SelectionMode.Extended}){List.SelectionChanged+=(_,_)=>SelectedIndexChanged?.Invoke(this,EventArgs.Empty);}
    internal WC.ItemCollection Items=>List.Items;internal System.Collections.IList SelectedItems=>List.SelectedItems;
    internal void SetSelected(int index,bool selected){var value=Items[index];if(selected&&!List.SelectedItems.Contains(value))List.SelectedItems.Add(value);else if(!selected)List.SelectedItems.Remove(value);}
    internal event EventHandler? SelectedIndexChanged;internal void BeginUpdate(){}internal void EndUpdate(){}
}
