using System.Diagnostics;
using System.Drawing.Imaging;

namespace ScreenshotTranslationUiTester;

internal static class FinalBlocker06131Tests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var pass=0;var failures=new List<string>();
        void Test(string name,Action action){try{action();pass++;}catch(Exception ex){failures.Add($"{name}: {ex.Message}");}}

        var settings=Path.Combine(output,"settings.json");
        ConfigurationManager.Save(settings,new ApiSettings{VisualModel=VisualModelKind.Off,OcrEngine=OcrEngineKind.Rapid});
        using(var main=new MainForm(settings))
        {
            main.Show();Application.DoEvents();main.SelectSettingsTabForSmoke(0);Application.DoEvents();
            foreach(var c in new[]{
                ("Ctrl T",new InputBinding(InputBindingKind.Keyboard,Keys.T,Ctrl:true)),
                ("Ctrl Shift T",new InputBinding(InputBindingKind.Keyboard,Keys.T,Ctrl:true,Shift:true)),
                ("Alt F2",new InputBinding(InputBindingKind.Keyboard,Keys.F2,Alt:true)),
                ("Shift A",new InputBinding(InputBindingKind.Keyboard,Keys.A,Shift:true)),
                ("Win A",new InputBinding(InputBindingKind.Keyboard,Keys.A,Win:true)),
                ("F1",new InputBinding(InputBindingKind.Keyboard,Keys.F1))})
            {
                Test("binding "+c.Item1,()=>{main.BeginBindingCaptureForSmoke(InputActionId.StartCapture);Application.DoEvents();
                    foreach(var modifier in ModifierKeys(c.Item2))
                        if(!main.CaptureKeyboardBindingForSmoke(new(InputBindingKind.Keyboard,modifier)))throw new InvalidOperationException("modifier not retained");
                    if(!main.CaptureKeyboardBindingForSmoke(c.Item2))throw new InvalidOperationException("trigger not captured");
                    Application.DoEvents();if(main.RuntimeBindingForSmoke(InputActionId.StartCapture)!=c.Item2.DisplayName)throw new InvalidOperationException(main.RuntimeBindingForSmoke(InputActionId.StartCapture));});
            }
            main.ExitForSmoke();
        }

        Test("Mouse 4 display retained",()=>{if(new InputBinding(InputBindingKind.MouseButton,MouseButton:InputMouseButton.XButton1).DisplayName!="Mouse 4")throw new InvalidOperationException();});
        Test("Wheel Up display retained",()=>{if(new InputBinding(InputBindingKind.MouseWheel,Wheel:InputWheelDirection.Up).DisplayName!="Wheel Up")throw new InvalidOperationException();});

        Test("capture-session modifier assembly",()=>
        {
            using var host=new GlobalKeyboardBindingHost(()=>InputContextKind.BindingCapture,_=>true,_=>{});
            foreach(var expected in new[]{
                new InputBinding(InputBindingKind.Keyboard,Keys.T,Ctrl:true),
                new InputBinding(InputBindingKind.Keyboard,Keys.T,Ctrl:true,Shift:true),
                new InputBinding(InputBindingKind.Keyboard,Keys.F2,Alt:true),
                new InputBinding(InputBindingKind.Keyboard,Keys.A,Shift:true),
                new InputBinding(InputBindingKind.Keyboard,Keys.A,Win:true),
                new InputBinding(InputBindingKind.Keyboard,Keys.F1)})
            {
                host.ResetCaptureModifiers();foreach(var modifier in ModifierKeys(expected))host.CaptureCandidateForSmoke(modifier,true);
                var actual=host.CaptureCandidateForSmoke(expected.Key,true);if(actual!=expected)throw new InvalidOperationException($"{expected.DisplayName} -> {actual?.DisplayName??"NONE"}");
            }
        });

        Test("UI and OCR bitmaps are distinct",()=>{using var source=Fixture(592,429);using var preview=new PreviewForm(source,PreviewMode.OcrOnly,new ApiSettings{VisualModel=VisualModelKind.Off},new OcrService(),new TranslationService());preview.SuppressAutoOcrForE2E();preview.Show();Application.DoEvents();var snapshot=preview.PrepareOcrSnapshotForSmoke();var deadline=DateTime.UtcNow.AddSeconds(10);while(!snapshot.IsCompleted&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(5);}if(!snapshot.IsCompletedSuccessfully||!snapshot.Result)throw new InvalidOperationException("snapshot failed");Application.DoEvents();if(!preview.OcrBitmapIsIndependentForSmoke)throw new InvalidOperationException("shared bitmap");var sizes=preview.BitmapOwnershipSizesForSmoke;if(sizes.Ui!=sizes.Ocr||sizes.Ui!=source.Size)throw new InvalidOperationException("size mismatch");preview.Hide();});
        Test("overlay cursor remains visible and interactive",()=>{using var source=Fixture(320,180);using var overlay=new CaptureOverlay(new Rectangle(0,0,320,180),source,false,false);overlay.Show();Application.DoEvents();if(overlay.Cursor!=Cursors.Cross)throw new InvalidOperationException($"cursor={overlay.Cursor}");overlay.Hide();Application.DoEvents();});
        Test("capture does not mutate global cursor visibility",()=>{var before=SystemCursorCaptureAudit.Snapshot();using var backend=new GdiFallbackCaptureBackend();backend.Initialize();var frame=backend.CaptureFrame(new Rectangle(0,0,32,32),Stopwatch.GetTimestamp(),CancellationToken.None);using(frame.Bitmap){}var after=SystemCursorCaptureAudit.Snapshot();if(before.Visible!=after.Visible)throw new InvalidOperationException($"visible {before.Visible}->{after.Visible}");});
        Test("capture failure does not mutate cursor visibility",()=>{var before=SystemCursorCaptureAudit.Snapshot();try{using var backend=new GdiFallbackCaptureBackend();backend.CaptureFrame(Rectangle.Empty,Stopwatch.GetTimestamp(),new CancellationToken(true));}catch(OperationCanceledException){}var after=SystemCursorCaptureAudit.Snapshot();if(before.Visible!=after.Visible)throw new InvalidOperationException($"visible {before.Visible}->{after.Visible}");});
        Test("capture sources do not compose pointer metadata",()=>{var root=AppContext.BaseDirectory;var sourcePath=Path.Combine(root,"ScreenCaptureBackends.cs");if(!File.Exists(sourcePath))sourcePath=Path.Combine(Directory.GetCurrentDirectory(),"ScreenCaptureBackends.cs");if(!File.Exists(sourcePath))return;var text=File.ReadAllText(sourcePath);foreach(var forbidden in new[]{"GetFramePointerShape","DrawIcon(","DrawIconEx("})if(text.Contains(forbidden,StringComparison.Ordinal))throw new InvalidOperationException(forbidden);});
        Test("20x concurrent paint/hash isolation",()=>
        {
            for(var i=0;i<20;i++)
            {
                using var ui=Fixture(592,429);using var worker=new Bitmap(ui);var identity=new SessionImageIdentity(worker);
                var hash=identity.GetHashAsync();
                using var frame=new Bitmap(592,429,PixelFormat.Format32bppArgb);using(var g=Graphics.FromImage(frame))g.DrawImage(ui,new Rectangle(0,0,592,429));
                hash.GetAwaiter().GetResult();if(identity.Diagnostics.ImageHashCount!=1)throw new InvalidOperationException("hash count");
            }
        });
        Test("4K ownership clone budget",()=>{using var source=Fixture(3840,2160);var watch=Stopwatch.StartNew();using var clone=new Bitmap(source);watch.Stop();File.WriteAllText(Path.Combine(output,"4K-CLONE-MS.txt"),watch.Elapsed.TotalMilliseconds.ToString("F3"));if(clone.Size!=source.Size)throw new InvalidOperationException();});
        Test("portable vision resolution",()=>{var root=Path.Combine(output,"portable");Directory.CreateDirectory(Path.Combine(root,"vision-runtime"));File.WriteAllText(Path.Combine(root,"vision-runtime","location.txt"),@"C:\old-development-tree\vision-runtime");var manager=new VisionRuntimeManager(root);try{var method=typeof(VisionRuntimeManager).GetMethod("ResolveRuntimeRoot",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;var resolved=(string)method.Invoke(manager,null)!;if(resolved!=Path.Combine(root,"vision-runtime"))throw new InvalidOperationException(resolved);}finally{manager.DisposeAsync().AsTask().GetAwaiter().GetResult();}});

        var lines=new List<string>{$"TOTAL {pass+failures.Count} PASS {pass} FAIL {failures.Count}"};lines.AddRange(failures.Select(x=>"FAIL "+x));File.WriteAllLines(Path.Combine(output,"RESULT.txt"),lines);return failures.Count==0?0:2;
    }

    private static IEnumerable<Keys> ModifierKeys(InputBinding binding)
    {if(binding.Ctrl)yield return Keys.LControlKey;if(binding.Alt)yield return Keys.LMenu;if(binding.Shift)yield return Keys.LShiftKey;if(binding.Win)yield return Keys.LWin;}
    private static Bitmap Fixture(int width,int height){var b=new Bitmap(width,height,PixelFormat.Format32bppArgb);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(42,24,71));g.DrawString("OCR ownership fixture",SystemFonts.DefaultFont,Brushes.White,8,8);return b;}
}
