namespace ScreenshotTranslationUiTester;

public sealed class OcrComparisonForm : Form
{
    private static readonly OcrEngineKind[] PrimaryEngines =
        [OcrEngineKind.Rapid, OcrEngineKind.Paddle];

    private readonly OcrRuntimeManager _runtime;
    private readonly OcrComparisonMode _mode;
    private readonly bool _autoRunPrimary;
    private readonly PictureBox _image = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(15, 17, 22)
    };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Label _activity = new()
    {
        AutoSize = true,
        ForeColor = Color.LightSkyBlue,
        Margin = new Padding(10, 10, 2, 2)
    };
    private readonly Dictionary<OcrEngineKind, RichTextBox> _outputs = [];
    private readonly Dictionary<OcrEngineKind, OcrEngineResult> _results = [];
    private readonly Dictionary<OcrEngineKind, CancellationTokenSource> _operations = [];
    private readonly Dictionary<OcrEngineKind, string> _requestIds = [];
    private readonly IReadOnlyList<OcrEngineKind> _visibleEngines;
    private Bitmap? _source;
    private string _imageHash = "";
    private string _imageSessionId = "";
    private long _imageGeneration;
    private bool _automaticRunStarted;
    private bool _closing;

    public OcrComparisonForm(OcrRuntimeManager runtime,
        OcrComparisonMode mode = OcrComparisonMode.Standard)
        : this(runtime, null, mode, false, true)
    {
    }

    public OcrComparisonForm(OcrRuntimeManager runtime, Bitmap initialImage,
        OcrComparisonMode mode = OcrComparisonMode.Standard, bool autoRunPrimary = true)
        : this(runtime, initialImage, mode, autoRunPrimary, true)
    {
    }

    private OcrComparisonForm(OcrRuntimeManager runtime, Bitmap? initialImage,
        OcrComparisonMode mode, bool autoRunPrimary, bool cloneInput)
    {
        _runtime = runtime;
        _mode = mode;
        _autoRunPrimary = autoRunPrimary;
        _visibleEngines = mode == OcrComparisonMode.ExperimentalWindows
            ? [OcrEngineKind.Rapid, OcrEngineKind.Paddle, OcrEngineKind.Windows]
            : PrimaryEngines;
        Text = mode == OcrComparisonMode.ExperimentalWindows
            ? "OCR 对比（含 Windows 实验）"
            : "OCR 对比";
        Size = new Size(1280, 820);
        MinimumSize = new Size(900, 600);
        BuildUi();FontManager.ApplyUi(this,new ApiSettings());UiDarkTheme.Apply(this);
        if (initialImage is not null) LoadBitmap(initialImage);
        Shown += async (_, _) =>
        {
            if (!_autoRunPrimary || _automaticRunStarted || _source is null) return;
            _automaticRunStarted = true;
            await RunPrimaryAsync();
        };
    }

    private void BuildUi()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(6) };
        Button Add(string text, EventHandler handler)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 34 };
            button.Click += handler;
            bar.Controls.Add(button);
            return button;
        }

        Add("选择图片", (_, _) => SelectImage());
        Add("Rapid（快速）", async (_, _) => await RunAsync(OcrEngineKind.Rapid));
        Add("Paddle（高质量）", async (_, _) => await RunAsync(OcrEngineKind.Paddle));
        if (_mode == OcrComparisonMode.ExperimentalWindows)
            Add("Windows（实验）", async (_, _) => await RunAsync(OcrEngineKind.Windows));
        Add("全部运行", async (_, _) => await RunVisibleAsync());
        Add("取消", (_, _) => CancelAll(invalidateRequests: true));
        bar.Controls.Add(_activity);

        foreach (var kind in _visibleEngines)
        {
            var text = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Microsoft YaHei UI", 10F),
                BackColor = Color.FromArgb(29, 34, 44),
                ForeColor = Color.White
            };
            _outputs[kind] = text;
            var title = kind switch
            {
                OcrEngineKind.Rapid => "Rapid（快速）",
                OcrEngineKind.Paddle => "Paddle（高质量）",
                _ => "Windows（实验）"
            };
            var page = new TabPage(title) { Tag = kind };
            page.Controls.Add(text);
            _tabs.TabPages.Add(page);
        }

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 620 };
        split.Panel1.Controls.Add(_image);
        split.Panel2.Controls.Add(_tabs);
        Controls.Add(split);
        Controls.Add(bar);
        _image.Paint += DrawBoxes;
        _tabs.SelectedIndexChanged += (_, _) => _image.Invalidate();
    }

    private void SelectImage()
    {
        using var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadImage(dialog.FileName);
    }

    private void LoadImage(string path)
    {
        using var loaded = new Bitmap(path);
        LoadBitmap(loaded);
    }

    public void LoadBitmap(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0)
            throw new ArgumentException("OCR Compare 输入图片尺寸无效。", nameof(image));
        var owned = new Bitmap(image.Width, image.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(owned))
            graphics.DrawImageUnscaled(image, Point.Empty);
        ReplaceImage(owned);
    }

    private void ReplaceImage(Bitmap ownedImage)
    {
        CancelAll(invalidateRequests: true);
        _image.Image = null;
        _source?.Dispose();
        _source = ownedImage;
        _imageHash = SessionServices.ComputeImageHash(_source);
        _imageSessionId = Guid.NewGuid().ToString("N");
        _imageGeneration++;
        _image.Image = _source;
        _results.Clear();
        foreach (var box in _outputs.Values) box.Clear();
        _activity.Text = $"ImageSession {_imageSessionId[..8]} · {_source.Width}×{_source.Height}";
        _image.Invalidate();
    }

    private Task RunPrimaryAsync() => Task.WhenAll(PrimaryEngines.Select(RunAsync));
    private Task RunVisibleAsync() => Task.WhenAll(_visibleEngines.Select(RunAsync));

    private async Task RunAsync(OcrEngineKind kind)
    {
        if (!_visibleEngines.Contains(kind))
            throw new InvalidOperationException($"{kind} 不属于当前 OCR Compare 模式。");
        if (_source is null)
        {
            AppDialog.Show(this, "OCR 对比", "请先选择图片。", AppDialogKind.Warning);
            return;
        }

        CancelEngine(kind, invalidateRequest: false);
        var operation = new CancellationTokenSource();
        var requestId = Guid.NewGuid().ToString("N");
        _operations[kind] = operation;
        _requestIds[kind] = requestId;
        var expectedSession = _imageSessionId;
        var expectedHash = _imageHash;
        var expectedGeneration = _imageGeneration;
        var box = _outputs[kind];
        box.Text = $"运行中…\nImageSession: {expectedSession}\nRequestId: {requestId}";
        _activity.Text = $"正在运行 {kind}… · 后台请求 {_operations.Count}";
        try
        {
            using var input = new Bitmap(_source);
            var result = await _runtime.RecognizeWithFallbackAsync(kind, input, "自动", operation.Token,
                requestId, expectedHash, expectedSession);
            if (!TryAcceptResult(kind, result, expectedGeneration, expectedSession, out var expiryReason))
            {
                box.Text = $"已忽略过期结果。\n{expiryReason}";
                return;
            }
            RenderResult(kind, result);
            SelectEngineTab(kind);
            _image.Invalidate();
        }
        catch (OperationCanceledException)
        {
            if (!_closing && !box.IsDisposed) box.Text = "已取消";
        }
        catch (Exception ex)
        {
            if (!_closing && !box.IsDisposed) box.Text = $"失败：{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (_operations.TryGetValue(kind, out var current) && ReferenceEquals(current, operation))
                _operations.Remove(kind);
            operation.Dispose();
            if (!_closing && !_activity.IsDisposed)
                _activity.Text = _operations.Count == 0 ? "OCR 已完成；后台请求 0" : $"后台请求 {_operations.Count}";
        }
    }

    private bool TryAcceptResult(OcrEngineKind kind, OcrEngineResult result, long expectedGeneration,
        string expectedSession, out string reason)
    {
        _requestIds.TryGetValue(kind, out var currentRequest);
        if (expectedGeneration != _imageGeneration || expectedSession != _imageSessionId ||
            result.ImageSessionId != expectedSession || currentRequest != result.RequestId)
        {
            reason = $"ImageSession/Request changed. expectedSession={expectedSession}, " +
                     $"currentSession={_imageSessionId}, resultSession={result.ImageSessionId}, " +
                     $"currentRequest={currentRequest}, resultRequest={result.RequestId}";
            return false;
        }
        _results[kind] = result;
        reason = "";
        return true;
    }

    private void RenderResult(OcrEngineKind kind, OcrEngineResult result)
    {
        var validity = result.ValidForQualityComparison ? "VALID" : "INVALID - FALLBACK";
        var confidenceStatus = result.ConfidenceAvailableBlockCount == 0
            ? "Unavailable (N/A)"
            : $"Available {result.ConfidenceAvailableBlockCount}/{result.Blocks.Count}";
        var lowConfidence = result.ConfidenceAvailableBlockCount == 0
            ? "N/A"
            : result.LowConfidenceBlockCount.ToString();
        var warning = result.ValidForQualityComparison ? "" :
            "\n本次实际引擎与请求不一致，不可用于该引擎质量比较。\n";
        _outputs[kind].Text =
            $"Quality result: {validity}\nRequested: {result.EngineRequested}\nActual: {result.EngineActual}\n" +
            $"ImageSession: {result.ImageSessionId}\nRequestId: {result.RequestId}\nImageHash: {result.ImageHash}\n" +
            $"Instance: {result.EngineInstanceId}\nModel: {result.ModelName}\nModel version: {result.ModelVersion}\n" +
            $"Time: {result.TotalMilliseconds}ms (load {result.ModelLoadMilliseconds}ms)\n" +
            $"Correction: {result.CorrectionMilliseconds}ms | Preprocess retry: {result.PreprocessRetryMilliseconds}ms\n" +
            $"Detected blocks: {result.Blocks.Count} | Confidence: {confidenceStatus} | Low confidence: {lowConfidence}\n" +
            $"Corrected blocks: {result.CorrectedBlockCount} | Corrections: {result.CorrectionCount}\n" +
            $"Ambiguous blocks: {result.AmbiguousBlockCount} | False-positive suspects: {result.FalsePositiveSuspectCount}\n" +
            $"Retry count: {result.PreprocessRetryCount} | Retry reason: {result.RetryReason}\n" +
            $"Retry preprocess: {result.RetryPreprocessType}\n" +
            $"Retry raw: {result.RetryRawText} | Chosen: {result.RetryChosenText}\n" +
            $"Character Top-K: {(result.CharacterTopKAvailable ? "available" : "unavailable")}\n" +
            $"Working set: {result.WorkingSetBytes / 1024d / 1024d:0.0} MiB\n" +
            $"Quality: UNRATED\nFallback: {result.FallbackUsed} {result.FallbackReason}{warning}\n" +
            $"\n=== Raw OCR ===\n{result.RawText}\n\n=== Corrected OCR ===\n{result.CorrectedText}\n\n" +
            string.Join("\n", result.Blocks.SelectMany(b => b.Corrections.Select(c =>
                $"[{b.Id}] {c.Original} -> {c.Corrected} | {c.Reason} | {c.Confidence}"))) +
            "\n" + string.Join("\n", result.Blocks.Where(b => b.UnresolvedAmbiguity).Select(b =>
                $"[{b.Id}] UNRESOLVED_AMBIGUITY raw={b.RawText} candidates={b.AmbiguityCandidates}"));
    }

    private void SelectEngineTab(OcrEngineKind kind)
    {
        var page = _tabs.TabPages.Cast<TabPage>()
            .First(x => x.Tag is OcrEngineKind value && value == kind);
        _tabs.SelectedTab = page;
    }

    private void DrawBoxes(object? sender, PaintEventArgs e)
    {
        if (_source is null || _tabs.SelectedTab?.Tag is not OcrEngineKind kind ||
            !_results.TryGetValue(kind, out var result) || result.ImageSessionId != _imageSessionId) return;
        var scale = Math.Min((float)_image.ClientSize.Width / _source.Width,
            (float)_image.ClientSize.Height / _source.Height);
        var offsetX = (_image.ClientSize.Width - _source.Width * scale) / 2f;
        var offsetY = (_image.ClientSize.Height - _source.Height * scale) / 2f;
        using var normalPen = new Pen(result.ValidForQualityComparison ? Color.Cyan : Color.OrangeRed, 2);
        using var lowPen = new Pen(Color.Yellow, 2);
        using var correctedPen = new Pen(Color.LimeGreen, 2);
        using var suspectPen = new Pen(Color.Orange, 2);
        using var ambiguousPen = new Pen(Color.MediumPurple, 2);
        foreach (var block in result.Blocks)
        {
            var pen = block.SuspectedFalsePositive ? suspectPen : block.UnresolvedAmbiguity ? ambiguousPen : block.Corrections.Count > 0 ? correctedPen : block.LowConfidence ? lowPen : normalPen;
            e.Graphics.DrawRectangle(pen, offsetX + block.BoundingBox.X * scale,
                offsetY + block.BoundingBox.Y * scale, block.BoundingBox.Width * scale,
                block.BoundingBox.Height * scale);
        }
    }

    private void CancelEngine(OcrEngineKind kind, bool invalidateRequest)
    {
        if (_operations.Remove(kind, out var operation)) operation.Cancel();
        if (invalidateRequest) _requestIds.Remove(kind);
    }

    private void CancelAll(bool invalidateRequests)
    {
        foreach (var kind in _operations.Keys.ToArray()) CancelEngine(kind, invalidateRequests);
        if (invalidateRequests) _requestIds.Clear();
    }

    internal void LoadImageForSmoke(string path) => LoadImage(path);
    internal void LoadBitmapForSmoke(Bitmap image) => LoadBitmap(image);
    internal Task RunEngineForSmokeAsync(OcrEngineKind kind) => RunAsync(kind);
    internal Task RunPrimaryForSmokeAsync() => RunPrimaryAsync();
    internal IReadOnlyDictionary<OcrEngineKind, OcrEngineResult> ResultsForSmoke => _results;
    internal int ActiveOperationCountForSmoke => _operations.Count;
    internal string ImageSessionForSmoke => _imageSessionId;
    internal IReadOnlyList<OcrEngineKind> VisibleEnginesForSmoke => _visibleEngines;
    internal bool AutoRunPrimaryForSmoke => _autoRunPrimary;
    internal bool HasEngineTabForSmoke(OcrEngineKind kind) => _outputs.ContainsKey(kind);
    internal string OutputForSmoke(OcrEngineKind kind) => _outputs[kind].Text;
    internal string RequestIdForSmoke(OcrEngineKind kind) => _requestIds.GetValueOrDefault(kind, "");
    internal void SelectTabForSmoke(OcrEngineKind kind) => SelectEngineTab(kind);
    internal string BeginSyntheticRequestForSmoke(OcrEngineKind kind)
    {
        var id = Guid.NewGuid().ToString("N");
        _requestIds[kind] = id;
        return id;
    }
    internal bool AcceptSyntheticResultForSmoke(OcrEngineKind kind, OcrEngineResult result) =>
        TryAcceptResult(kind, result, _imageGeneration, _imageSessionId, out _);
    internal string ImageHashForSmoke => _imageHash;
    internal Bitmap CloneSourceForSmoke() => _source is null
        ? throw new InvalidOperationException("No image loaded.") : new Bitmap(_source);
    internal void SaveUiSmokeScreenshot(string path, OcrEngineKind kind)
    {
        SelectEngineTab(kind);
        Refresh();
        Application.DoEvents();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        CancelAll(invalidateRequests: true);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            CancelAll(invalidateRequests: true);
            _image.Image = null;
            _source?.Dispose();
        }
        base.Dispose(disposing);
    }
}
