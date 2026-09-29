using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

internal static class PreviewRenderCancellationSelfTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly JsonSerializerOptions Json = new()
    { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    internal static int Run(string output, string fixture)
    {
        Directory.CreateDirectory(output);
        var rows = new List<object>(); var failures = 0; var serial = 0;
        void Test(string name, Func<Rig, object> action)
        {
            try
            {
                using var rig = new Rig(fixture, Path.Combine(output, (++serial).ToString("00")));
                var evidence = action(rig);
                rows.Add(new { Name = name, Pass = true, Evidence = evidence });
            }
            catch (Exception ex)
            {
                failures++;
                rows.Add(new { Name = name, Pass = false, Error = SafeDiagnosticOutput.Compose("test failure", ex) });
            }
        }

        for (var iteration = 0; iteration < 3; iteration++)
        {
            var cancel = iteration > 0;
            Test(cancel ? $"Original NEW-018 real renderer 40 ms cancellation {iteration}" : "Original NEW-018 real renderer no-cancel control", rig =>
            {
                using var cts = new CancellationTokenSource();
                var watch = Stopwatch.StartNew();
                rig.Observe(); var task = rig.Start(cts.Token);
                if (cancel) cts.CancelAfter(40);
                var accepted = rig.Finish(task, cancel); watch.Stop();
                if (cancel)
                {
                    // The background worker can now stop before a final bitmap is
                    // allocated. Retain the exactly-once checks whenever one exists.
                    rig.AssertSentinel();
                    Require(rig.LastInput?.DisposeCount==1,"canceled source snapshot not released once");
                    if(rig.LastPending is not null)rig.AssertRejectedResources();
                }
                else
                {
                    Require(accepted, "normal render rejected");
                    Require(rig.Display.Size == rig.Source.Size, "normal render dimensions changed");
                    using var control = CorePipelineCorpusRunner.RenderProduct(rig.Source, rig.Core,
                        FontManager.ResolveTranslationImageProfile(new ApiSettings())).Bitmap;
                    Require(PixelHash(control) == PixelHash(rig.Display), "snapshot changed normal render pixels");
                    rig.Display.Save(Path.Combine(rig.Output, "FINAL.png"), ImageFormat.Png);
                    Require(IsDisposed(rig.Sentinel), "replaced display was not released");
                    Require(rig.LastInput?.DisposeCount == 1 && rig.LastPending?.DisposeCount == 0,
                        "normal ownership was not transferred exactly once");
                }
                return new { CancelRequested = cancel, CancellationObserved = cancel, Accepted = accepted,
                    SharedDisplayChanged = !ReferenceEquals(rig.Sentinel, rig.Display),
                    DisplayWidth = rig.Display.Width, DisplayHeight = rig.Display.Height,
                    ElapsedMs = watch.ElapsedMilliseconds, RealRenderer = true };
            });
        }

        Test("Cancellation before snapshot capture", rig =>
        {
            using var cts = new CancellationTokenSource(); cts.Cancel(); rig.Observe();
            rig.Finish(rig.Start(cts.Token), true); rig.AssertSentinel();
            Require(rig.LastInput is null && rig.LastPending is null, "pre-cancel allocated render resources");
            return new { Allocated = false };
        });
        foreach (var phase in new[] { "before-render", "inside-render", "after-render" })
        {
            var blockedPhase = phase;
            Test("Cancellation at controlled " + phase + " barrier", rig =>
            {
                using var cts = new CancellationTokenSource(); using var barrier = new RenderBarrier();
                rig.Observe(blockedPhase, barrier); var task = rig.Start(cts.Token);
                barrier.WaitEntered(); cts.Cancel(); barrier.Release(); rig.Finish(task, true);
                rig.AssertRejectedResources(); return new { Phase = blockedPhase, Rejected = true };
            });
        }
        Test("Cancellation after render on UI immediately before commit", rig =>
        {
            using var cts = new CancellationTokenSource();
            rig.Observe(beforeCommit: cts.Cancel); rig.Finish(rig.Start(cts.Token), true);
            rig.AssertRejectedResources(); return new { Rejected = true };
        });
        Test("Invalidation while completed result waits in UI queue", rig =>
        {
            using var barrier = new RenderBarrier(); rig.Observe("after-render", barrier);
            var task = rig.Start(); barrier.WaitEntered(); barrier.Release(); rig.Context.WaitQueued();
            rig.Form.InvalidateTranslationForSmoke("queued-test");
            Require(!rig.Finish(task), "queued obsolete result committed"); rig.AssertRejectedResources();
            return new { Rejected = true, QueueObserved = true };
        });
        Test("Queued invalidation and commit use the same UI serial order", rig =>
        {
            using var barrier = new RenderBarrier(); rig.Observe("after-render", barrier);
            var task = rig.Start(); barrier.WaitEntered();
            rig.Context.Post(_ => rig.Form.InvalidateTranslationForSmoke("queued-before-result"), null);
            barrier.Release(); Require(!rig.Finish(task), "earlier UI invalidation lost");
            rig.AssertRejectedResources(); return new { Rejected = true };
        });
        Test("A slow request cannot replace B newer request completing first", rig =>
        {
            using var barrier = new RenderBarrier(); rig.Observe("before-render", barrier);
            var source = rig.Source; var a = rig.Start(); barrier.WaitEntered();
            var aInput = rig.LastInput;
            rig.Form.InvalidateTranslationForSmoke("new-request-B"); Set(rig.Form, "_currentTranslationRequestId", "REQUEST-B");
            rig.Observe(color: Color.Blue); var b = rig.Start(); Require(rig.Finish(b), "B did not commit");
            var validB = rig.Display; barrier.Release(); Require(!rig.Finish(a), "A overwrote B");
            Require(ReferenceEquals(validB, rig.Display) && !IsDisposed(validB), "B was replaced or disposed");
            Require(ReferenceEquals(source, rig.Source) && aInput?.DisposeCount == 1, "source/ownership changed");
            Require(rig.Form.CurrentTranslationRequestIdForSmoke == "REQUEST-B", "final request identity changed");
            return new { FinalRequest = rig.Form.CurrentTranslationRequestIdForSmoke, SameSourceObject = true, BPreserved = true };
        });
        Test("Same source and request label still reject an older translation generation", rig =>
        {
            using var barrier = new RenderBarrier(); rig.Observe("before-render", barrier);
            var generation = rig.Form.TranslationGenerationForSmoke; var source = rig.Source;
            var task = rig.Start(); barrier.WaitEntered(); rig.Form.InvalidateTranslationForSmoke("same-source");
            Set(rig.Form, "_currentTranslationRequestId", "REQUEST-A"); barrier.Release();
            Require(!rig.Finish(task), "old generation committed"); rig.AssertRejectedResources();
            Require(ReferenceEquals(source, rig.Source), "test changed source identity");
            return new { BeforeGeneration = generation, AfterGeneration = rig.Form.TranslationGenerationForSmoke, SameSourceObject = true };
        });
        Test("Re-OCR generation and request invalidate the old render", rig =>
        {
            using var barrier = new RenderBarrier(); rig.Observe("before-render", barrier);
            var task = rig.Start(); barrier.WaitEntered();
            Set(rig.Form, "_ocrGeneration", rig.Form.OcrGenerationForSmoke + 1);
            Set(rig.Form, "_currentOcrRequestId", "REOCR-B"); barrier.Release();
            Require(!rig.Finish(task), "Re-OCR identity was ignored"); rig.AssertRejectedResources();
            return new { Rejected = true, Stage = "production commit identity guard; no OCR worker" };
        });
        Test("Editor Core/document replacement rejects old state even without cancellation", rig =>
        {
            using var barrier = new RenderBarrier(); rig.Observe("before-render", barrier);
            var task = rig.Start(); barrier.WaitEntered(); rig.ReplaceCore(); barrier.Release();
            Require(!rig.Finish(task), "replaced Core/document was ignored"); rig.AssertRejectedResources();
            return new { Rejected = true, Stage = "production commit object-identity guard; no editor dialog" };
        });
        Test("Close transition during rendering rejects its result", rig =>
        {
            using var barrier = new RenderBarrier(); rig.Observe("after-render", barrier);
            var task = rig.Start(); barrier.WaitEntered();
            Call(rig.Form, "OnFormClosing", new FormClosingEventArgs(CloseReason.UserClosing, false));
            barrier.Release(); Require(!rig.Finish(task), "closing Preview accepted a render"); rig.AssertRejectedResources();
            return new { Rejected = true, WindowShown = false, Stage = "real OnFormClosing plus commit guard" };
        });
        Test("Cancelled result leaves original/translated selection and Save/Copy source usable", rig =>
        {
            using var cts = new CancellationTokenSource(); rig.Observe(beforeCommit: cts.Cancel);
            rig.Finish(rig.Start(cts.Token), true); rig.AssertRejectedResources();
            foreach (var mode in new[] { ImageViewMode.Original, ImageViewMode.Translated })
            {
                rig.Form.SetPreviewSelectionForSmoke(OcrEngineKind.Rapid, mode, TextViewMode.Translation);
                using var copy = rig.Form.CloneVisibleImageForTest();
                Require(copy.Size == (mode == ImageViewMode.Original ? rig.Source.Size : rig.Sentinel.Size), "wrong export image");
                copy.Save(Path.Combine(rig.Output, mode + "-SAVE-COPY-SOURCE.png"), ImageFormat.Png);
            }
            return new { BothModesReadable = true, SavedPng = true, ClipboardTouched = false };
        });
        Test("Exception after local result creation releases only local resources", rig =>
        {
            rig.Observe(beforeCommit: () => throw new InvalidOperationException("SYNTHETIC_COMMIT_BOUNDARY_FAILURE"));
            RequireThrows<InvalidOperationException>(() => rig.Finish(rig.Start())); rig.AssertRejectedResources();
            return new { InputDisposedOnce = true, PendingDisposedOnce = true, CurrentImageUsable = true };
        });
        Test("Renderer exception releases independent source without touching current image", rig =>
        {
            rig.Observe(renderFailure: true); RequireThrows<InvalidOperationException>(() => rig.Finish(rig.Start()));
            rig.AssertSentinel(); Require(rig.LastInput?.DisposeCount == 1 && rig.LastPending is null, "exception ownership invalid");
            return new { InputDisposedOnce = true, NoReturnedBitmap = true };
        });
        Test("Worker failure after bitmap return releases its pending bitmap", rig =>
        {
            rig.Observe(afterRender: () => throw new InvalidOperationException("SYNTHETIC_POST_RENDER_FAILURE"));
            RequireThrows<InvalidOperationException>(() => rig.Finish(rig.Start())); rig.AssertRejectedResources();
            return new { InputDisposedOnce = true, PendingDisposedOnce = true };
        });
        Test("Worker snapshot does not observe mutations of live bitmap Core or polygons", rig =>
        {
            using var barrier = new RenderBarrier(); rig.Observe("before-render", barrier);
            var task = rig.Start(); barrier.WaitEntered(); var input = rig.LastInput!;
            var sourcePixel = input.Source.GetPixel(0, 0).ToArgb();
            var block = rig.Core.VisualBlocks[0]; var capturedText = input.Core.Translations[block.BlockId].TranslatedText;
            var capturedRole = input.Core.VisualBlocks[0].RoleHint; var polygon = input.Core.RawLines[0].Polygon[0];
            rig.Source.SetPixel(0, 0, Color.FromArgb(sourcePixel ^ 0x00FFFFFF));
            rig.Core.Translations[block.BlockId].TranslatedText = "SYNTHETIC_MUTATION"; block.RoleHint = "SYNTHETIC_ROLE";
            rig.Core.RawLines[0].Polygon[0] = new PointF(-321, -654);
            Require(input.Source.GetPixel(0, 0).ToArgb() == sourcePixel, "source bitmap shared");
            Require(input.Core.Translations[block.BlockId].TranslatedText == capturedText &&
                input.Core.VisualBlocks[0].RoleHint == capturedRole && input.Core.RawLines[0].Polygon[0] == polygon,
                "Core snapshot shared mutable state");
            rig.Form.InvalidateTranslationForSmoke("snapshot-test"); barrier.Release();
            Require(!rig.Finish(task), "invalidated snapshot committed"); rig.AssertRejectedResources();
            return new { BitmapIndependent = true, CoreValuesIndependent = true, PolygonIndependent = true };
        });
        Test("Synchronous appearance render supersedes outstanding render of the same request", rig =>
        {
            using var barrier = new RenderBarrier(); rig.Observe("before-render", barrier);
            var a = rig.Start(); barrier.WaitEntered();
            rig.Observe(color: Color.Green); Call(rig.Form, "BuildTranslatedDisplay"); var newer = rig.Display;
            barrier.Release(); Require(!rig.Finish(a), "older async render replaced synchronous appearance result");
            Require(ReferenceEquals(newer, rig.Display) && !IsDisposed(newer), "new appearance image lost");
            return new { SameRequest = true, NewerRenderPreserved = true };
        });
        Test("Non-owner thread cannot capture or commit shared Preview state", rig =>
        {
            var task = Task.Run(async () => await rig.Start());
            RequireThrows<InvalidOperationException>(() => task.GetAwaiter().GetResult()); rig.AssertSentinel();
            return new { WrongThreadRejected = true };
        });

        File.WriteAllText(Path.Combine(output, "PATH-CANCEL-SELFTESTS.json"), JsonSerializer.Serialize(new
        {
            Pass = rows.Count - failures, Fail = failures, RealApiCalls = 0, OcrWorkerStarted = false,
            WindowShown = false, ClipboardTouched = false, Fixture = fixture,
            Coverage = "Actual Preview private wrapper and shared production commit state machine on its owning STA thread; real renderer for original no-cancel + two 40 ms cases; barriers inject local renderer for controlled races.",
            VisibleGuiAcceptance = "MANUAL PENDING; no assertion of full desktop visual behavior", Rows = rows
        }, Json));
        return failures == 0 ? 0 : 1;
    }

    private static void Require(bool condition, string detail)
    { if (!condition) throw new InvalidOperationException(detail); }
    private static void RequireThrows<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static bool IsDisposed(Bitmap image)
    { try { image.GetPixel(0, 0); return false; } catch (ArgumentException) { return true; } }
    private static void Set(object target, string name, object? value) => typeof(PreviewForm).GetField(name, Private)!.SetValue(target, value);
    private static object? Get(object target, string name) => typeof(PreviewForm).GetField(name, Private)!.GetValue(target);
    private static object? Call(object target, string name, params object[] args)
    {
        try { return typeof(PreviewForm).GetMethod(name, Private)!.Invoke(target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static string PixelHash(Bitmap source)
    {
        using var image = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image)) graphics.DrawImageUnscaled(source, 0, 0);
        var bits = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var row = new byte[image.Width * 4];
            for (var y = 0; y < image.Height; y++)
            { Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, row.Length); hash.AppendData(row); }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { image.UnlockBits(bits); }
    }

    private sealed class RenderBarrier : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(false), _released = new(false);
        internal void Block() { _entered.Set(); Require(_released.Wait(TimeSpan.FromSeconds(30)), "barrier release timed out"); }
        internal void WaitEntered() => Require(_entered.Wait(TimeSpan.FromSeconds(30)), "renderer barrier not reached");
        internal void Release() => _released.Set();
        public void Dispose() => _released.Set();
    }

    private sealed class PumpContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));
        internal void WaitQueued()
        { var timer = Stopwatch.StartNew(); while (_queue.IsEmpty) { Require(timer.Elapsed < TimeSpan.FromSeconds(30), "UI continuation did not queue"); Thread.Sleep(1); } }
        internal void Finish(Task task)
        {
            var timer = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                Require(timer.Elapsed < TimeSpan.FromSeconds(60), "test task timed out");
                if (_queue.TryDequeue(out var work)) work.Callback(work.State); else Thread.Sleep(1);
            }
        }
    }

    private sealed class Rig : IDisposable
    {
        internal string Output { get; }
        internal PreviewForm Form { get; }
        internal PumpContext Context { get; } = new();
        internal Bitmap Sentinel { get; } = new(8, 8);
        internal Bitmap Source => (Bitmap)Get(Form, "_original")!;
        internal Bitmap Display => (Bitmap)Get(Form, "_translatedDisplay")!;
        internal CorePipelineDocument Core => (CorePipelineDocument)Get(Form, "_corePipelineV2")!;
        internal PreviewRenderInput? LastInput;
        internal PendingPreviewRender? LastPending;
        private readonly OcrEngineResult _ocr;
        private readonly SynchronizationContext? _previousContext;
        private readonly SessionServices _session;
        private readonly OcrRuntimeManager _runtime;
        private readonly VisionRuntimeManager _vision;
        internal Rig(string fixture, string output)
        {
            Output = output; Directory.CreateDirectory(output);
            _previousContext = SynchronizationContext.Current;
            using var source = new Bitmap(Path.Combine(fixture, "SOURCE.png"));
            _ocr = JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(Path.Combine(fixture, "PRODUCT-OCR-REPLAY.json")), Json)!;
            _session = new SessionServices(Path.Combine(output, "history"));
            _runtime = new OcrRuntimeManager(output, new OcrService()); _vision = new VisionRuntimeManager(output);
            Form = new PreviewForm(source, PreviewMode.OcrOnly, new ApiSettings { PreviewAlwaysOnTop = false },
                new OcrService(), new TranslationService(), _runtime, _vision, _session);
            Set(Form, "_suppressAutoOcrForE2E", true); Set(Form, "_allowUiImageDisplay", true);
            ReplaceCore();
            using var translations = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "TRANSLATED-TEXT.json")));
            foreach (var item in translations.RootElement.GetProperty("Items").EnumerateArray())
            {
                var state = Core.Translations[item.GetProperty("BlockId").GetString()!];
                state.TranslatedText = item.GetProperty("TranslatedText").GetString() ?? "";
                state.State = Enum.Parse<BlockTranslationState>(item.GetProperty("State").GetString()!);
            }
            Set(Form, "_translatedDisplay", Sentinel); Set(Form, "_currentTranslationRequestId", "REQUEST-A");
            _ = Form.Handle; // Hidden handle only: never Show/activate a user-visible window.
            SynchronizationContext.SetSynchronizationContext(Context);
        }
        internal void ReplaceCore()
        {
            var method = typeof(PreviewForm).GetMethod("BuildCorePipelineV2", Private, null, [typeof(OcrEngineResult), typeof(Size)], null)!;
            var core = (CorePipelineDocument)method.Invoke(null, [_ocr, Source.Size])!;
            Set(Form, "_corePipelineV2", core);
            Set(Form, "_document", typeof(PreviewForm).GetMethod("BuildCoreProductDocument", Private)!.Invoke(null, [core, _ocr]));
        }
        internal void Observe(string? blockedPhase = null, RenderBarrier? barrier = null, Color? color = null,
            Action? beforeCommit = null, bool renderFailure = false, Action? afterRender = null)
        {
            LastInput = null; LastPending = null;
            Form.RenderTestHooks = new PreviewRenderTestHooks
            {
                InputCaptured = input => LastInput = input, ResultCreated = pending => LastPending = pending,
                Phase = phase => { if (phase == blockedPhase) barrier!.Block(); if (phase == "before-commit") beforeCommit?.Invoke(); if (phase == "after-render") afterRender?.Invoke(); },
                Render = blockedPhase is not null || color is not null || beforeCommit is not null || renderFailure || afterRender is not null
                    ? input =>
                    {
                        if (blockedPhase == "inside-render") barrier!.Block();
                        if (renderFailure) throw new InvalidOperationException("SYNTHETIC_RENDER_FAILURE");
                        var bitmap = new Bitmap(input.Source.Width, input.Source.Height);
                        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color ?? Color.Red);
                        return new CorePipelineProductRender(bitmap, [], new(1, 2, 3, 4, 0, 10, false, 0, 0));
                    } : null
            };
        }
        internal Task<bool> Start(CancellationToken token = default) => (Task<bool>)Call(Form, "BuildTranslatedDisplayResponsiveAsync", token)!;
        internal bool Finish(Task<bool> task, bool cancelled = false)
        {
            Context.Finish(task);
            if (cancelled) { RequireThrows<OperationCanceledException>(() => task.GetAwaiter().GetResult()); return false; }
            return task.GetAwaiter().GetResult();
        }
        internal void AssertSentinel()
        { Require(ReferenceEquals(Sentinel, Display) && !IsDisposed(Sentinel), "current valid display changed or disposed"); }
        internal void AssertRejectedResources()
        {
            AssertSentinel(); Require(LastInput?.DisposeCount == 1, "source snapshot not released once");
            Require(LastPending?.DisposeCount == 1 && LastPending.OwnedBitmap is null, "rejected bitmap not released once");
        }
        public void Dispose()
        {
            Form.Dispose(); _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _vision.DisposeAsync().AsTask().GetAwaiter().GetResult(); _session.Dispose();
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }
    }
}
