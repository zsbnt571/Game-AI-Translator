using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

public sealed partial class PreviewForm
{
    private readonly int _renderOwnerThreadId = Environment.CurrentManagedThreadId;
    private long _renderGeneration;
    private int _lastRenderFailedCount;
    private string CompletionStatus(int missing)
    {
        if(missing==0&&_lastRenderFailedCount==0)return "处理完成";
        var parts=new List<string>();
        if(missing>0)parts.Add($"{missing} 段未翻译（原文已保留，可重新翻译）");
        if(_lastRenderFailedCount>0)parts.Add($"{_lastRenderFailedCount} 段译文未绘出（原文已保留）");
        return "部分完成，"+string.Join("；",parts);
    }
    internal PreviewRenderTestHooks? RenderTestHooks { get; set; }

    private sealed record PreviewRenderIdentity(string RequestId, long TranslationGeneration,
        string ImageSessionId, string OcrRequestId, long OcrGeneration, OcrEngineKind OcrEngine,
        long RenderGeneration, CorePipelineDocument Core, OcrDocument Document, Bitmap Source);

    private sealed record PreviewRenderWork(PreviewRenderIdentity Identity, PreviewRenderInput Input,
        PreviewRenderTestHooks? Hooks, BackgroundComputeDevice Device, BackgroundTreatment Treatment, ProcessingTaskTrace? Trace) : IDisposable
    { public void Dispose() => Input.Dispose(); }

    private void RequireRenderOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _renderOwnerThreadId)
            throw new InvalidOperationException("Preview render capture and commit must run on the owning UI thread.");
    }

    private PreviewRenderWork? CaptureRenderWork(CancellationToken token)
    {
        RequireRenderOwnerThread();
        token.ThrowIfCancellationRequested();
        if (_closing || IsDisposed || Disposing || _document is null) return null;
        if(_historyViewOnly)
        {SetStatus("正在查看保存的历史结果；点击重新翻译后按当前版本生成。");return null;}
        var core = ProductControlPlaneAuthority.RequireCore(_corePipelineV2, "CaptureRenderWork");
        var identity = new PreviewRenderIdentity(_currentTranslationRequestId, _translationGeneration,
            _imageSessionId, _currentOcrRequestId, _ocrGeneration, _settings.OcrEngine,
            ++_renderGeneration, core, _document, _original);
        var input = new PreviewRenderInput(_original, core, _busy && _processingTask?.Typography is {} frozenTypography ? frozenTypography : FontManager.ResolveTranslationImageProfile(_settings));
        var hooks = RenderTestHooks;
        try { hooks?.InputCaptured?.Invoke(input); return new(identity, input, hooks, _processingTask?.Device ?? _settings.BackgroundComputeDevice, _processingTask?.Treatment ?? _settings.BackgroundTreatment, _processingTask); }
        catch { input.Dispose(); throw; }
    }

    private static PendingPreviewRender ComputeRender(PreviewRenderWork work,CancellationToken cancellationToken=default)
    {
        using var traceScope=work.Trace?.Enter();
        work.Hooks?.Phase?.Invoke("before-render");
        var raw = work.Hooks?.Render?.Invoke(work.Input) ?? CorePipelineCorpusRunner.RenderProduct(
            work.Input.Source, work.Input.Core, work.Input.Typography,cancellationToken:cancellationToken,backgroundDevice:work.Device,treatment:work.Treatment);
        var pending = new PendingPreviewRender(raw);
        try
        {
            work.Hooks?.ResultCreated?.Invoke(pending);
            work.Hooks?.Phase?.Invoke("after-render");
            return pending;
        }
        catch { pending.Dispose(); throw; }
    }

    private bool CommitRender(PreviewRenderWork work, PendingPreviewRender pending, CancellationToken token)
    {
        RequireRenderOwnerThread();
        // UI invalidation, new requests, editor/OCR replacement and close run on this
        // same thread. There is no await, message pump or test callback between the
        // decision and ownership swap: this is the render commit linearization point.
        token.ThrowIfCancellationRequested();
        var identity = work.Identity;
        if (_closing || IsDisposed || Disposing || identity.RenderGeneration != _renderGeneration ||
            identity.RequestId != _currentTranslationRequestId || identity.TranslationGeneration != _translationGeneration ||
            identity.ImageSessionId != _imageSessionId || identity.OcrRequestId != _currentOcrRequestId ||
            identity.OcrGeneration != _ocrGeneration || identity.OcrEngine != _settings.OcrEngine ||
            !ReferenceEquals(identity.Core, _corePipelineV2) || !ReferenceEquals(identity.Document, _document) ||
            !ReferenceEquals(identity.Source, _original)) return false;

        var previous = _translatedDisplay;
        _translatedDisplay = pending.TransferBitmap();
        _lastCorePipelineTiming = pending.Timing;
        var renderFailures=pending.Audits.GroupBy(a=>a.BlockId).Where(g=>
            g.Any(a=>a.TranslationState==BlockTranslationState.Accepted)&&
            !g.Any(a=>a.TextDrawn||a.Result==SourceTextNoOp.Result)).Select(g=>g.Last()).ToArray();
        _lastRenderFailedCount=renderFailures.Length;
        foreach(var failure in renderFailures)AppLog.Write("render-source-preserved",
            $"imageSession={identity.ImageSessionId} block={failure.BlockId} reason={failure.Result} translated_text_accepted=true text_drawn=false");
        // Do not leave PictureBox pointing at a disposed previous image, including
        // synchronous appearance re-renders while a translated view is selected.
        if (previous is not null && ReferenceEquals(_picture.Image, previous))
        { _picture.Image = _translatedDisplay; _imageAssignCount++; }
        previous?.Dispose();
        var timing = pending.Timing;
        identity.Document.Metrics.BackgroundMs = (long)Math.Round(timing.BackgroundMs);
        identity.Document.Metrics.LayoutMs = (long)Math.Round(timing.LayoutMs);
        identity.Document.Metrics.DrawMs = (long)Math.Round(timing.DrawMs + timing.StyleMs);
        identity.Document.Metrics.EncodeMs = (long)Math.Round(timing.EncodeMs);
        RecalculateTotalTime();
        AppLog.Write("core-v2-product", $"active=true imageSession={identity.ImageSessionId} blocks={work.Input.Core.VisualBlocks.Count} product_in_memory=true diagnostics={timing.DiagnosticsEnabled} diagnostic_png_writes={timing.FullFrameDiagnosticPngWrites} diagnostic_json_writes={timing.DiagnosticJsonWrites} style_ms={timing.StyleMs:F3} background_ms={timing.BackgroundMs:F3} layout_ms={timing.LayoutMs:F3} draw_ms={timing.DrawMs:F3} encode_ms={timing.EncodeMs:F3} total_ms={timing.TotalMs:F3}");
        return true;
    }

    private void RenderSynchronously()
    {
        using var work = CaptureRenderWork(CancellationToken.None);
        if (work is null) return;
        using var pending = ComputeRender(work);
        work.Hooks?.Phase?.Invoke("before-commit");
        CommitRender(work, pending, CancellationToken.None);
    }

    private async Task<bool> RenderResponsiveAsync(CancellationToken token)
    {
        RequireRenderOwnerThread();
        if (SynchronizationContext.Current is null)
            throw new InvalidOperationException("Asynchronous Preview rendering requires the owning UI synchronization context.");
        using var work = CaptureRenderWork(token);
        if (work is null) return false;
        using var pending = await Task.Run(() => ComputeRender(work,token), token);
        work.Hooks?.Phase?.Invoke("before-commit");
        return CommitRender(work, pending, token);
    }
}
