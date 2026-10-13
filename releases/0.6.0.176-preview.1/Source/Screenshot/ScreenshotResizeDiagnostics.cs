using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Explicit test entry only. Measures synthetic native resize work separately
// from forced artifact rendering; does not observe personal desktop pixels.
internal static class ScreenshotResizeDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    private const int Composited=0x02000000;
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint handle,int index,nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle,int message,nint wParam,nint lParam);

    private static IEnumerable<Control> Descendants(Control root)
    {yield return root;foreach(Control child in root.Controls)foreach(var nested in Descendants(child))yield return nested;}
    private static void Pump(int milliseconds)
    {var timer=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(2);}while(timer.ElapsedMilliseconds<milliseconds);}
    private static void Complete(Task task)
    {var timer=Stopwatch.StartNew();while(!task.IsCompleted){if(timer.ElapsedMilliseconds>12000)throw new TimeoutException("Resize fixture refresh timed out.");Application.DoEvents();Thread.Yield();}task.GetAwaiter().GetResult();Application.DoEvents();}
    private static Control FieldControl(MainForm form,string name)=>(Control)typeof(MainForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
    private static void SetClient(MainForm form,Size size)
    {for(var i=0;i<3&&form.ClientSize!=size;i++){form.Size=new(form.Width+size.Width-form.ClientSize.Width,form.Height+size.Height-form.ClientSize.Height);Application.DoEvents();}if(form.ClientSize!=size)throw new InvalidOperationException("Native client size mismatch.");}

    internal static int Run(string output,bool visibleProductionDefaults=false,bool bufferExperiments=false)
    {
        if(!ScreenshotRebuildSelfTests.IsActive||!AppDataPaths.HasExplicitRoot||!AppDataPaths.DisableGlobalInput)
            throw new InvalidOperationException("Resize diagnostics requires the fresh-data launch guard.");
        visibleProductionDefaults|=bufferExperiments;
        var reports=new List<object>();var failures=new List<string>();MainForm? form=null;
        try
        {
            var display=Screen.PrimaryScreen?.WorkingArea??new Rectangle(0,0,3840,2160);
            form=new MainForm(null,new(CaptureFlashDiagnosticMode.CaptureOnly,false,false)){ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=visibleProductionDefaults?new(display.Left+24,display.Top+24):new(-24000,-24000)};
            form.Show();form.MinimumSize=new(100,100);form.NavigateForSmoke("主页");Complete(form.RefreshScreenshotWorkspaceForSmoke());
            var host=FieldControl(form,"_mainContentHost");var screenshot=FieldControl(form,"_shotPage");
            while(screenshot.Parent is not null&&!ReferenceEquals(screenshot.Parent,host))screenshot=screenshot.Parent;
            if(!ReferenceEquals(screenshot.Parent,host))throw new InvalidOperationException("Screenshot page is not attached to the workspace host.");
            var originalStyle=GetWindowLongPtr(host.Handle,-20);
            var batchOverride=typeof(MainForm).GetProperty("ScreenshotResizeRedrawBatchOverrideForSmoke",BindingFlags.Instance|BindingFlags.NonPublic);
            using var source=new Bitmap(422,885,PixelFormat.Format32bppPArgb);
            using(var graphics=Graphics.FromImage(source))
            {using var brush=new LinearGradientBrush(new Rectangle(Point.Empty,source.Size),Color.SlateGray,Color.Wheat,35f);graphics.FillRectangle(brush,new Rectangle(Point.Empty,source.Size));using var line=new Pen(Color.DarkSlateBlue,2);for(var y=12;y<885;y+=13)graphics.DrawLine(line,16,y,400,y);}
            foreach(var index in new[]{0,1})form.ScreenshotPaneForSmoke(index).SetContent(source,source,"Synthetic source text",string.Concat(Enumerable.Repeat("这是独立窗口调整测试使用的合成译文内容。",9)));
            form.SetScreenshotModesForSmoke(0,1);
            using var events=new Instrumentation(form,host,screenshot);
            var split=(ScreenshotRebuildSplit)FieldControl(form,"_shotSplit");
            var styled=new Control[]{form,form.ScreenshotPaneForSmoke(0),form.ScreenshotPaneForSmoke(1),split,split.Panel1,split.Panel2};
            var flags=new[]{ControlStyles.OptimizedDoubleBuffer,ControlStyles.DoubleBuffer,ControlStyles.ResizeRedraw};
            var getStyle=typeof(Control).GetMethod("GetStyle",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var setStyle=typeof(Control).GetMethod("SetStyle",BindingFlags.Instance|BindingFlags.NonPublic)!;
            bool HasStyle(Control control,ControlStyles flag)=>(bool)getStyle.Invoke(control,[flag])!;
            var originalFlags=styled.ToDictionary(control=>control,control=>flags.ToDictionary(flag=>flag,flag=>HasStyle(control,flag)));
            void RestoreStyles(){foreach(var item in originalFlags)foreach(var flag in item.Value)setStyle.Invoke(item.Key,[flag.Key,flag.Value]);}
            void DisableBuffers(Control control,bool resize){setStyle.Invoke(control,[ControlStyles.OptimizedDoubleBuffer|ControlStyles.DoubleBuffer,false]);if(resize)setStyle.Invoke(control,[ControlStyles.ResizeRedraw,false]);}
            if(bufferExperiments)
            {
                form.ScreenshotPaintTraceForSmoke=(stage,milliseconds)=>events.Count("Form.ManagedPaint."+stage+"Ms",milliseconds);
                ScreenshotRebuildCanvas.PaintTimingForSmoke=(canvas,stage,milliseconds)=>events.Count(events.Role(canvas)+".ManagedPaint."+stage+"Ms",milliseconds);
            }
            // Native and forced-paint phases are separate sequences. Artifact
            // rendering never changes page visibility for later native samples.
            var largeVisible=new Size(Math.Min(3200,display.Width-80),Math.Min(1800,display.Height-80));
            var sizes=bufferExperiments?new[]{largeVisible}:visibleProductionDefaults?new[]{new Size(1920,1080),largeVisible}:new[]{new Size(1920,1080),new Size(3840,2160)};
            foreach(var resolution in sizes)
            foreach(var textRight in visibleProductionDefaults?new[]{true}:new[]{false,true})
            foreach(var fit in visibleProductionDefaults?new[]{ScreenshotPaneFitMode.Window}:new[]{ScreenshotPaneFitMode.Window,ScreenshotPaneFitMode.Width})
            foreach(var skip in visibleProductionDefaults?new[]{false}:new[]{false,true})
            foreach(var composite in visibleProductionDefaults?new[]{true}:new[]{true,false})
            foreach(var bufferVariant in bufferExperiments?new[]{"Default","FormBufferOpposite","PaneBuffersOff","PaneAndSplitBuffersOff","PaneAndSplitBuffersAndResizeRedrawOff","PaneSplitAndHostsBuffersAndResizeRedrawOff"}:new[]{"Default"})
            {
                events.Enabled=false;
                RestoreStyles();
                if(bufferVariant=="FormBufferOpposite")setStyle.Invoke(form,[ControlStyles.OptimizedDoubleBuffer|ControlStyles.DoubleBuffer,!(originalFlags[form][ControlStyles.OptimizedDoubleBuffer]||originalFlags[form][ControlStyles.DoubleBuffer])]);
                if(bufferVariant.StartsWith("Pane",StringComparison.Ordinal))
                {
                    var resize=bufferVariant.Contains("ResizeRedraw",StringComparison.Ordinal);
                    foreach(var pane in new[]{form.ScreenshotPaneForSmoke(0),form.ScreenshotPaneForSmoke(1)})DisableBuffers(pane,resize);
                    if(bufferVariant.Contains("Split",StringComparison.Ordinal))DisableBuffers(split,resize);
                    if(bufferVariant.Contains("Hosts",StringComparison.Ordinal)){DisableBuffers(split.Panel1,resize);DisableBuffers(split.Panel2,resize);}
                }
                if(!visibleProductionDefaults)
                {
                    if(batchOverride is not null)batchOverride.SetValue(form,!skip);else form.ScreenshotResizeSkipRedrawBatchForSmoke=skip;
                    SetWindowLongPtr(host.Handle,-20,(nint)(composite?originalStyle.ToInt64()|Composited:originalStyle.ToInt64()&~Composited));
                    SetWindowPos(host.Handle,0,0,0,0,0,0x0037);
                }
                SetClient(form,resolution);form.SetScreenshotSplitForSmoke(50);form.SetScreenshotModesForSmoke(0,textRight?3:1);
                foreach(var pane in new[]{form.ScreenshotPaneForSmoke(0),form.ScreenshotPaneForSmoke(1)}){if(fit==ScreenshotPaneFitMode.Window)pane.FitWindow();else pane.FitWidth();}
                foreach(Control page in host.Controls)page.Visible=ReferenceEquals(page,screenshot);
                Pump(350);
                if(host.Controls.Cast<Control>().Count(page=>page.Visible)!=1||!screenshot.Visible||!IsWindowVisible(screenshot.Handle)||!form.ScreenshotPaneForSmoke(0).Visible||!form.ScreenshotPaneForSmoke(1).Visible)
                    throw new InvalidOperationException("Native screenshot page visibility invariant failed before resize measurement.");
                var outer=form.Size;
                var setters=new List<double>();var nativeFlushes=new List<double>();var forcedPaints=new List<double>();
                var setterWork=new Dictionary<string,double>();var nativeWork=new Dictionary<string,double>();var forcedWork=new Dictionary<string,double>();
                int samples=visibleProductionDefaults&&!bufferExperiments?24:12;
                void ChangeSize(int step)=>form.Size=new(outer.Width-(step%7+1)*16,outer.Height-(step%5+1)*11);
                void FlushNative(){Application.DoEvents();UpdateWindow(form.Handle);foreach(var control in events.NativeControls)if(control.IsHandleCreated&&control.Visible)UpdateWindow(control.Handle);Application.DoEvents();}
                SendMessage(form.Handle,0x0231,0,0); // WM_ENTERSIZEMOVE: normal resize lifecycle without real mouse input.
                for(var warmup=0;warmup<3;warmup++){ChangeSize(warmup);FlushNative();}
                events.Enabled=true;
                for(var step=0;step<samples;step++)
                {
                    events.Reset();var timer=Stopwatch.StartNew();ChangeSize(step);timer.Stop();setters.Add(timer.Elapsed.TotalMilliseconds);events.Accumulate(setterWork);
                    events.Reset();timer.Restart();FlushNative();timer.Stop();nativeFlushes.Add(timer.Elapsed.TotalMilliseconds);events.Accumulate(nativeWork);
                }
                events.Enabled=false;SendMessage(form.Handle,0x0232,0,0);SetClient(form,resolution);Pump(300);
                using(var bitmap=new Bitmap(resolution.Width,resolution.Height,PixelFormat.Format32bppPArgb))
                {
                    for(var step=0;step<samples;step++)
                    {
                        ChangeSize(step);FlushNative();
                        var visibility=host.Controls.Cast<Control>().ToDictionary(control=>control,control=>control.Visible);
                        events.Reset();events.Enabled=true;var timer=Stopwatch.StartNew();
                        form.DrawCurrentPageForSmoke(bitmap);timer.Stop();forcedPaints.Add(timer.Elapsed.TotalMilliseconds);events.Accumulate(forcedWork);events.Enabled=false;
                        foreach(var item in visibility)item.Key.Visible=item.Value;
                    }
                }
                reports.Add(new
                {
                    NativeClientPixels=resolution,form.DeviceDpi,SourceImagePixels=source.Size,ContentMode=textRight?"ImageAndText":"DualImages",FitMode=fit.ToString(),SkipRedrawBatch=visibleProductionDefaults?(bool?)null:skip,HostComposited=visibleProductionDefaults?(bool?)null:composite,ProductionDefaults=visibleProductionDefaults&&!bufferExperiments,BufferVariant=bufferVariant,
                    ObservedHostComposited=(GetWindowLongPtr(host.Handle,-20).ToInt64()&Composited)!=0,HostVisible=host.Visible,Samples=samples,
                    Styles=styled.Select(control=>new{Role=events.Role(control),OptimizedDoubleBuffer=HasStyle(control,ControlStyles.OptimizedDoubleBuffer),DoubleBuffer=HasStyle(control,ControlStyles.DoubleBuffer),ResizeRedraw=HasStyle(control,ControlStyles.ResizeRedraw),AllPaintingInWmPaint=HasStyle(control,ControlStyles.AllPaintingInWmPaint),UserPaint=HasStyle(control,ControlStyles.UserPaint)}).ToArray(),
                    SizeSetter=Statistics(setters),NativePaintFlush=Statistics(nativeFlushes),PairedNativeResizeTotal=Statistics(setters.Zip(nativeFlushes,(setter,paint)=>setter+paint).ToList()),ForcedDrawToBitmap=Statistics(forcedPaints),
                    SizeSetterEvents=setterWork,NativePaintEvents=nativeWork,ForcedPaintEvents=forcedWork,
                    PageVisibility=host.Controls.Cast<Control>().Select(control=>new{ScreenshotPage=ReferenceEquals(control,screenshot),control.Visible,control.GetType().Name}).ToArray()
                });
                File.WriteAllText(Path.Combine(output,"RESIZE-PROGRESS.json"),JsonSerializer.Serialize(new{Completed=reports.Count,Reports=reports},JsonOptions));
            }
            events.Enabled=false;RestoreStyles();form.ScreenshotPaintTraceForSmoke=null;ScreenshotRebuildCanvas.PaintTimingForSmoke=null;SetWindowLongPtr(host.Handle,-20,originalStyle);form.ScreenshotResizeSkipRedrawBatchForSmoke=false;batchOverride?.SetValue(form,null);
            foreach(var index in new[]{0,1})form.ScreenshotPaneForSmoke(index).ClearContent();
        }
        catch(Exception ex){failures.Add(SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally
        {
            if(form is not null){try{form.ExitForSmoke();form.Dispose();}catch(Exception ex){failures.Add(SafeDiagnosticOutput.ExceptionSummary(ex));}}
            File.WriteAllText(Path.Combine(output,"RESIZE-BENCHMARK.json"),JsonSerializer.Serialize(new
            {
                Passed=failures.Count==0,Failures=failures,BuildIdentity.BuildId,Reports=reports,
                VisibleProductionDefaults=visibleProductionDefaults&&!bufferExperiments,BufferExperiments=bufferExperiments,
                MeasurementScope=bufferExperiments?"Visible isolated container-buffer experiments, synthetic data and per-sample native resize latency. Diagnostic overrides apply only to this process; not application FPS or manual visual acceptance.":visibleProductionDefaults?"Short visible isolated native resize test using production defaults. Size setter plus invalidated native paint flush measured per sample, separate from forced DrawToBitmap. This does not establish steady application FPS or manual acceptance.":"Offscreen native Size setter and layout work, invalidated WM_PAINT flush, and separately forced DrawToBitmap. Offscreen native painting may be clipped by Windows. This does not measure physical-screen composition or application FPS.",
                PersonalDataRead=false,RealApiRequests=0,OcrRequests=0,RealMouseInput=false,GlobalInputDisabled=AppDataPaths.DisableGlobalInput,FinishedUtc=DateTimeOffset.UtcNow
            },JsonOptions));
        }
        return failures.Count==0?0:1;
    }

    private static object Statistics(List<double> times)
    {var sorted=times.Order().ToArray();return new{MeanMs=times.Average(),P50Ms=sorted[sorted.Length/2],P95Ms=sorted[(int)Math.Ceiling(sorted.Length*.95)-1],MaxMs=sorted[^1],MeasurementsMs=times};}
    private sealed class Instrumentation:IDisposable
    {
        private readonly Dictionary<string,double> counts=[];
        private readonly List<Probe> probes=[];
        private readonly List<Action> detach=[];
        private readonly Dictionary<Control,string> roles=[];
        internal readonly List<Control> NativeControls=[];
        internal bool Enabled;
        internal Instrumentation(MainForm form,Control host,Control screenshot)
        {
            var roots=new Dictionary<Control,string>{{form,"Form"},{host,"Host"}};
            foreach(Control page in host.Controls)roots[page]=ReferenceEquals(page,screenshot)?"ScreenshotPage":"HiddenPage"+host.Controls.GetChildIndex(page);
            foreach(var field in new[]{"_shotToolbar","_shotBody","_shotSplit","_shotLeft","_shotRight"})roots[FieldControl(form,field)]=field;
            var split=(ScreenshotRebuildSplit)FieldControl(form,"_shotSplit");roots[split.Panel1]="PaneHost0";roots[split.Panel2]="PaneHost1";
            var canvasIndex=0;foreach(var canvas in Descendants(screenshot).OfType<ScreenshotRebuildCanvas>())roots[canvas]="Canvas"+canvasIndex++;
            foreach(var entry in roots)
            {
                var control=entry.Key;var role=entry.Value;NativeControls.Add(control);
                roles[control]=role;
                if(control.IsHandleCreated)probes.Add(new Probe(control,role,this));
                LayoutEventHandler layout=(_,_)=>Count(role+".Layout");EventHandler size=(_,_)=>Count(role+".SizeChanged");
                InvalidateEventHandler invalidated=(_,_)=>Count(role+".Invalidated");PaintEventHandler paint=(_,_)=>Count(role+".PaintEvent");
                control.Layout+=layout;control.SizeChanged+=size;control.Invalidated+=invalidated;control.Paint+=paint;
                detach.Add(()=>{control.Layout-=layout;control.SizeChanged-=size;control.Invalidated-=invalidated;control.Paint-=paint;});
            }
            foreach(Control page in host.Controls)
            {
                var group=ReferenceEquals(page,screenshot)?"ScreenshotTree":"HiddenTree"+host.Controls.GetChildIndex(page);
                foreach(var control in Descendants(page))
                {LayoutEventHandler handler=(_,_)=>Count(group+".Layout");control.Layout+=handler;detach.Add(()=>control.Layout-=handler);}
            }
        }
        internal void Count(string key,double value=1){if(Enabled)counts[key]=counts.GetValueOrDefault(key)+value;}
        internal string Role(Control control)=>roles.GetValueOrDefault(control,control.GetType().Name);
        internal void Reset()=>counts.Clear();
        internal void Accumulate(Dictionary<string,double> total){foreach(var pair in counts)total[pair.Key]=total.GetValueOrDefault(pair.Key)+pair.Value;}
        public void Dispose(){Enabled=false;foreach(var probe in probes)probe.ReleaseHandle();foreach(var action in detach)action();}
    }
    private sealed class Probe:NativeWindow
    {
        private readonly string role;private readonly Instrumentation owner;
        internal Probe(Control control,string role,Instrumentation owner){this.role=role;this.owner=owner;AssignHandle(control.Handle);}
        protected override void WndProc(ref Message message)
        {
            var name=message.Msg switch{0x0005=>"WM_SIZE",0x000B=>"WM_SETREDRAW",0x000F=>"WM_PAINT",0x0014=>"WM_ERASEBKGND",0x0046=>"WM_WINDOWPOSCHANGING",0x0047=>"WM_WINDOWPOSCHANGED",0x0083=>"WM_NCCALCSIZE",_=>null};
            if(name is null||!owner.Enabled){base.WndProc(ref message);return;}
            owner.Count(role+"."+name);var timer=Stopwatch.StartNew();try{base.WndProc(ref message);}finally{timer.Stop();owner.Count(role+"."+name+"Ms",timer.Elapsed.TotalMilliseconds);}
        }
    }
}
