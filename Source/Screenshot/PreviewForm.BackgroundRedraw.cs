using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

public sealed partial class PreviewForm
{
    // Explicit redraw creates a new task/history result. Merely changing a
    // setting leaves the displayed bitmap and any in-flight task untouched.
    internal async Task RedrawUsingCurrentModeAsync()
    {
        RequireRenderOwnerThread();
        if(_busy||_ocrTask is {IsCompleted:false}){SetStatus("请等待当前任务完成，或取消后再重绘。");return;}
        if(_historyViewOnly)
        {
            var hash=SessionServices.ComputeImageHash(_original);
            var decision=HistoryRegenerationPolicy.Evaluate(_historySnapshot,_original.Size,hash);
            if(decision.Blocked||decision.ReOcr||_historySnapshot is not {} stored)
            {SetStatus("此历史缺少可靠的重绘数据，已保留保存结果；请使用重新识别或重新翻译。");return;}
            var rebuilt=HistoryRegenerationPolicy.RebuildForRegeneration(stored,_original.Size,_settings,Guid.NewGuid().ToString("N"),_original);
            if(rebuilt.Translations.Values.Count(x=>x.State==BlockTranslationState.Accepted)!=stored.AcceptedTranslations.Count)
            {SetStatus("此历史的译文归属或校验已变化，已保留保存结果；请使用重新翻译。");return;}
            foreach(var b in rebuilt.VisualBlocks)
            {
                var s=rebuilt.Translations[b.BlockId];
                if(b.TextSelection==TextSelectionAction.Preserve){s.State=BlockTranslationState.Preserved;s.TranslatedText=b.SourceText;}
                else if(s.State!=BlockTranslationState.Accepted){s.State=BlockTranslationState.Failed;s.TranslatedText="";s.FailureReason="STORED_TRANSLATION_NOT_ACCEPTED";}
            }
            var text=_document?.FullTranslation??"";
            _corePipelineV2=rebuilt;_document?.DebugImage?.Dispose();
            _document=BuildCoreProductDocument(rebuilt,new OcrEngineResult{EngineActual=_settings.OcrEngine,ModelName="History redraw"});
            _document.FullTranslation=text;_document.TranslationStale=false;_historyViewOnly=false;_historyRequiresReOcr=false;
        }
        if(_corePipelineV2 is null||_document is null||_document.TranslationStale||!_corePipelineV2.Translations.Values.Any(x=>x.State==BlockTranslationState.Accepted))
        {SetStatus("没有可直接重绘的已接受译文；请先识别并翻译。");return;}
        BeginProcessingOperation("Redraw");ReplaceOperation();
        var task=RunBackgroundRedrawAsync(_operation!.Token);_translationTask=task;await task;
    }

    private async Task RunBackgroundRedrawAsync(CancellationToken token)
    {
        var trace=_processingTask!;using var scope=trace.Enter();var settings=ApiSettingsSnapshot.Copy(_settings);
        settings.BackgroundTreatment=trace.Treatment;settings.BackgroundComputeDevice=trace.Device;
        SetTranslationBusy(true);
        try
        {
            SetStatus("正在按当前模式重绘");
            using(trace.Timer.Stage("Renderer"))
                if(!await BuildTranslatedDisplayResponsiveAsync(token))return;
            using(trace.Timer.Stage("Final UI Commit"))CommitFinalPreviewAtomically();
            trace.Timer.MarkFinalResultReady();
            using(trace.Timer.Stage("History Save"))
                _session.AddHistory(_original,settings.OcrEngine,_document!.Text,_document.FullTranslation,
                    settings.TargetLanguage,settings,_translatedDisplay,HistoryCoreSnapshot.Capture(_corePipelineV2!,
                        (_processingProvenance??new CoreProcessingProvenance()) with {BackgroundExecution=trace.FinalIdentity,
                            BackgroundTreatment=trace.Treatment,RenderStrategyVersion=trace.Treatment==BackgroundTreatment.Lightweight?LightweightBackgroundRecovery.StrategyVersion:"fix3-fine",
                            ContentContract=CoreTranslationContentValidator.ContractVersion,TranslationContext=HistoryRegenerationPolicy.TranslationContext(settings)}));
            var missing=_corePipelineV2!.Translations.Values.Count(x=>x.State==BlockTranslationState.Failed);
            SetStatus(CompletionStatus(missing)+"；重绘未调用 API");
        }
        catch(OperationCanceledException){SetStatus("重绘已取消，原图与已有结果保留。");}
        catch(Exception ex){SetTranslationError("重绘失败，已有结果保留。",ex.Message);}
        finally
        {
            using(trace.Timer.Stage("Background Lifetime Release"))await GeneralBackgroundRecovery.ReleaseAsync();
            trace.WriteMetadata();
            if(trace.Timer.FinalResultReadyMs is not null)CompleteWallClock();
            else
            {
                _lastTimingPath=Path.Combine(AppDataPaths.ProductLogsRoot,$"wallclock-{trace.Timer.OperationId}.json");
                trace.Timer.Complete(_lastTimingPath);
            }
            SetTranslationBusy(false);
        }
    }
}
