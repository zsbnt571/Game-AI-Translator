using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

// Explicit private diagnostics: hidden real Preview, current OCR and provider, no synthetic acceptance.
internal static class ScopedProductionChainHarness
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,PropertyNameCaseInsensitive=true,
        Converters={new JsonStringEnumConverter()}};
    private sealed record Input(string Id,string SourcePath,string SourceSHA256,string? OcrReplayPath)
    {
        public string? HistorySnapshotPath {get;init;}
        public string? HistorySnapshotSHA256 {get;init;}
        public string? HistoryImagePath {get;init;}
        public string? HistoryImageSHA256 {get;init;}
    }
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Save(string path,object value)=>File.WriteAllText(path,JsonSerializer.Serialize(value,Json));
    private static object? Get(object item,string name)=>item.GetType().GetField(name,Private)!.GetValue(item);
    private static void Set(object item,string name,object? value)=>item.GetType().GetField(name,Private)!.SetValue(item,value);

    internal static int Run(string output,string manifestPath,string sharedApplicationRoot,string workerWrapper,string roundPurpose)
    {
        var fixRoot=ValidationPaths.RequiredRoot("ST_FIX_ROOT")+Path.DirectorySeparatorChar;
        void Inside(string path)
        {if(!Path.GetFullPath(path).StartsWith(fixRoot,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Evidence/input must be within the registered fix root");}
        foreach(var path in new[]{output,manifestPath,workerWrapper,AppContext.BaseDirectory,AppDataPaths.Root})Inside(path);
        if(!AppDataPaths.HasExplicitRoot||!AppDataPaths.DisableGlobalInput)
            throw new InvalidOperationException("Production diagnostics require formal --data-root and --disable-global-input.");
        var readOnlyInputRoot=ValidationPaths.RequiredRoot("ST_FIX_READONLY_INPUT_ROOT")+Path.DirectorySeparatorChar;
        void InputInside(string path)
        {
            var full=Path.GetFullPath(path);
            if(!full.StartsWith(fixRoot,StringComparison.OrdinalIgnoreCase)&&!full.StartsWith(readOnlyInputRoot,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Input is outside registered evidence roots.");
        }
        var runtimeRoot=Path.GetFullPath(sharedApplicationRoot).TrimEnd(Path.DirectorySeparatorChar);
        var registeredRuntime=ValidationPaths.RequiredRoot("ST_FIX_SHARED_OCR_ROOT");
        if(!runtimeRoot.Equals(registeredRuntime,StringComparison.OrdinalIgnoreCase)||
            !File.Exists(Path.Combine(runtimeRoot,"runtime","python","python.exe")))
            throw new InvalidOperationException("Shared runtime identity/path mismatch.");
        var sourceManifest=Environment.GetEnvironmentVariable("ST_FIX_SOURCE_MANIFEST_PATH")??
            throw new InvalidOperationException("Stage source manifest required");
        Inside(sourceManifest);
        var sourceManifestHash=Hash(sourceManifest);
        var diagnosticsSetting=Environment.GetEnvironmentVariable("ST_FIX_COMPACT_DIAGNOSTICS")??"0";
        if(diagnosticsSetting!="0"&&diagnosticsSetting!="1")throw new InvalidOperationException("Explicit compact diagnostics mode must be 0 or 1");
        var compactDiagnostics=diagnosticsSetting=="1";
        var semanticAuditSetting=Environment.GetEnvironmentVariable("ST_FIX_COMPLETE_SEMANTIC_CORPUS")??"0";
        if(semanticAuditSetting!="0"&&semanticAuditSetting!="1")throw new InvalidOperationException("Explicit complete semantic corpus mode must be 0 or 1");
        var completeSemanticCorpus=semanticAuditSetting=="1";
        var diagnosticsMode=compactDiagnostics?"COMPACT_PRODUCT":"FULL_DIAGNOSTIC";
        var independentRenderPath=compactDiagnostics?"CorePipelineCorpusRunner.RenderProduct":"CorePipelineCorpusRunner.Render";
        if(Environment.GetEnvironmentVariable("SCREENSHOT_TRANSLATOR_V2_FROZEN_TEST")=="1")
            throw new InvalidOperationException("Frozen test mode cannot be used for current production OCR");
        var inputs=JsonSerializer.Deserialize<Input[]>(File.ReadAllText(manifestPath),Json)??[];
        if(inputs.Length==0||inputs.Select(x=>x.Id).Distinct().Count()!=inputs.Length)throw new InvalidDataException("Unique input ids required");
        if(!AuditInputCountAllowed(completeSemanticCorpus,inputs.Length))throw new InvalidDataException("Complete semantic corpus audit is bounded to 1 through 37 inputs");
        foreach(var input in inputs)
        {
            InputInside(input.SourcePath);if(input.OcrReplayPath is not null)InputInside(input.OcrReplayPath);
            if(input.HistorySnapshotPath is not null)
            {
                InputInside(input.HistorySnapshotPath);
                if(input.OcrReplayPath is not null || Hash(input.HistorySnapshotPath)!=input.HistorySnapshotSHA256)
                    throw new InvalidDataException("History snapshot identity or conflicting replay mode");
                if(input.HistoryImagePath is not null)
                {
                    InputInside(input.HistoryImagePath);
                    if(Hash(input.HistoryImagePath)!=input.HistoryImageSHA256)throw new InvalidDataException("History image identity");
                }
            }
            if(input.Id!=Path.GetFileName(input.Id)||input.Id.IndexOfAny(Path.GetInvalidFileNameChars())>=0||Hash(input.SourcePath)!=input.SourceSHA256)
                throw new InvalidDataException("Source identity or id mismatch");
        }
        Directory.CreateDirectory(output);
        var realSettingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameAITranslator","ScreenshotTranslationTester","settings.json");
        // Read-only direct deserialization: never invoke settings migration/save, never serialize settings.
        var settings=JsonSerializer.Deserialize<ApiSettings>(File.ReadAllText(realSettingsPath),Json)??throw new InvalidDataException("Settings missing");
        if(string.IsNullOrWhiteSpace(settings.ApiKey)||settings.Model!="deepseek-v4-flash"||
            !Uri.TryCreate(settings.ApiUrl,UriKind.Absolute,out var baseUri)||baseUri.Host!="api.deepseek.com"||
            settings.OcrEngine!=OcrEngineKind.Rapid||settings.VisualModel!=VisualModelKind.Off)
            throw new InvalidOperationException("Current configuration differs from the verified Rapid/Off/DeepSeek configuration; no request sent");
        SafeDiagnosticOutput.RegisterCredential(settings.ApiKey);
        var userOcrCache=settings.OcrCacheEnabled;var userTranslationCache=settings.TranslationCacheEnabled;
        settings.OcrCacheEnabled=false;settings.TranslationCacheEnabled=false;settings.PreviewAlwaysOnTop=false;
        var codeHash=Hash(typeof(PreviewForm).Assembly.Location);
        var workerPath=Path.Combine(AppContext.BaseDirectory,"workers","rapid_worker.py");Inside(workerPath);
        Environment.SetEnvironmentVariable("ST_FIX_PRODUCT_WORKER_PATH",workerPath);
        Save(Path.Combine(output,"CONFIG-ALLOWLIST.json"),new{BuildIdentity.BuildId,CodeHash=codeHash,
            SourceManifest=sourceManifest,SourceManifestSHA256=sourceManifestHash,InputManifestSHA256=Hash(manifestPath),
            DataRoot=AppDataPaths.Root,LogsRoot=AppDataPaths.LogsRoot,HistoryRoot=AppDataPaths.HistoryRoot,CacheRoot=AppDataPaths.CacheRoot,
            FormalDataRoot=true,GlobalInputDisabled=AppDataPaths.DisableGlobalInput,SharedRuntime=runtimeRoot,ProcessId=Environment.ProcessId,ContentContract=CoreTranslationContentValidator.ContractVersion,
            Provider=settings.ProviderDisplayName,Base=settings.ApiUrl,Model=settings.Model,Target=settings.TargetLanguage,
            Prompt=TranslationPromptBuilder.Version,settings.TranslationStyle,settings.SourceLanguage,settings.OcrLanguage,
            OcrEngine=settings.OcrEngine.ToString(),VisualModel=settings.VisualModel.ToString(),UserOcrCache=userOcrCache,
            UserTranslationCache=userTranslationCache,TestOcrCache=false,TestTranslationCache=false,
            settings.FirstByteTimeoutSeconds,settings.RequestTimeoutSeconds,Purpose=roundPurpose,
            WorkerPath=workerPath,WorkerSHA256=Hash(workerPath),WrapperSHA256=Hash(workerWrapper),
            CredentialHandling="Read-only memory; no value, fragment, length or fingerprint emitted",
            StageCoverage="Hidden current Preview RunOcr/RunTranslation/Core acceptance/render commit; separately recorded diagnostic render; no desktop input or visible window",
            CompactDiagnostics=compactDiagnostics,DiagnosticsMode=diagnosticsMode,DiagnosticsEnabled=!compactDiagnostics,
            IndependentRenderPath=independentRenderPath,PixelEqualityRequired=true,
            CompleteSemanticCorpus=completeSemanticCorpus,SemanticValidationBypassed=false,AuditInputLimit=completeSemanticCorpus?37:(int?)null,
            TimingScope="Instrumented correctness run, not a production latency benchmark"});
        if(Environment.GetEnvironmentVariable("ST_FIX_PRODUCTION_PREFLIGHT_ONLY")=="1")
        {
            Save(Path.Combine(output,"PREFLIGHT-NO-REQUESTS.json"),new{Images=0,HTTPRequests=0,ConfigurationMatches=true,
                Inputs=inputs.Length,AllInputHashesVerified=true,FormalDataRoot=AppDataPaths.Root,SharedRuntime=runtimeRoot,SourceManifestSHA256=sourceManifestHash});
            return 0;
        }
        var rows=new List<object>();var failed=0;var semanticRejectedImages=0;var totalRequests=0;
        foreach(var input in inputs)
        {
            Console.WriteLine("PRODUCTION BEGIN "+input.Id);
            var folder=Path.Combine(output,"fixtures",input.Id);Directory.CreateDirectory(folder);
            var events=new ConcurrentQueue<TranslationBoundaryEvent>();
            var previous=SynchronizationContext.Current;var context=new PumpContext();
            using var source=new Bitmap(input.SourcePath);
            using var session=new SessionServices(Path.Combine(folder,"history"));
            var runtime=new OcrRuntimeManager(sharedApplicationRoot,new OcrService());
            var vision=new VisionRuntimeManager(sharedApplicationRoot);
            var spec=new ExternalOcrSpec(OcrEngineKind.Rapid,Path.Combine(sharedApplicationRoot,"runtime","python","python.exe"),
                workerWrapper,"RapidOCR PP-OCRv6 + ONNX Runtime CPU",Path.Combine(sharedApplicationRoot,"runtime"),Path.Combine(folder,"ocr-logs"));
            Set(runtime,"_external",new ExternalOcrEngine(spec));
            using var form=new PreviewForm(source,PreviewMode.OcrOnly,settings,new OcrService(),new TranslationService(),runtime,vision,session);
            Set(form,"_suppressAutoOcrForE2E",true);Set(form,"_allowUiImageDisplay",true);
            OcrEngineResult? observedOcr=null;string? ocrFailure=null;string failure="";string failureMessage="";long pixelDifference=-1;IReadOnlyList<BlockRenderAudit>? renderAudits=null;
            CorePipelineProductTiming? independentRenderTiming=null;
            form.DiagnosticOcrCompleted=result=>observedOcr=result;
            form.DiagnosticOcrFailure=error=>ocrFailure=error.GetType().FullName;
            _=form.Handle;SynchronizationContext.SetSynchronizationContext(context);
            var timer=Stopwatch.StartNew();CorePipelineDocument? core=null;
            try
            {
                using var capture=TranslationBoundaryDiagnostics.Begin(events.Enqueue);
                var regenerationStarted=false;
                if(input.HistorySnapshotPath is not null)
                {
                    var saved=JsonSerializer.Deserialize<HistoryCoreSnapshot>(File.ReadAllText(input.HistorySnapshotPath),Json)
                        ??throw new InvalidDataException("History snapshot missing");
                    using var savedImage=input.HistoryImagePath is null?null:new Bitmap(input.HistoryImagePath);
                    var oldText=string.Join("\n\n",saved.AcceptedTranslations.Values);
                    form.RestoreHistoryState(string.Join("\n",saved.Lines.Select(x=>x.CorrectedText)),oldText,savedImage,saved);
                    var viewNoOcr=form.OcrTaskForSmoke is null&&observedOcr is null;
                    var viewNoApi=events.All(x=>x.Stage!="RequestBody");
                    using var displayed=form.CloneTranslatedImageForTest();
                    var viewMatches=savedImage is null||displayed is not null&&
                        SessionServices.ComputeImageHash(savedImage)==SessionServices.ComputeImageHash(displayed);
                    Save(Path.Combine(folder,"HISTORY-VIEW-CHECK.json"),new
                    {ViewNoOcr=viewNoOcr,ViewNoApi=viewNoApi,SavedPixelsUnchanged=viewMatches,
                     SnapshotUnchanged=Hash(input.HistorySnapshotPath)==input.HistorySnapshotSHA256,
                     SourceAvailable=true,SnapshotProvenance=saved.Provenance});
                    if(!viewNoOcr||!viewNoApi||!viewMatches)throw new InvalidOperationException("Historical view changed data or started processing");
                    // Explicit regeneration is the point at which the source snapshot may be prepared.
                    context.Finish(form.PrepareOcrSnapshotForSmoke(),TimeSpan.FromSeconds(30));
                    form.StartTranslationForSmoke();regenerationStarted=true;
                    if(form.OcrTaskForSmoke is {} upgrading)context.Finish(upgrading,TimeSpan.FromMinutes(3));
                    if(ocrFailure is not null)throw new InvalidOperationException("Regeneration OCR failed: "+ocrFailure);
                }
                else if(input.OcrReplayPath is null)
                {
                    context.Finish(form.PrepareAndStartOcrForSmokeAsync(),TimeSpan.FromMinutes(3));
                    if(form.OcrTaskForSmoke is { } ocrTask)context.Finish(ocrTask,TimeSpan.FromMinutes(3));
                    if(observedOcr is null||ocrFailure is not null)throw new InvalidOperationException("OCR did not complete successfully: "+ocrFailure);
                }
                else
                {
                    observedOcr=JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(input.OcrReplayPath),Json)??throw new InvalidDataException("OCR replay missing");
                    var build=typeof(PreviewForm).GetMethod("BuildCorePipelineV2",BindingFlags.Static|BindingFlags.NonPublic,null,[typeof(OcrEngineResult),typeof(Size)],null)!;
                    core=(CorePipelineDocument)build.Invoke(null,[observedOcr,source.Size])!;
                    Set(form,"_corePipelineV2",core);
                    Set(form,"_document",typeof(PreviewForm).GetMethod("BuildCoreProductDocument",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[core,observedOcr]));
                }
                source.Save(Path.Combine(folder,"SOURCE.png"),ImageFormat.Png);
                Save(Path.Combine(folder,"PRODUCT-OCR-REPLAY.json"),observedOcr);
                if(!regenerationStarted)form.StartTranslationForSmoke();
                if(form.TranslationTaskForSmoke is not { } translationTask)throw new InvalidOperationException("Actual Preview translation task did not start");
                context.Finish(translationTask,TimeSpan.FromMinutes(4));
                core=(CorePipelineDocument?)Get(form,"_corePipelineV2")??throw new InvalidDataException("Preview Core missing");
                using var product=form.CloneTranslatedImageForTest()??throw new InvalidDataException("Preview committed no translated bitmap");
                product.Save(Path.Combine(folder,"PRODUCT-FINAL.png"),ImageFormat.Png);
                Bitmap diagnosticBitmap;
                if(compactDiagnostics)
                {
                    var rendered=CorePipelineCorpusRunner.RenderProduct(source,core,FontManager.ResolveTranslationImageProfile(settings),
                        backgroundDiagnostics:Environment.GetEnvironmentVariable("ST_BACKGROUND_COMPACT_CAPTURE")=="1"?folder:null);
                    diagnosticBitmap=rendered.Bitmap;renderAudits=rendered.Audits;independentRenderTiming=rendered.Timing;
                }
                else
                {
                    renderAudits=CorePipelineCorpusRunner.Render(source,core,folder,FontManager.ResolveTranslationImageProfile(settings));
                    diagnosticBitmap=new Bitmap(Path.Combine(folder,"07-FINAL-TRANSLATED.png"));
                }
                using(var diagnostic=diagnosticBitmap)
                {
                    if(product.Size!=diagnostic.Size)throw new InvalidDataException("Actual Preview versus independent render size differs");
                    pixelDifference=0;
                    for(var y=0;y<product.Height;y++)for(var x=0;x<product.Width;x++)
                        if(product.GetPixel(x,y).ToArgb()!=diagnostic.GetPixel(x,y).ToArgb())pixelDifference++;
                }
                if(compactDiagnostics)
                {
                    Save(Path.Combine(folder,"PRODUCT-RENDER-AUDITS.json"),renderAudits!);
                    Save(Path.Combine(folder,"PRODUCT-RENDER-TIMING.json"),independentRenderTiming!);
                }
                Save(Path.Combine(folder,"TRANSLATED-TEXT.json"),new{Items=core.Translations.Values,Audits=renderAudits,
                    AcceptedMappingOrigin="Actual Preview.RunTranslationAsync acceptance branch",Snapshot=HistoryCoreSnapshot.Capture(core)});
                if(pixelDifference!=0)throw new InvalidDataException("Actual Preview versus diagnostic pixels differ");
                if(core.Translations.Values.Any(x=>x.State==BlockTranslationState.Failed))throw new InvalidDataException("Product rejected one or more blocks");
            }
            catch(Exception ex){failure=ex.GetType().FullName??"Error";failureMessage=SafeDiagnosticOutput.Redact(ex.Message);failed++;}
            finally
            {
                timer.Stop();form.CancelTranslationForSmoke();
                Save(Path.Combine(folder,"PRIVATE-PRODUCTION-BOUNDARIES.json"),new{SchemaVersion=2,
                    Provenance="Actual default-off hooks; checkpoint before worker shutdown; output copies redacted, no Authorization headers",Events=events.ToArray()});
                try
                {
                    context.Finish(runtime.DisposeAsync().AsTask(),TimeSpan.FromSeconds(30));
                    context.Finish(vision.DisposeAsync().AsTask(),TimeSpan.FromSeconds(30));
                }
                catch(Exception ex){if(failure.Length==0)failed++;failure="DiagnosticCleanup:"+ex.GetType().Name;}
                SynchronizationContext.SetSynchronizationContext(previous);
            }
            var captured=events.ToArray();
            Save(Path.Combine(folder,"PRIVATE-PRODUCTION-BOUNDARIES.json"),new{SchemaVersion=2,
                Provenance="Actual default-off product hooks; output copies redacted before observation; no Authorization headers",Events=captured});
            var requestCount=captured.Count(x=>x.Stage=="RequestBody");totalRequests+=requestCount;
            long promptTokens=0,completionTokens=0,totalTokens=0;
            foreach(var body in captured.Where(x=>x.Stage=="ProductionRawCaptured"))
            {
                try{using var doc=JsonDocument.Parse(body.Value);if(doc.RootElement.TryGetProperty("usage",out var usage))
                    {promptTokens+=usage.TryGetProperty("prompt_tokens",out var p)?p.GetInt64():0;completionTokens+=usage.TryGetProperty("completion_tokens",out var c)?c.GetInt64():0;totalTokens+=usage.TryGetProperty("total_tokens",out var t)?t.GetInt64():0;}}
                catch(JsonException){}
            }
            var statuses=captured.Where(x=>x.Stage=="HttpStatus").Select(x=>int.Parse(x.Value)).ToArray();
            var rejectedBlocks=core?.Translations.Values.Count(x=>x.State==BlockTranslationState.Failed)??0;
            var semanticRejection=IsVerifiedSemanticRejection(failure,failureMessage,rejectedBlocks,pixelDifference,requestCount,statuses);
            if(semanticRejection)semanticRejectedImages++;
            var auditStopReason=AuditStopReason(completeSemanticCorpus,failed,semanticRejectedImages,statuses);
            rows.Add(new{input.Id,BuildIdentity.BuildId,CodeHash=codeHash,SourceSHA256=input.SourceSHA256,
                SourceManifestSHA256=sourceManifestHash,ContentContract=CoreTranslationContentValidator.ContractVersion,
                Provider=settings.ProviderDisplayName,Model=settings.Model,Endpoint=captured.FirstOrDefault(x=>x.Stage=="TransportEndpoint")?.Value,
                Prompt=TranslationPromptBuilder.Version,OcrContextPromptVersion=captured.Any(x=>x.Stage=="OcrContextPrompt")?TranslationPromptBuilder.OcrContextVersion:null,
                OcrContextBlocks=captured.Where(x=>x.Stage=="OcrContextPrompt").Select(x=>x.BlockId).Distinct().Count(),
                Target=settings.TargetLanguage,Images=1,FreshOcr=observedOcr is not null&&input.OcrReplayPath is null,
                RunMode=input.HistorySnapshotPath is not null?"CURRENT_REGENERATED":"FRESH_CURRENT_PRODUCT",
                HistorySnapshotPath=input.HistorySnapshotPath,
                HistorySnapshotUnchanged=input.HistorySnapshotPath is null||(Hash(input.HistorySnapshotPath)==input.HistorySnapshotSHA256),
                OcrReplay=input.OcrReplayPath is not null,HTTPRequests=requestCount,
                InitialHttpRequests=captured.Count(x=>x.Stage=="RequestMode"&&x.Value=="segments"),
                RecoveryHttpRequests=captured.Count(x=>x.Stage=="RequestMode"&&x.Value!="segments"),
                TransportRetries=0,RequestAccounting="Initial + Recovery = HTTP; no inner transport retries",CacheHits=0,
                ContentRecoveries=captured.Count(x=>x.Stage=="ContentRecoveredAndRevalidated"),ContentRejectedResponses=captured.Count(x=>x.Stage=="ContentRejected"),
                FitPreservedBlocks=renderAudits?.Count(x=>x.Result=="FIT_FAILED_CONTENT_PRESERVED")??0,ActualDrawnBlocks=renderAudits?.Count(x=>x.TextDrawn)??0,
                AcceptedButNotDrawn=renderAudits?.Count(x=>x.TranslationState==BlockTranslationState.Accepted&&!x.TextDrawn)??0,
                RejectedDetails=core?.Translations.Values.Where(x=>x.State==BlockTranslationState.Failed).Select(x=>new{x.BlockId,x.FailureReason}).ToArray(),
                AcceptedBlocks=core?.Translations.Values.Count(x=>x.State==BlockTranslationState.Accepted)??0,
                RejectedBlocks=core?.Translations.Values.Count(x=>x.State==BlockTranslationState.Failed)??0,
                Failed=failure.Length>0,FailureType=failure,FailureMessage=failureMessage,OCRFailureType=ocrFailure,HttpStatuses=statuses,
                CompleteSemanticCorpus=completeSemanticCorpus,VerifiedSemanticRejection=semanticRejection,AuditStopReason=auditStopReason,
                PromptTokens=promptTokens,CompletionTokens=completionTokens,TotalTokens=totalTokens,
                CompactDiagnostics=compactDiagnostics,DiagnosticsMode=diagnosticsMode,DiagnosticsEnabled=!compactDiagnostics,
                IndependentRenderPath=independentRenderPath,IndependentRenderTiming=independentRenderTiming,
                ActualPreviewVsDiagnosticChangedPixels=pixelDifference,ElapsedMs=timer.ElapsedMilliseconds,Output=folder});
            Save(Path.Combine(output,"API-RUN-LEDGER.json"),new{Purpose=roundPurpose,ImagesCompleted=rows.Count,ExpectedImages=inputs.Length,
                HTTPRequests=totalRequests,FailedImages=failed,VerifiedSemanticRejectedImages=semanticRejectedImages,
                NonSemanticFailedImages=failed-semanticRejectedImages,CompleteSemanticCorpus=completeSemanticCorpus,
                SemanticValidationBypassed=false,AuditStopReason=auditStopReason,
                CompactDiagnostics=compactDiagnostics,DiagnosticsMode=diagnosticsMode,
                DiagnosticsEnabled=!compactDiagnostics,IndependentRenderPath=independentRenderPath,
                Rows=rows,UserVisualAcceptance="NOT_PASSED",Publishing="NOT_AUTHORIZED"});
            Console.WriteLine($"PRODUCTION END {input.Id} requests={requestCount} failure={failure}");
            if(auditStopReason.Length>0)break;
        }
        return rows.Count==inputs.Length&&failed==0?0:1;
    }

    // This policy only controls a bounded private audit. Failed product blocks
    // remain failed, keep their original response, and are never retried here.
    internal static bool AuditInputCountAllowed(bool completeSemanticCorpus,int count)=>
        count>0&&(!completeSemanticCorpus||count<=37);

    internal static bool IsVerifiedSemanticRejection(string failure,string message,int rejectedBlocks,long changedPixels,int requests,IReadOnlyList<int> statuses)=>
        failure=="System.IO.InvalidDataException"&&message=="Product rejected one or more blocks"&&
        rejectedBlocks>0&&changedPixels==0&&requests>0&&statuses.Count==requests&&statuses.All(x=>x==200);

    internal static string AuditStopReason(bool completeSemanticCorpus,int failures,int semanticRejections,IReadOnlyList<int> statuses)
    {
        if(statuses.Any(x=>x is 401 or 403))return "AUTHENTICATION_OR_AUTHORIZATION";
        if(statuses.Contains(429))return "RATE_LIMIT";
        if(failures<0||semanticRejections<0||semanticRejections>failures)return "INVALID_AUDIT_COUNTERS";
        var boundedFailures=completeSemanticCorpus?failures-semanticRejections:failures;
        return boundedFailures>=2?"BOUNDED_NONSEMANTIC_OR_DEFAULT_FAILURES":"";
    }

    private sealed class PumpContext:SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback,object? State)> _queue=new();
        public override void Post(SendOrPostCallback callback,object? state)=>_queue.Enqueue((callback,state));
        internal void Finish(Task task,TimeSpan timeout)
        {
            var timer=Stopwatch.StartNew();
            while(!task.IsCompleted)
            {
                if(timer.Elapsed>timeout)throw new TimeoutException("Bounded hidden component operation timed out");
                if(_queue.TryDequeue(out var work))work.Callback(work.State);else Thread.Sleep(2);
            }
            task.GetAwaiter().GetResult();
        }
    }
}
