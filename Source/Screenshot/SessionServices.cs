using System.Security.Cryptography;
using System.Text;

namespace ScreenshotTranslationUiTester;

public sealed class SessionServices : IDisposable
{
    private readonly LruCache<string, OcrEngineResult> _ocr = new(32);
    private readonly LruCache<string, TranslationBatchResult> _translations = new(128);
    private readonly PersistentHistoryStore _history;
    private readonly object _gate = new();
    public event Action? HistoryChanged;
    public int OcrHits { get; private set; }
    public int OcrMisses { get; private set; }
    public int TranslationHits { get; private set; }
    public int TranslationMisses { get; private set; }
    public int OcrCount => _ocr.Count;
    public int TranslationCount => _translations.Count;
    public int HistoryCount => _history.Snapshot().Count;
    private PreviewSelectionState? _lastPreviewSelection;

    public SessionServices(string? historyRoot=null)
    {
        _history=new PersistentHistoryStore(historyRoot??(AppDataPaths.HasExplicitRoot?AppDataPaths.HistoryRoot:Path.Combine(AppDataPaths.TempRoot,"ScreenshotTranslator","session-history")));
    }

    public PreviewSelectionState? LastPreviewSelection
    {
        get { lock (_gate) return _lastPreviewSelection; }
    }

    public void RememberPreviewSelection(OcrEngineKind engine, ImageViewMode image, TextViewMode text)
    {
        lock (_gate) _lastPreviewSelection = new PreviewSelectionState(engine, image, text);
    }

    public static string CreateOcrKey(string imageHash,OcrEngineKind engine,string model)=>
        $"{imageHash}|requested={engine}|actual={engine}|{model}|coverage={HistoryRegenerationPolicy.OcrCoverageContract}|quality={OcrQualityPipeline.EvidenceVersion}";
    public string CreateOcrKey(Bitmap image,OcrEngineKind engine,string model)=>CreateOcrKey(ComputeImageHash(image),engine,model);
    public static string ComputeImageHash(Bitmap image)
    {
        return new SessionImageIdentity(image).GetHashAsync().GetAwaiter().GetResult();
    }
    public bool TryGetOcr(string key, out OcrEngineResult value)
    {
        if (_ocr.TryGet(key, out value!))
        {
            OcrHits++; value = Clone(value); value.CacheHit = true;
            value.RequestId = Guid.NewGuid().ToString("N"); value.ResultObjectId = Guid.NewGuid().ToString("N");
            value.ResultTimestamp = DateTimeOffset.UtcNow;
            return true;
        }
        OcrMisses++; return false;
    }
    public void PutOcr(string key, OcrEngineResult value)
    {
        if (value.Success && value.EngineRequested == value.EngineActual && !value.FallbackUsed)
            _ocr.Put(key, Clone(value));
    }
    public static string TranslationKey(IEnumerable<TranslationItem> items, ApiSettings settings)
        => TranslationCacheKeyBuilder.Build(items, settings);
    public bool TryGetTranslation(string key, out TranslationBatchResult value)
    {
        if (_translations.TryGet(key, out var cached))
        {
            TranslationHits++;
            value = new TranslationBatchResult(new Dictionary<string, string>(cached.Translations), 0, 0,
                cached.MissingIds.ToArray(), cached.UsedResponseFormat, cached.FullTranslation,
                cached.SegmentMappingReliable,cached.RecoveryStats,
                cached.Allocations?.ToDictionary(x=>x.Key,x=>x.Value.ToArray(),StringComparer.Ordinal));
            return true;
        }
        value = default!;
        TranslationMisses++; return false;
    }
    public void PutTranslation(string key, TranslationBatchResult value)
    {
        if (value.MissingIds.Count == 0 && value.Translations.Count > 0) _translations.Put(key, value);
    }

    public void AddHistory(Bitmap image, OcrEngineKind engine, string source, string translation,
        string targetLanguage, ApiSettings settings, Bitmap? translatedImage = null,
        HistoryCoreSnapshot? coreSnapshot = null)
    {
        _history.Add(image, engine, source, translation, targetLanguage, settings, translatedImage, coreSnapshot);
        HistoryChanged?.Invoke();
    }
    public IReadOnlyList<SessionHistoryItem> SnapshotHistory() => _history.Snapshot();
    public long HistoryUsageBytes=>_history.UsageBytes;
    public void ApplyHistoryCleanup(ApiSettings settings){_history.ApplyCleanup(settings);HistoryChanged?.Invoke();}
    public void ClearHistory(){_history.Clear();HistoryChanged?.Invoke();}
    public void ClearCaches() { _ocr.Clear(); _translations.Clear(); }
    public void Clear() => ClearCaches();
    public void Dispose() => ClearCaches();

    private static OcrEngineResult Clone(OcrEngineResult source) => new()
    {
        ImageSessionId = source.ImageSessionId, RequestId = source.RequestId, ImageHash = source.ImageHash,
        EngineRequested = source.EngineRequested, EngineActual = source.EngineActual,
        EngineInstanceId = source.EngineInstanceId, ModelName = source.ModelName, ModelVersion = source.ModelVersion,
        SourceStagesJson=source.SourceStagesJson,CoverageRecoveryJson=source.CoverageRecoveryJson,
        ResultTimestamp = source.ResultTimestamp, FallbackUsed = source.FallbackUsed,
        RawText = source.RawText, NormalizedText = source.NormalizedText, CorrectedText = source.CorrectedText, TotalMilliseconds = source.TotalMilliseconds,
        ModelLoadMilliseconds = source.ModelLoadMilliseconds, CacheHit = source.CacheHit,
        FallbackReason = source.FallbackReason, Error = source.Error, WorkingSetBytes = source.WorkingSetBytes,
        ResultObjectId = source.ResultObjectId, WorkerPid = source.WorkerPid,
        InputWidth = source.InputWidth, InputHeight = source.InputHeight, InputBytes = source.InputBytes,
        SendMilliseconds = source.SendMilliseconds, ReceiveMilliseconds = source.ReceiveMilliseconds,
        CorrectionMilliseconds = source.CorrectionMilliseconds, PreprocessRetryMilliseconds = source.PreprocessRetryMilliseconds,
        PreprocessRetryCount = source.PreprocessRetryCount, LowConfidenceBlockCount = source.LowConfidenceBlockCount,
        CorrectedBlockCount = source.CorrectedBlockCount, CorrectionCount = source.CorrectionCount,
        FalsePositiveSuspectCount = source.FalsePositiveSuspectCount, CharacterTopKAvailable = source.CharacterTopKAvailable,
        AmbiguousBlockCount = source.AmbiguousBlockCount,
        ConfidenceAvailableBlockCount = source.ConfidenceAvailableBlockCount,
        ConfidenceUnavailableBlockCount = source.ConfidenceUnavailableBlockCount,
        QualityPipelineApplied = source.QualityPipelineApplied,
        WindowsLegacyRuleInvocationCount = source.WindowsLegacyRuleInvocationCount,
        RetryOriginalRawText = source.RetryOriginalRawText, RetryRawText = source.RetryRawText,
        RetryChosenText = source.RetryChosenText, RetryReason = source.RetryReason,
        RetryPreprocessType = source.RetryPreprocessType,
        RetryConfirmationEvidenceJson = source.RetryConfirmationEvidenceJson,
        Blocks = source.Blocks.Select(x => new OcrEngineBlock { Id = x.Id, RawText = x.RawText,
            NormalizedText = x.NormalizedText, CorrectedText = x.CorrectedText, Confidence = x.Confidence, BoundingBox = x.BoundingBox,
            Polygon = x.Polygon.ToArray(), ReadingOrder = x.ReadingOrder, LineIndex = x.LineIndex, Enabled = x.Enabled,
            Script = x.Script, SemanticType = x.SemanticType, LowConfidence = x.LowConfidence,
            SuspectedFalsePositive = x.SuspectedFalsePositive, PreprocessRetrySuggested = x.PreprocessRetrySuggested,
            PreprocessCandidateText = x.PreprocessCandidateText, PreprocessCandidateConfidence = x.PreprocessCandidateConfidence,
            UnresolvedAmbiguity = x.UnresolvedAmbiguity, AmbiguityCandidates = x.AmbiguityCandidates,
            OcrRequestImageSha256 = x.OcrRequestImageSha256,
            OcrAlternatives = x.OcrAlternatives.Select(c => c.Copy()).ToArray(),
            Corrections = x.Corrections.ToList() }).ToList()
    };
}

public sealed record SessionHistoryItem(DateTimeOffset Timestamp, OcrEngineKind Engine,
    string SourceSummary, string TranslationSummary, string TargetLanguage, byte[] ThumbnailJpeg, byte[] OriginalPng,
    string? SourceText = null, string? TranslationText = null, byte[]? TranslatedImage = null,
    string? SourceImagePath = null, string? TranslatedImagePath = null,
    HistoryCoreSnapshot? CoreSnapshot = null);

public sealed record PreviewSelectionState(OcrEngineKind Engine, ImageViewMode Image, TextViewMode Text);

internal sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _list = [];
    private readonly object _gate = new();
    public int Count { get { lock (_gate) return _map.Count; } }
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node)) { value = default!; return false; }
            _list.Remove(node); _list.AddFirst(node); value = node.Value.Value; return true;
        }
    }
    public void Put(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.Remove(key, out var existing)) _list.Remove(existing);
            var node = _list.AddFirst((key, value)); _map[key] = node;
            while (_map.Count > capacity) { var last = _list.Last!; _map.Remove(last.Value.Key); _list.RemoveLast(); }
        }
    }
    public void Clear() { lock (_gate) { _map.Clear(); _list.Clear(); } }
}
