using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Runs only inside the guarded fresh-data smoke harness. Messages target our
// own offscreen controls; this never injects system input or uses the clipboard.
internal static class ScreenshotImageInteractionSelfTests
{
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle,int message,nint wParam,nint lParam);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle,uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle,int command);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPos(nint handle,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle,out NativeRectangle rectangle);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle,int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle,out uint processId);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle {public int Left,Top,Right,Bottom;}
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle,int attribute,out int value,int size);
    private const BindingFlags PrivateInstance=BindingFlags.Instance|BindingFlags.NonPublic;
    private static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException(message);}
    internal static IEnumerable<T> Find<T>(Control root) where T:Control
    {
        if(root is T match)yield return match;
        foreach(Control child in root.Controls)foreach(var item in Find<T>(child))yield return item;
    }
    private static nint Position(Point point)=>(nint)((point.X&0xffff)|((point.Y&0xffff)<<16));
    internal static void Wheel(ScreenshotRebuildCanvas canvas,int delta,Point anchor)
        =>SendMessage(canvas.Handle,0x020A,(nint)(delta<<16),Position(canvas.PointToScreen(anchor)));
    private static void Mouse(Control control,int message,Point point,bool left=false)
        =>SendMessage(control.Handle,message,left?(nint)1:0,Position(point));
    internal static void Drag(ScreenshotRebuildCanvas canvas,Point start,Point end)
    {
        Mouse(canvas,0x0201,start,true);Mouse(canvas,0x0200,end,true);Mouse(canvas,0x0202,end);
    }
    private static PointF ImagePoint(ScreenshotRebuildCanvas canvas,Size source,Point point)
    {
        var bounds=canvas.ImageBounds;
        return new((point.X-bounds.X)*source.Width/(float)bounds.Width,(point.Y-bounds.Y)*source.Height/(float)bounds.Height);
    }
    private sealed class Fixture:IDisposable
    {
        internal readonly Form Host;
        internal readonly ScreenshotRebuildPane Pane;
        internal readonly ScreenshotRebuildCanvas Canvas;
        internal readonly Bitmap Image;
        internal Fixture(Action<int> pump,Size? imageSize=null)
        {
            var size=imageSize??new Size(422,885);Image=new(size.Width,size.Height);
            using(var graphics=Graphics.FromImage(Image)){graphics.Clear(Color.Navy);graphics.FillRectangle(Brushes.Gold,10,10,size.Width/2,size.Height/2);}
            Host=new(){ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-24000,-24000),ClientSize=new(800,680)};
            Pane=new(){Dock=DockStyle.Fill};Host.Controls.Add(Pane);Host.Show();
            Pane.SetContent(Image,Image,"Alpha beta\nGamma delta\n中文原文","甲乙丙丁\nTranslated text\n另一行");
            Canvas=Find<ScreenshotRebuildCanvas>(Pane).Single();pump(200);
        }
        public void Dispose(){Pane.ClearContent();Host.Close();Host.Dispose();Image.Dispose();}
    }
    internal static void Run(Action<string,Action> check,Action<int> pump)
    {
        Require(ScreenshotRebuildSelfTests.IsActive,"Interaction verification requires the guarded smoke entry.");
        check("native unmodified wheel zooms both image modes around the pointer",()=>
        {
            foreach(var shape in new[]{new Size(422,885),new Size(1200,800)})
            {
                using var fixture=new Fixture(pump,shape);var canvas=fixture.Canvas;
                foreach(var mode in new[]{ScreenshotPaneView.OriginalImage,ScreenshotPaneView.TranslatedImage})
                {
                    fixture.Pane.View=mode;fixture.Pane.FitWindow();pump(40);
                    var bounds=canvas.ImageBounds;var anchor=new Point(bounds.X+bounds.Width*2/5,bounds.Y+bounds.Height*2/5);
                    var before=ImagePoint(canvas,shape,anchor);var priorZoom=canvas.Zoom;
                    Wheel(canvas,120,anchor);var after=ImagePoint(canvas,shape,anchor);
                    Require(canvas.FitMode==ScreenshotPaneFitMode.Manual&&canvas.Zoom>priorZoom,"Unmodified native wheel did not zoom in.");
                    Require(Math.Abs(after.X-before.X)<3&&Math.Abs(after.Y-before.Y)<3,"Wheel zoom moved the image point under the pointer.");
                    Wheel(canvas,-120,anchor);
                    Require(Math.Abs(canvas.Zoom-priorZoom)<.0001f,"Opposite wheel step did not restore the original zoom.");
                }
            }
        });
        check("fit-sized images follow left drag and stop immediately at mouse release",()=>
        {
            using var fixture=new Fixture(pump);var canvas=fixture.Canvas;var before=canvas.ImageBounds;var zoom=canvas.Zoom;
            var events=0;canvas.ViewStateChanged+=(_,_)=>events++;
            var start=new Point(before.X+before.Width/2,before.Y+before.Height/2);var end=new Point(start.X+67,start.Y+43);
            Drag(canvas,start,end);var after=canvas.ImageBounds;
            Require(after.Location==new Point(before.X+67,before.Y+43),"Fit-sized image did not move with the native left drag.");
            Require(canvas.FitMode==ScreenshotPaneFitMode.Manual&&Math.Abs(canvas.Zoom-zoom)<.0001f&&events>0,"Dragging changed zoom or failed to publish persistent view state.");
            Require(!canvas.Capture&&canvas.Cursor==Cursors.Default,"Mouse release left the image in dragging mode.");
            Mouse(canvas,0x0200,new(start.X+140,start.Y+90),true);
            Require(canvas.ImageBounds==after,"A released drag continued moving the image.");
        });
        check("image drag stays recoverable at every edge and fitting restores its center",()=>
        {
            using var fixture=new Fixture(pump);var canvas=fixture.Canvas;
            foreach(var endpoint in new[]{new Point(20000,20000),new Point(-20000,-20000),new Point(20000,-20000),new Point(-20000,20000)})
            {
                fixture.Pane.FitWindow();Drag(canvas,new(300,200),endpoint);
                var visible=Rectangle.Intersect(canvas.ClientRectangle,canvas.ImageBounds);
                var minX=Math.Min(canvas.ImageBounds.Width,48*canvas.DeviceDpi/96);var minY=Math.Min(canvas.ImageBounds.Height,48*canvas.DeviceDpi/96);
                Require(visible.Width>=minX-1&&visible.Height>=minY-1,"Dragging hid the image completely outside the canvas.");
            }
            fixture.Pane.FitWindow();Require(canvas.PanOffset==Point.Empty&&canvas.ScrollOffset==Point.Empty&&canvas.FitMode==ScreenshotPaneFitMode.Window,"Fit window did not clear the moved image state.");
            Drag(canvas,new(300,200),new(345,240));fixture.Pane.FitWidth();
            Require(canvas.PanOffset==Point.Empty&&canvas.ScrollOffset==Point.Empty&&canvas.FitMode==ScreenshotPaneFitMode.Width,"Fit width did not clear the moved image state.");
        });
        check("capture loss, missing content and source replacement terminate image dragging",()=>
        {
            using var fixture=new Fixture(pump);var canvas=fixture.Canvas;
            Mouse(canvas,0x0201,new(300,200),true);Mouse(canvas,0x0200,new(330,220),true);canvas.Capture=false;
            var bounds=canvas.ImageBounds;Mouse(canvas,0x0200,new(400,280),true);
            Require(canvas.ImageBounds==bounds&&canvas.Cursor==Cursors.Default,"Capture loss left an active drag.");
            Mouse(canvas,0x0201,new(300,200),true);fixture.Pane.ClearContent();
            Require(!canvas.Capture,"Clearing content retained mouse capture.");
            var state=fixture.Pane.CaptureViewState();Wheel(canvas,120,new(200,150));Drag(canvas,new(200,150),new(280,210));
            Require(fixture.Pane.CaptureViewState()==state&&!canvas.Capture,"Missing image input changed persistent view state.");
            fixture.Pane.SetContent(fixture.Image,fixture.Image,"text","译文");Mouse(canvas,0x0201,new(300,200),true);
            using var replacement=new Bitmap(500,500);canvas.SetImage(replacement);
            Require(!canvas.Capture&&canvas.Cursor==Cursors.Default,"Replacing the image retained the old drag.");
            canvas.SetImage(null);
        });
        check("pan and pointer zoom round-trip through layout JSON without changing old layouts",()=>
        {
            using var fixture=new Fixture(pump);var canvas=fixture.Canvas;
            Drag(canvas,new(300,200),new(345,230));var anchor=new Point(340,250);var before=ImagePoint(canvas,fixture.Image.Size,anchor);
            Wheel(canvas,240,anchor);var after=ImagePoint(canvas,fixture.Image.Size,anchor);
            Require(Math.Abs(before.X-after.X)<3&&Math.Abs(before.Y-after.Y)<3,"Pointer zoom after dragging lost its anchor.");
            var saved=fixture.Pane.CaptureViewState();var encoded=JsonSerializer.Serialize(new ScreenshotWorkspaceLayout{Left=saved});
            var restored=JsonSerializer.Deserialize<ScreenshotWorkspaceLayout>(encoded)!.Normalize();
            fixture.Pane.FitWindow();fixture.Pane.ApplyViewState(restored.Left);
            Require(fixture.Pane.CaptureViewState()==saved,"New pan/zoom layout did not round-trip.");
            var legacy=JsonSerializer.Deserialize<ScreenshotWorkspaceLayout>("{\"Left\":{\"View\":0,\"FitMode\":2,\"Zoom\":2,\"ScrollX\":0,\"ScrollY\":35}}")!.Normalize();
            Require(legacy.Left.PanX==0&&legacy.Left.PanY==0&&legacy.Left.ScrollY==35,"Old layout without pan fields changed meaning.");
        });
        check("native text right click preserves selection and copy actions emit exactly their intended text",()=>
        {
            using var fixture=new Fixture(pump);var pane=fixture.Pane;pane.View=ScreenshotPaneView.SourceText;pump(40);
            var text=Find<RichTextBox>(pane).Single();var menu=text.ContextMenuStrip??throw new InvalidOperationException("Text has no context menu.");
            var items=menu.Items.OfType<ToolStripMenuItem>().ToDictionary(x=>x.Text??"");
            Require(items.ContainsKey("复制所选")&&items.ContainsKey("复制全部")&&items.ContainsKey("全选"),"Text menu is incomplete.");
            text.Select(6,4);var selection=text.SelectedText;var opened=0;
            menu.Opening+=(_,e)=>{opened++;Require(text.SelectionStart==6&&text.SelectionLength==4,"Opening the context menu changed the selected text.");e.Cancel=true;};
            Mouse(text,0x0204,new(100,65));Require(text.SelectionStart==6&&text.SelectionLength==4,"Right-button down changed selection.");
            Mouse(text,0x0205,new(100,65));
            SendMessage(text.Handle,0x007B,text.Handle,Position(text.PointToScreen(new(100,65))));
            Require(opened>0&&text.SelectionStart==6&&text.SelectionLength==4,"Native context menu did not preserve selection.");
            var payloads=new List<string>();pane.CopyRequested+=(_,_)=>payloads.Add(pane.SelectedTextOrAll);
            items["复制所选"].PerformClick();items["复制全部"].PerformClick();
            Require(payloads.SequenceEqual(new[]{selection,pane.CurrentText}),"Copy selection/all emitted the wrong payload.");
            Require(text.SelectionStart==6&&text.SelectionLength==4&&pane.SelectedTextOrAll==selection,"Copy all permanently changed selection semantics.");
            var command=Message.Create(text.Handle,0x0100,0,0);object?[] arguments=[command,Keys.Shift|Keys.F10];
            var handled=(bool)text.GetType().GetMethod("ProcessCmdKey",PrivateInstance)!.Invoke(text,arguments)!;
            Require(handled&&opened>1&&text.SelectionStart==6&&text.SelectionLength==4,"Keyboard context menu did not preserve selection.");
        });
        check("text menu selection enablement, select-all and independent image state remain correct",()=>
        {
            using var fixture=new Fixture(pump);var pane=fixture.Pane;Drag(fixture.Canvas,new(300,200),new(340,225));var before=pane.CaptureViewState();
            foreach(var mode in new[]{ScreenshotPaneView.SourceText,ScreenshotPaneView.TranslatedText})
            {
                pane.View=mode;var text=Find<RichTextBox>(pane).Single();var menu=text.ContextMenuStrip!;
                var items=menu.Items.OfType<ToolStripMenuItem>().ToDictionary(x=>x.Text??"");text.Select(0,0);
                typeof(ContextMenuStrip).GetMethod("OnOpening",PrivateInstance)!.Invoke(menu,[new CancelEventArgs()]);
                Require(!items["复制所选"].Enabled&&items["复制全部"].Enabled&&items["全选"].Enabled,"Empty selection has incorrect menu enablement.");
                items["全选"].PerformClick();Require(text.SelectionLength==text.TextLength,"Select all did not include the entire current text.");
            }
            pane.View=ScreenshotPaneView.OriginalImage;Require(pane.CaptureViewState()==before,"Switching to text and back lost image pan/zoom.");
            pane.ClearContent();pane.View=ScreenshotPaneView.SourceText;var empty=Find<RichTextBox>(pane).Single();
            typeof(ContextMenuStrip).GetMethod("OnOpening",PrivateInstance)!.Invoke(empty.ContextMenuStrip,[new CancelEventArgs()]);
            Require(empty.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().All(x=>!x.Enabled),"Missing text kept copy/select-all actions enabled.");
        });
    }

    internal static int RunVisibleSplit(string output,Action<int> pump,Action<Task> complete)
    {
        var checks=new List<object>();var failures=new List<string>();MainForm? main=null;Bitmap? portrait=null;
        try
        {
            Require(ScreenshotRebuildSelfTests.IsActive&&AppDataPaths.DisableGlobalInput,"Visible verification requires isolated input guards.");
            var area=Screen.PrimaryScreen!.WorkingArea;
            main=new MainForm(null,new(CaptureFlashDiagnosticMode.CaptureOnly,false,false))
            {ShowInTaskbar=false,TopMost=true,StartPosition=FormStartPosition.Manual,Location=new(area.X+24,area.Y+24)};
            main.Show();main.MinimumSize=new(100,100);main.ClientSize=new(Math.Min(1600,area.Width-80),Math.Min(1000,area.Height-80));
            main.NavigateForSmoke("主页");complete(main.RefreshScreenshotWorkspaceForSmoke());pump(200);
            ShowWindow(main.Handle,5);SetWindowPos(main.Handle,(nint)(-1),0,0,0,0,0x0043);main.Activate();pump(100);
            Require(TemporaryWindowCaptureAllowance.TryBegin(main,out var allowance,out var captureError),captureError);
            using var captureAllowance=allowance;
            portrait=new Bitmap(422,885);
            using(var graphics=Graphics.FromImage(portrait))
            {
                graphics.Clear(Color.FromArgb(235,232,216));
                for(var y=0;y<885;y+=90)graphics.FillRectangle(y%180==0?Brushes.Navy:Brushes.DarkGoldenrod,0,y,422,42);
            }
            foreach(var pane in new[]{main.ScreenshotPaneForSmoke(0),main.ScreenshotPaneForSmoke(1)})
                pane.SetContent(portrait,portrait,"Synthetic source text","示例译文\nLow / 低\nMedium / 中\nHigh / 高\nEpic / 史诗\nCinematic / 电影级\nCustom / 自定义");
            var split=Find<ScreenshotRebuildSplit>(main).Single();
            Bitmap CaptureClient()
            {
                var bounds=main.RectangleToScreen(main.ClientRectangle);
                Require(area.Contains(bounds),"Test window client escaped the primary working area.");
                GetWindowRect(main.Handle,out var nativeBounds);NativeMethods.GetWindowDisplayAffinity(main.Handle,out var originalAffinity);DwmGetWindowAttribute(main.Handle,14,out var cloaked,4);
                var centerWindow=WindowFromPoint(new Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2));GetWindowThreadProcessId(centerWindow,out var centerProcess);
                File.WriteAllText(Path.Combine(output,"CAPTURE-WINDOW-STATE.json"),JsonSerializer.Serialize(new{Handle=main.Handle.ToInt64(),main.Visible,main.Enabled,main.Opacity,main.TopMost,main.Location,main.Bounds,ClientScreen=bounds,NativeBounds=new{nativeBounds.Left,nativeBounds.Top,nativeBounds.Right,nativeBounds.Bottom},main.DeviceDpi,Affinity=originalAffinity,NativeVisible=IsWindowVisible(main.Handle),Cloaked=cloaked,NativeStyle=GetWindowLongPtr(main.Handle,-16).ToInt64(),NativeExStyle=GetWindowLongPtr(main.Handle,-20).ToInt64(),SelfProcess=Environment.ProcessId,CenterProcess=centerProcess,CenterWindow=centerWindow.ToInt64(),CenterRoot=GetAncestor(centerWindow,2).ToInt64()},new JsonSerializerOptions{WriteIndented=true}));
                foreach(var point in new[]{new Point(bounds.Left+8,bounds.Top+8),new Point(bounds.Right-8,bounds.Top+8),new Point(bounds.Left+8,bounds.Bottom-8),new Point(bounds.Right-8,bounds.Bottom-8),new Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2)})
                    Require(GetAncestor(WindowFromPoint(point),2)==main.Handle,"Self-capture refused because another top-level window covers the test client.");
                Require(IsWindowVisible(main.Handle)&&main.Visible&&main.Opacity==1&&NativeMethods.GetWindowDisplayAffinity(main.Handle,out var affinity)&&affinity==0,"Self-capture requires an opaque visible capturable test window.");
                var bitmap=new Bitmap(bounds.Width,bounds.Height);
                using var graphics=Graphics.FromImage(bitmap);graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);
                return bitmap;
            }
            static object Difference(Bitmap first,Bitmap second,Rectangle region)
            {
                region.Intersect(new Rectangle(Point.Empty,first.Size));var different=0;var samples=0;
                for(var y=region.Top;y<region.Bottom;y+=2)for(var x=region.Left;x<region.Right;x+=2)
                {
                    var a=first.GetPixel(x,y);var b=second.GetPixel(x,y);samples++;
                    if(Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)))>12)different++;
                }
                return new{DifferentSamples=different,Samples=samples,Fraction=samples==0?0:different/(double)samples};
            }
            foreach(var textMode in new[]{true,false})
            {
                main.SetScreenshotModesForSmoke(0,textMode?3:1);main.SetScreenshotSplitForSmoke(50);pump(250);
                main.TopMost=false;main.TopMost=true;
                var positioned=SetWindowPos(main.Handle,(nint)(-1),0,0,0,0,0x0043);
                File.WriteAllText(Path.Combine(output,"PRE-DRAG-WINDOW.json"),JsonSerializer.Serialize(new{Positioned=positioned,LastError=Marshal.GetLastWin32Error(),ExStyle=GetWindowLongPtr(main.Handle,-20).ToInt64(),Handle=main.Handle.ToInt64()}));
                DwmFlush();
                var divider=(Rectangle)typeof(ScreenshotRebuildSplit).GetField("divider",PrivateInstance)!.GetValue(split)!;
                var start=new Point(divider.Left+divider.Width/2,split.Height/2);
                Mouse(split,0x0201,start,true);Require(split.Capture,"Native divider mouse down did not capture the drag.");
                foreach(var ratio in new[]{25,75,35,65})
                {
                    var target=new Point((int)Math.Round((split.Width-divider.Width)*ratio/100d+divider.Width/2d),split.Height/2);
                    Mouse(split,0x0200,target,true);
                    Require(split.Capture&&IsWindowVisible(split.Handle)&&IsWindowVisible(split.Panel1.Handle)&&IsWindowVisible(split.Panel2.Handle),"Divider movement lost capture or left native children hidden.");
                    Require(Math.Abs(split.SplitPercent-ratio)<.2,"Native divider move did not reach the requested ratio.");
                    var first=main.ScreenshotPaneForSmoke(0);var second=main.ScreenshotPaneForSmoke(1);
                    Require(!split.Panel1.Bounds.IntersectsWith(split.Panel2.Bounds)&&first.Width==split.Panel1.ClientSize.Width&&second.Width==split.Panel2.ClientSize.Width,"Native panes overlap or retained their old dimensions mid-drag.");
                    // Wait for the compositor only. Do not pump, invalidate,
                    // UpdateWindow, or DrawToBitmap before the first capture.
                    DwmFlush();using var immediate=CaptureClient();
                    var name=$"split-{(textMode?"image-text":"dual-image")}-{ratio}";
                    immediate.Save(Path.Combine(output,name+"-during-drag.png"));
                    WorkspaceRepaint.Complete(split);DwmFlush();using var reference=CaptureClient();
                    reference.Save(Path.Combine(output,name+"-forced-reference.png"));
                    var firstRect=new Rectangle(main.PointToClient(first.PointToScreen(Point.Empty)),new Size(first.Width,Math.Min(first.Height,100*first.DeviceDpi/96)));
                    var secondRect=new Rectangle(main.PointToClient(second.PointToScreen(Point.Empty)),new Size(second.Width,Math.Min(second.Height,100*second.DeviceDpi/96)));
                    var splitRect=new Rectangle(main.PointToClient(split.PointToScreen(Point.Empty)),split.Size);
                    checks.Add(new{Name=name,Ratio=split.SplitPercent,CaptureBeforeMouseUp=split.Capture,NativeChildrenVisible=true,
                        LeftHeaderDiff=Difference(immediate,reference,firstRect),RightHeaderDiff=Difference(immediate,reference,secondRect),SplitDiff=Difference(immediate,reference,splitRect),
                        first.Bounds,RightBounds=second.Bounds});
                }
                Mouse(split,0x0202,new(split.Width/2,split.Height/2));Require(!split.Capture,"Divider release retained mouse capture.");
            }
            foreach(var pane in new[]{main.ScreenshotPaneForSmoke(0),main.ScreenshotPaneForSmoke(1)})pane.ClearContent();
        }
        catch(Exception ex){failures.Add(SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally
        {
            if(main is not null){try{foreach(var pane in new[]{main.ScreenshotPaneForSmoke(0),main.ScreenshotPaneForSmoke(1)})pane.ClearContent();main.ExitForSmoke();main.Dispose();}catch(Exception ex){failures.Add(SafeDiagnosticOutput.ExceptionSummary(ex));}}
            portrait?.Dispose();
            File.WriteAllText(Path.Combine(output,"VISIBLE-SPLIT-RESULT.json"),JsonSerializer.Serialize(new
            {
                Passed=failures.Count==0,Failures=failures,Checks=checks,BuildIdentity.BuildId,
                Scope="Local HWND mouse messages and own synthetic visible client pixels captured before MouseUp; initial capture uses DwmFlush only. Later explicit repaint is a comparison reference, never the initial evidence.",
                GlobalInputInjected=false,PersonalDataRead=false,ClipboardTouched=false,RealApiRequests=0,OcrRequests=0,
                VisualReviewRequired=true,FinishedUtc=DateTimeOffset.UtcNow
            },new JsonSerializerOptions{WriteIndented=true}));
        }
        return failures.Count==0?0:1;
    }
}
