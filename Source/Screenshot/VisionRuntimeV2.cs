using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed record VisionRuntimeStatus(bool Ready, string Detail, int? WorkerPid);

public sealed class VisionRuntimeManager : IAsyncDisposable
{
    private readonly string _applicationRoot;
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private Task? _stderrDrain;
    private VisualModelKind? _activeModel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public int? ActiveWorkerPid => _process is { HasExited: false } ? _process.Id : null;
    public int WorkerStartCount { get; private set; }
    public int ModelLoadCount { get; private set; }
    public int RequestCount { get; private set; }
    public int WorkerRestartCount { get; private set; }
    public long LastRequestDuration { get; private set; }
    public long WorkingSetBytes { get { try{return _process is {HasExited:false}?_process.WorkingSet64:0;}catch{return 0;} } }
    private bool _hasStarted;

    public VisionRuntimeManager(string applicationRoot) => _applicationRoot = applicationRoot;

    public async Task<VisionRuntimeStatus> CheckAsync(CancellationToken token)
    {
        try
        {
            await _gate.WaitAsync(token);
            try { EnsureStarted(null); await SendAsync(new { command = "check" }, token); var line = await ReadAsync(token); return new(true, line, ActiveWorkerPid); }
            finally { _gate.Release(); }
        }
        catch (Exception ex) { return new(false, ex.Message, ActiveWorkerPid); }
    }

    public async Task<VisualAnalysisResult> AnalyzeAsync(Bitmap image, VisualModelKind kind, long generation, CancellationToken token)
    {
        if (kind == VisualModelKind.Off) return new([], [], [], [], new("Off", 0, "Disabled"));
        var clientWatch = Stopwatch.StartNew();
        var path = Path.Combine(AppDataPaths.TempRoot, $"vision-v2-{Guid.NewGuid():N}.png");
        image.Save(path, ImageFormat.Png);
        try
        {
            var queueWatch = Stopwatch.StartNew();
            await _gate.WaitAsync(token);
            queueWatch.Stop();
            try
            {
                // Paddle and PyTorch ship different cuDNN builds on Windows.
                // A model switch gets a fresh worker so DLL state can never
                // leak across backend families.
                if (_activeModel.HasValue && _activeModel.Value != kind) await StopWorkerAsync();
                var startupWatch = Stopwatch.StartNew(); EnsureStarted(kind); startupWatch.Stop();
                await SendAsync(new { command = "analyze", model = kind.ToString(), imagePath = path, generation }, token);
                var json = await ReadAsync(token);
                var wire = JsonSerializer.Deserialize<WireResult>(json, JsonOptions) ?? throw new InvalidDataException("Vision Worker returned empty JSON.");
                if (!string.Equals(wire.status, "OK", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(wire.error);
                RequestCount++;if(wire.loadMs>0)ModelLoadCount++;LastRequestDuration=wire.totalMs;
                var regions = wire.regions.Select(x => new VisualRegion { VisualRegionId = x.id,
                    Polygon = x.polygon.Select(p => new PointF(p[0], p[1])).ToArray(), VisualRoleHint = ParseRole(x.role),
                    Confidence = x.confidence, ReadingOrderHint = x.readingOrder, ParentContainerHint = x.parentId,
                    SourceModel = x.sourceModel, OptionalTextHint = x.optionalTextHint }).ToArray();
                _activeModel = kind;
                clientWatch.Stop();
                return new(regions,
                    regions.Select(x => new RoleHint(x.VisualRegionId, x.VisualRoleHint, x.Confidence)).ToArray(),
                    regions.Where(x => x.ReadingOrderHint.HasValue).Select(x => new ReadingOrderHint(x.VisualRegionId, x.ReadingOrderHint!.Value, x.Confidence)).ToArray(),
                    wire.layoutRelations.Select(x => new LayoutRelation(x.parentId, x.childId, x.relation, x.confidence)).ToArray(),
                    new(wire.model, clientWatch.ElapsedMilliseconds, wire.status,
                        $"worker={wire.totalMs}ms queue={queueWatch.ElapsedMilliseconds}ms startup={startupWatch.ElapsedMilliseconds}ms load={wire.loadMs}ms preprocess={wire.preprocessMs}ms inference={wire.inferenceMs}ms postprocess={wire.postprocessMs}ms memory={wire.workingSetBytes} pid={ActiveWorkerPid}"));
            }
            catch(OperationCanceledException){await StopWorkerAsync();throw;}
            catch(EndOfStreamException){await StopWorkerAsync();throw;}
            catch(IOException){await StopWorkerAsync();throw;}
            finally { _gate.Release(); }
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private void EnsureStarted(VisualModelKind? kind)
    {
        if (_process is { HasExited: false }) return;
        if(_process is not null){try{_input?.Dispose();_output?.Dispose();_process.Dispose();}catch{} _input=null;_output=null;_process=null;if(_hasStarted)WorkerRestartCount++;}
        var runtime = ResolveRuntimeRoot();
        var environment = kind == VisualModelKind.Florence2BaseFt ? "florence-venv" : "venv";
        var python = Path.Combine(runtime, "python", "python.exe");
        var script = Path.Combine(_applicationRoot, "workers", "vision_worker.py");
        if (!File.Exists(python)) throw new VisionRuntimeMissingException("V2 视觉识别运行环境缺失。", "V2 Vision Python runtime is missing.", python);
        if (!File.Exists(script)) throw new VisionRuntimeMissingException("视觉识别 Worker 文件缺失。", "Vision worker is missing.", script);
        var info = new ProcessStartInfo(python, $"-u \"{script}\"") { WorkingDirectory = runtime,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        AppDataPaths.ConfigureChildEnvironment(info);
        info.Environment["PADDLE_PDX_CACHE_HOME"] = Path.Combine(runtime, "paddlex");
        info.Environment["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True";
        info.Environment["HF_HOME"] = Path.Combine(runtime, "huggingface");
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        info.Environment["PADDLE_V2_DISABLE_MODELSCOPE"] = "1";
        info.Environment["PYTHONPATH"] = Path.Combine(runtime, environment, "Lib", "site-packages");
        info.Environment["PYTHONNOUSERSITE"] = "1";
        info.Environment["PYTHONUTF8"] = "1";
        // NVIDIA wheels keep their native DLLs below site-packages; make the
        // bundled locations visible without relying on a machine CUDA PATH.
        var nvidia = Path.Combine(runtime, environment, "Lib", "site-packages", "nvidia");
        var nativeBins = new[] { "cudnn", "cu13", "cublas", "cuda_runtime", "cufft", "curand", "cusolver", "cusparse", "nvjitlink" }
            .SelectMany(name => new[] { Path.Combine(nvidia, name, "bin"), Path.Combine(nvidia, name, "bin", "x86_64") }).Where(Directory.Exists);
        info.Environment["PATH"] = string.Join(Path.PathSeparator, nativeBins.Append(info.Environment["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? ""));
        _process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start Vision worker.");
        WorkerStartCount++;_hasStarted=true;
        _input = _process.StandardInput; _output = _process.StandardOutput;
        var logRoot=AppDataPaths.RuntimeLogs(runtime, "vision");
        Directory.CreateDirectory(logRoot);
        var log = Path.Combine(logRoot, "vision-worker.log");
        _stderrDrain = Task.Run(async () => { while (await _process.StandardError.ReadLineAsync() is { } line) await File.AppendAllTextAsync(log, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}"); });
    }

    private string ResolveRuntimeRoot()
    {
        // Release candidates are portable: never follow a stale pointer or scan
        // ancestor/development output trees. A missing bundled runtime is an
        // explicit package error at the normal EnsureStarted file check.
        return Path.Combine(_applicationRoot, "vision-runtime");
    }
    private async Task SendAsync(object value, CancellationToken token) { await _input!.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token); await _input.FlushAsync(token); }
    private async Task<string> ReadAsync(CancellationToken token) => await _output!.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromMinutes(5), token) ?? throw new EndOfStreamException("Vision worker exited.");
    private static RegionRoleType ParseRole(string value)
    {
        var normalized = value.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim();
        if (normalized.Equals("figure", StringComparison.OrdinalIgnoreCase) || normalized.Equals("photo", StringComparison.OrdinalIgnoreCase) || normalized.Equals("picture", StringComparison.OrdinalIgnoreCase)) return RegionRoleType.Image;
        if (normalized.Equals("illustration", StringComparison.OrdinalIgnoreCase) || normalized.Equals("artwork", StringComparison.OrdinalIgnoreCase)) return RegionRoleType.Illustration;
        return Enum.TryParse<RegionRoleType>(value, true, out var role) ? role : RegionRoleType.Unknown;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { await StopWorkerAsync(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    private async Task StopWorkerAsync()
    {
        if (_process is { HasExited: false })
        {
            try { await SendAsync(new { command = "shutdown" }, CancellationToken.None); } catch { }
            if (!_process.WaitForExit(1500)) _process.Kill(true);
        }
        _input?.Dispose(); _output?.Dispose(); _process?.Dispose();
        _process = null; _input = null; _output = null; _activeModel = null;
        if (_stderrDrain is not null) try { await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        _stderrDrain = null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed class WireResult { public string status { get; set; } = ""; public string error { get; set; } = ""; public string model { get; set; } = ""; public long totalMs { get; set; } public long loadMs { get; set; } public long preprocessMs { get; set; } public long inferenceMs { get; set; } public long postprocessMs { get; set; } public long workingSetBytes { get; set; } public List<WireRegion> regions { get; set; } = []; public List<WireRelation> layoutRelations { get; set; } = []; }
    private sealed class WireRegion { public string id { get; set; } = ""; public List<float[]> polygon { get; set; } = []; public string role { get; set; } = "Unknown"; public float confidence { get; set; } public int? readingOrder { get; set; } public string? parentId { get; set; } public string sourceModel { get; set; } = ""; public string? optionalTextHint { get; set; } }
    private sealed class WireRelation { public string parentId { get; set; } = ""; public string childId { get; set; } = ""; public string relation { get; set; } = ""; public float confidence { get; set; } }
}

public sealed class RecognitionPipelineV2(OcrRuntimeManager ocr, VisionRuntimeManager vision)
{
    public async Task<(RecognitionDocumentV2 Document, VisualAnalysisResult Visual, OcrEngineResult Ocr)> RunAsync(
        Bitmap source, ApiSettings settings, long generation, CancellationToken token)
    {
        var totalWatch = Stopwatch.StartNew();
        // Vision and OCR encode/process concurrently. GDI+ Bitmap is not safe for
        // concurrent Image.Save/LockBits calls, even when both consumers read.
        // Give each parallel branch one private snapshot; the session source is
        // reused only after both branches have completed.
        using var visionSource = new Bitmap(source);
        using var ocrSource = new Bitmap(source);
        var visionTask = vision.AnalyzeAsync(visionSource, settings.VisualModel, generation, token);
        var ocrTask = ocr.RecognizeWithFallbackAsync(settings.OcrEngine, ocrSource, settings.OcrLanguage, token,
            imageSessionId: Guid.NewGuid().ToString("N"), allowAutomaticWindowsFallback: false);
        await Task.WhenAll(visionTask, ocrTask);
        var visualResult = await visionTask; var ocrResult = await ocrTask;
        var visualRegions = visualResult.VisualRegions.ToList(); var blocks = ocrResult.Blocks.ToList();
        RealPathDiagnosticTrace.Stage("POST_OCR",blocks.Select(RealPathDiagnosticTrace.OcrRow));
        var cropOcrCount = 0; var cropOcrWatch = Stopwatch.StartNew();
        foreach (var vr in visualRegions.Where(v => Coverage(v.BoundingBox, blocks) < .15f && v.VisualRoleHint != RegionRoleType.Unknown))
        {
            token.ThrowIfCancellationRequested(); var cropBox = Rectangle.Round(RectangleF.Intersect(vr.BoundingBox, new RectangleF(PointF.Empty, source.Size)));
            if (cropBox.Width < 8 || cropBox.Height < 8) continue;
            using var crop = source.Clone(cropBox, source.PixelFormat);
            try
            {
                cropOcrCount++;
                var retry = await ocr.RecognizeWithFallbackAsync(settings.OcrEngine, crop, settings.OcrLanguage, token,
                    allowAutomaticWindowsFallback: false);
                foreach (var b in retry.Blocks) { b.Id = $"CROP-{vr.VisualRegionId}-{b.Id}"; b.BoundingBox = new(b.BoundingBox.X + cropBox.X, b.BoundingBox.Y + cropBox.Y, b.BoundingBox.Width, b.BoundingBox.Height); b.Polygon = b.Polygon.Select(p => new PointF(p.X + cropBox.X, p.Y + cropBox.Y)).ToArray(); blocks.Add(b); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLog.Write("vision", $"Crop OCR failed region={vr.VisualRegionId}", ex); }
        }
        cropOcrWatch.Stop();
        var cvWatch = Stopwatch.StartNew(); var cv = CoarseRegionProposer.Propose(source); cvWatch.Stop();
        var fusionWatch = Stopwatch.StartNew();
        var regions = RegionProposalFusion.Fuse(cv, visualRegions, blocks, generation);
        RealPathDiagnosticTrace.Stage("POST_REGION_FUSION",regions.Select(RealPathDiagnosticTrace.RegionRow));
        fusionWatch.Stop();
        // Only high-certainty structural boundaries are assigned before grouping.
        // The full role classifier depends on paragraph context and must run after
        // grouping; running it line-by-line fragments normal prose.
        for (var i = 0; i < regions.Count; i++)
        {
            var classified = ClassifyGroupingBoundary(regions[i]);
            if ((regions[i].ManualOverrideFlags & RegionManualOverrideFlags.Role) == 0 && classified.HasValue)
            { regions[i].RoleType = classified.Value.Role; regions[i].RoleConfidence = classified.Value.Confidence; }
            regions[i].SourceSegments = regions[i].SourceSegments.Select(x => x with
            { Role = regions[i].RoleType, RoleConfidence = regions[i].RoleConfidence, OriginRegionId = regions[i].RegionId }).ToList();
        }
        RecoverContinuationRoles(regions);
        var sourceArea = Math.Max(1, source.Width * source.Height);
        var exclusions = visualRegions.Where(x => x.VisualRoleHint is RegionRoleType.Image or RegionRoleType.Illustration ||
                x.VisualRoleHint == RegionRoleType.Unknown && string.IsNullOrWhiteSpace(x.OptionalTextHint) &&
                x.BoundingBox.Width * x.BoundingBox.Height / sourceArea <= .65f && Coverage(x.BoundingBox, blocks) < .15f)
            .Select(x => x.Polygon.ToArray()).Where(x => x.Length >= 3).ToList();
        foreach (var region in regions) region.LayoutExclusionPolygons = exclusions.Select(x => x.ToArray()).ToList();
        if (string.Equals(Environment.GetEnvironmentVariable("SCREENSHOT_TRANSLATOR_PHASE1_TRACE"), "1", StringComparison.Ordinal))
            foreach (var region in regions)
                Console.WriteLine($"PHASE1-PREGROUP role={region.RoleType} blocks={string.Join(',', region.SourceBlockIds)} text={region.StructuredText}");
        var groupingWatch = Stopwatch.StartNew();
        regions = SemanticRegionGrouperV2.Group(regions, generation);
        RealPathDiagnosticTrace.Stage("POST_SEMANTIC_GROUPING",regions.Select(RealPathDiagnosticTrace.RegionRow));
        groupingWatch.Stop(); var roleWatch = Stopwatch.StartNew();
        for (var i = 0; i < regions.Count; i++)
        {
            var classified = RegionRoleClassifierV2.Classify(regions[i], regions);
            if ((regions[i].ManualOverrideFlags & RegionManualOverrideFlags.Role) == 0 &&
                (regions[i].RoleType == RegionRoleType.Unknown || classified.Confidence > regions[i].RoleConfidence))
            { regions[i].RoleType = classified.Role; regions[i].RoleConfidence = classified.Confidence; }
            var resolvedRole = regions[i].RoleType; var resolvedConfidence = regions[i].RoleConfidence;
            regions[i].SourceSegments = regions[i].SourceSegments.Select(x => x.Role is RegionRoleType.Unknown or RegionRoleType.Automatic
                ? x with { Role = resolvedRole, RoleConfidence = resolvedConfidence, OriginRegionId = regions[i].RegionId }
                : x with { OriginRegionId = regions[i].RegionId }).ToList();
            regions[i].ReadingOrder = i + 1; regions[i].GroupId = $"GRP{i + 1:000}";
        }
        roleWatch.Stop();
        regions = SemanticFieldNormalizerV2.Normalize(regions, generation);
        for(var i=0;i<regions.Count;i++){regions[i].ReadingOrder=i+1;regions[i].GroupId=$"GRP{i+1:000}";}
        var document = new RecognitionDocumentV2 { VisionGeneration = generation, OcrGeneration = generation, RegionGeneration = generation, TranslationGeneration = generation };
        var coverageResult = RecognitionCoverageFinalizer.Finalize(regions, blocks, source.Size, generation);
        document.Regions.AddRange(regions); document.SourceLineCoverage.AddRange(coverageResult.Coverage);
        var unitWatch=Stopwatch.StartNew(); document.TranslationUnits.AddRange(TranslationUnitBuilderV2.Build(regions)); unitWatch.Stop();
        RealPathDiagnosticTrace.Stage("POST_TRANSLATION_UNIT_BUILD",document.TranslationUnits.Select(RealPathDiagnosticTrace.UnitRow));
        document.CoverageSummary = RecognitionCoverageFinalizer.BindTranslationUnits(document);
        totalWatch.Stop();
        document.Diagnostics = new(visualResult.ModelDiagnostics.ElapsedMs, ocrResult.TotalMilliseconds,
            fusionWatch.ElapsedMilliseconds, groupingWatch.ElapsedMilliseconds, roleWatch.ElapsedMilliseconds, totalWatch.ElapsedMilliseconds,
            visualResult.ModelDiagnostics.Model, ocrResult.EngineActual, visualRegions.Count, blocks.Count,
            regions.Count, document.TranslationUnits.Count, cropOcrCount, cropOcrWatch.ElapsedMilliseconds,
            cvWatch.ElapsedMilliseconds, unitWatch.ElapsedMilliseconds, regions.Count,
            coverageResult.ElapsedMs, document.CoverageSummary.Unaccounted, document.CoverageSummary.MissingEraseGeometry);
        return (document, visualResult, ocrResult);
    }
    private static (RegionRoleType Role, float Confidence)? ClassifyGroupingBoundary(RecognitionRegion region)
    {
        if (region.RoleType is RegionRoleType.Image or RegionRoleType.Illustration or RegionRoleType.Button or
            RegionRoleType.Choice or RegionRoleType.Metadata or RegionRoleType.UILabel)
            return (region.RoleType, region.RoleConfidence);

        var text = region.StructuredText.Trim();
        if (text.Length == 0) return null;
        var quoteCount = text.Count(x => x is '"' or '\u201c' or '\u201d');
        if ((text.StartsWith('"') || text.StartsWith('\u201c')) && quoteCount % 2 == 1)
            return (RegionRoleType.Dialogue, .86f);
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^(?:SYNOPSIS|DESCRIPTION|SUMMARY|ABOUT|\u5267\u60c5\u7b80\u4ecb)\s*:?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return (RegionRoleType.Header, .95f);
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^(?:continue|next|ok|cancel|\u7ee7\u7eed|\u786e\u5b9a|\u53d6\u6d88)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return (RegionRoleType.Button, .95f);
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^(?:P(?:A|R)GE\s*\d+|\d+[\u4e07\u842cKMB]?\s*(?:views?|likes?|comments?|\u6761)?|\d+\s*(?:minutes?|hours?|days?)\s*ago|\d+\s*(?:\u5206\u949f|\u5c0f\u65f6|\u5929)\u524d)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return (RegionRoleType.Metadata, .94f);
        return null;
    }
    private static void RecoverContinuationRoles(List<RecognitionRegion> regions)
    {
        var ordered = regions.Where(x => x.SourceBlockIds.Count > 0).OrderBy(x => x.BoundingBox.Top).ThenBy(x => x.BoundingBox.Left).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].RoleType != RegionRoleType.Dialogue) continue;
            var openingText = ordered[i].StructuredText.TrimStart();
            if (!openingText.StartsWith('"') && !openingText.StartsWith('\u201c')) continue;
            var quoteCount = ordered[i].StructuredText.Count(x => x is '"' or '\u201c' or '\u201d');
            var previous = ordered[i];
            var searchFrom = i + 1;
            while (searchFrom < ordered.Count && quoteCount % 2 == 1)
            {
                var a = previous.BoundingBox;
                var next = ordered.Skip(searchFrom).Select((candidate, offset) => (candidate, index: searchFrom + offset))
                    .Where(x => x.candidate.BoundingBox.Top >= a.Top - a.Height * .25f)
                    .Where(x => x.candidate.BoundingBox.Top - a.Bottom <= Math.Max(a.Height, x.candidate.BoundingBox.Height) * .65f)
                    .Select(x =>
                    {
                        var b = x.candidate.BoundingBox;
                        var overlap = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
                        return (x.candidate, x.index, overlapRatio: overlap / Math.Max(1, Math.Min(a.Width, b.Width)));
                    }).Where(x => x.overlapRatio >= .55f).OrderBy(x => x.candidate.BoundingBox.Top).FirstOrDefault();
                if (next.candidate is null) break;
                var candidate = next.candidate;
                if (candidate.RoleType is RegionRoleType.BodyParagraph or RegionRoleType.Narration or RegionRoleType.Unknown)
                {
                    candidate.RoleType = RegionRoleType.Dialogue; candidate.RoleConfidence = Math.Max(candidate.RoleConfidence, .8f);
                    candidate.SourceSegments = candidate.SourceSegments.Select(x => x with { Role = RegionRoleType.Dialogue, RoleConfidence = candidate.RoleConfidence }).ToList();
                    quoteCount += candidate.StructuredText.Count(x => x is '"' or '\u201c' or '\u201d'); previous = candidate; searchFrom = next.index + 1;
                    if (candidate.StructuredText.TrimEnd().EndsWith('"') || candidate.StructuredText.TrimEnd().EndsWith('\u201d')) break;
                }
                else break;
            }
        }
    }
    private static float Coverage(RectangleF box, IReadOnlyList<OcrEngineBlock> blocks) => box.Width <= 0 || box.Height <= 0 ? 0 : Math.Min(1, blocks.Select(x => RectangleF.Intersect(box, x.BoundingBox)).Where(x => x.Width > 0 && x.Height > 0).Sum(x => x.Width * x.Height) / (box.Width * box.Height));
}

public static class CoarseRegionProposer
{
    public static IReadOnlyList<RectangleF> Propose(Bitmap source)
    {
        var scale = Math.Min(1f, 1000f / Math.Max(source.Width, source.Height)); using var work = scale < 1 ? new Bitmap(source, new Size(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)))) : new Bitmap(source);
        var active = new bool[work.Height];
        for (var y = 1; y < work.Height - 1; y += 2) { var changes = 0; var previous = work.GetPixel(0, y).GetBrightness(); for (var x = 2; x < work.Width; x += 2) { var current = work.GetPixel(x, y).GetBrightness(); if (Math.Abs(current - previous) > .18f) changes++; previous = current; } active[y] = changes >= Math.Max(2, work.Width / 100); }
        var output = new List<RectangleF>(); int start = -1;
        for (var y = 0; y <= work.Height; y++) { var on = y < work.Height && (active[y] || (y > 0 && active[y - 1])); if (on && start < 0) start = y; if (!on && start >= 0) { if (y - start >= 4) output.Add(new(0, start / scale, source.Width, (y - start) / scale)); start = -1; } }
        return output;
    }
}
