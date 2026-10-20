using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Isolated synthetic native scrollbar check. No global input or clipboard use.
internal static class ScreenshotScrollbarThemeSelfTests
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; internal Rectangle Bounds=>Rectangle.FromLTRB(Left,Top,Right,Bottom); }
    [StructLayout(LayoutKind.Sequential)] private struct BarInfo
    {public int Size;public NativeRect Rect;public int ArrowSize,ThumbTop,ThumbBottom,Reserved;[MarshalAs(UnmanagedType.ByValArray,SizeConst=6)]public uint[] State;}
    [DllImport("user32.dll")] private static extern bool GetScrollBarInfo(nint handle,int objectId,ref BarInfo info);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle,int message,nint wParam,nint lParam);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle,uint flags);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle,int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    private const BindingFlags PrivateInstance=BindingFlags.Instance|BindingFlags.NonPublic;
    private static void Require(bool condition,string text){if(!condition)throw new InvalidOperationException(text);}
    private static nint Position(Point point)=>(nint)((point.X&0xffff)|((point.Y&0xffff)<<16));
    private static IEnumerable<T> Find<T>(Control root) where T:Control=>ScreenshotImageInteractionSelfTests.Find<T>(root);
    private static void Wheel(Control control,int delta)
        =>SendMessage(control.Handle,0x020A,(nint)(delta<<16),Position(control.PointToScreen(new Point(control.ClientSize.Width/2,control.ClientSize.Height/2))));
    private static BarInfo Bar(Control control,bool horizontal=false)
    {
        var info=new BarInfo{Size=Marshal.SizeOf<BarInfo>(),State=new uint[6]};
        Require(GetScrollBarInfo(control.Handle,horizontal?-6:-5,ref info),"Cannot inspect native scrollbar.");
        Require((info.State[0]&0x8000)==0&&info.Rect.Bounds.Width>0&&info.Rect.Bounds.Height>0,"Expected overflow scrollbar is hidden.");return info;
    }
    private static void Seed()
    {
        var settings=new ApiSettings{HistoryCleanupByAge=false,HistoryCleanupByCount=false,HistoryCleanupBySpace=false};
        using var source=new Bitmap(422,885);using(var graphics=Graphics.FromImage(source)){graphics.Clear(Color.SlateGray);graphics.FillRectangle(Brushes.Gold,15,15,380,130);}
        using var session=new SessionServices(AppDataPaths.HistoryRoot);
        for(var index=0;index<28;index++)
        {
            var group=index%2==0?"KEEP":"OTHER";
            var original=$"SCROLLBAR-{group}-{index:00}\n"+string.Join('\n',Enumerable.Range(1,130).Select(line=>$"Synthetic source line {line:000} — readable original text."));
            var translated=$"滚动条示例-{group}-{index:00}\n"+string.Join('\n',Enumerable.Range(1,130).Select(line=>$"第 {line:000} 行合成译文，用于检查文字滚动与选区。"));
            session.AddHistory(source,OcrEngineKind.Rapid,original,translated,"zh",settings,source);
        }
    }
    internal static int Run(string output,Action<int> pump,Action<Task> complete)
    {
        // A real outer loop keeps WinForms' synchronization context installed
        // while the existing settings-save refresh awaits file decoding.
        int result=1;EventHandler? start=null;
        start=(_,_)=>
        {
            Application.Idle-=start;
            try{result=RunInMessageLoop(output,pump,complete);}
            finally{Application.ExitThread();}
        };
        Application.Idle+=start;
        try{Application.Run();return result;}
        finally{Application.Idle-=start;}
    }
    private static int RunInMessageLoop(string output,Action<int> pump,Action<Task> complete)
    {
        Require(ScreenshotRebuildSelfTests.IsActive&&AppDataPaths.DisableGlobalInput,"Scrollbar verification requires an isolated launch.");
        var checks=new List<object>();var snapshots=new List<object>();var failures=new List<string>();MainForm? main=null;
        void Check(string name,Action body){try{body();checks.Add(new{Name=name,Passed=true});}catch(Exception ex){failures.Add(name+": "+SafeDiagnosticOutput.ExceptionSummary(ex));checks.Add(new{Name=name,Passed=false});}}
        try
        {
            Seed();var area=Screen.PrimaryScreen!.WorkingArea;
            main=new MainForm(null,new(CaptureFlashDiagnosticMode.CaptureOnly,false,false))
            {ShowInTaskbar=false,TopMost=true,StartPosition=FormStartPosition.Manual,Location=new(area.Left+24,area.Top+24)};
            main.Show();main.MinimumSize=new(100,100);main.ClientSize=new(Math.Min(1800,area.Width-80),Math.Min(1100,area.Height-80));
            main.NavigateForSmoke("主页");complete(main.RefreshScreenshotWorkspaceForSmoke());main.ToggleScreenshotHistoryForSmoke();pump(200);
            var history=Find<ScreenshotRebuildHistory>(main.LegacyWindow).Single();var left=main.ScreenshotPaneForSmoke(0);var right=main.ScreenshotPaneForSmoke(1);
            var leftCanvas=Find<ScreenshotRebuildCanvas>(left).Single();var rightCanvas=Find<ScreenshotRebuildCanvas>(right).Single();
            var leftText=Find<RichTextBox>(left).Single();var rightText=Find<RichTextBox>(right).Single();
            var originalHandles=new[]{history.Handle,leftCanvas.Handle,rightCanvas.Handle,leftText.Handle,rightText.Handle};
            void SetUserTheme(ApplicationThemeMode mode)
            {
                var wantDark=mode==ApplicationThemeMode.Night;
                if((UiTheme.Current.Main.GetBrightness()<.5f)==wantDark)return;
                var themeButton=(Button)typeof(MainForm).GetField("_themeIcon",PrivateInstance)!.GetValue(main)!;
                themeButton.PerformClick();
                // Follow the existing button-triggered refresh. Starting a
                // second refresh here would change the scenario being tested.
                pump(40);var wait=System.Diagnostics.Stopwatch.StartNew();
                while(main.ScreenshotWorkspaceStateForSmoke().Loading)
                {if(wait.ElapsedMilliseconds>12000)throw new TimeoutException("The real theme button's existing refresh did not finish.");pump(25);}
                Require((UiTheme.Current.Main.GetBrightness()<.5f)==wantDark,"The real theme button did not apply the requested appearance.");
            }
            void Snapshot(string name,params (string Name,Control Control,bool Horizontal)[] targets)
            {
                pump(220);main.TopMost=false;main.TopMost=true;Require(SetWindowPos(main.Handle,(nint)(-1),0,0,0,0,0x0043),"Cannot present isolated test window.");
                Require((GetWindowLongPtr(main.Handle,-20).ToInt64()&8)!=0,"Native test window is not topmost.");
                Require(TemporaryWindowCaptureAllowance.TryBegin(main,out var allowance,out var error),error);using var scope=allowance;
                DwmFlush();var screen=main.RectangleToScreen(main.ClientRectangle);Require(area.Contains(screen),"Self-capture escaped the working area.");
                foreach(var point in new[]{new Point(screen.Left+8,screen.Top+8),new Point(screen.Right-8,screen.Top+8),new Point(screen.Left+8,screen.Bottom-8),new Point(screen.Right-8,screen.Bottom-8),new Point(screen.Left+screen.Width/2,screen.Top+screen.Height/2)})
                    Require(GetAncestor(WindowFromPoint(point),2)==main.Handle,"Self-capture refused an occluded test window.");
                Require(IsWindowVisible(main.Handle)&&main.Opacity==1&&NativeMethods.GetWindowDisplayAffinity(main.Handle,out var affinity)&&affinity==0,"Test window is not opaque, visible and capturable.");
                using var image=new Bitmap(screen.Width,screen.Height);using(var graphics=Graphics.FromImage(image))graphics.CopyFromScreen(screen.Location,Point.Empty,screen.Size,CopyPixelOperation.SourceCopy);
                image.Save(Path.Combine(output,name+".png"));var bars=new List<object>();var trackMatches=true;
                foreach(var target in targets)
                {
                    var bar=Bar(target.Control,target.Horizontal);var rect=bar.Rect.Bounds;Require(screen.Contains(rect),"Scrollbar is outside the own-client capture.");
                    var values=new List<double>();var start=bar.ArrowSize+3;var end=(target.Horizontal?rect.Width:rect.Height)-bar.ArrowSize-3;
                    for(var axis=start;axis<end;axis+=3)
                    {
                        if(axis>=bar.ThumbTop-2&&axis<=bar.ThumbBottom+2)continue;
                        var point=target.Horizontal?new Point(rect.Left+axis,rect.Top+rect.Height/2):new Point(rect.Left+rect.Width/2,rect.Top+axis);
                        var color=image.GetPixel(point.X-screen.Left,point.Y-screen.Top);values.Add((color.R+color.G+color.B)/3d);
                    }
                    Require(values.Count>0,"No exposed scrollbar track pixels were sampled.");
                    trackMatches&=name.Contains("dark",StringComparison.Ordinal)?values.Average()<100&&values.All(value=>value<160):values.Average()>180;
                    double? cornerBrightness=null;
                    if(target.Horizontal)
                    {
                        var vertical=Bar(target.Control);var point=new Point(vertical.Rect.Left+vertical.Rect.Bounds.Width/2,rect.Top+rect.Height/2);
                        Require(screen.Contains(point),"Scrollbar corner escaped the own-client capture.");var color=image.GetPixel(point.X-screen.Left,point.Y-screen.Top);cornerBrightness=(color.R+color.G+color.B)/3d;
                        trackMatches&=name.Contains("dark",StringComparison.Ordinal)?cornerBrightness<130:cornerBrightness>120;
                    }
                    bars.Add(new{target.Name,target.Horizontal,ScreenBounds=rect,TrackSamples=values.Count,MeanBrightness=values.Average(),WhiteFraction=values.Count(value=>value>210)/(double)values.Count,MinBrightness=values.Min(),MaxBrightness=values.Max(),CornerBrightness=cornerBrightness});
                }
                snapshots.Add(new{Name=name,Bars=bars,SelfWindowVerified=true,main.DeviceDpi});
                Check(name+" native scrollbar tracks immediately match the active theme",()=>Require(trackMatches,"At least one actual scrollbar track kept the opposite theme."));
                File.WriteAllText(Path.Combine(output,"THEME-PROGRESS.json"),JsonSerializer.Serialize(snapshots,new JsonSerializerOptions{WriteIndented=true}));
            }
            Check("populated history exposes its native vertical scrollbar",()=>{Require(history.Count==28,"Expected 28 isolated history records.");Bar(history);});
            main.SetScreenshotModesForSmoke(2,3);pump(100);
            Snapshot("01-light-text",("History",history,false),("OriginalText",leftText,false),("TranslatedText",rightText,false));
            leftText.Select(12,23);rightText.Select(16,19);var selectedId=main.ScreenshotWorkspaceStateForSmoke().SelectedId;
            SetUserTheme(ApplicationThemeMode.Night);
            Snapshot("02-dark-text",("History",history,false),("OriginalText",leftText,false),("TranslatedText",rightText,false));
            Check("theme switch preserves native handles and the selected history record",()=>
            {Require(originalHandles.SequenceEqual(new[]{history.Handle,leftCanvas.Handle,rightCanvas.Handle,leftText.Handle,rightText.Handle}),"Theme switch recreated native controls.");Require(main.ScreenshotWorkspaceStateForSmoke().SelectedId==selectedId,"Theme switch lost the selected history record.");});
            // The pre-existing settings save reloads record objects and clears
            // RichTextBox selection. Start the independent scrolling check
            // with an explicit new selection after that reload has settled.
            rightText.Focus();rightText.Select(16,19);rightText.ScrollToCaret();SendMessage(rightText.Handle,0x0115,(nint)6,0);
            Check("long-text native page scroll retains the selected text",()=>
            {
                var before=SendMessage(rightText.Handle,0x00CE,0,0).ToInt32();Wheel(rightText,-120);pump(40);
                var after=SendMessage(rightText.Handle,0x00CE,0,0).ToInt32();
                SendMessage(rightText.Handle,0x0115,(nint)3,0);var afterPage=SendMessage(rightText.Handle,0x00CE,0,0).ToInt32();
                File.WriteAllText(Path.Combine(output,"TEXT-SCROLL-INPUT.json"),JsonSerializer.Serialize(new{BeforeLine=before,AfterWheelLine=after,AfterPageLine=afterPage,rightText.Focused,rightText.Visible,rightText.TextLength,WheelScrollLines=SystemInformation.MouseWheelScrollLines,rightText.SelectionStart,rightText.SelectionLength}));
                // A direct WM_MOUSEWHEEL did not scroll this native RichEdit
                // in the unchanged-theme control run either. Record it as
                // unverified; native paging is the asserted scroll path.
                Require(afterPage>after,"Text page-down did not scroll.");
                Require(rightText.SelectionStart==16&&rightText.SelectionLength==19,"Text scrolling discarded its selection.");
            });
            Check("history wheel, native page command and keyboard selection still scroll",()=>
            {
                var before=history.AutoScrollPosition;Wheel(history,-120);pump(40);Require(history.AutoScrollPosition.Y<before.Y,"History wheel did not scroll.");
                var wheelOffset=history.AutoScrollPosition.Y;
                SendMessage(history.Handle,0x0115,(nint)3,0);Require(history.AutoScrollPosition.Y<wheelOffset,"History native page-down command did not update offset.");
                typeof(ScreenshotRebuildHistory).GetMethod("OnKeyDown",PrivateInstance)!.Invoke(history,[new KeyEventArgs(Keys.End)]);pump(40);
                Require(history.SelectedIndex==27&&history.AutoScrollPosition.Y<0,"History keyboard End did not select and reveal the last record.");complete(main.RefreshScreenshotWorkspaceForSmoke());
            });
            main.SetScreenshotModesForSmoke(0,1);leftCanvas.SetZoom(5);rightCanvas.SetZoom(5);pump(150);
            Snapshot("03-dark-image-bars",("History",history,false),("OriginalImageVertical",leftCanvas,false),("OriginalImageHorizontal",leftCanvas,true),("TranslatedImageVertical",rightCanvas,false),("TranslatedImageHorizontal",rightCanvas,true));
            Check("native image page-scroll commands move both axes without changing zoom",()=>
            {
                var original=leftCanvas.ScrollOffset;var zoom=leftCanvas.Zoom;SendMessage(leftCanvas.Handle,0x0114,(nint)3,0);SendMessage(leftCanvas.Handle,0x0115,(nint)3,0);
                Require(leftCanvas.ScrollOffset.X>original.X&&leftCanvas.ScrollOffset.Y>original.Y&&leftCanvas.Zoom==zoom,"Image scrollbar commands changed zoom or failed to move an axis.");
            });
            var beforeLeft=left.CaptureViewState();var beforeRight=right.CaptureViewState();SetUserTheme(ApplicationThemeMode.Day);
            Snapshot("04-light-image-bars",("History",history,false),("OriginalImageVertical",leftCanvas,false),("OriginalImageHorizontal",leftCanvas,true),("TranslatedImageVertical",rightCanvas,false),("TranslatedImageHorizontal",rightCanvas,true));
            Check("image theme switch preserves zoom, pan and scroll state",()=>Require(left.CaptureViewState()==beforeLeft&&right.CaptureViewState()==beforeRight,"Changing theme changed the current image view."));
            Check("history filtering and folding retain their selection and scrollbars across themes",()=>
            {
                main.SetScreenshotSearchForSmoke("KEEP");complete(main.RefreshScreenshotWorkspaceForSmoke());Require(history.Count==14,"History filter changed behavior.");Bar(history);var selected=main.ScreenshotWorkspaceStateForSmoke().SelectedId;
                SetUserTheme(ApplicationThemeMode.Night);main.ToggleScreenshotHistoryForSmoke();main.ToggleScreenshotHistoryForSmoke();pump(80);
                Require(history.Count==14&&main.ScreenshotWorkspaceStateForSmoke().SelectedId==selected,"Theme/folding changed filter or selection.");Bar(history);
            });
            Snapshot("05-dark-filtered-history",("History",history,false));
        }
        catch(Exception ex)
        {
            failures.Add(SafeDiagnosticOutput.ExceptionSummary(ex));
            if(main is not null)
            {
                var state=new Dictionary<string,object?>();foreach(var field in new[]{"_shotLoading","_shotImagesLoading","_shotRefreshPending","_shotApplying","_shotClosed","_shotRestore","_shotEpoch","_shotHistoryVersion","_shotLoadedVersion"})state[field]=typeof(MainForm).GetField(field,PrivateInstance)!.GetValue(main);
                var task=(Task)typeof(MainForm).GetField("_shotSelectionTask",PrivateInstance)!.GetValue(main)!;state["SelectionTaskStatus"]=task.Status.ToString();state["SelectionTaskException"]=task.Exception?.ToString();state["LeftTextLength"]=main.ScreenshotPaneForSmoke(0).CurrentText.Length;state["RightTextLength"]=main.ScreenshotPaneForSmoke(1).CurrentText.Length;
                File.WriteAllText(Path.Combine(output,"FAILURE-STATE.json"),JsonSerializer.Serialize(state,new JsonSerializerOptions{WriteIndented=true}));
            }
        }
        finally
        {
            if(main is not null){try{main.ExitForSmoke();main.Dispose();}catch(Exception ex){failures.Add(SafeDiagnosticOutput.ExceptionSummary(ex));}}
            File.WriteAllText(Path.Combine(output,"SCROLLBAR-THEME-RESULT.json"),JsonSerializer.Serialize(new{Passed=failures.Count==0,Failures=failures,Checks=checks,Snapshots=snapshots,BuildIdentity.BuildId,PersonalDataRead=false,GlobalInputInjected=false,RealApiRequests=0,OcrRequests=0,ClipboardTouched=false,VisualReviewRequired=true,ObservedLimitations=new[]{"Directed WM_MOUSEWHEEL did not scroll RichEdit in either the unchanged-theme control run or this candidate. Physical text-wheel and thumb-drag behavior require manual acceptance; no global input was injected.","The pre-existing real settings-save path reloads history text and resets its text selection. This theme-only change does not modify that lifecycle."},Scope="Native visible own-window scrollbar track pixels and bounded UI input checks; no performance or FPS claim.",FinishedUtc=DateTimeOffset.UtcNow},new JsonSerializerOptions{WriteIndented=true}));
        }
        return failures.Count==0?0:1;
    }
}
