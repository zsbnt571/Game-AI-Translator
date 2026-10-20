using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// This explicit entry owns a fresh test data root. It never imports settings,
// starts capture/OCR, reads the user's game library, or requests translation.
internal static class ScreenshotRebuildSelfTests
{
    private const string Prefix="--screenshot-rebuild-smoke=";
    private static string? _validatedRoot;
    internal static bool IsActive=>_validatedRoot is not null;
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle,int message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle,int index);

    internal static void ValidateLaunch(string[] args)
    {
        if(!args.Any(x=>x.StartsWith("--screenshot-rebuild-smoke",StringComparison.OrdinalIgnoreCase)))return;
        var smoke=args.Where(x=>x.StartsWith(Prefix,StringComparison.OrdinalIgnoreCase)).ToArray();
        var roots=args.Where(x=>x.StartsWith("--data-root=",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(smoke.Length!=1||roots.Length!=1||!args.Contains("--disable-global-input",StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Screenshot rebuild verification requires one output, one explicit fresh --data-root, and --disable-global-input.");
        var root=AppDataPaths.ValidateExplicitRoot(roots[0]["--data-root=".Length..]);
        if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Screenshot rebuild verification refuses existing data.");
        var output=AppDataPaths.ValidateExplicitRoot(smoke[0][Prefix.Length..]);
        if(!output.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Verification output must be inside its dedicated data root.");
        _validatedRoot=root;
    }

    internal static int Run(string output,string fixtureRoot,bool benchmarkOnly=false,bool resizeBenchmark=false)
    {
        if(!IsActive||!AppDataPaths.HasExplicitRoot||!AppDataPaths.DisableGlobalInput||
           !string.Equals(_validatedRoot,AppDataPaths.Root,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Screenshot rebuild launch guard was not completed.");
        fixtureRoot=Path.GetFullPath(fixtureRoot);
        if(!File.Exists(Path.Combine(fixtureRoot,"FIXTURE-PROVENANCE.json")))
            throw new ArgumentException("Prototype fixture provenance is required.");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(AppDataPaths.Root,"SCREENSHOT-REBUILD-TEST-ONLY.json"),JsonSerializer.Serialize(new
        {
            Purpose="Offscreen screenshot UI verification using user-provided prototype assets and fresh synthetic History",
            BuildIdentity.BuildId,PersonalDataImported=false
        },JsonOptions));
        ConfigurationManager.Save(AppDataPaths.SettingsPath,new ApiSettings
        {
            ApiUrl="",ApiKey="",Model="",VisualModel=VisualModelKind.Off,ThemeMode=ApplicationThemeMode.Day,
            CloseMainWindowBehavior=CloseMainWindowBehavior.Exit,HistoryCleanupByCount=false,
            HistoryCleanupByAge=false,HistoryCleanupBySpace=false,ImageTranslationEnabled=false
        });
        // The profile dropdown intentionally filters incomplete unused profiles.
        // Seed two complete keyless test choices so wheel selection is observable.
        // No part of this harness invokes translation or contacts these endpoints.
        var profileFixture=new FusionState
        {
            SchemaVersion=2,CommonProfile="smoke-profile-a",
            Profiles=new()
            {
                ["smoke-profile-a"]=new ApiSettings{ApiUrl="http://127.0.0.1:1/v1",ApiKey="",AllowEmptyApiKey=true,Model="smoke-only-a"},
                ["smoke-profile-b"]=new ApiSettings{ApiUrl="http://127.0.0.1:1/v1",ApiKey="",AllowEmptyApiKey=true,Model="smoke-only-b"}
            },
            ProfileInfo=new()
            {
                ["smoke-profile-a"]=new(){Name="测试方案 A",Origin="Isolated UI fixture"},
                ["smoke-profile-b"]=new(){Name="测试方案 B",Origin="Isolated UI fixture"}
            }
        };
        File.WriteAllText(Path.Combine(AppDataPaths.Root,"fusion-settings.json"),FusionSecrets.Serialize(profileFixture));
        if(Environment.GetCommandLineArgs().Contains("--screenshot-scrollbar-theme-visible",StringComparer.OrdinalIgnoreCase))
            return ScreenshotScrollbarThemeSelfTests.Run(output,Pump,Complete);
        if(Environment.GetCommandLineArgs().Contains("--screenshot-interaction-visible",StringComparer.OrdinalIgnoreCase))
        {SeedHistory(fixtureRoot);return ScreenshotImageInteractionSelfTests.RunVisibleSplit(output,Pump,Complete);}
        if(resizeBenchmark){SeedHistory(fixtureRoot);var command=Environment.GetCommandLineArgs();return ScreenshotResizeDiagnostics.Run(output,command.Contains("--screenshot-resize-visible",StringComparer.OrdinalIgnoreCase),command.Contains("--screenshot-resize-buffer-experiments",StringComparer.OrdinalIgnoreCase));}
        return benchmarkOnly?Benchmark(output,fixtureRoot):Verify(output,fixtureRoot);
    }

    // These measurements exercise native paint and event paths using synthetic
    // images. They describe the measured workload, never display refresh rate.
    private static int Benchmark(string output,string fixtureRoot)
    {
        var metrics=new List<object>(); MainForm? main=null;
        var failures=new List<string>();
        void CompleteWithoutFixedDelay(Task task)
        {
            var timer=System.Diagnostics.Stopwatch.StartNew();
            while(!task.IsCompleted)
            {
                if(timer.ElapsedMilliseconds>12000)throw new TimeoutException("Benchmark refresh timed out.");
                Application.DoEvents();Thread.Yield();
            }
            task.GetAwaiter().GetResult();Application.DoEvents();
        }
        void Measure(string name,Action<int> work,int samples=24,object? detail=null)
        {
            for(var i=0;i<3;i++)work(i);
            var times=new double[samples];
            for(var i=0;i<samples;i++)
            {
                var timer=System.Diagnostics.Stopwatch.StartNew();work(i);timer.Stop();times[i]=timer.Elapsed.TotalMilliseconds;
            }
            var sorted=times.Order().ToArray();
            metrics.Add(new{Name=name,Samples=samples,MeanMs=times.Average(),P50Ms=sorted[sorted.Length/2],P95Ms=sorted[(int)Math.Ceiling(sorted.Length*.95)-1],MaxMs=sorted[^1],Detail=detail,MeasurementsMs=times});
            File.WriteAllText(Path.Combine(output,"BENCHMARK-PROGRESS.json"),JsonSerializer.Serialize(new{Last=name,Metrics=metrics},JsonOptions));
        }
        static Bitmap Synthetic(Size size)
        {
            var image=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
            using var graphics=Graphics.FromImage(image);
            using var gradient=new LinearGradientBrush(new Rectangle(Point.Empty,size),Color.FromArgb(21,49,72),Color.FromArgb(226,216,181),40f);
            graphics.FillRectangle(gradient,new Rectangle(Point.Empty,size));
            using var pen=new Pen(Color.FromArgb(185,230,242),Math.Max(1,size.Width/500f));
            for(var y=0;y<size.Height;y+=Math.Max(8,size.Height/80))graphics.DrawLine(pen,0,y,size.Width,y);
            for(var x=0;x<size.Width;x+=Math.Max(8,size.Width/60))graphics.DrawLine(pen,x,0,x,size.Height);
            return image;
        }
        try
        {
            foreach(var sourceSize in new[]{new Size(422,885),new Size(3840,2160)})
            {
                using var image=Synthetic(sourceSize);
                using var host=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-24000,-24000),ClientSize=new(800,600)};
                var canvas=new ScreenshotRebuildCanvas{Dock=DockStyle.Fill};host.Controls.Add(canvas);host.Show();canvas.SetImage(image);Pump(300);
                using var target=new Bitmap(canvas.ClientSize.Width,canvas.ClientSize.Height,PixelFormat.Format32bppPArgb);
                var bounds=new Rectangle(Point.Empty,target.Size);
                var details=new{SourceSize=sourceSize,CanvasPixels=canvas.ClientSize,canvas.DeviceDpi,Path="Native canvas.DrawToBitmap -> OnPaint",SamplesExcludeFixedPump=true};
                Measure($"canvas-repeat-{sourceSize.Width}x{sourceSize.Height}",_=>canvas.DrawToBitmap(target,bounds),32,details);
                canvas.SetZoom(2f);Pump(300);
                Measure($"canvas-pan-{sourceSize.Width}x{sourceSize.Height}",i=>{canvas.AutoScrollPosition=new((i%4)*16,(i%6)*21);canvas.DrawToBitmap(target,bounds);},24,details);
                canvas.SetImage(null);host.Close();
            }
            SeedHistory(fixtureRoot);
            main=new MainForm(null,new(CaptureFlashDiagnosticMode.CaptureOnly,false,false))
            {ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-24000,-24000)};
            main.Show();main.MinimumSize=new(100,100);main.ClientSize=new(1920,1080);
            main.NavigateForSmoke("主页");CompleteWithoutFixedDelay(main.RefreshScreenshotWorkspaceForSmoke());
            main.PerformLayout();Pump();
            for(var attempt=0;attempt<3&&main.ClientSize!=new Size(1920,1080);attempt++)
            {main.Size=new(main.Width+1920-main.ClientSize.Width,main.Height+1080-main.ClientSize.Height);main.PerformLayout();Pump();}
            using var page=new Bitmap(2200,1400,PixelFormat.Format32bppPArgb);
            foreach(var sourceSize in new[]{new Size(422,885),new Size(3840,2160)})
            {
                using var original=Synthetic(sourceSize);using var translated=Synthetic(sourceSize);
                foreach(var index in new[]{0,1})main.ScreenshotPaneForSmoke(index).SetContent(original,translated,English,Chinese);
                main.SetScreenshotModesForSmoke(0,1);Pump();
                var details=new{SourceSize=sourceSize,StartingClientPixels=main.ClientSize,main.DeviceDpi,Path="Native event/layout + DrawCurrentPageForSmoke",SamplesExcludeFixedPump=true};
                Measure($"split-layout-paint-{sourceSize.Width}x{sourceSize.Height}",i=>{main.SetScreenshotSplitForSmoke(38+(i%13)*2);Application.DoEvents();main.DrawCurrentPageForSmoke(page);},26,details);
                main.SetScreenshotSplitForSmoke(50);
                var outer=main.Size;
                Measure($"window-resize-layout-paint-{sourceSize.Width}x{sourceSize.Height}",i=>{main.Size=new(outer.Width-(i%5)*30,outer.Height-(i%4)*15);Application.DoEvents();main.DrawCurrentPageForSmoke(page);},24,details);
                main.Size=outer;
                Measure($"content-switch-layout-paint-{sourceSize.Width}x{sourceSize.Height}",i=>{main.SetScreenshotModesForSmoke(i%2==0?2:0,i%2==0?3:1);Application.DoEvents();main.DrawCurrentPageForSmoke(page);},24,details);
                foreach(var index in new[]{0,1})main.ScreenshotPaneForSmoke(index).ClearContent();
            }
            main.SetScreenshotModesForSmoke(0,1);CompleteWithoutFixedDelay(main.SelectScreenshotRecordForSmoke(1));CompleteWithoutFixedDelay(main.SelectScreenshotRecordForSmoke(0));
            var decodeField=typeof(MainForm).GetField("_historyThumbnailDecodeCount",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var beforeDecode=(int)decodeField.GetValue(main)!;
            var beforeWorkspace=main.ScreenshotWorkspacePerformanceForSmoke();
            Measure("same-record-refresh",_=>CompleteWithoutFixedDelay(main.RefreshScreenshotWorkspaceForSmoke()),12,new{Path="Async refresh completed without fixed Pump delay; thumbnail count measured separately"});
            var afterDecode=(int)decodeField.GetValue(main)!;
            metrics.Add(new{Name="same-record-thumbnail-decodes",Before=beforeDecode,After=afterDecode,Delta=afterDecode-beforeDecode,Note="Thumbnail count only; original/translated full image decode is not inferred from this counter."});
            var afterWorkspace=main.ScreenshotWorkspacePerformanceForSmoke();
            metrics.Add(new{Name="same-record-full-image-decodes",Before=beforeWorkspace.SelectionDecodes,After=afterWorkspace.SelectionDecodes,Delta=afterWorkspace.SelectionDecodes-beforeWorkspace.SelectionDecodes});
        }
        catch(Exception ex){failures.Add(SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally
        {
            if(main is not null){try{main.ExitForSmoke();main.Dispose();}catch(Exception ex){failures.Add(SafeDiagnosticOutput.ExceptionSummary(ex));}}
            File.WriteAllText(Path.Combine(output,"BENCHMARK.json"),JsonSerializer.Serialize(new
            {
                Passed=failures.Count==0,Failures=failures,BuildIdentity.BuildId,Metrics=metrics,
                PersonalDataRead=false,RealApiRequests=0,OcrRequests=0,GlobalInputDisabled=AppDataPaths.DisableGlobalInput,
                Scope="Isolated offscreen native paint and interaction latency. No app FPS or physical-screen smoothness claim.",FinishedUtc=DateTimeOffset.UtcNow
            },JsonOptions));
        }
        return failures.Count==0?0:1;
    }

    private static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException(message);}

    private static void Pump(int milliseconds=100)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        do{Application.DoEvents();Thread.Sleep(8);}while(watch.ElapsedMilliseconds<milliseconds);
    }

    private static void Complete(Task task)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        while(!task.IsCompleted)
        {
            if(watch.ElapsedMilliseconds>12000)throw new TimeoutException("Screenshot workspace refresh did not finish.");
            Pump(20);
        }
        task.GetAwaiter().GetResult();Pump();
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach(Control child in control.Controls)foreach(var nested in Descendants(child))yield return nested;
    }

    private static Dictionary<string,string> HistoryHashes()=>Directory.GetFiles(AppDataPaths.HistoryRoot,"*",SearchOption.AllDirectories)
        .ToDictionary(p=>Path.GetRelativePath(AppDataPaths.HistoryRoot,p),p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))),StringComparer.OrdinalIgnoreCase);

    private static void SeedHistory(string fixtureRoot)
    {
        var settings=new ApiSettings{HistoryCleanupByCount=false,HistoryCleanupByAge=false,HistoryCleanupBySpace=false};
        using(var history=new SessionServices(AppDataPaths.HistoryRoot))
        {
            using var source=new Bitmap(Path.Combine(fixtureRoot,"forest-original.png"));
            using var translated=new Bitmap(Path.Combine(fixtureRoot,"forest-translated.png"));
            history.AddHistory(source,OcrEngineKind.Rapid,"MISSING-SOURCE: retained source text","缺图记录：文字仍然保留。","zh",settings,translated);
            var broken=history.SnapshotHistory().Single();
            foreach(var path in new[]{broken.SourceImagePath,broken.TranslatedImagePath})
            {
                Require(path is not null&&Path.GetFullPath(path).StartsWith(AppDataPaths.HistoryRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Test image path escaped its data root.");
                File.Delete(path!);
            }
            history.AddHistory(source,OcrEngineKind.Rapid,"UNTRANSLATED-SOURCE: source image has no translation.","","zh",settings);
            foreach(var record in new[]{("forest","THE BIRCHWOOD CAMP","白桦林营地"),("lighthouse","THE RIVERSIDE LIGHTHOUSE","河岸灯塔"),("observatory","THE MISTVALE OBSERVATORY","雾谷观测站")})
            {
                using var original=new Bitmap(Path.Combine(fixtureRoot,record.Item1+"-original.png"));
                using var target=new Bitmap(Path.Combine(fixtureRoot,record.Item1+"-translated.png"));
                history.AddHistory(original,OcrEngineKind.Rapid,record.Item2+"\r\n\r\n"+English,
                    record.Item3+" · 任务档案\r\n\r\n"+Chinese,"zh",settings,target);
            }
        }
        // Test-owned fixture metadata matches the supplied demonstration chronology.
        var index=Path.Combine(AppDataPaths.HistoryRoot,"index.json");
        var entries=JsonNode.Parse(File.ReadAllText(index))!.AsArray();
        foreach(var entry in entries)
        {
            var source=entry!["SourceText"]!.GetValue<string>();
            var prototype=source.StartsWith("THE MISTVALE")?"observatory":source.StartsWith("THE RIVERSIDE")?"lighthouse":source.StartsWith("THE BIRCHWOOD")?"forest":null;
            entry["Timestamp"]=JsonValue.Create(new DateTimeOffset(DateTime.Today.AddDays(prototype=="forest"?-1:prototype is null?-10:0).AddHours(prototype=="observatory"?14:prototype=="lighthouse"?13:21).AddMinutes(prototype=="observatory"?32:prototype=="lighthouse"?18:8)));
            if(prototype is null)continue;
            var imageName=entry["Id"]!.GetValue<string>()+".translated.png";
            File.Copy(Path.Combine(fixtureRoot,prototype+"-translated.png"),Path.Combine(AppDataPaths.HistoryRoot,imageName));
            var previous=Path.Combine(AppDataPaths.HistoryRoot,entry["TranslatedImageFile"]!.GetValue<string>());
            entry["TranslatedImageFile"]=imageName;
            File.Delete(previous);
        }
        File.WriteAllText(index,entries.ToJsonString(JsonOptions));
    }

    private static int Verify(string output,string fixtureRoot)
    {
        MainForm? form=null;var checks=new List<object>();var failures=0;
        void Check(string name,Action check)
        {
            File.WriteAllText(Path.Combine(output,"PROGRESS.json"),JsonSerializer.Serialize(new{Current=name,Checks=checks},JsonOptions));
            try{check();checks.Add(new{Name=name,Passed=true});}
            catch(Exception ex){failures++;checks.Add(new{Name=name,Passed=false,Error=SafeDiagnosticOutput.ExceptionSummary(ex)});}
        }
        MainForm Open()
        {
            var main=new MainForm(null,new(CaptureFlashDiagnosticMode.CaptureOnly,false,false))
            {ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-24000,-24000)};
            main.Show();main.MinimumSize=new(100,100);
            var desired=new Size((int)Math.Round(1280*main.DeviceDpi/96d),(int)Math.Round(800*main.DeviceDpi/96d));
            main.ClientSize=desired;
            main.NavigateForSmoke("主页");Complete(main.RefreshScreenshotWorkspaceForSmoke());main.PerformLayout();Pump();
            // The existing borderless shell adds nonclient resize margins. Correct
            // the measured client area instead of assuming ClientSize assignment
            // produces the requested physical dimensions on every Windows DPI.
            for(var attempt=0;attempt<3&&main.ClientSize!=desired;attempt++)
            {
                main.Size=new(main.Width+desired.Width-main.ClientSize.Width,main.Height+desired.Height-main.ClientSize.Height);
                main.PerformLayout();Pump();
            }
            Require(main.ClientSize==desired,"Native client dimensions differ from the requested logical comparison size.");
            return main;
        }
        void Snapshot(string name)
        {
            Pump();using var bitmap=new Bitmap(form!.ClientSize.Width,form.ClientSize.Height);
            form.DrawCurrentPageForSmoke(bitmap);bitmap.Save(Path.Combine(output,name+"-native.png"),ImageFormat.Png);
            using var logical=new Bitmap(1280,800);
            using(var g=Graphics.FromImage(logical)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(bitmap,new Rectangle(0,0,1280,800));}
            logical.Save(Path.Combine(output,name+"-1280x800.png"),ImageFormat.Png);
            File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(new
            {
                ActualDeviceDpi=form.DeviceDpi,NativeClientSize=form.ClientSize,NormalizedComparisonSize=new Size(1280,800),
                RequestedLogicalSize=new Size(1280,800),Workspace=form.ScreenshotWorkspaceStateForSmoke(),
                LeftContentViewport=form.ScreenshotPaneForSmoke(0).ContentViewportBounds,
                RightContentViewport=form.ScreenshotPaneForSmoke(1).ContentViewportBounds,
                SearchCue=Descendants(form.LegacyWindow).OfType<TextBox>().Where(x=>x.Name=="ScreenshotHistorySearch").Select(x=>new{x.Text,x.PlaceholderText,x.Focused,x.IsHandleCreated,x.Visible}).ToArray(),
                Controls=Descendants(form.LegacyWindow).Where(x=>x.Visible&&x.GetType().Name.StartsWith("Screenshot",StringComparison.Ordinal)).Select(x=>new
                {Type=x.GetType().Name,x.Name,x.Bounds,x.ClientSize,ScreenBounds=new Rectangle(form.PointToClient(x.PointToScreen(Point.Empty)),x.Size)}).ToArray(),
                Note="Offscreen WinForms DrawToBitmap. Native pixels are retained; the 1280x800 image is resampled for prototype comparison. Not a manual display acceptance test."
            },JsonOptions));
        }
        try
        {
            form=Open();Check("fresh empty History remains stable",()=>
            {var state=form.ScreenshotWorkspaceStateForSmoke();Require(state.RecordCount==0&&state.LeftVisible&&state.RightVisible,"Empty state collapsed or contains records.");Snapshot("00-empty");});
            form.ExitForSmoke();form.Dispose();form=null;Pump();SeedHistory(fixtureRoot);var historyBefore=HistoryHashes();form=Open();
            Check("prototype images load through persisted History",()=>
            {
                var state=form.ScreenshotWorkspaceStateForSmoke();Require(state.RecordCount==5&&state.LeftMode==0&&state.RightMode==1,"Default history state differs.");
                Require(form.ScreenshotPaneForSmoke(0).CurrentBitmap is {Width:1200,Height:800}&&form.ScreenshotPaneForSmoke(1).CurrentBitmap is {Width:1200,Height:800},"Prototype images missing or dimensions changed.");
                Snapshot("01-prototype-dual-image");
            });
            Check("compact toolbar, history rail and viewport match the specified logical proportions",()=>
            {
                var state=form.ScreenshotWorkspaceStateForSmoke();var scale=state.DeviceDpi/96d;
                Require(Math.Abs(state.ToolbarHeight/scale-46)<=1,"Screenshot toolbar is not 46 logical pixels.");
                Require(Math.Abs(state.HistoryBounds.Width/scale-62)<=1,"Collapsed history rail is not 62 logical pixels.");
                Require(Math.Abs(state.LeftBounds.Width-state.RightBounds.Width)<=2,"Default panes do not have equal widths.");
                Require(form.ScreenshotPaneForSmoke(0).ContentViewportBounds.Height/scale>=450&&form.ScreenshotPaneForSmoke(1).ContentViewportBounds.Height/scale>=450,"Image viewport is too short for the reference's compact layout.");
            });
            Check("screenshot profile and former menu actions are directly available without record metadata",()=>
            {
                var toolbar=form.ScreenshotToolbarBoundsForSmoke();
                foreach(var name in new[]{"Profile","EditProfile","Retranslate","EditRecord","ResetSplit"})
                    Require(toolbar.ContainsKey("ScreenshotToolbar"+name),name+" is not directly visible in the toolbar.");
                Require(!toolbar.ContainsKey("ScreenshotToolbarOverflow"),"Full-width toolbar still requires an overflow menu.");
                var profile=Descendants(form.LegacyWindow).Single(x=>x.Name=="ScreenshotToolbarProfile");
                Require(profile.Parent is Panel&&profile.Visible,"Profile selector is still hosted in a popup.");
                Require(typeof(MainForm).GetField("_shotRecordTitle",BindingFlags.Instance|BindingFlags.NonPublic) is null&&typeof(MainForm).GetField("_shotMetadata",BindingFlags.Instance|BindingFlags.NonPublic) is null,"Low-value record information still occupies the toolbar.");
            });
            Check("toolbar keeps all controls inside the row without overlap at common and narrow widths",()=>
            {
                var original=form.Size;var scale=form.DeviceDpi/96d;
                foreach(var logicalWidth in new[]{1280,960,800})
                {
                    var desired=(int)Math.Round(logicalWidth*scale);
                    form.Width+=desired-form.ClientSize.Width;form.PerformLayout();Pump();
                    var bounds=form.ScreenshotToolbarBoundsForSmoke();var entries=bounds.ToArray();
                    Require(bounds.ContainsKey("ScreenshotToolbarProfile"),"Narrow layout hid the profile selector.");
                    Require(bounds.ContainsKey("ScreenshotToolbarRetranslate"),"Common/narrow width hid retranslation too early.");
                    foreach(var item in entries)Require(item.Value.Left>=0&&item.Value.Right<=form.ClientSize.Width&&item.Value.Top>=0&&item.Value.Bottom<=form.ScreenshotWorkspaceStateForSmoke().ToolbarHeight,"Toolbar item escaped its row: "+item.Key);
                    for(var left=0;left<entries.Length;left++)for(var right=left+1;right<entries.Length;right++)
                        Require(!entries[left].Value.IntersectsWith(entries[right].Value),"Toolbar controls overlap: "+entries[left].Key+" / "+entries[right].Key);
                    if(logicalWidth>=960)Require(!bounds.ContainsKey("ScreenshotToolbarOverflow"),"Common-width toolbar unnecessarily hides actions.");
                    else Require(bounds.ContainsKey("ScreenshotToolbarOverflow"),"Narrow toolbar lost hidden actions.");
                }
                form.Size=original;form.PerformLayout();Pump();
            });
            Check("canvas cache preserves final quality, reuses stable paints and releases only its own images",()=>
            {
                using var source=new Bitmap(422,885,PixelFormat.Format32bppPArgb);
                using(var graphics=Graphics.FromImage(source))
                {
                    using var brush=new LinearGradientBrush(new Rectangle(0,0,422,885),Color.FromArgb(160,20,90,180),Color.FromArgb(230,225,150,20),35f);
                    graphics.FillRectangle(brush,0,0,422,885);
                }
                var sourcePixel=source.GetPixel(150,250);
                using var host=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-24000,-24000),ClientSize=new(800,600)};
                var canvas=new ScreenshotRebuildCanvas{Dock=DockStyle.Fill};host.Controls.Add(canvas);host.Show();canvas.SetImage(source);Pump(300);
                using var target=new Bitmap(canvas.ClientSize.Width,canvas.ClientSize.Height,PixelFormat.Format32bppPArgb);var bounds=new Rectangle(Point.Empty,target.Size);
                canvas.DrawToBitmap(target,bounds);var first=canvas.CacheDiagnostics;
                Require(first.Pixels>0&&!first.RefinementPending,"Idle canvas has no final-quality cache.");
                for(var paint=0;paint<8;paint++)canvas.DrawToBitmap(target,bounds);
                Require(canvas.CacheDiagnostics.Builds==first.Builds&&canvas.CacheDiagnostics.Hits>=first.Hits+8,"Stable paints repeat the image resampling work.");
                canvas.SetZoom(1.4f);canvas.DrawToBitmap(target,bounds);
                Require(canvas.CacheDiagnostics.RefinementPending,"Zoom did not defer final resampling until input settles.");
                Pump(300);canvas.DrawToBitmap(target,bounds);var settled=canvas.CacheDiagnostics;
                Require(!settled.RefinementPending&&settled.Builds>first.Builds,"Settled zoom failed to build its final image.");
                var cached=(Bitmap)typeof(ScreenshotRebuildCanvas).GetField("displayCache",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(canvas)!;
                using(var expected=new Bitmap(cached.Width,cached.Height,PixelFormat.Format32bppPArgb))
                {
                    using(var graphics=Graphics.FromImage(expected))
                    {graphics.CompositingMode=CompositingMode.SourceCopy;graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;graphics.DrawImage(source,new Rectangle(Point.Empty,expected.Size),new Rectangle(Point.Empty,source.Size),GraphicsUnit.Pixel);}
                    for(var y=7;y<expected.Height;y+=23)for(var x=7;x<expected.Width;x+=17)
                        Require(cached.GetPixel(x,y).ToArgb()==expected.GetPixel(x,y).ToArgb(),"Final cached image differs from the established high-quality alpha-preserving resample.");
                }
                var events=0;canvas.SetZoom(5);Pump(300);canvas.ViewStateChanged+=(_,_)=>events++;
                var invalidations=0;canvas.Invalidated+=(_,_)=>invalidations++;
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                typeof(ScreenshotRebuildCanvas).GetMethod("OnMouseWheel",flags)!.Invoke(canvas,[new HandledMouseEventArgs(MouseButtons.None,0,30,30,120)]);
                Require(invalidations==0&&events==0,"Wheel input at the zoom limit still invalidates or changes the image state.");
                using var large=new Bitmap(3840,2160,PixelFormat.Format32bppPArgb);canvas.SetImage(large);
                Require(canvas.CacheDiagnostics.Pixels==0,"Changing the source retains the previous image cache.");
                canvas.SetZoom(5);Pump(300);canvas.DrawToBitmap(target,bounds);
                Require(canvas.CacheDiagnostics.SizeLimitExceeded&&canvas.CacheDiagnostics.Pixels==0,"Large zoom exceeds the bounded image cache policy.");
                canvas.SetImage(null);Require(canvas.CacheDiagnostics.Pixels==0,"Clearing an image retained its display cache.");
                Require(source.GetPixel(150,250)==sourcePixel&&large.Width==3840,"Canvas altered or disposed a borrowed source image.");host.Close();
            });
            Check("same-record refresh and mode changes avoid repeated image decoding and full-theme work",()=>
            {
                form.SetScreenshotModesForSmoke(0,1);Pump(750);var before=form.ScreenshotWorkspacePerformanceForSmoke();
                for(var repeat=0;repeat<3;repeat++)Complete(form.RefreshScreenshotWorkspaceForSmoke());
                Require(form.ScreenshotWorkspacePerformanceForSmoke().SelectionDecodes==before.SelectionDecodes,"Refreshing the same record decodes its images again.");
                for(var mode=0;mode<4;mode++)form.SetScreenshotModesForSmoke(mode,3-mode);
                form.SetScreenshotModesForSmoke(0,1);Pump(750);var after=form.ScreenshotWorkspacePerformanceForSmoke();
                Require(after.FullThemeApplies==before.FullThemeApplies,"Switching content reapplies the complete screenshot theme.");
                Require(after.LayoutWrites==before.LayoutWrites,"Returning to unchanged view settings rewrites the layout file.");
            });
            Check("continuous split input defers layout writes until idle and persists the final position",()=>
            {
                form.SetScreenshotSplitForSmoke(50);Pump(750);var before=form.ScreenshotWorkspacePerformanceForSmoke().LayoutWrites;
                for(var step=0;step<10;step++){form.SetScreenshotSplitForSmoke(51+step);Pump(80);Require(form.ScreenshotWorkspacePerformanceForSmoke().LayoutWrites==before,"Continuous input causes synchronous intermediate layout writes.");}
                Pump(750);Require(form.ScreenshotWorkspacePerformanceForSmoke().LayoutWrites==before+1,"Idle did not persist exactly one final layout.");
                var saved=ScreenshotWorkspaceLayoutStore.Load(Path.Combine(AppDataPaths.Root,"screenshot-workspace.json"));
                Require(Math.Abs(saved.SplitPercent-60)<.1,"The final split position was not saved.");form.SetScreenshotSplitForSmoke(50);Pump(750);
            });
            Check("screenshot-only native resize policy preserves other pages and all control handles",()=>
            {
                var host=(WorkspacePageHost)typeof(MainForm).GetField("_mainContentHost",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
                var hostHandle=host.Handle;var canvases=Descendants(form.ScreenshotPaneForSmoke(0)).OfType<ScreenshotRebuildCanvas>().Concat(Descendants(form.ScreenshotPaneForSmoke(1)).OfType<ScreenshotRebuildCanvas>()).ToArray();
                var handles=canvases.Select(control=>control.Handle).ToArray();
                foreach(var page in new[]{"主页","内嵌翻译","设置","主页","主页"})
                {
                    Require(form.NavigateForSmoke(page),"Page navigation invariant failed: "+page);Pump(150);var screenshot=page=="主页";
                    Require(host.CompositeChildren==!screenshot&&form.ScreenshotResizeUsesRedrawBatchForSmoke==!screenshot,"Native resize policy leaked across pages.");
                    Require(((GetWindowLongPtr(host.Handle,-20).ToInt64()&0x02000000)!=0)==!screenshot,"Native host style differs from its page policy.");
                    Require(host.Handle==hostHandle&&canvases.Select(control=>control.Handle).SequenceEqual(handles),"Switching native policy recreated a window handle.");
                }
                var size=form.Size;form.WindowState=FormWindowState.Minimized;Pump(120);form.WindowState=FormWindowState.Normal;Pump(250);form.Size=size;Pump(150);
                Require(form.ScreenshotPaneForSmoke(0).Visible&&form.ScreenshotPaneForSmoke(1).Visible&&host.Handle==hostHandle,"Minimize/restore lost the screenshot surface.");
                form.WindowState=FormWindowState.Maximized;Pump(180);Require(host.Visible&&form.ScreenshotPaneForSmoke(0).Visible&&form.ScreenshotPaneForSmoke(1).Visible,"Maximizing the window loses a screenshot pane.");
                form.WindowState=FormWindowState.Normal;form.Size=size;Pump(200);Require(host.Handle==hostHandle&&canvases.Select(control=>control.Handle).SequenceEqual(handles),"Maximize/restore recreated screenshot window handles.");
                Require(typeof(ScreenshotHistoryRail).GetProperty("HasSelection",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic) is null,"Redundant selected-record count remains on the collapsed history rail.");
                var visibility=host.Controls.Cast<Control>().Select(control=>control.Visible).ToArray();
                using var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height);form.DrawCurrentPageForSmoke(bitmap);
                Require(host.Controls.Cast<Control>().Select(control=>control.Visible).SequenceEqual(visibility),"Artifact rendering changes hidden page visibility.");
            });
            Check("four-edge screenshot frame exactly preserves the original border pixels",()=>
            {
                var paint=typeof(MainForm).GetMethod("PaintWorkspaceBorder",BindingFlags.Instance|BindingFlags.NonPublic)!;
                using var oldFrame=new Bitmap(form.ClientSize.Width,form.ClientSize.Height,PixelFormat.Format32bppPArgb);
                using var newFrame=new Bitmap(form.ClientSize.Width,form.ClientSize.Height,PixelFormat.Format32bppPArgb);
                try
                {
                    foreach(var pair in new[]{(oldFrame,false),(newFrame,true)})
                    {using var graphics=Graphics.FromImage(pair.Item1);graphics.Clear(Color.Magenta);form.ScreenshotFrameSeparateLinesOverrideForSmoke=pair.Item2;paint.Invoke(form,[graphics]);}
                    var width=oldFrame.Width;var height=oldFrame.Height;var border=Math.Max(4,(int)Math.Ceiling(form.DeviceDpi/96d)+2);
                    for(var y=0;y<height;y++)for(var x=0;x<width;x++)
                        if(x<border||x>=width-border||y<border||y>=height-border)Require(oldFrame.GetPixel(x,y)==newFrame.GetPixel(x,y),"Four-edge border pixels differ from the established frame.");
                }
                finally{form.ScreenshotFrameSeparateLinesOverrideForSmoke=null;}
            });
            Check("height-only resizing does not repeatedly change screenshot button sizes",()=>
            {
                form.SetScreenshotModesForSmoke(0,1);var original=form.Size;Pump(250);
                var buttons=new[]{form.ScreenshotPaneForSmoke(0),form.ScreenshotPaneForSmoke(1)}.SelectMany(Descendants).OfType<ScreenshotRebuildButton>().ToArray();var sizeEvents=0;
                EventHandler changed=(_,_)=>sizeEvents++;foreach(var button in buttons)button.SizeChanged+=changed;
                try{for(var step=0;step<8;step++){form.Height=original.Height-(step%3+1)*13;Pump(25);}Require(sizeEvents==0,"Height-only resize repeatedly changes fixed toolbar button widths/heights.");}
                finally{foreach(var button in buttons)button.SizeChanged-=changed;form.Size=original;Pump(200);}
            });
            Check("repeated narrow and wide layouts retain readable controls across compact breakpoints",()=>
            {
                var original=form.Size;var scale=form.DeviceDpi/96d;
                try
                {
                    foreach(var logicalWidth in new[]{1080,760,1000,800,1280,760,1280})
                    {
                        var desired=(int)Math.Round(logicalWidth*scale);form.Width+=desired-form.ClientSize.Width;form.PerformLayout();Pump(60);
                        foreach(var pane in new[]{form.ScreenshotPaneForSmoke(0),form.ScreenshotPaneForSmoke(1)})
                        {
                            Require(pane.ContentViewportBounds.Width>0&&pane.ContentViewportBounds.Height>0,"Narrow pane lost its content viewport.");
                            foreach(var button in Descendants(pane).OfType<ScreenshotRebuildButton>().Where(button=>button.Visible))
                                Require(button.Parent!.ClientRectangle.Contains(button.Bounds),"Compact pane button escaped its own header/toolbar.");
                        }
                    }
                }
                finally{form.Size=original;form.PerformLayout();Pump(250);}
            });
            Check("fit-width and manual resize preserve scroll position and clamp it within the final image extent",()=>
            {
                var original=form.Size;using var portrait=new Bitmap(422,885);var pane=form.ScreenshotPaneForSmoke(0);
                var canvas=Descendants(pane).OfType<ScreenshotRebuildCanvas>().Single();
                try
                {
                    pane.SetContent(portrait,portrait,"Synthetic text","合成译文");pane.View=ScreenshotPaneView.OriginalImage;pane.FitWidth();Pump(250);canvas.AutoScrollPosition=new(0,150);var before=canvas.ScrollOffset;
                    Require(before.Y>0,"Portrait fit-width fixture did not expose a scroll range.");form.Height+=35;Pump(250);
                    Require(canvas.ScrollOffset.Y>0&&Math.Abs(canvas.ScrollOffset.Y-before.Y)<=1,"Fit-width resize unnecessarily resets the vertical scroll position.");
                    pane.ZoomIn();canvas.SetZoom(2);Pump(250);canvas.AutoScrollPosition=new(120,200);before=canvas.ScrollOffset;form.Width+=70;Pump(250);
                    Require(canvas.ScrollOffset.Y>0,"Manual resize loses the current vertical image region.");
                    canvas.AutoScrollPosition=new(int.MaxValue,int.MaxValue);form.Height+=60;Pump(300);
                    var position=canvas.ScrollOffset;var maxX=canvas.HorizontalScroll.Visible?Math.Max(0,canvas.HorizontalScroll.Maximum-canvas.HorizontalScroll.LargeChange+1):0;
                    var maxY=canvas.VerticalScroll.Visible?Math.Max(0,canvas.VerticalScroll.Maximum-canvas.VerticalScroll.LargeChange+1):0;
                    Require(position.X>=0&&position.Y>=0&&position.X<=maxX+1&&position.Y<=maxY+1,"Manual image scroll exceeds its final bounds after resize.");
                    using var bitmap=new Bitmap(canvas.ClientSize.Width,canvas.ClientSize.Height);canvas.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));var cached=canvas.CacheDiagnostics;
                    Require(cached.Pixels==0||Math.Abs(cached.Size.Width/(double)cached.Size.Height-422d/885)<.005,"Final resized display image has the wrong aspect ratio.");
                }
                finally
                {
                    pane.ClearContent();form.Size=original;Complete(form.SelectScreenshotRecordForSmoke(1));Complete(form.SelectScreenshotRecordForSmoke(0));
                    foreach(var item in new[]{form.ScreenshotPaneForSmoke(0),form.ScreenshotPaneForSmoke(1)})item.FitWindow();Pump(250);
                }
            });
            ScreenshotImageInteractionSelfTests.Run(Check,Pump);
            Check("prototype history expansion preserves selection",()=>
            {
                var before=form.ScreenshotWorkspaceStateForSmoke();form.ToggleScreenshotHistoryForSmoke();Pump();var after=form.ScreenshotWorkspaceStateForSmoke();
                Require(after.HistoryVisible&&after.SelectedId==before.SelectedId,"History expansion changed selection.");
                Require(Math.Abs(after.HistoryBounds.Width/(after.DeviceDpi/96d)-262)<=1,"Expanded history is not 262 logical pixels.");Snapshot("02-prototype-expanded-history");
                form.ToggleScreenshotHistoryForSmoke();Pump();Require(!form.ScreenshotWorkspaceStateForSmoke().HistoryVisible,"History did not collapse.");
            });
            Check("history right click selects the intended record before its menu action",()=>
            {
                form.ToggleScreenshotHistoryForSmoke();Pump();
                var history=Descendants(form.LegacyWindow).OfType<ScreenshotRebuildHistory>().Single();
                var positions=(Rectangle[])typeof(ScreenshotRebuildHistory).GetField("bounds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(history)!;
                var second=positions[1];var point=new Point(second.Left+second.Width/2,second.Top+history.AutoScrollPosition.Y+second.Height/2);
                typeof(ScreenshotRebuildHistory).GetMethod("OnMouseDown",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(history,[new MouseEventArgs(MouseButtons.Right,1,point.X,point.Y,0)]);
                Complete(form.RefreshScreenshotWorkspaceForSmoke());
                Require(history.SelectedIndex==1&&history.SelectedRecord!.SourceText!.StartsWith("THE RIVERSIDE"),"Right click did not select the clicked record.");
                Complete(form.SelectScreenshotRecordForSmoke(0));form.ToggleScreenshotHistoryForSmoke();Pump();
            });
            Check("direct screenshot profile selector retains wheel protection",()=>
            {
                form.PrepareScreenshotMoreMenuForSmoke();
                var box=(ComboBox)typeof(MainForm).GetField("_shotProfile",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
                Require(box.Visible&&box.Parent is Panel,"Profile selection is not a visible toolbar control.");
                Require(box.DrawMode==DrawMode.OwnerDrawFixed,$"Screenshot profile DrawMode is {box.DrawMode}, expected OwnerDrawFixed.");
                Require(box.Items.Count>=3,$"Two complete synthetic profiles plus Follow Common were expected; got {box.Items.Count} choices.");
                var selected=box.SelectedIndex;box.DroppedDown=false;
                foreach(var delta in new[]{120,-120})SendMessage(box.Handle,0x20A,(IntPtr)((long)delta<<16),IntPtr.Zero);
                Pump();Require(box.SelectedIndex==selected,"Mouse wheel changed the screenshot profile.");
                var deliberate=selected==0?1:0;box.SelectedIndex=deliberate;Pump();Require(box.SelectedIndex==deliberate,"Deliberate profile selection was blocked.");
                box.SelectedIndex=selected;Pump();
            });
            Check("original image and translated text reproduce prototype content modes",()=>
            {form.SetScreenshotModesForSmoke(0,3);Pump();Require(form.ScreenshotPaneForSmoke(1).CurrentText.StartsWith("雾谷观测站"),"Translated text differs.");Snapshot("03-prototype-original-and-text");});
            for(var left=0;left<4;left++)for(var right=0;right<4;right++)
            {
                var l=left;var r=right;Check($"independent content selection {l}/{r}",()=>
                {
                    form.SetScreenshotModesForSmoke(l,r);Pump(25);var state=form.ScreenshotWorkspaceStateForSmoke();
                    Require(state.LeftMode==l&&state.RightMode==r&&state.LeftVisible&&state.RightVisible,"Content modes are not independent.");
                    foreach(var index in new[]{0,1})Require(form.ScreenshotPaneForSmoke(index).HasContent,"Complete record lost content.");
                });
            }
            Check("independent zoom and text size",()=>
            {
                form.SetScreenshotModesForSmoke(0,1);var left=form.ScreenshotPaneForSmoke(0);var right=form.ScreenshotPaneForSmoke(1);var before=right.CaptureViewState();
                left.ZoomIn();Pump();Require(right.CaptureViewState()==before,"Left zoom changed right pane.");left.FitWindow();
                form.SetScreenshotModesForSmoke(2,3);var rightFont=right.FontSize;left.FontSize=24;Pump();Require(left.FontSize==24&&right.FontSize==rightFont,"Font sizes are linked.");left.FontSize=18;
            });
            Check("swap carries independent view states",()=>
            {
                form.SetScreenshotModesForSmoke(0,3);var left=form.ScreenshotPaneForSmoke(0).CaptureViewState();var right=form.ScreenshotPaneForSmoke(1).CaptureViewState();
                form.SwapScreenshotPanesForSmoke();Pump();Require(form.ScreenshotPaneForSmoke(0).CaptureViewState()==right&&form.ScreenshotPaneForSmoke(1).CaptureViewState()==left,"Swap did not carry both states.");
            });
            Check("maximize and restore both panes",()=>
            {
                form.SetScreenshotModesForSmoke(0,1);
                foreach(var index in new[]{0,1})
                {
                    form.MaximizeScreenshotPaneForSmoke(index);Pump();var state=form.ScreenshotWorkspaceStateForSmoke();
                    Require(state.MaximizedPane==index&&(index==0?state.LeftVisible&&!state.RightVisible:state.RightVisible&&!state.LeftVisible),"Maximize visibility differs.");
                    Snapshot("04-maximized-"+index);form.MaximizeScreenshotPaneForSmoke(index);Pump();Require(form.ScreenshotWorkspaceStateForSmoke().MaximizedPane==-1,"Maximized pane did not restore.");
                }
            });
            Check("split ratio and selection survive record changes",()=>
            {
                form.SetScreenshotModesForSmoke(2,3);form.SetScreenshotSplitForSmoke(62);Complete(form.SelectScreenshotRecordForSmoke(1));var state=form.ScreenshotWorkspaceStateForSmoke();
                Require(Math.Abs(state.SplitPercent-62)<.1&&state.LeftMode==2&&state.RightMode==3,"Record switch reset pane state.");
                Complete(form.SelectScreenshotRecordForSmoke(0));form.SetScreenshotSplitForSmoke(50);
            });
            Check("source, translation, empty search and dates filter persisted records",()=>
            {
                form.SetScreenshotSearchForSmoke("THE RIVERSIDE LIGHTHOUSE");Complete(form.RefreshScreenshotWorkspaceForSmoke());Require(form.ScreenshotWorkspaceStateForSmoke().RecordCount==1,"Source search mismatch.");
                form.SetScreenshotSearchForSmoke("白桦林营地");Complete(form.RefreshScreenshotWorkspaceForSmoke());Require(form.ScreenshotWorkspaceStateForSmoke().RecordCount==1,"Translation search mismatch.");
                form.SetScreenshotSearchForSmoke("NO-SUCH-PROTOTYPE");Complete(form.RefreshScreenshotWorkspaceForSmoke());Require(form.ScreenshotWorkspaceStateForSmoke().RecordCount==0,"No-match search retained records.");Snapshot("05-no-search-matches");
                form.SetScreenshotSearchForSmoke("",1);Complete(form.RefreshScreenshotWorkspaceForSmoke());Require(form.ScreenshotWorkspaceStateForSmoke().RecordCount==2,"Today filter mismatch.");
                form.SetScreenshotSearchForSmoke("",2);Complete(form.RefreshScreenshotWorkspaceForSmoke());Require(form.ScreenshotWorkspaceStateForSmoke().RecordCount==3,"Recent filter mismatch.");
                form.SetScreenshotSearchForSmoke("",0);Complete(form.RefreshScreenshotWorkspaceForSmoke());Require(form.ScreenshotWorkspaceStateForSmoke().RecordCount==5,"Clearing filter lost records.");
            });
            form.SetScreenshotSearchForSmoke("",0);Complete(form.RefreshScreenshotWorkspaceForSmoke());
            Check("untranslated and missing images never reuse stale content",()=>
            {
                form.SetScreenshotModesForSmoke(0,1);form.SetScreenshotSearchForSmoke("UNTRANSLATED-SOURCE");Complete(form.RefreshScreenshotWorkspaceForSmoke());
                Require(form.ScreenshotPaneForSmoke(0).CurrentBitmap is not null&&form.ScreenshotPaneForSmoke(1).CurrentBitmap is null,"Untranslated image reused stale content.");Snapshot("06-untranslated");
                var untranslatedDecodes=form.ScreenshotWorkspacePerformanceForSmoke().SelectionDecodes;Complete(form.RefreshScreenshotWorkspaceForSmoke());
                Require(form.ScreenshotWorkspacePerformanceForSmoke().SelectionDecodes==untranslatedDecodes,"An untranslated record is decoded again on an unchanged refresh.");
                form.SetScreenshotSearchForSmoke("MISSING-SOURCE");Complete(form.RefreshScreenshotWorkspaceForSmoke());
                Require(form.ScreenshotPaneForSmoke(0).CurrentBitmap is null&&form.ScreenshotPaneForSmoke(1).CurrentBitmap is null,"Missing images reused stale content.");
                var missingDecodes=form.ScreenshotWorkspacePerformanceForSmoke().SelectionDecodes;Complete(form.RefreshScreenshotWorkspaceForSmoke());
                Require(form.ScreenshotWorkspacePerformanceForSmoke().SelectionDecodes==missingDecodes,"A missing-image record is retried on an unchanged refresh.");
                Require(!Descendants(form.LegacyWindow).Single(x=>x.Name=="ScreenshotToolbarRetranslate").Enabled&&Descendants(form.LegacyWindow).Single(x=>x.Name=="ScreenshotToolbarEditRecord").Enabled,"Missing source image leaves unsafe retranslation enabled or prevents text editing.");
                form.SetScreenshotModesForSmoke(2,3);Require(form.ScreenshotPaneForSmoke(0).CurrentText.Contains("MISSING-SOURCE")&&form.ScreenshotPaneForSmoke(1).CurrentText.Contains("文字仍然保留"),"Missing images lost retained text.");Snapshot("07-retained-text");
            });
            form.SetScreenshotSearchForSmoke("",0);Complete(form.RefreshScreenshotWorkspaceForSmoke());Complete(form.SelectScreenshotRecordForSmoke(0));
            Check("night theme retains compact layout",()=>
            {form.SetThemeForSmoke(ApplicationThemeMode.Night);form.SetScreenshotModesForSmoke(0,1);Pump();Snapshot("08-night");form.SetThemeForSmoke(ApplicationThemeMode.Day);Pump();});
            Check("layout choices survive closing and reopening",()=>
            {
                form.SetScreenshotModesForSmoke(3,2);form.SetScreenshotSplitForSmoke(62);form.ScreenshotPaneForSmoke(0).FontSize=20;form.ScreenshotPaneForSmoke(1).FontSize=16;
                if(!form.ScreenshotWorkspaceStateForSmoke().HistoryVisible)form.ToggleScreenshotHistoryForSmoke();var selected=form.ScreenshotWorkspaceStateForSmoke().SelectedId;Pump(400);
                form.ExitForSmoke();form.Dispose();form=null;Pump();form=Open();var state=form.ScreenshotWorkspaceStateForSmoke();
                Require(state.LeftMode==3&&state.RightMode==2&&state.HistoryVisible&&Math.Abs(state.SplitPercent-62)<.1&&state.SelectedId==selected,"Saved layout was not restored.");
                Require(form.ScreenshotPaneForSmoke(0).FontSize==20&&form.ScreenshotPaneForSmoke(1).FontSize==16,"Saved font sizes were not restored.");Snapshot("09-restored-layout");
            });
            Check("independent image drag and wheel states survive swapping and closing the workspace",()=>
            {
                form.SetScreenshotModesForSmoke(0,1);var left=form.ScreenshotPaneForSmoke(0);var right=form.ScreenshotPaneForSmoke(1);
                left.FitWindow();right.FitWindow();Pump(50);var untouched=right.CaptureViewState();
                var canvas=Descendants(left).OfType<ScreenshotRebuildCanvas>().Single();
                ScreenshotImageInteractionSelfTests.Drag(canvas,new(200,180),new(243,207));
                ScreenshotImageInteractionSelfTests.Wheel(canvas,120,new(230,190));
                var moved=left.CaptureViewState();Require(moved.PanX!=0||moved.PanY!=0,"Workspace drag did not produce persisted pan.");
                Require(right.CaptureViewState()==untouched,"Left image input changed the right view state.");
                form.SwapScreenshotPanesForSmoke();Require(left.CaptureViewState()==untouched&&right.CaptureViewState()==moved,"Swapping panes lost pan or zoom state.");
                var selected=form.ScreenshotWorkspaceStateForSmoke().SelectedId;form.ExitForSmoke();form.Dispose();form=null;Pump();form=Open();
                Require(form.ScreenshotWorkspaceStateForSmoke().SelectedId==selected&&form.ScreenshotPaneForSmoke(0).CaptureViewState()==untouched&&form.ScreenshotPaneForSmoke(1).CaptureViewState()==moved,"Close/reopen lost the independent moved image states.");
            });
            Check("moving private data preserves a selected record saved with its old absolute path",()=>
            {
                var selected=form.ScreenshotWorkspaceStateForSmoke().SelectedId;var path=Path.Combine(AppDataPaths.Root,"screenshot-workspace.json");
                form.ExitForSmoke();form.Dispose();form=null;Pump();
                var saved=JsonNode.Parse(File.ReadAllText(path))!.AsObject();saved["SelectedRecord"]="X:\\old-private-build\\data\\History\\"+selected;
                File.WriteAllText(path,saved.ToJsonString(JsonOptions));form=Open();
                Require(form.ScreenshotWorkspaceStateForSmoke().SelectedId==selected,"Moving data lost the selection saved by the old installation.");
                form.SetScreenshotSplitForSmoke(63);form.ExitForSmoke();form.Dispose();form=null;Pump();
                var migrated=JsonNode.Parse(File.ReadAllText(path))!["SelectedRecord"]!.GetValue<string>();
                Require(migrated==selected&&!migrated.Contains(':')&&!migrated.Contains('\\')&&!migrated.Contains('/'),"New layout writes retain an old installation path.");form=Open();
            });
            Check("viewing and changing layout preserve all History bytes",()=>
            {var after=HistoryHashes();Require(historyBefore.Count==after.Count&&historyBefore.All(p=>after.TryGetValue(p.Key,out var hash)&&hash==p.Value),"Read-only viewing modified History.");});
        }
        catch(Exception ex){failures++;checks.Add(new{Name="Harness",Passed=false,Error=SafeDiagnosticOutput.ExceptionSummary(ex)});}
        finally
        {
            if(form is not null){try{form.ExitForSmoke();form.Dispose();Pump();}catch(Exception ex){failures++;checks.Add(new{Name="Dispose",Passed=false,Error=SafeDiagnosticOutput.ExceptionSummary(ex)});}}
            File.WriteAllText(Path.Combine(output,"RESULT.json"),JsonSerializer.Serialize(new
            {
                Passed=failures==0,Failures=failures,Checks=checks,DataRoot=AppDataPaths.Root,
                FixtureSource="User-provided fusion-prototype-analysis.zip static image assets; freshly seeded test History",
                RealApiRequests=0,OcrRequests=0,PersonalHistoryRead=false,PersonalSettingsRead=false,GlobalInputDisabled=AppDataPaths.DisableGlobalInput,
                FinishedUtc=DateTimeOffset.UtcNow,VisualAcceptance="Screenshots require visual review; passing interaction checks does not establish visual fidelity"
            },JsonOptions));
        }
        return failures==0?0:1;
    }

    private const string English="To the next traveler who reaches the station,\r\n\r\nIf you are reading this letter, the old bridge is still standing. Close the northern shutters before sunset and check the oil lamp beside the fireplace. At night, mist hides the entire mountain road. Do not descend alone on an unmarked path.\r\n\r\nThe observation logs are in the right-hand desk drawer. The blue notebook records the weather; the brown one marks nearby villages. Crossed-out dates are a warning from the former keeper: the bells sometimes ring an hour before dawn.\r\n\r\nAt sunrise, take a map and a spare lamp to the riverside tower. Beyond the third signpost, a fallen birch hides an iron box containing our final observation records.\r\n\r\nOBJECTIVES\r\nCheck the northern shutters. Retrieve the observation logs. Reach the riverside tower.\r\n\r\nTHE KEEPER\r\nBring the iron box back before deciding whether to continue north. May your journey be safe.";
    private const string Chinese="致下一位抵达观测站的旅行者：\r\n\r\n如果你正在阅读这封信，说明旧桥仍然完好。请在日落之前关上北侧的百叶窗，检查壁炉旁的煤油灯。山里的薄雾会在夜间遮住整条山路，不要独自沿着没有标记的小径下山。\r\n\r\n观测日志保存在书桌右侧的抽屉里。蓝色封面记录气温与风向，棕色封面标注附近村庄的位置。反复划掉的日期是前任守站人留下的提醒：这里的钟声，有时比黎明早一个小时。\r\n\r\n明天清晨，带上地图和备用灯，前往河岸边的石塔。第三处路标后有一棵倒下的白桦树，树干下的铁盒里保存着最后一次观测记录。\r\n\r\n任务目标\r\n检查北侧百叶窗；取回观测日志；前往河岸石塔。\r\n\r\n守站人\r\n把铁盒带回来，再决定是否继续向北。愿你一路平安。";
}
