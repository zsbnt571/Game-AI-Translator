using System.Globalization;
using System.Runtime.CompilerServices;
using W = System.Windows;
using WM = System.Windows.Media;
using WI = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

internal sealed partial class NativeLibraryNavigation
{
    private NativeNavigation? native;
    private Size? nativeViewport;
    private Size NavigationViewport => nativeViewport ?? new Size(1,1);
    public W.FrameworkElement NativeView
    {
        get
        {
            if(native is not null)return native;
            native=new(this);return native;
        }
    }
    private bool disposed;
    internal Font Font {get;private set;}=SystemFonts.MessageBoxFont!;
    internal int DeviceDpi {get;private set;}=96;
    internal void UpdatePresentation(Font font,int dpi){if(Font.Equals(font)&&DeviceDpi==dpi)return;Font=font;DeviceDpi=dpi;Flatten();Invalidate();}
    private bool Capture {get=>native?.IsMouseCaptured==true;set{if(value)native?.CaptureMouse();else native?.ReleaseMouseCapture();}}
    private bool Focus()=>native?.Focus()==true;
    private void Invalidate()=>native?.InvalidateVisual();
    private void Invalidate(Rectangle area)=>Invalidate();
    private void SetTip(string? value){if(native is not null)native.ToolTip=string.IsNullOrWhiteSpace(value)?null:value;}
    private sealed class NativeNavigation:W.FrameworkElement
    {
        private readonly NativeLibraryNavigation model;
        private bool keyboardFocus,pointerFocus;
        private readonly ConditionalWeakTable<LibraryNavigationNode,RowText> rowTexts=new();
        private readonly Queue<WeakReference<LibraryNavigationNode>> textOrder=new();
        private const int TextCacheLimit=256;
        private WM.Typeface? typeface;
        private string? typefaceFamily;
        private readonly record struct TextKey(string Source,ApplicationLanguage Language,double Width,double FontSize,double Dpi,int Color,double X,double RowHeight);
        private sealed class RowText
        {
            internal TextKey Key;
            internal CultureInfo? Culture;
            internal WM.Typeface? Typeface;
            internal WM.DrawingGroup? Drawing;
            internal double Height;
        }
        private double Scale=>model.DeviceDpi/96d;
        internal NativeNavigation(NativeLibraryNavigation model)
        {
            this.model=model;Focusable=true;FocusVisualStyle=null;ClipToBounds=true;UseLayoutRounding=true;SnapsToDevicePixels=true;
            WM.TextOptions.SetTextFormattingMode(this,WM.TextFormattingMode.Display);
            W.Automation.AutomationProperties.SetName(this,UiLanguage.Text("游戏导航"));
            MouseDown+=(_,e)=>{if(e.ChangedButton is WI.MouseButton.XButton1 or WI.MouseButton.XButton2)return;HandlePointerDown(e.ChangedButton==WI.MouseButton.Right?MouseButtons.Right:MouseButtons.Left,e.ClickCount,Point(e.GetPosition(this)));e.Handled=true;};
            MouseUp+=(_,e)=>{if(e.ChangedButton is WI.MouseButton.XButton1 or WI.MouseButton.XButton2)return;var p=Point(e.GetPosition(this));model.OnMouseUp(new(e.ChangedButton==WI.MouseButton.Right?MouseButtons.Right:MouseButtons.Left,1,p.X,p.Y,0));e.Handled=true;};
            MouseMove+=(_,e)=>{var p=Point(e.GetPosition(this));model.OnMouseMove(new(e.LeftButton==WI.MouseButtonState.Pressed?MouseButtons.Left:MouseButtons.None,0,p.X,p.Y,0));};
            MouseLeave+=(_,_)=>model.OnMouseLeave(EventArgs.Empty);
            MouseWheel+=(_,e)=>{model.OnMouseWheel(new HandledMouseEventArgs(MouseButtons.None,0,0,0,e.Delta));e.Handled=true;};
            LostMouseCapture+=(_,_)=>model.dragging=false;
            GotKeyboardFocus+=(_,_)=>{if(!pointerFocus){keyboardFocus=true;InvalidateVisual();}};
            LostKeyboardFocus+=(_,_)=>{if(keyboardFocus)InvalidateVisual();};
            KeyDown+=(_,e)=>{keyboardFocus=true;var args=new KeyEventArgs((Keys)WI.KeyInterop.VirtualKeyFromKey(e.Key)|Control.ModifierKeys);model.OnKeyDown(args);e.Handled=args.Handled;InvalidateVisual();};
        }
        private void HandlePointerDown(MouseButtons button,int clicks,Point point)
        {
            bool clearKeyboardIndicator=keyboardFocus&&IsKeyboardFocused;
            keyboardFocus=false;pointerFocus=true;
            try{model.OnMouseDown(new(button,clicks,point.X,point.Y,0));}
            finally{pointerFocus=false;}
            // Selection, expansion and scrolling invalidate their changed state.
            // An unchanged pointer click only needs a repaint if it hides a
            // keyboard indicator that was already visible.
            if(clearKeyboardIndicator)InvalidateVisual();
        }
        private Point Point(W.Point p)=>new((int)(p.X*Scale),(int)(p.Y*Scale));
        protected override W.Size ArrangeOverride(W.Size finalSize)
        {
            model.nativeViewport=new((int)Math.Round(finalSize.Width*Scale),(int)Math.Round(finalSize.Height*Scale));
            model.offset=Math.Clamp(model.offset,0,model.ScrollMaximum);
            return finalSize;
        }
        protected override W.Automation.Peers.AutomationPeer OnCreateAutomationPeer()=>new NavigationPeer(this);
        private sealed class NavigationPeer(NativeNavigation owner):W.Automation.Peers.FrameworkElementAutomationPeer(owner)
        {protected override string GetNameCore()=>UiLanguage.Text("游戏导航");protected override W.Automation.Peers.AutomationControlType GetAutomationControlTypeCore()=>W.Automation.Peers.AutomationControlType.Tree;}
        private RowText Text(LibraryNavigationNode node,double width,Color color,WM.Typeface face,double fontSize,double dpi,CultureInfo culture,double x,double rowHeight)
        {
            if(!rowTexts.TryGetValue(node,out var cached))
            {
                // Weak keys do not retain removed branches; the bounded queue
                // also limits glyph storage when browsing a very large tree.
                while(textOrder.Count>=TextCacheLimit)
                    if(textOrder.Dequeue().TryGetTarget(out var prior))rowTexts.Remove(prior);
                cached=new();rowTexts.Add(node,cached);textOrder.Enqueue(new(node));
            }
            var key=new TextKey(node.Text,UiLanguage.Current,width,fontSize,dpi,color.ToArgb(),x,rowHeight);
            if(cached.Drawing is not null&&cached.Key==key&&ReferenceEquals(cached.Typeface,face)&&ReferenceEquals(cached.Culture,culture))return cached;
            var text=new WM.FormattedText(UiLanguage.Text(node.Text),culture,W.FlowDirection.LeftToRight,face,fontSize,WpfDrawing.Brush(color),dpi)
            {MaxTextWidth=width,MaxLineCount=1,Trimming=W.TextTrimming.CharacterEllipsis};
            var drawing=new WM.DrawingGroup();
            // Keep the text's fractional baseline inside the drawing instead
            // of its placement transform. Only the row's Y moves on scroll;
            // this preserves the original text rasterization and glyph reuse.
            double textHeight=text.Height;
            using(var context=drawing.Open())context.DrawText(text,new W.Point(x,(rowHeight-textHeight)/2));
            drawing.Freeze();
            cached.Key=key;cached.Typeface=face;cached.Culture=culture;cached.Drawing=drawing;cached.Height=textHeight;
            return cached;
        }
        protected override void OnRender(WM.DrawingContext dc)
        {
            using var timing=DesktopRenderMetrics.Measure("navigation-wpf");
            var p=UiTheme.Current;dc.DrawRectangle(WpfDrawing.Brush(UiTheme.Sidebar),null,new W.Rect(RenderSize));
            double scale=Scale,height=model.ItemHeight/scale,offset=model.offset/scale;
            var family=model.Font.FontFamily.Name;
            if(typeface is null||typefaceFamily!=family){typeface=new WM.Typeface(family);typefaceFamily=family;}
            double fontSize=model.Font.SizeInPoints*96/72;var culture=CultureInfo.CurrentUICulture;
            for(int i=Math.Max(0,model.offset/model.ItemHeight);i<model.rows.Count&&i*height-offset<ActualHeight;i++)
            {
                var node=model.rows[i];if(node.Tag is not LibraryNavigationTarget target)continue;
                bool selected=node==model.SelectedNode;double y=i*height-offset;
                if(selected||node.Name==model.hoverKey)dc.DrawRoundedRectangle(WpfDrawing.Brush(selected?WorkspaceSkin.SoftAccent:p.Control),null,new W.Rect(6,y,Math.Max(1,ActualWidth-12-(model.ScrollMaximum>0?10:0)),height),5,5);
                if(selected&&keyboardFocus&&IsKeyboardFocused)dc.DrawRoundedRectangle(null,new WM.Pen(WpfDrawing.Brush(p.Accent),1),new W.Rect(7,y+1,Math.Max(1,ActualWidth-14-(model.ScrollMaximum>0?10:0)),Math.Max(1,height-2)),5,5);
                string key=target.Kind switch{"engine" or "heading"=>model.expanded.Contains(node.Name)?"down":"next","collection" or "folder"=>"folder","tag"=>"tag","new-category" or "new-folder" or "add-root"=>"plus","game"=>"game",_=>target.Value switch{"recent"=>"calendar","running"=>"play","favorite"=>"tag",_=>"grid"}};
                var ink=selected?p.Accent:p.SecondaryText;double x=14+node.Level*12;
                var icon=target.Kind=="game"&&model.icons.TryGetValue(target.Value,out var image)?WpfDrawing.Image(image):WpfDrawing.Icon(key,ink);
                dc.DrawImage(icon,new W.Rect(x,y+(height-17)/2,17,17));
                var text=Text(node,Math.Max(1,ActualWidth-x-42),selected?p.Accent:p.Text,typeface,fontSize,scale,culture,x+27,height);
                dc.PushTransform(new WM.TranslateTransform(0,y));
                dc.DrawDrawing(text.Drawing);dc.Pop();
                if(target.Kind=="game"&&model.running.Contains(target.Value))dc.DrawEllipse(WpfDrawing.Brush(Color.SeaGreen),null,new W.Point(ActualWidth-18,y+height/2),3,3);
            }
            var thumb=model.Thumb;if(!thumb.IsEmpty)dc.DrawRoundedRectangle(WpfDrawing.Brush(Color.FromArgb(115,p.SecondaryText)),null,new W.Rect((thumb.X+3)/Scale,thumb.Y/Scale,4,thumb.Height/Scale),2,2);
        }
    }
}
