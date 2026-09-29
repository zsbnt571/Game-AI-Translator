using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static class MegaVisualFidelityE2EHarness
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    internal static async Task<int> RunAsync(string outputRoot, string pairingManifestPath,
        string settingsPath, string applicationRoot, string runLabel, string? replayRoot = null,
        string? pairFilter = null, string? r2ModeText = null,string? translationOverridesPath=null)
    {
        Directory.CreateDirectory(outputRoot);
        var inputs = JsonSerializer.Deserialize<List<PairingInput>>(
            File.ReadAllText(pairingManifestPath), JsonOptions) ?? [];
        if (inputs.Count != 37 || inputs.Count(x => x.Set == "OLD") != 18 || inputs.Count(x => x.Set == "NEW") != 19)
            throw new InvalidDataException("Mega R1 requires exact OLD-18 + NEW-19 pairing identity.");
        if(!string.IsNullOrWhiteSpace(pairFilter))
        {
            var requested=pairFilter.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            inputs=inputs.Where(x=>requested.Contains(x.PairId)).ToList();
            if(inputs.Count!=requested.Count)throw new InvalidDataException("Pair filter contains an unknown identity.");
        }

        var settings = ConfigurationManager.Load(settingsPath, false);
        var r2Mode=Enum.TryParse<SourceStyleRenderModeR2>(r2ModeText,true,out var parsedR2Mode)
            ?parsedR2Mode:SourceStyleRenderModeR2.Integrated;
        if (!string.IsNullOrWhiteSpace(replayRoot))
            return RunReplay(outputRoot, inputs, settings, settingsPath, runLabel, replayRoot,r2Mode,
                translationOverridesPath);
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("DeepSeek API is not configured.");
        settings.OcrEngine = OcrEngineKind.Rapid;
        settings.VisualModel = VisualModelKind.Off;
        settings.TargetLanguage = "zh-CN";
        settings.TranslationCacheEnabled = false;
        SaveRedactedSettings(settings, settingsPath, outputRoot, runLabel);

        var summaryRows = new List<string[]>();
        var ocrRows = new List<string[]>();
        var translationRows = new List<string[]>();
        var materialRows = new List<string[]>();
        var allPass = true;
        long generation = 0;
        await using var runtime = new OcrRuntimeManager(applicationRoot, new OcrService());

        foreach (var input in inputs.OrderBy(x => x.PairId, StringComparer.Ordinal))
        {
            Console.WriteLine($"MEGA {runLabel} BEGIN {input.PairId}");
            var fixtureRoot = Path.Combine(outputRoot, "fixtures", input.PairId);
            Directory.CreateDirectory(fixtureRoot);
            using var source = new Bitmap(input.ProductInputPath);
            source.Save(Path.Combine(fixtureRoot, "SOURCE.png"), ImageFormat.Png);

            var total = Stopwatch.StartNew();
            var ocrTimer = Stopwatch.StartNew();
            OcrEngineResult ocr;
            try
            {
                ocr = await runtime.RecognizeWithFallbackAsync(OcrEngineKind.Rapid, source, "英语",
                    CancellationToken.None, requestId: $"mega-{runLabel}-{input.PairId}",
                    imageSessionId: $"mega-{runLabel}-{generation++:000}", allowAutomaticWindowsFallback: false);
            }
            catch (Exception ex)
            {
                SaveException(Path.Combine(fixtureRoot, "OCR-FAIL.json"), ex);
                summaryRows.Add([input.PairId,input.Set,"0","0","0","0","0","0","0","0","0","0","0","0","FAIL_OCR"]);
                allPass = false;
                continue;
            }
            ocrTimer.Stop();
            File.WriteAllText(Path.Combine(fixtureRoot, "PRODUCT-OCR-REPLAY.json"),
                JsonSerializer.Serialize(ocr, JsonOptions));
            if (!string.IsNullOrWhiteSpace(ocr.StylizedTitleRecoveryTraceJson))
                File.WriteAllText(Path.Combine(fixtureRoot, "TITLE-RECOVERY-PROPOSAL-TRACE.json"),
                    ocr.StylizedTitleRecoveryTraceJson);

            var coreTimer = Stopwatch.StartNew();
            var document = BuildCore(ocr, source.Size);
            coreTimer.Stop();
            CorePipelineCorpusRunner.SaveStructureImages(source, document, fixtureRoot);

            var translationTimer = Stopwatch.StartNew();
            TranslationBatchResult? response = null;
            TranslationRecoveryStats? failedRecovery = null;
            Exception? apiFailure = null;
            var httpStatuses = new List<int>();
            var translatable = document.VisualBlocks
                .Where(x => x.TextSelection == TextSelectionAction.Translate).ToArray();
            if (translatable.Length > 0)
            {
                try
                {
                    var provider = CorePipelineV2DeepSeekRunner.CreateProductionProvider(new TranslationService(), settings);
                    var progress = new CaptureProgress<TranslationProgress>(x =>
                    {
                        if (x.HttpStatusCode.HasValue) httpStatuses.Add(x.HttpStatusCode.Value);
                    });
                    response = await provider.TranslateAsync(translatable.Select(ToTranslationItem).ToArray(),
                        settings, progress, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    apiFailure = ex;
                    if(ex is TranslationRecoveryFailedException recoveryFailure)
                        failedRecovery=recoveryFailure.RecoveryStats;
                    SaveException(Path.Combine(fixtureRoot, "REAL-API-FAIL.json"), ex);
                }
            }
            translationTimer.Stop();

            foreach (var block in document.VisualBlocks)
            {
                var state = document.Translations[block.BlockId];
                if (block.TextSelection == TextSelectionAction.Preserve)
                {
                    state.TranslatedText = block.SourceText;
                    state.State = BlockTranslationState.Preserved;
                    state.FailureReason = block.TextSelectionReason;
                }
                else if (response?.Translations.TryGetValue(block.BlockId, out var translated) == true &&
                         !string.IsNullOrWhiteSpace(translated) &&
                         CorePipelineEngine.NumericTokensMatch(block.SourceText, translated))
                {
                    state.TranslatedText = translated.Trim();
                    state.State = BlockTranslationState.Accepted;
                    state.FailureReason = "";
                }
                else
                {
                    state.TranslatedText = response?.Translations.GetValueOrDefault(block.BlockId, "").Trim() ?? "";
                    state.State = BlockTranslationState.Failed;
                    state.FailureReason = apiFailure?.GetType().Name ??
                        (string.IsNullOrWhiteSpace(state.TranslatedText) ? "MISSING_BLOCK_TRANSLATION" : "NUMERIC_FIDELITY_MISMATCH");
                }
                translationRows.Add([input.PairId,block.BlockId,string.Join('|',block.Lines.Select(x=>x.SourceId)),
                    block.RoleHint,block.SourceText,state.TranslatedText,state.State.ToString(),state.FailureReason]);
            }
            var publishedUtc = DateTimeOffset.UtcNow;
            File.WriteAllText(Path.Combine(fixtureRoot, "TRANSLATED-TEXT.json"), JsonSerializer.Serialize(new
            {
                PublishedBeforeRenderer = true,
                PublishedUtc = publishedUtc,
                TranslationOwner = "CURRENT_CORE_BLOCKID",
                IdentityContract = TranslationIdentityContract.CoreV2Block.ToString(),
                AiAllocation = "NOT_USED",
                Items = document.VisualBlocks.Select(block => new
                {
                    block.BlockId,
                    SourceIds = block.Lines.Select(x => x.SourceId),
                    block.SourceText,
                    document.Translations[block.BlockId].TranslatedText,
                    State = document.Translations[block.BlockId].State.ToString(),
                    document.Translations[block.BlockId].FailureReason
                })
            }, JsonOptions));

            var renderTimer = Stopwatch.StartNew();
            var productRender = CorePipelineCorpusRunner.RenderProduct(source, document,
                FontManager.ResolveTranslationImageProfile(settings),r2Mode);
            using var productBitmap=productRender.Bitmap;
            renderTimer.Stop();
            productBitmap.Save(Path.Combine(fixtureRoot,"07-FINAL-TRANSLATED.png"),ImageFormat.Png);
            var previewTimer=Stopwatch.StartNew();
            var preview=CaptureActualPreviewOnSta(source,productBitmap,document,settings,fixtureRoot);
            previewTimer.Stop();
            total.Stop();

            var audits=productRender.Audits;
            var accepted = document.Translations.Values.Count(x => x.State == BlockTranslationState.Accepted);
            var preserved = document.Translations.Values.Count(x => x.State == BlockTranslationState.Preserved);
            var failed = document.Translations.Values.Count(x => x.State == BlockTranslationState.Failed);
            var committed = audits.Count(x => x.AtomicCommit);
            var fitPreserved = audits.Count(x => x.Result.Contains("FIT_FAILED", StringComparison.Ordinal));
            var status = apiFailure is null && failed == 0 && preview.CurrentPixelExact &&
                         !productRender.Timing.DiagnosticsEnabled && productRender.Timing.FullFrameDiagnosticPngWrites==0 &&
                         productRender.Timing.DiagnosticJsonWrites==0 ? "PASS" : "FAIL";
            allPass &= status == "PASS";
            summaryRows.Add([input.PairId,input.Set,source.Width.ToString(),source.Height.ToString(),
                ocr.Blocks.Count.ToString(),document.VisualBlocks.Count.ToString(),translatable.Length.ToString(),
                accepted.ToString(),preserved.ToString(),failed.ToString(),
                (response?.RequestCount??failedRecovery?.TotalRequestCount??0).ToString(),
                ocrTimer.ElapsedMilliseconds.ToString(),translationTimer.ElapsedMilliseconds.ToString(),
                renderTimer.ElapsedMilliseconds.ToString(),status,
                productRender.Timing.StyleMs.ToString("F3",System.Globalization.CultureInfo.InvariantCulture),
                productRender.Timing.BackgroundMs.ToString("F3",System.Globalization.CultureInfo.InvariantCulture),
                productRender.Timing.LayoutMs.ToString("F3",System.Globalization.CultureInfo.InvariantCulture),
                productRender.Timing.DrawMs.ToString("F3",System.Globalization.CultureInfo.InvariantCulture),
                productRender.Timing.EncodeMs.ToString("F3",System.Globalization.CultureInfo.InvariantCulture),
                previewTimer.ElapsedMilliseconds.ToString(),preview.CurrentPixelExact.ToString(),
                preview.CurrentDifferentPixels.ToString()]);
            ocrRows.Add([input.PairId,ocr.EngineActual.ToString(),ocr.Blocks.Count.ToString(),
                ocr.StylizedTitleRecoveryChecked.ToString(),ocr.StylizedTitleRecoveryTriggered.ToString(),
                ocr.StylizedTitleRecoveryAcceptedCandidateCount.ToString(),
                ocr.StylizedTitleRecoveryLocalRecognitionCount.ToString(),
                ocr.StylizedTitleRecoveryMilliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture),
                ocr.RawText.Replace("\r", " ").Replace("\n", " | ")]);
            materialRows.Add([input.PairId,"BASELINE_PRODUCT_VISUAL","USER_REVIEW_REQUIRED",
                committed.ToString(),fitPreserved.ToString(),"NOT_AUTO_ACCEPTED"]);
            File.WriteAllText(Path.Combine(fixtureRoot, "PRODUCT-REAL-E2E-SUMMARY.json"), JsonSerializer.Serialize(new
            {
                input.PairId,input.Set,input.SourcePath,input.ReferencePath,input.ProductInputPath,
                SourceFormat=input.SourceFormat,SourceWidth=source.Width,SourceHeight=source.Height,
                OcrEngineRequested="Rapid",OcrEngineActual=ocr.EngineActual.ToString(),VisualModel="Off",
                OcrBlocks=ocr.Blocks.Count,CoreBlocks=document.VisualBlocks.Count,TranslationItems=translatable.Length,
                Accepted=accepted,Preserved=preserved,Failed=failed,FidelityRejects=failed,
                AtomicCommits=committed,FitPreserved=fitPreserved,
                ApiRequests=response?.RequestCount??failedRecovery?.TotalRequestCount??0,
                ApiWaitMs=response?.WaitMs??0,HttpStatuses=httpStatuses.Distinct(),
                PromptTokens=response?.TransportStats?.PromptTokens??0,
                CompletionTokens=response?.TransportStats?.CompletionTokens??0,
                TotalTokens=response?.TransportStats?.TotalTokens??0,
                HttpFailures=response?.TransportStats?.HttpFailureCount??0,
                TransportFailures=response?.TransportStats?.TransportFailureCount??0,
                TranslationRetries=response?.RecoveryStats?.RetryRequestCount??failedRecovery?.RetryRequestCount??0,
                TranslationBatchCount=response?.RequestCount??failedRecovery?.TotalRequestCount??0,
                OcrMs=ocrTimer.ElapsedMilliseconds,CoreMs=coreTimer.ElapsedMilliseconds,
                TranslationMs=translationTimer.ElapsedMilliseconds,RenderMs=renderTimer.ElapsedMilliseconds,
                RendererStyleMs=productRender.Timing.StyleMs,RendererBackgroundMs=productRender.Timing.BackgroundMs,
                RendererLayoutMs=productRender.Timing.LayoutMs,RendererDrawMs=productRender.Timing.DrawMs,
                RendererEncodeMs=productRender.Timing.EncodeMs,RendererDiagnosticsEnabled=productRender.Timing.DiagnosticsEnabled,
                RendererDiagnosticPngWrites=productRender.Timing.FullFrameDiagnosticPngWrites,
                RendererDiagnosticJsonWrites=productRender.Timing.DiagnosticJsonWrites,
                PreviewCommitMs=previewTimer.ElapsedMilliseconds,preview.CurrentPixelExact,preview.CurrentDifferentPixels,
                preview.SourcePixelExact,preview.SourceDifferentPixels,preview.SurfaceSize,preview.DisplayScale,
                TotalMs=total.ElapsedMilliseconds,EarlyPublishOrder=true,VisionInferenceCount=0,
                OcrBoundaryStatus=ocr.BoundaryFastPathStatus,OcrBoundaryTriggered=ocr.BoundaryDeepAnalysisCount>0,
                OcrBoundaryDeepAnalysisCount=ocr.BoundaryDeepAnalysisCount,
                OcrBoundaryHighProposalCount=ocr.BoundaryHighProposalCount,
                OcrBoundaryReOcrLineCount=ocr.BoundaryReOcrLineCount,
                OcrBoundaryCropCallCount=ocr.BoundaryCropCallCount,
                OcrBoundaryChangedLineCount=ocr.BoundaryChangedLineCount,
                OcrBoundaryMs=ocr.BoundaryFastPathMilliseconds,
                MicrotextStatus="RESEARCH_NOT_PRODUCT",MicrotextTriggered=false,
                BackgroundPath="CURRENT_R2_INTEGRATED_WITH_SOURCE_PRESERVING_SAFETY_FALLBACK",
                StructuralStatus="PASS",ApiStatus=status,UserVisualStatus="NOT_TESTED",
                ApiKeyRecorded=false,AuthorizationRecorded=false,Candidate="NO"
            }, JsonOptions));
            Console.WriteLine($"MEGA {runLabel} END {input.PairId} ocr={ocr.Blocks.Count} core={document.VisualBlocks.Count} " +
                              $"items={translatable.Length} failed={failed} totalMs={total.ElapsedMilliseconds}");
        }

        WriteCsv(Path.Combine(outputRoot, "RESULTS.csv"),
            ["PairId","Set","Width","Height","OcrBlocks","CoreBlocks","TranslationItems","Accepted","Preserved","Failed","ApiRequests","OcrMs","TranslationMs","RenderMs","Status","StyleMs","BackgroundMs","LayoutMs","DrawMs","EncodeMs","PreviewCommitMs","PreviewPixelExact","PreviewDifferentPixels"], summaryRows);
        WriteCsv(Path.Combine(outputRoot, "OCR-COVERAGE.csv"),
            ["PairId","Engine","OcrBlocks","TitleRecoveryChecked","TitleRecoveryTriggered","TitleRecoveryAccepted","LocalRecognitionCalls","TitleRecoveryMs","RecognizedText"], ocrRows);
        WriteCsv(Path.Combine(outputRoot, "TRANSLATION-COVERAGE.csv"),
            ["PairId","BlockId","SourceIds","RoleHint","SourceText","Translation","State","FailureReason"], translationRows);
        WriteCsv(Path.Combine(outputRoot, "MATERIAL-REGRESSIONS.csv"),
            ["PairId","Scope","VisualStatus","AtomicCommits","FitPreserved","AutomatedAcceptance"], materialRows);
        File.WriteAllText(Path.Combine(outputRoot, "RUN-SUMMARY.json"), JsonSerializer.Serialize(new
        {
            RunLabel=runLabel,Images=inputs.Count,Old=inputs.Count(x=>x.Set=="OLD"),New=inputs.Count(x=>x.Set=="NEW"),
            RealDeepSeek=true,Provider=settings.ProviderDisplayName,settings.Model,settings.TargetLanguage,
            OcrEngine="Rapid",VisualModel="Off",Cache=false,
            ApiRequests=summaryRows.Sum(x=>int.Parse(x[10])),FailedImages=summaryRows.Count(x=>x[14]!="PASS"),
            ApiKeyRecorded=false,AuthorizationRecorded=false,Candidate="NO",Status=allPass?"PASS":"FAIL"
        }, JsonOptions));
        return allPass ? 0 : 12;
    }

    private static int RunReplay(string outputRoot,IReadOnlyList<PairingInput> inputs,ApiSettings settings,
        string settingsPath,string runLabel,string replayRoot,SourceStyleRenderModeR2 r2Mode,
        string? translationOverridesPath)
    {
        settings.OcrEngine=OcrEngineKind.Rapid;settings.VisualModel=VisualModelKind.Off;
        settings.TargetLanguage="zh-CN";settings.TranslationCacheEnabled=false;
        SaveRedactedSettings(settings,settingsPath,outputRoot,runLabel);
        var rows=new List<string[]>();var blockIdsStable=true;var preExistingTranslationFailures=0;
        var overrides=LoadTranslationOverrides(translationOverridesPath);
        var compactDiagnostics=Environment.GetEnvironmentVariable("ST_FIX_REPLAY_DIAGNOSTIC_MODE")=="compact";
        foreach(var input in inputs.OrderBy(x=>x.PairId,StringComparer.Ordinal))
        {
            var prior=Path.Combine(replayRoot,"fixtures",input.PairId);
            var fixture=Path.Combine(outputRoot,"fixtures",input.PairId);Directory.CreateDirectory(fixture);
            using var source=new Bitmap(input.ProductInputPath);source.Save(Path.Combine(fixture,"SOURCE.png"),ImageFormat.Png);
            var ocr=JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(Path.Combine(prior,"PRODUCT-OCR-REPLAY.json")),JsonOptions)
                ?? throw new InvalidDataException($"Invalid OCR replay for {input.PairId}");
            var document=BuildCore(ocr,source.Size);
            using var translated=JsonDocument.Parse(File.ReadAllText(Path.Combine(prior,"TRANSLATED-TEXT.json")));
            var replayItems=translated.RootElement.GetProperty("Items").EnumerateArray()
                .ToDictionary(x=>x.GetProperty("BlockId").GetString()??"",x=>x,StringComparer.Ordinal);
            overrides.TryGetValue(input.PairId,out var fixtureOverride);
            var currentBlockIds=document.VisualBlocks.Select(x=>x.BlockId).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            var replayBlockIds=replayItems.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            var preservedCurrentIds=document.VisualBlocks.Where(x=>x.TextSelection==TextSelectionAction.Preserve)
                .Select(x=>x.BlockId).ToHashSet(StringComparer.Ordinal);
            var preservedCurrentSourceIds=document.VisualBlocks.Where(x=>x.TextSelection==TextSelectionAction.Preserve)
                .SelectMany(x=>x.Lines.Select(line=>line.SourceId)).ToHashSet(StringComparer.Ordinal);
            var coveredCurrent=currentBlockIds.All(id=>preservedCurrentIds.Contains(id)||replayItems.ContainsKey(id)||
                (fixtureOverride?.Items.ContainsKey(id)??false));
            var obsoleteReplay=replayBlockIds.Where(id=>!currentBlockIds.Contains(id,StringComparer.Ordinal))
                .Where(id=>!IsReplayPreserveRedistribution(replayItems[id],preservedCurrentSourceIds))
                .OrderBy(id=>id,StringComparer.Ordinal).ToArray();
            var declaredReplacements=(fixtureOverride?.Replaces??[]).OrderBy(id=>id,StringComparer.Ordinal).ToArray();
            var fixtureBlockIdsStable=coveredCurrent&&obsoleteReplay.SequenceEqual(declaredReplacements,StringComparer.Ordinal);
            blockIdsStable&=fixtureBlockIdsStable;
            foreach(var block in document.VisualBlocks)
            {
                var state=document.Translations[block.BlockId];
                if(block.TextSelection==TextSelectionAction.Preserve)
                {
                    state.TranslatedText=block.SourceText;
                    state.State=BlockTranslationState.Preserved;
                    state.FailureReason=block.TextSelectionReason;
                    continue;
                }
                if(!replayItems.TryGetValue(block.BlockId,out var item))
                {
                    if(fixtureOverride?.Items.TryGetValue(block.BlockId,out var overrideText)==true&&
                       !string.IsNullOrWhiteSpace(overrideText))
                    {
                        state.TranslatedText=overrideText.Trim();state.State=BlockTranslationState.Accepted;
                        state.FailureReason="TEST_ONLY_TRANSLATION_OVERRIDE_FOR_INTENTIONAL_GROUP_SPLIT";continue;
                    }
                    state.State=BlockTranslationState.Failed;state.FailureReason="REPLAY_BLOCK_ID_MISMATCH";continue;
                }
                state.TranslatedText=item.GetProperty("TranslatedText").GetString()??"";
                var priorState=item.GetProperty("State").GetString()??"Failed";
                state.State=Enum.TryParse<BlockTranslationState>(priorState,true,out var parsed)?parsed:BlockTranslationState.Failed;
                state.FailureReason=state.State==BlockTranslationState.Failed?"REPLAY_PRIOR_FAILED":"";
            }
            var timer=Stopwatch.StartNew();
            IReadOnlyList<BlockRenderAudit> audits;
            if(compactDiagnostics)
            {
                var captureBackground=Environment.GetEnvironmentVariable("ST_BACKGROUND_COMPACT_CAPTURE")=="1";
                var rendered=CorePipelineCorpusRunner.RenderProduct(source,document,
                    FontManager.ResolveTranslationImageProfile(settings),r2Mode,
                    backgroundDiagnostics:captureBackground?fixture:null);
                timer.Stop();
                using(var bitmap=rendered.Bitmap)
                    bitmap.Save(Path.Combine(fixture,"07-FINAL-TRANSLATED.png"),ImageFormat.Png);
                audits=rendered.Audits;
                File.WriteAllText(Path.Combine(fixture,"PRODUCT-OCR-REPLAY.json"),JsonSerializer.Serialize(ocr,JsonOptions));
                var compactOptions=new JsonSerializerOptions(JsonOptions);
                compactOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                File.WriteAllText(Path.Combine(fixture,"TRANSLATED-TEXT.json"),JsonSerializer.Serialize(new
                {Items=document.Translations.Values,Audits=audits,FixedReplay=true,RealApiCalls=0},compactOptions));
                File.WriteAllText(Path.Combine(fixture,"RENDER-SUMMARY.json"),JsonSerializer.Serialize(new
                {Mode="compact",rendered.Timing,Audits=audits},compactOptions));
                if(!captureBackground && (rendered.Timing.DiagnosticsEnabled||rendered.Timing.FullFrameDiagnosticPngWrites!=0||
                   rendered.Timing.DiagnosticJsonWrites!=0))
                    throw new InvalidDataException("Compact replay unexpectedly wrote expensive renderer diagnostics.");
            }
            else
            {
                CorePipelineCorpusRunner.SaveStructureImages(source,document,fixture);
                audits=CorePipelineCorpusRunner.Render(source,document,fixture,
                    FontManager.ResolveTranslationImageProfile(settings),r2Mode);
                timer.Stop();
            }
            var failed=document.Translations.Values.Count(x=>x.State==BlockTranslationState.Failed);
            preExistingTranslationFailures+=failed;
            rows.Add([input.PairId,input.Set,ocr.Blocks.Count.ToString(),document.VisualBlocks.Count.ToString(),
                document.Translations.Values.Count(x=>x.State==BlockTranslationState.Accepted).ToString(),
                document.Translations.Values.Count(x=>x.State==BlockTranslationState.Preserved).ToString(),failed.ToString(),
                audits.Count(x=>x.AtomicCommit).ToString(),timer.ElapsedMilliseconds.ToString(),"0",
                !fixtureBlockIdsStable?"BLOCK_ID_DRIFT":failed==0?"PASS":"PREEXISTING_TRANSLATION_FAILURE"]);
        }
        WriteCsv(Path.Combine(outputRoot,"RESULTS.csv"),
            ["PairId","Set","OcrBlocks","CoreBlocks","Accepted","Preserved","Failed","AtomicCommits","RenderMs","RealApiCalls","Status"],rows);
        File.WriteAllText(Path.Combine(outputRoot,"RUN-SUMMARY.json"),JsonSerializer.Serialize(new
        {
            RunLabel=runLabel,Images=inputs.Count,ReplaySource=Path.GetFullPath(replayRoot),RealDeepSeek=false,
            RealApiCalls=0,CoreBlockIdContract=blockIdsStable?"PASS":"FAIL",
            PreExistingTranslationFailures=preExistingTranslationFailures,Candidate="NO",TranslationOverrides=overrides.Count,
            DiagnosticMode=compactDiagnostics?"compact":"full",
            Status=blockIdsStable?"PASS_WITH_BASELINE_TRANSLATION_STATUS":"FAIL_BLOCK_ID_DRIFT"
        },JsonOptions));
        return blockIdsStable?0:13;
    }

    private sealed record ReplayTranslationOverride(string[] Replaces,Dictionary<string,string> Items);
    private sealed record ReplayTranslationOverrideFile(Dictionary<string,ReplayTranslationOverride> Pairs);

    private static bool IsReplayPreserveRedistribution(JsonElement replayItem,IReadOnlySet<string> preservedCurrentSourceIds)
    {
        if(!replayItem.TryGetProperty("State",out var state)||
           !string.Equals(state.GetString(),nameof(BlockTranslationState.Preserved),StringComparison.OrdinalIgnoreCase)||
           !replayItem.TryGetProperty("SourceIds",out var sourceIds)||sourceIds.ValueKind!=JsonValueKind.Array)
            return false;
        var ids=sourceIds.EnumerateArray().Select(x=>x.GetString()).Where(x=>!string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
        return ids.Length>0&&ids.All(preservedCurrentSourceIds.Contains);
    }

    private static Dictionary<string,ReplayTranslationOverride> LoadTranslationOverrides(string? path)
    {
        if(string.IsNullOrWhiteSpace(path))return new(StringComparer.Ordinal);
        var parsed=JsonSerializer.Deserialize<ReplayTranslationOverrideFile>(File.ReadAllText(path),JsonOptions)
            ??throw new InvalidDataException("Invalid translation override file.");
        return new Dictionary<string,ReplayTranslationOverride>(parsed.Pairs,StringComparer.Ordinal);
    }

    internal static CorePipelineDocument BuildCore(OcrEngineResult result, Size size)
    {
        var raw = result.Blocks.Where(x => x.Enabled &&
                     (!string.IsNullOrWhiteSpace(x.CorrectedText) || !string.IsNullOrWhiteSpace(x.RawText)))
            .OrderBy(x => x.ReadingOrder).ThenBy(x => x.BoundingBox.Top).ThenBy(x => x.BoundingBox.Left)
            .Select(x => new RawOcrLine(x.Id,x.RawText,
                string.IsNullOrWhiteSpace(x.CorrectedText)?x.RawText:x.CorrectedText,
                x.Polygon.Length>=3?x.Polygon.ToArray():RectanglePolygon(x.BoundingBox),
                x.BoundingBox,x.Confidence??0,x.ReadingOrder)).ToArray();
        return CorePipelineEngine.Analyze(size,raw);
    }

    private static TranslationItem ToTranslationItem(VisualBlock block) =>
        new(block.BlockId, block.SourceText, MapRole(block.RoleHint),
            block.Lines.Select(x=>x.SourceId).ToArray(), TranslationIdentityContract.CoreV2Block);

    private static StructuredTextRole MapRole(string role) =>
        Enum.TryParse<StructuredTextRole>(role,true,out var parsed) ? parsed : StructuredTextRole.Unknown;

    private static PointF[] RectanglePolygon(RectangleF rectangle) =>
        [new(rectangle.Left,rectangle.Top),new(rectangle.Right,rectangle.Top),
         new(rectangle.Right,rectangle.Bottom),new(rectangle.Left,rectangle.Bottom)];

    private static void SaveRedactedSettings(ApiSettings settings,string settingsPath,string output,string runLabel)
    {
        File.WriteAllText(Path.Combine(output,"SETTINGS-REDACTED.json"),JsonSerializer.Serialize(new
        {
            RunLabel=runLabel,Provider=settings.ProviderDisplayName,settings.ApiUrl,settings.Model,
            settings.TargetLanguage,TranslationStyle=settings.TranslationStyle.ToString(),
            OcrEngine="Rapid",VisualModel="Off",TranslationCacheEnabled=false,
            SettingsPathHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(settingsPath)))),
            ApiKeyPresent=!string.IsNullOrWhiteSpace(settings.ApiKey),ApiKeyRecorded=false,AuthorizationRecorded=false
        },JsonOptions));
    }

    private static void SaveException(string path,Exception ex)
    {
        var chain=new List<object>();
        for(Exception? current=ex;current is not null;current=current.InnerException)
            chain.Add(new{ErrorType=current.GetType().FullName,current.Message,HResult=$"0x{current.HResult:X8}"});
        File.WriteAllText(path,JsonSerializer.Serialize(new{Errors=chain,ApiKeyRecorded=false,AuthorizationRecorded=false},JsonOptions));
    }

    private static PreviewCaptureResult CaptureActualPreviewOnSta(Bitmap source,Bitmap rendered,
        CorePipelineDocument document,ApiSettings settings,string fixtureRoot)
    {
        PreviewCaptureResult? result=null;Exception? failure=null;
        using var sourceCopy=new Bitmap(source);using var renderedCopy=new Bitmap(rendered);
        var thread=new Thread(() =>
        {
            try
            {
                var previewSettings=JsonSerializer.Deserialize<ApiSettings>(JsonSerializer.Serialize(settings,JsonOptions),JsonOptions)
                    ?? throw new InvalidOperationException("Unable to clone preview settings.");
                previewSettings.VisualModel=VisualModelKind.Off;previewSettings.PreviewAlwaysOnTop=false;
                previewSettings.PreviewTextPanelVisible=false;previewSettings.PreviewDefaultText=PreviewDefaultText.Hidden;
                previewSettings.PreviewDefaultImage=PreviewDefaultImage.Translated;
                previewSettings.PreviewWindowSizingMode=PreviewWindowSizingMode.Fixed;
                previewSettings.FixedPreviewWidth=1280;previewSettings.FixedPreviewHeight=800;
                using var form=new PreviewForm(sourceCopy,PreviewMode.OcrOnly,previewSettings,new OcrService(),new TranslationService());
                form.SuppressAutoOcrForE2E();form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;
                form.Location=new Point(-32000,-32000);form.Show();Application.DoEvents();
                var legacy=new OcrDocument
                {
                    Text=string.Join(Environment.NewLine,document.VisualBlocks.Select(x=>x.SourceText)),
                    FullTranslation=string.Join(Environment.NewLine,document.VisualBlocks.Select(x=>document.Translations[x.BlockId].TranslatedText)),
                    SourceWidth=sourceCopy.Width,SourceHeight=sourceCopy.Height
                };
                form.CommitTranslatedDisplayAtomicallyForSmoke(renderedCopy,legacy);Application.DoEvents();
                using var current=form.CloneVisibleImageForTest();
                using var sourceClone=form.CloneSourceImageForTest();
                using var surface=form.CapturePictureSurfaceForSmoke();
                current.Save(Path.Combine(fixtureRoot,"ACTUAL-PREVIEW-CURRENT.png"),ImageFormat.Png);
                sourceClone.Save(Path.Combine(fixtureRoot,"ACTUAL-PREVIEW-SOURCE.png"),ImageFormat.Png);
                surface.Save(Path.Combine(fixtureRoot,"ACTUAL-PREVIEW-SURFACE.png"),ImageFormat.Png);
                var currentDiff=CountDifferentPixels(renderedCopy,current);
                var sourceDiff=CountDifferentPixels(sourceCopy,sourceClone);
                result=new PreviewCaptureResult(currentDiff==0,currentDiff,sourceDiff==0,sourceDiff,
                    surface.Size,form.DisplayScaleForSmoke);
                form.Close();Application.DoEvents();
            }
            catch(Exception ex){failure=ex;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InvalidOperationException("Actual Preview output capture failed.",failure);
        return result??throw new InvalidOperationException("Actual Preview output capture produced no result.");
    }

    private static int CountDifferentPixels(Bitmap left,Bitmap right)
    {
        if(left.Size!=right.Size)return -1;
        using var a=ReadOnlyBitmapPixelBuffer.Create(left);using var b=ReadOnlyBitmapPixelBuffer.Create(right);
        var count=0;
        for(var y=0;y<a.Height;y++)for(var x=0;x<a.Width;x++)
            if(a.GetPixel(x,y).ToArgb()!=b.GetPixel(x,y).ToArgb())count++;
        return count;
    }

    private static void WriteCsv(string path,string[] header,IEnumerable<string[]> rows)
    {
        static string Q(string value)=>'"'+value.Replace("\"","\"\"")+'"';
        File.WriteAllLines(path,new[]{string.Join(',',header.Select(Q))}
            .Concat(rows.Select(row=>string.Join(',',row.Select(Q)))),new UTF8Encoding(true));
    }

    private sealed class CaptureProgress<T>(Action<T> capture):IProgress<T>
    {
        public void Report(T value)=>capture(value);
    }

    private sealed record PairingInput(string PairId,string Set,string SourcePath,string ReferencePath,
        string ProductInputPath,string SourceFormat);
    private sealed record PreviewCaptureResult(bool CurrentPixelExact,int CurrentDifferentPixels,
        bool SourcePixelExact,int SourceDifferentPixels,Size SurfaceSize,float DisplayScale);
}
