namespace ScreenshotTranslationUiTester;

public sealed partial class OcrComparisonForm : DesktopForm
{
    private static readonly OcrEngineKind[] PrimaryEngines =
        [OcrEngineKind.Rapid, OcrEngineKind.Paddle];

    private readonly OcrRuntimeManager _runtime;
    private readonly OcrComparisonMode _mode;
    private readonly bool _autoRunPrimary;
    internal override bool UsesNativePresentation=>true;
    private readonly NativePreviewPicture _image=new(){SizeMode=PictureBoxSizeMode.Zoom};
    private readonly System.Windows.Controls.TabControl _tabs=new();
    private readonly NativeRpgLabel _activity=new();
    private readonly Dictionary<OcrEngineKind,NativePreviewText> _outputs=[];
    private readonly Dictionary<OcrEngineKind,Func<string>> _outputRenderers=[];
    private System.Windows.Controls.Grid? _nativeCompareRoot;
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
        AppBranding.Apply(this);
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
        UiLanguage.LanguageChanged+=RefreshLanguage;
        RefreshLanguage();
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
        var root=new System.Windows.Controls.Grid();_nativeCompareRoot=root;root.RowDefinitions.Add(new(){Height=System.Windows.GridLength.Auto});root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=System.Windows.GridLength.Auto});
        var bar=NativeDialogControls.Row(NativeDialogControls.Button("选择图片",SelectImage),NativeDialogControls.Button("Rapid（快速）",async()=>await RunAsync(OcrEngineKind.Rapid)),NativeDialogControls.Button("Paddle（高质量）",async()=>await RunAsync(OcrEngineKind.Paddle)));
        if(_mode==OcrComparisonMode.ExperimentalWindows)bar.Children.Add(NativeDialogControls.Button("Windows（实验）",async()=>await RunAsync(OcrEngineKind.Windows)));
        bar.Children.Add(NativeDialogControls.Button("全部运行",async()=>await RunVisibleAsync()));bar.Children.Add(NativeDialogControls.Button("取消",()=>CancelAll(true)));bar.Margin=new(8);root.Children.Add(bar);
        var body=new System.Windows.Controls.Grid();body.ColumnDefinitions.Add(new());body.ColumnDefinitions.Add(new(){Width=new(6)});body.ColumnDefinitions.Add(new());System.Windows.Controls.Grid.SetRow(body,1);root.Children.Add(body);body.Children.Add(_image.View);var splitter=new System.Windows.Controls.GridSplitter{HorizontalAlignment=System.Windows.HorizontalAlignment.Stretch};splitter.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,"Fusion.Border");System.Windows.Controls.Grid.SetColumn(splitter,1);body.Children.Add(splitter);System.Windows.Controls.Grid.SetColumn(_tabs,2);body.Children.Add(_tabs);
        foreach(var kind in _visibleEngines){var text=new NativePreviewText();_outputs[kind]=text;var title=kind switch{OcrEngineKind.Rapid=>"Rapid（快速）",OcrEngineKind.Paddle=>"Paddle（高质量）",_=>"Windows（实验）"};_tabs.Items.Add(new System.Windows.Controls.TabItem{Header=UiLanguage.Text(title),Tag=kind,Content=text.View});}
        _tabs.SelectedIndex=0;_tabs.SelectionChanged+=(_,_)=>_image.Invalidate();_image.Overlay=DrawNativeOverlay;_activity.Margin=new(12,6,12,6);System.Windows.Controls.Grid.SetRow(_activity.View,2);root.Children.Add(_activity.View);
    }

    private void SelectImage()
    {
        using var dialog = new OpenFileDialog { Filter = UiLanguage.Text("图片|*.png;*.jpg;*.jpeg;*.bmp") };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadImage(dialog.FileName);
    }

    private void RefreshLanguage(object? sender=null,EventArgs? e=null)
    {
        Text=UiLanguage.Text(_mode==OcrComparisonMode.ExperimentalWindows?"OCR 对比（含 Windows 实验）":"OCR 对比");
        if(_nativeCompareRoot is not null)WpfUiLanguage.Dialog(_nativeCompareRoot);
        foreach(var pair in _outputRenderers)_outputs[pair.Key].Text=pair.Value();
    }
    private void SetOutput(OcrEngineKind kind,Func<string> render)
    {
        _outputRenderers[kind]=render;_outputs[kind].Text=render();
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
        _outputRenderers.Clear();
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
        SetOutput(kind,()=>UiLanguage.Format($"运行中…\nImageSession: {expectedSession}\nRequestId: {requestId}"));
        _activity.Text = $"正在运行 {kind}… · 后台请求 {_operations.Count}";
        try
        {
            using var input = new Bitmap(_source);
            var result = await _runtime.RecognizeWithFallbackAsync(kind, input, "自动", operation.Token,
                requestId, expectedHash, expectedSession);
            if (!TryAcceptResult(kind, result, expectedGeneration, expectedSession, out var expiryReason))
            {
                SetOutput(kind,()=>UiLanguage.Format($"已忽略过期结果。\n{expiryReason}"));
                return;
            }
            RenderResult(kind, result);
            SelectEngineTab(kind);
            _image.Invalidate();
        }
        catch (OperationCanceledException)
        {
            if (!_closing && !box.IsDisposed) SetOutput(kind,()=>UiLanguage.Text("已取消"));
        }
        catch (Exception ex)
        {
            if (!_closing && !box.IsDisposed) SetOutput(kind,()=>UiLanguage.Format($"失败：{ex.GetType().Name}: {UiLanguage.Text(ex.Message)}"));
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
        => SetOutput(kind,()=>FormatResult(result));

    private void SelectEngineTab(OcrEngineKind kind)
    {
        var page = _tabs.Items.Cast<System.Windows.Controls.TabItem>()
            .First(x => x.Tag is OcrEngineKind value && value == kind);
        _tabs.SelectedItem = page;
    }

    private void DrawBoxes(object? sender, PaintEventArgs e)
    {
        if (_source is null || (_tabs.SelectedItem as System.Windows.Controls.TabItem)?.Tag is not OcrEngineKind kind ||
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
        base.OnFormClosing(e);
        if(e.Cancel)return;
        _closing = true;
        CancelAll(invalidateRequests: true);
    }

    protected override void Dispose(bool disposing)
    {
        if(IsDisposed||Disposing)return;
        if (disposing)
        {
            UiLanguage.LanguageChanged-=RefreshLanguage;
            _outputRenderers.Clear();
            _closing = true;
            CancelAll(invalidateRequests: true);
            _image.Image = null;
            _source?.Dispose();
            foreach (var output in _outputs.Values) output.Dispose();
        }
        base.Dispose(disposing);
    }
}
