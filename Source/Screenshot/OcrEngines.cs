using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

public interface IOcrEngine : IAsyncDisposable
{
    OcrEngineKind Kind { get; }
    string DisplayName { get; }
    Task<OcrEngineResult> RecognizeAsync(Bitmap image, string language, CancellationToken cancellationToken);
}

public static class WindowsOcrIsolationDiagnostics
{
    private static int _instances;
    private static int _recognizeCalls;
    public static int Instances => Volatile.Read(ref _instances);
    public static int RecognizeCalls => Volatile.Read(ref _recognizeCalls);
    public static void Reset() { Interlocked.Exchange(ref _instances, 0); Interlocked.Exchange(ref _recognizeCalls, 0); }
    internal static void InstanceCreated() => Interlocked.Increment(ref _instances);
    internal static void RecognitionStarted() => Interlocked.Increment(ref _recognizeCalls);
}

public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly OcrService _service;
    private readonly string _instanceId = $"windows-{Guid.NewGuid():N}";
    public WindowsOcrEngine(OcrService service)
    {
        _service = service;
        WindowsOcrIsolationDiagnostics.InstanceCreated();
    }
    public OcrEngineKind Kind => OcrEngineKind.Windows;
    public string DisplayName => "Windows OCR";

    public async Task<OcrEngineResult> RecognizeAsync(Bitmap image, string language, CancellationToken token)
    {
        WindowsOcrIsolationDiagnostics.RecognitionStarted();
        var document = await _service.RecognizeAsync(image, language, token, CleanupStrength.Off);
        var snapshot = document.OriginalSnapshot ?? throw new InvalidDataException("Windows OCR未返回原始快照。");
        var blocks = snapshot.Lines.Select((line, index) => new OcrEngineBlock
        {
            Id = line.SegmentId,
            RawText = line.Text,
            NormalizedText = MinimalOcrNormalizer.Normalize(line.Text),
            Confidence = line.Confidence,
            BoundingBox = line.Bounds,
            Polygon = RectanglePolygon(line.Bounds),
            ReadingOrder = line.ReadingOrder,
            LineIndex = index
        }).ToList();
        return new OcrEngineResult
        {
            EngineRequested = Kind,
            EngineActual = Kind,
            EngineInstanceId = _instanceId,
            ModelName = "Windows.Media.Ocr",
            ModelVersion = Environment.OSVersion.VersionString,
            RawText = string.Join(Environment.NewLine, blocks.Select(x => x.RawText)),
            NormalizedText = string.Join(Environment.NewLine, blocks.Select(x => x.NormalizedText)),
            Blocks = blocks,
            TotalMilliseconds = document.Metrics.PreprocessMs + document.Metrics.OcrMs,
            ModelLoadMilliseconds = 0,
            WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static PointF[] RectanglePolygon(RectangleF r) =>
        [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];
}

public sealed class OcrRuntimeManager : IAsyncDisposable
{
    private readonly string _applicationRoot;
    private readonly OcrService _windowsService;
    private ExternalOcrEngine? _external;
    private OcrRuntimeStatus? _lastRapidCheck;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OcrRuntimeManager(string applicationRoot, OcrService windowsService)
    {
        _applicationRoot = applicationRoot;
        _windowsService = windowsService;
    }

    public string RuntimeRoot => FusionRuntime.Root;
    public string LogsRoot => AppDataPaths.RuntimeLogs(RuntimeRoot, "ocr");
    public int? ActiveWorkerPid => _external?.ProcessId;

    public OcrRuntimeStatus GetStatus(OcrEngineKind kind)
    {
        if (kind == OcrEngineKind.Windows)
            return new(kind, true, "Windows.Media.Ocr", "系统组件");
        if (kind == OcrEngineKind.Rapid && ModelManagerOperations.RapidNativePathTooLong(_applicationRoot))
            return new(kind, false, "RapidOCR", ModelManagerOperations.LongPathMessage, "PATH_TOO_LONG");
        if (kind == OcrEngineKind.Rapid)
        {
            var dependencies = OcrDependencyChecker.InspectFiles(_applicationRoot, RuntimeRoot);
            if (dependencies.State != OcrDependencyState.NotChecked)
                return _lastRapidCheck = new(kind, false, "RapidOCR", dependencies.Detail, dependencies.State.ToString());
            return _lastRapidCheck ?? new(kind, false, "RapidOCR", dependencies.Detail, "NotChecked");
        }
        var spec = ResolveWorker(kind);
        var ready = File.Exists(spec.PythonPath) && File.Exists(spec.ScriptPath);
        return new(kind, false, spec.ModelName,
            ready ? "本地文件存在，尚未检查引擎。" : $"Missing：缺少本地运行环境 {spec.PythonPath}", ready ? "NotChecked" : "Missing");
    }

    public async Task<OcrEngineResult> RecognizeWithFallbackAsync(
        OcrEngineKind requested, Bitmap image, string language, CancellationToken token,
        string? requestId = null, string? imageHash = null, string? imageSessionId = null,
        bool allowAutomaticWindowsFallback = false, OcrLoad ocrLoad = OcrLoad.Standard)
    {
        requestId ??= Guid.NewGuid().ToString("N");
        imageHash ??= SessionServices.ComputeImageHash(image);
        if (requested == OcrEngineKind.Windows)
            return Stamp(await new WindowsOcrEngine(_windowsService).RecognizeAsync(image, language, token),
                requestId, imageHash, imageSessionId ?? "", requested, image.Size, false, "");
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                if (_external is null || _external.Kind != requested || _external.Load != ocrLoad)
                {
                    if (requested == OcrEngineKind.Rapid)
                    {
                        var dependencies = await OcrDependencyChecker.CheckAsync(_applicationRoot, token, RuntimeRoot);
                        if (!dependencies.Ready) throw new InvalidOperationException(dependencies.Detail);
                    }
                    if (_external is not null) await _external.DisposeAsync();
                    _external = new ExternalOcrEngine(ResolveWorker(requested),ocrLoad);
                }
                var result = await _external.RecognizeAsync(image, language, token);
                if (requested == OcrEngineKind.Rapid)
                    _lastRapidCheck = new(requested, true, "RapidOCR", "Ready：本地依赖及实际识别通过。", "Ready");
                OcrQualityPipeline.Apply(result);
                await RetryOneSuspiciousBlockAsync(result, image, language, token);
                return Stamp(result, requestId, imageHash, imageSessionId ?? "", requested, image.Size, false, "");
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (requested == OcrEngineKind.Rapid)
                _lastRapidCheck = new(requested, false, "RapidOCR", "本地 OCR 识别未通过，请重新检查运行依赖。", "CHECK_FAILED");
            if (!allowAutomaticWindowsFallback)
            {
                AppLog.Write("ocr", $"request={requestId} image={imageHash} requested={requested} failed; automatic Windows fallback disabled size={image.Width}x{image.Height}", ex);
                throw new InvalidOperationException($"{requested} OCR 失败：{ex.Message}", ex);
            }
            var fallback = await new WindowsOcrEngine(_windowsService).RecognizeAsync(image, language, token);
            var reason = $"{requested}本次不可用，已临时使用Windows OCR：{ex.GetType().Name}: {ex.Message}";
            AppLog.Write("ocr", $"request={requestId} image={imageHash} requested={requested} fallback=Windows size={image.Width}x{image.Height}", ex);
            return Stamp(fallback, requestId, imageHash, imageSessionId ?? "", requested, image.Size, true, reason);
        }
    }

    private async Task RetryOneSuspiciousBlockAsync(OcrEngineResult result, Bitmap image, string language, CancellationToken token)
    {
        var suspicious = result.Blocks.FirstOrDefault(x=>OcrQualityPipeline.IsParagraphConfidenceOutlier(x,result.Blocks))
            ?? result.Blocks.FirstOrDefault(x => x.PreprocessRetrySuggested);
        if (suspicious is null || _external is null) return;
        var watch = Stopwatch.StartNew();
        try
        {
            var paragraphRetry=OcrQualityPipeline.IsParagraphConfidenceOutlier(suspicious,result.Blocks);
            using var roi = paragraphRetry ? OcrQualityPipeline.CreateNeutralRetryRoi(image,suspicious.BoundingBox)
                : OcrQualityPipeline.CreateRetryRoi(image, suspicious.BoundingBox);
            var retry = await _external.RecognizeAsync(roi, language, token);
            var best = retry.Blocks.Where(x=>paragraphRetry
                    ?OcrQualityPipeline.CoversNeutralRetryLine(x,suspicious.BoundingBox,roi.Size,2)
                    :OcrQualityPipeline.CoversRetryLine(x,suspicious.BoundingBox))
                .OrderByDescending(x => x.Confidence ?? 0).FirstOrDefault();
            if(paragraphRetry && OcrQualityPipeline.IsFragmentedLatinLine(suspicious.RawText))
                best=OcrQualityPipeline.ComposeNeutralRetryLine(retry.Blocks,suspicious.BoundingBox,roi.Size,2,
                    (suspicious.Confidence??0)+.12f);
            result.PreprocessRetryCount = 1;
            if(paragraphRetry && OcrQualityPipeline.IsFragmentedLatinLine(suspicious.RawText) && best is not null)
            {
                suspicious.PreprocessCandidateText=best.RawText;
                suspicious.PreprocessCandidateConfidence=best.Confidence;
                // Confirm against native source pixels. Overscaling can exceed the
                // detector's input limit and introduce a second, lossy resampling.
                using var confirmationRoi=OcrQualityPipeline.CreateNeutralRetryRoi(image,suspicious.BoundingBox,1);
                var confirmation=await _external.RecognizeAsync(confirmationRoi,language,token);
                result.PreprocessRetryCount=2;
                var same=OcrQualityPipeline.ComposeNeutralRetryLine(confirmation.Blocks,suspicious.BoundingBox,confirmationRoi.Size,1,
                    (suspicious.Confidence??0)+.12f);
                if(same is not null&&MinimalOcrNormalizer.Normalize(same.RawText)!=MinimalOcrNormalizer.Normalize(best.RawText))same=null;
                result.RetryConfirmationEvidenceJson=System.Text.Json.JsonSerializer.Serialize(new
                {
                    Contract=OcrQualityPipeline.EvidenceVersion,SourceBlockId=suspicious.Id,suspicious.RawText,
                    SourceBounds=suspicious.BoundingBox,FirstScale=2,First=best,FirstBlocks=retry.Blocks,ConfirmationScale=1,
                    Confirmation=confirmation.Blocks,Consensus=same is not null
                });
                if(same is null)
                {
                    result.RetryOriginalRawText=suspicious.RawText;result.RetryRawText=best.RawText;
                    result.RetryChosenText=suspicious.CorrectedText;result.RetryReason="FragmentedParagraphConsensusNotProved";
                    return;
                }
            }
            if (best is not null)
                OcrQualityPipeline.ApplyRetryCandidate(result, suspicious, best.RawText, best.Confidence,paragraphRetry);
        }
        finally
        {
            watch.Stop();
            result.PreprocessRetryMilliseconds = watch.ElapsedMilliseconds;
        }
    }

    private static OcrEngineResult Stamp(OcrEngineResult result, string requestId, string imageHash, string imageSessionId,
        OcrEngineKind requested, Size inputSize, bool fallback, string reason)
    {
        if (!result.QualityPipelineApplied) OcrQualityPipeline.Apply(result);
        result.ImageSessionId = imageSessionId;
        result.RequestId = requestId;
        result.ImageHash = imageHash;
        result.EngineRequested = requested;
        result.FallbackUsed = fallback;
        result.FallbackReason = reason;
        result.ResultTimestamp = DateTimeOffset.UtcNow;
        result.ResultObjectId = Guid.NewGuid().ToString("N");
        result.InputWidth = inputSize.Width;
        result.InputHeight = inputSize.Height;
        return result;
    }

    public async Task<IReadOnlyList<OcrRuntimeStatus>> CheckAllAsync(CancellationToken token)
    {
        var statuses = new List<OcrRuntimeStatus> { GetStatus(OcrEngineKind.Windows) };
        foreach (var kind in new[] { OcrEngineKind.Rapid, OcrEngineKind.Paddle })
            statuses.Add(await CheckAsync(kind, token));
        return statuses;
    }

    public async Task<OcrRuntimeStatus> CheckAsync(OcrEngineKind kind, CancellationToken token)
    {
        var status = GetStatus(kind);
        if (kind == OcrEngineKind.Windows) return status;
        if (kind == OcrEngineKind.Rapid)
        {
            var dependencies = await OcrDependencyChecker.CheckAsync(_applicationRoot, token, RuntimeRoot);
            if (!dependencies.Ready)
                return _lastRapidCheck = new(kind, false, "RapidOCR", dependencies.Detail, dependencies.State.ToString());
        }
        else if (status.Code != "NotChecked") return status;
        try
        {
            await using var engine = new ExternalOcrEngine(ResolveWorker(kind));
            var checkedStatus = ParseCheck(status, await engine.CheckAsync(token));
            if (kind == OcrEngineKind.Rapid) _lastRapidCheck = checkedStatus;
            return checkedStatus;
        }
        catch (Exception ex)
        {
            var failed = status with { Ready = false, Detail = "检查未通过：" + ex.Message, Code = "CHECK_FAILED" };
            if (kind == OcrEngineKind.Rapid) _lastRapidCheck = failed;
            return failed;
        }
    }

    private static OcrRuntimeStatus ParseCheck(OcrRuntimeStatus status, string json)
    {
        using var document=JsonDocument.Parse(json);var root=document.RootElement;
        string Read(string name)=>root.TryGetProperty(name,out var value)?value.GetString()??"":"";
        bool Flag(string name)=>root.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.True;
        var ready=Read("engineStatus")=="ENVIRONMENT_READY" &&
            (status.Engine!=OcrEngineKind.Rapid || Flag("componentsReady")&&Flag("modelsValid")&&Flag("recognitionPassed")&&Read("code")=="READY");
        var detail=ready?Read("detail"):Read("errorMessage");
        return status with{Ready=ready,Code=Read("code"),Detail=string.IsNullOrEmpty(detail)?json:detail};
    }

    public async Task<OcrRuntimeStatus> RepairModelsAsync(bool allowNetwork, CancellationToken token)
    {
        if (allowNetwork)
            return new(OcrEngineKind.Rapid, false, "RapidOCR", "本 Alpha 版本不下载 OCR 环境或模型；请自行合法提供固定版本的本地依赖。", "LOCAL_DEPENDENCY_REQUIRED");
        if (FusionRuntime.IsSharedPath(RuntimeRoot))
            return new(OcrEngineKind.Rapid, false, "RapidOCR", FusionRuntime.ReadOnlyMessage, "DEPENDENCY_READ_ONLY");
        var status=GetStatus(OcrEngineKind.Rapid);if(!status.Ready)return status;
        await _gate.WaitAsync(token);
        try
        {
            // A repaired file must not be masked by an already loaded session.
            if(_external is not null){await _external.DisposeAsync();_external=null;}
            await using var engine=new ExternalOcrEngine(ResolveWorker(OcrEngineKind.Rapid));
            using var document=JsonDocument.Parse(await engine.RepairAsync(allowNetwork,token));
            var root=document.RootElement;
            var success=root.GetProperty("engineStatus").GetString()=="REPAIR_COMPLETE";
            var detail=root.GetProperty(success?"detail":"errorMessage").GetString()??"修复未完成";
            return status with{Ready=false,Code=root.GetProperty("code").GetString()??"REPAIR_FAILED",Detail=detail};
        }
        catch(Exception ex){return status with{Ready=false,Code="REPAIR_FAILED",Detail="修复未完成或已超时："+ex.Message};}
        finally{_gate.Release();}
    }

    private ExternalOcrSpec ResolveWorker(OcrEngineKind kind)
    {
        // Portable final candidates only resolve the runtime bundled beside the
        // executable. Development location pointers must never leak into a package.
        var effectiveRoot = RuntimeRoot;
        var scriptName = kind == OcrEngineKind.Paddle ? "paddle_worker.py" : "rapid_worker.py";
        var model = kind == OcrEngineKind.Paddle
            ? "PP-OCRv6_medium (Paddle CPU, oneDNN disabled)"
            : "RapidOCR PP-OCRv6 + ONNX Runtime CPU";
        return new(kind, OcrDependencyChecker.ResolvePython(effectiveRoot),
            Path.Combine(_applicationRoot, "workers", scriptName), model, effectiveRoot, LogsRoot);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { if (_external is not null) await _external.DisposeAsync(); _external = null; }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}

public sealed record OcrRuntimeStatus(OcrEngineKind Engine, bool Ready, string Model, string Detail, string Code = "");
internal sealed record ExternalOcrSpec(OcrEngineKind Kind, string PythonPath, string ScriptPath,
    string ModelName, string RuntimeRoot, string LogsRoot);

internal sealed class ExternalOcrEngine : IOcrEngine
{
    private readonly ExternalOcrSpec _spec;
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private Task? _stderrDrain;
    private bool _restartUsed;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    public OcrEngineKind Kind => _spec.Kind;
    public string DisplayName => Kind == OcrEngineKind.Paddle ? "PaddleOCR" : "RapidOCR";
    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;

    public OcrLoad Load { get; }
    public ExternalOcrEngine(ExternalOcrSpec spec, OcrLoad load=OcrLoad.Standard) { _spec=spec;Load=load; }

    public async Task<OcrEngineResult> RecognizeAsync(Bitmap image, string language, CancellationToken token)
    {
        var tempRoot = Path.Combine(AppDataPaths.TempRoot, "ScreenshotTranslatorRc3");
        Directory.CreateDirectory(tempRoot);
        var imagePath = Path.Combine(tempRoot, $"ocr-{Guid.NewGuid():N}.png");
        image.Save(imagePath, ImageFormat.Png);
        var inputBytes = new FileInfo(imagePath).Length;
        LogDiagnostic($"request begin instance={_instanceId} input={image.Width}x{image.Height} bytes={inputBytes}");
        try
        {
            try
            {
                var result = await RecognizePathAsync(imagePath, image.Size, token);
                if (Kind == OcrEngineKind.Rapid)
                    await ApplyStylizedTitleRecoveryAsync(imagePath, result, token);
                result.InputWidth = image.Width; result.InputHeight = image.Height; result.InputBytes = inputBytes;
                return result;
            }
            catch (Exception ex) when (!_restartUsed)
            {
                LogDiagnostic($"first attempt failed type={ex.GetType().Name} message={ex.Message}; restarting once");
                _restartUsed = true;
                await StopAsync();
                var result = await RecognizePathAsync(imagePath, image.Size, token);
                if (Kind == OcrEngineKind.Rapid)
                    await ApplyStylizedTitleRecoveryAsync(imagePath, result, token);
                result.InputWidth = image.Width; result.InputHeight = image.Height; result.InputBytes = inputBytes;
                return result;
            }
        }
        catch (OperationCanceledException)
        {
            LogDiagnostic("request cancelled; stopping worker so no orphan inference/output remains");
            await StopAsync();
            throw;
        }
        catch (Exception ex)
        {
            var exit = _process is null ? "none" : (_process.HasExited ? _process.ExitCode.ToString() : "running");
            LogDiagnostic($"request failed type={ex.GetType().Name} message={ex.Message} exitCode={exit}");
            throw;
        }
        finally { try { File.Delete(imagePath); } catch { } }
    }

    private async Task<OcrEngineResult> RecognizePathAsync(string imagePath, Size canvas, CancellationToken token)
    {
        EnsureStarted();
        var sendWatch = Stopwatch.StartNew();
        await _input!.WriteLineAsync(JsonSerializer.Serialize(new { command = "recognize", imagePath }).AsMemory(), token);
        await _input.FlushAsync(token);
        sendWatch.Stop();
        var receiveWatch = Stopwatch.StartNew();
        var line = await _output!.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromMinutes(5), token);
        receiveWatch.Stop();
        if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException($"{DisplayName} Worker未返回JSON。");
        var wire = JsonSerializer.Deserialize<WireResult>(line, JsonOptions)
            ?? throw new InvalidDataException($"{DisplayName}返回空JSON。");
        if (!string.IsNullOrWhiteSpace(wire.errorMessage)) throw new InvalidOperationException(wire.errorMessage);
        var requestImageHash = OcrAlternativeEvidenceValidator.FileHash(imagePath);
        var blocks = BuildWireBlocks(wire, requestImageHash, canvas, Kind);
        return new OcrEngineResult
        {
            EngineRequested = Kind, EngineActual = Kind, EngineInstanceId = _instanceId,
            ModelName = wire.model, ModelVersion = wire.modelVersion,
            RawText = wire.rawResult, NormalizedText = MinimalOcrNormalizer.Normalize(wire.rawResult),
            Blocks = blocks, TotalMilliseconds = wire.totalMs,
            SourceStagesJson=wire.sourceStages.ValueKind==JsonValueKind.Undefined?"":wire.sourceStages.GetRawText(),
            CoverageRecoveryJson=wire.coverageRecovery.ValueKind==JsonValueKind.Undefined?"":wire.coverageRecovery.GetRawText(),
            ModelLoadMilliseconds = wire.coldStartMs, WorkingSetBytes = wire.workingSetBytes,
            Error = wire.errorMessage, WorkerPid = ProcessId,
            SendMilliseconds = sendWatch.ElapsedMilliseconds, ReceiveMilliseconds = receiveWatch.ElapsedMilliseconds,
            BoundaryFastPathStatus = wire.boundary?.status ?? "NOT_REPORTED",
            BoundaryDeepAnalysisCount = wire.boundary?.deepAnalysisCount ?? 0,
            BoundaryHighProposalCount = wire.boundary?.highProposalCount ?? 0,
            BoundaryReOcrLineCount = wire.boundary?.reOcrLineCount ?? 0,
            BoundaryCropCallCount = wire.boundary?.cropCallCount ?? 0,
            BoundaryChangedLineCount = wire.boundary?.changedLineCount ?? 0,
            BoundaryFastPathMilliseconds = wire.boundary?.totalMs ?? 0,
            BoundaryFastPathTraceJson = wire.boundary is null ? "" : JsonSerializer.Serialize(wire.boundary,JsonOptions)
        };
    }

    private static List<OcrEngineBlock> BuildWireBlocks(WireResult wire, string requestImageHash,
        Size canvas, OcrEngineKind kind)
    {
        var blocks = wire.blocks.Select((x, index) => new OcrEngineBlock
        {
            Id = string.IsNullOrWhiteSpace(x.id) ? $"{kind.ToString()[0]}{index + 1:000}" : x.id,
            RawText = x.text,
            NormalizedText = MinimalOcrNormalizer.Normalize(x.text),
            Confidence = x.confidence,
            BoundingBox = new RectangleF(x.x, x.y, x.width, x.height),
            Polygon = x.polygon?.Select(p => new PointF(p[0], p[1])).ToArray() ?? [],
            LineIndex = x.lineIndex,
            ReadingOrder = x.readingOrder,
            OcrRequestImageSha256 = requestImageHash,
            OcrAlternatives = OcrAlternativeEvidenceValidator.Validate(wire.ocrAlternativesContract,
                x.ocrAlternatives, requestImageHash,
                string.IsNullOrWhiteSpace(x.id) ? $"{kind.ToString()[0]}{index + 1:000}" : x.id,
                new RectangleF(x.x, x.y, x.width, x.height), canvas)
        }).ToList();
        return blocks;
    }

    internal static IReadOnlyList<OcrEngineBlock> ParseWireBlocksForTest(string json,
        string requestImagePath, Size canvas, OcrEngineKind kind)
    {
        var wire = JsonSerializer.Deserialize<WireResult>(json, JsonOptions)
            ?? throw new InvalidDataException("Missing worker result");
        return BuildWireBlocks(wire, OcrAlternativeEvidenceValidator.FileHash(requestImagePath), canvas, kind);
    }

    private async Task ApplyStylizedTitleRecoveryAsync(string imagePath, OcrEngineResult result, CancellationToken token)
    {
        try
        {
            EnsureStarted();
            var source = result.Blocks.Select(x => new
            {
                text = x.RawText,
                x = x.BoundingBox.X,
                y = x.BoundingBox.Y,
                width = x.BoundingBox.Width,
                height = x.BoundingBox.Height
            }).ToArray();
            await _input!.WriteLineAsync(JsonSerializer.Serialize(new
            {
                command = "recover_stylized_title",
                imagePath,
                blocks = source
            }).AsMemory(), token);
            await _input.FlushAsync(token);
            var line = await _output!.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromMinutes(2), token);
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidDataException("RapidOCR title recovery worker returned no JSON.");
            var wire = JsonSerializer.Deserialize<WireTitleRecovery>(line, JsonOptions)
                ?? throw new InvalidDataException("RapidOCR title recovery returned empty JSON.");
            if (!string.IsNullOrWhiteSpace(wire.errorMessage))
                throw new InvalidOperationException(wire.errorMessage);

            result.StylizedTitleRecoveryChecked = wire.@checked;
            result.StylizedTitleRecoveryTriggered = wire.triggered;
            result.StylizedTitleRecoveryMilliseconds = wire.totalMs;
            result.StylizedTitleRecoveryRawCandidateCount = wire.rawCandidateCount;
            result.StylizedTitleRecoveryAcceptedCandidateCount = wire.acceptedCandidateCount;
            result.StylizedTitleRecoveryLocalRecognitionCount = wire.localRecognitionCount;
            result.StylizedTitleRecoveryReason = wire.reason;
            result.StylizedTitleRecoveryTraceJson = JsonSerializer.Serialize(wire, JsonOptions);
            result.TotalMilliseconds += (long)Math.Ceiling(Math.Max(0, wire.totalMs));

            foreach (var recovered in wire.blocks)
            {
                var idNumber = result.Blocks.Count + 1;
                string id;
                do { id = $"R{idNumber++:000}"; }
                while (result.Blocks.Any(x => string.Equals(x.Id, id, StringComparison.Ordinal)));
                var block = new OcrEngineBlock
                {
                    Id = id,
                    RawText = recovered.text,
                    NormalizedText = MinimalOcrNormalizer.Normalize(recovered.text),
                    Confidence = recovered.confidence,
                    BoundingBox = new RectangleF(recovered.x, recovered.y, recovered.width, recovered.height),
                    Polygon = recovered.polygon?.Select(p => new PointF(p[0], p[1])).ToArray() ?? [],
                    LineIndex = -1,
                    ReadingOrder = 0
                };
                result.Blocks.Add(block);
                if (!string.IsNullOrWhiteSpace(recovered.proposalId))
                    result.StylizedTitleRecoveryProposalIds.Add(recovered.proposalId);
            }
            if (wire.blocks.Count > 0)
            {
                result.RawText = string.Join(Environment.NewLine, result.Blocks
                    .OrderBy(x => x.ReadingOrder).ThenBy(x => x.BoundingBox.Top).ThenBy(x => x.BoundingBox.Left)
                    .Select(x => x.RawText));
                result.NormalizedText = MinimalOcrNormalizer.Normalize(result.RawText);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Recovery is a precision enhancement. A diagnostic failure must
            // leave the already-complete baseline OCR result usable.
            result.StylizedTitleRecoveryChecked = false;
            result.StylizedTitleRecoveryTriggered = false;
            result.StylizedTitleRecoveryReason = $"RECOVERY_UNAVAILABLE:{ex.GetType().Name}";
            LogDiagnostic($"stylized title recovery skipped type={ex.GetType().Name} message={ex.Message}");
        }
    }

    public async Task<string> CheckAsync(CancellationToken token)
    {
        EnsureStarted();
        await _input!.WriteLineAsync("{\"command\":\"check\"}".AsMemory(), token);
        await _input.FlushAsync(token);
        return await _output!.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(45), token) ?? "";
    }

    public async Task<string> RepairAsync(bool allowNetwork, CancellationToken token)
    {
        EnsureStarted();
        await _input!.WriteLineAsync(JsonSerializer.Serialize(new{command="repair",allowNetwork}).AsMemory(),token);
        await _input.FlushAsync(token);
        return await _output!.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(75),token)??"";
    }

    private void EnsureStarted()
    {
        if (_process is { HasExited: false }) return;
        if(Kind==OcrEngineKind.Rapid && ModelManagerOperations.RapidNativePathTooLong(Path.GetDirectoryName(_spec.RuntimeRoot)!))
            throw new InvalidOperationException(ModelManagerOperations.LongPathMessage);
        if (!File.Exists(_spec.PythonPath)) throw new FileNotFoundException("OCR Python运行环境不存在。", _spec.PythonPath);
        if (!File.Exists(_spec.ScriptPath)) throw new FileNotFoundException("OCR Worker不存在。", _spec.ScriptPath);
        Directory.CreateDirectory(_spec.LogsRoot);
        var info = new ProcessStartInfo(_spec.PythonPath)
        {
            WorkingDirectory = _spec.RuntimeRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // Ignore inherited Python paths and site hooks. Add only the explicit
        // local package directory and this application's worker source directory.
        info.ArgumentList.Add("-I"); info.ArgumentList.Add("-S"); info.ArgumentList.Add("-B"); info.ArgumentList.Add("-u");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("import sys,runpy;sys.path[:0]=[sys.argv[1],sys.argv[2]];runpy.run_path(sys.argv[3],run_name='__main__')");
        info.ArgumentList.Add(OcrDependencyChecker.ResolveSitePackages(_spec.RuntimeRoot));
        info.ArgumentList.Add(Path.GetDirectoryName(_spec.ScriptPath)!);
        info.ArgumentList.Add(_spec.ScriptPath);
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ST_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment.Remove("VIRTUAL_ENV"); info.Environment.Remove("CONDA_PREFIX");
        info.Environment["PATH"] = string.Join(Path.PathSeparator, Path.GetDirectoryName(_spec.PythonPath), Environment.SystemDirectory);
        AppDataPaths.ConfigureChildEnvironment(info);
        info.Environment["FLAGS_use_mkldnn"] = "0";
        info.Environment["ST_FUSION_RUNTIME_ROOT"] = _spec.RuntimeRoot;
        info.Environment["ST_OCR_LOAD"]=Kind==OcrEngineKind.Rapid && Load==OcrLoad.Low?"low":"standard";
        var localPaddleHome = Path.Combine(_spec.RuntimeRoot, "models", "paddlex-home");
        info.Environment["PADDLEX_HOME"] = AppDataPaths.HasExplicitRoot && Kind==OcrEngineKind.Rapid
            ? Path.Combine(AppDataPaths.CacheRoot,"paddlex")
            : localPaddleHome;
        info.Environment["HF_HOME"] = AppDataPaths.HasExplicitRoot ? Path.Combine(AppDataPaths.CacheRoot, "huggingface") : Path.Combine(_spec.RuntimeRoot, "models", "huggingface-home");
        _process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动{DisplayName} Worker。");
        LogDiagnostic($"worker start pid={_process.Id} python={_spec.PythonPath} model={_spec.ModelName}");
        _input = _process.StandardInput; _output = _process.StandardOutput;
        var errorPath = Path.Combine(_spec.LogsRoot, $"{Kind.ToString().ToLowerInvariant()}-worker.log");
        _stderrDrain = Task.Run(async () =>
        {
            while (await _process.StandardError.ReadLineAsync() is { } text)
                await File.AppendAllTextAsync(errorPath, $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
        });
    }

    private async Task StopAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await Task.Run(() => _process.WaitForExit(2500));
            }
        }
        catch { try { _process?.Kill(true); } catch { } }
        _input?.Dispose(); _output?.Dispose(); _process?.Dispose();
        _input = null; _output = null; _process = null;
        if (_stderrDrain is not null) try { await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
    private void LogDiagnostic(string message)
    {
        try
        {
            Directory.CreateDirectory(_spec.LogsRoot);
            File.AppendAllText(Path.Combine(_spec.LogsRoot, "ocr-integrity.log"),
                $"{DateTimeOffset.Now:O} engine={Kind} {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed class WireResult
    {
        public string errorMessage { get; set; } = ""; public string model { get; set; } = "";
        public string modelVersion { get; set; } = "";
        public long totalMs { get; set; } public long coldStartMs { get; set; }
        public long workingSetBytes { get; set; } public string rawResult { get; set; } = "";
        public List<WireBlock> blocks { get; set; } = [];
        public WireBoundary? boundary { get; set; }
        public string ocrAlternativesContract { get; set; } = "";
        public JsonElement sourceStages { get; set; }
        public JsonElement coverageRecovery { get; set; }
    }
    private sealed class WireBoundary
    {
        public string contract { get; set; } = ""; public string status { get; set; } = "";
        public string errorMessage { get; set; } = ""; public int lineCount { get; set; }
        public int deepAnalysisCount { get; set; } public int highProposalCount { get; set; }
        public int reOcrLineCount { get; set; } public int cropCallCount { get; set; }
        public int changedLineCount { get; set; } public double routerMs { get; set; }
        public double analyzerMs { get; set; } public double reOcrMs { get; set; }
        public double totalMs { get; set; } public JsonElement[] trace { get; set; } = [];
    }
    private sealed class WireBlock
    {
        public string id { get; set; } = ""; public string text { get; set; } = "";
        public float? confidence { get; set; } public float x { get; set; } public float y { get; set; }
        public float width { get; set; } public float height { get; set; }
        public List<float[]>? polygon { get; set; } public int lineIndex { get; set; } public int readingOrder { get; set; }
        public string proposalId { get; set; } = ""; public string cropSha256 { get; set; } = "";
        public List<OcrAlternativeEvidence> ocrAlternatives { get; set; } = [];
    }
    private sealed class WireTitleRecovery
    {
        public string errorMessage { get; set; } = "";
        public double totalMs { get; set; }
        public bool @checked { get; set; }
        public bool triggered { get; set; }
        public string reason { get; set; } = "";
        public float medianOcrHeight { get; set; }
        public int anchorCount { get; set; }
        public int rawCandidateCount { get; set; }
        public int acceptedCandidateCount { get; set; }
        public int localRecognitionCount { get; set; }
        public List<WireBlock> blocks { get; set; } = [];
        public JsonElement[] trace { get; set; } = [];
    }
}

internal static class MinimalOcrNormalizer
{
    public static string Normalize(string value)
    {
        value = value.Replace("\r\n", "\n").Replace('\r', '\n');
        value = new string(value.Where(c => c is '\n' or '\t' || !char.IsControl(c)).ToArray());
        return string.Join("\n", value.Split('\n')
            .Select(line => Regex.Replace(line, "[ \\t]{2,}", " ").TrimEnd())).Trim();
    }
}
