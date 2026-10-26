using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Export a fixed field list. Never serialize a session, API settings, raw
// status/error strings, game paths, dialogue or translation dictionaries.
internal sealed record TranslationDiagnosticReport(DateTimeOffset CapturedAt,string Version,string Engine,
    bool Connected,bool Enabled,bool RestorePending,int MissingProfileFields,EmbeddedTranslationQueueSnapshot? Sample)
{
    internal static string EngineLabel(string? adapter)=>adapter switch
    {
        "wolf"=>"WOLF", "unreal"=>"Unreal", "renpy"=>"Ren’Py",
        "rpg-maker-mv" or "rpg-maker-mz"=>"RPG Maker",
        "unity-generic" or "unity-mono" or "unity-il2cpp"=>"Unity",
        "godot3" or "godot4"=>"Godot", "gamemaker"=>"GameMaker",
        "web-construct"=>"Construct 3", "web-rpg"=>"网页游戏",
        "defold"=>"Defold", _=>"其他游戏接入"
    };
    internal string Text()
    {
        var lines=new List<string>{UiLanguage.Text("Fusion 翻译状态报告"),UiLanguage.Format($"时间：{CapturedAt:yyyy-MM-dd HH:mm:ss zzz}"),UiLanguage.Format($"版本：{Version}；接入：{UiLanguage.Text(Engine)}"),
            UiLanguage.Format($"游戏连接：{UiLanguage.Text(Connected?"已连接":"未连接")}；翻译：{UiLanguage.Text(Enabled?"已开启":"关闭或已停止")}"),""};
        if(MissingProfileFields>0)lines.Add(UiLanguage.Text("当前所选翻译方案尚未配置完整，请点击翻译方案旁的“编辑”补齐必填项。"));
        if(RestorePending)lines.Add(UiLanguage.Text("游戏原文恢复尚未确认；请按游戏详情的提示重试关闭翻译。"));
        if(Sample is {} s)
        {
            if(!Connected||!Enabled)lines.Add(UiLanguage.Text("以下是停止前保留的最近一次采样，不代表当前游戏页面。"));
            var view=TranslationProgressPresentation.Describe(s);
            lines.Add(view.Summary);lines.Add(view.NextStep);lines.Add("");lines.AddRange(view.Details);
            lines.Add("");
            if(s.Latency is {} latency)
            {
                lines.Add(UiLanguage.Text("分阶段耗时（毫秒）：前台排队仅统计已观察文字；其余为会话操作统计，包含后台预译。应用回执不代表画面已确认。"));
                foreach(var (label,stage) in new[]{("前台排队",latency.ForegroundQueue),("翻译服务（含预译）",latency.Provider),("当前画面翻译服务",latency.ForegroundProvider),("观察到译文就绪",latency.ObservedToReady),("游戏轮询",latency.BridgePoll),("译文应用",latency.BridgeApply),("缓存保存",latency.CacheSave)})
                    lines.Add(stage is null?UiLanguage.Format($"{UiLanguage.Text(label)}：暂无采样。"):stage.Samples<=0?UiLanguage.Format($"{UiLanguage.Text(label)}：暂无完成采样；进行中 {Math.Max(0,stage.Active)}。"):
                        UiLanguage.Format($"{UiLanguage.Text(label)}：{stage.Samples} 次，平均 {Milliseconds(stage.MeanMs)}，最大 {Milliseconds(stage.MaxMs)}；最近 {stage.WindowSamples} 次 P50 {OptionalMilliseconds(stage.P50Ms)}，P95 {OptionalMilliseconds(stage.P95Ms)}；失败 {Math.Max(0,stage.Failures)}，进行中 {Math.Max(0,stage.Active)}。"));
                lines.Add(UiLanguage.Text("P50/P95最多统计最近256次完成采样；译文就绪表示通过检查，尚不代表游戏画面已显示。"));
                lines.Add(UiLanguage.Format($"等待前台调度：{Math.Max(0,latency.WaitingForeground)}；最久 {Milliseconds(latency.OldestForegroundMs)}。未纳入追踪：{Math.Max(0,latency.UntrackedForeground)}。"));
            }
            else lines.Add(UiLanguage.Text("分阶段耗时：暂无采样。"));
        }
        else lines.Add(UiLanguage.Text(Connected?"连接已建立。点击“开启翻译”后，在游戏中进入菜单或对话页面。":"尚无翻译采样。选择已配置的翻译方案，点击“翻译并启动”，再进入菜单或对话页面。"));
        lines.Add("");lines.Add(UiLanguage.Text("反馈漏翻时，请一并提供当时的画面。此报告不含原文、译文、密钥、游戏路径或存档。"));
        return string.Join(Environment.NewLine,lines);
    }
    internal string Json()
    {
        var s=Sample;
        object? counters=s is null?null:new
        {
            s.DirectoryTexts,s.DirectoryCachedTexts,s.RuntimeCapturedTexts,s.RuntimeEligibleTexts,s.RuntimeCachedTexts,
            s.CatalogDiscoveredTexts,s.CatalogDeferredTexts,s.CatalogLocaleUnconfirmed,
            s.RuntimeOutsideDirectory,s.RuntimeOutsideDirectoryCached,s.RuntimeExcludedTexts,s.RuntimeNonTextTexts,
            s.CatalogExcludedTexts,s.ReadOnlyTexts,s.ActiveRequests,s.RetryScheduledTexts,s.RetryStoppedTexts,
            s.BridgeStoredTexts,s.PendingBridgeEntries,s.ActiveRenderedBindings,s.ActiveCachedWaitingBindings,
            s.WritebacksConfirmed,s.WritebacksFailed,s.NativeWritebackPending,s.FontBlockedBindings,
            s.CatalogReading,s.CatalogIncomplete,s.CatalogIssueCount,s.CatalogRetryPending,s.BridgePaused,
            s.TerminologyPendingNames,s.TerminologyPreparing,
            s.FontDeferredComponents,FontWarningPresent=s.FontWarning.Length>0,
            RuntimeNotePresent=s.RuntimeNote.Length>0,
            FailureReasons=(s.FailureCodes??[]).Select(TranslationAttemptStatus.Reason).Distinct().ToArray()
        };
        return JsonSerializer.Serialize(new{Schema=2,CapturedAt,Version,Engine,Connected,Enabled,RestorePending,MissingProfileFields,
            SampleIsCurrent=Connected&&Enabled,Counts=counters,Latency=LatencyExport(s?.Latency),Scope="observed-text-only; cache-and-control-receipts-are-not-pixel-proof"},new JsonSerializerOptions{WriteIndented=true});
    }
    private static double? SafeMilliseconds(double value)=>double.IsFinite(value)&&value>=0?value:null;
    private static string OptionalMilliseconds(double? value)=>value is {} number?Milliseconds(number):UiLanguage.Text("未知");
    private static string Milliseconds(double value)=>SafeMilliseconds(value) is {} valid?valid.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture):UiLanguage.Text("未知");
    private static object? StageExport(TranslationStageTiming? stage)=>stage is null?null:new
    {
        Samples=Math.Max(0,stage.Samples),
        LastMs=stage.Samples>0?SafeMilliseconds(stage.LastMs):null,
        MaxMs=stage.Samples>0?SafeMilliseconds(stage.MaxMs):null,
        MeanMs=stage.Samples>0?SafeMilliseconds(stage.MeanMs):null,
        P50Ms=stage.WindowSamples>0&&stage.P50Ms is {} p50?SafeMilliseconds(p50):null,
        P95Ms=stage.WindowSamples>0&&stage.P95Ms is {} p95?SafeMilliseconds(p95):null,
        WindowSamples=Math.Clamp(stage.WindowSamples,0,256),
        Failures=Math.Max(0,stage.Failures),Active=Math.Max(0,stage.Active),OldestActiveMs=SafeMilliseconds(stage.OldestActiveMs)
    };
    private static object? LatencyExport(TranslationLatencySnapshot? latency)=>latency is null?null:new
    {
        Scope="foreground-queue: observed-visible-text; other-stages: session-operations-including-pretranslation",
        ForegroundQueue=StageExport(latency.ForegroundQueue),Provider=StageExport(latency.Provider),
        BridgePoll=StageExport(latency.BridgePoll),BridgeApply=StageExport(latency.BridgeApply),CacheSave=StageExport(latency.CacheSave),
        ForegroundProvider=StageExport(latency.ForegroundProvider),ObservedToReady=StageExport(latency.ObservedToReady),
        WaitingForeground=Math.Max(0,latency.WaitingForeground),OldestForegroundMs=SafeMilliseconds(latency.OldestForegroundMs),
        UntrackedForeground=Math.Max(0,latency.UntrackedForeground),
        Runtime=RuntimeExport(latency.Runtime)
    };
    private static Dictionary<string,double?> RuntimeExport(IReadOnlyDictionary<string,double> runtime)
    {
        var result=new Dictionary<string,double?>(StringComparer.Ordinal);
        foreach(string key in TranslationLatency.RuntimeMetricKeys)
        {
            if(!runtime.TryGetValue(key,out double value)||SafeMilliseconds(value) is null)continue;
            string? sampleKey=key.StartsWith("observedToCache",StringComparison.Ordinal)?"observedToCacheSamples":
                key.StartsWith("cacheToReadback",StringComparison.Ordinal)?"cacheToReadbackSamples":
                key.StartsWith("glyphWait",StringComparison.Ordinal)?"glyphWaitSamples":key=="maxDiscoveryMs"?"discoverySnapshots":null;
            bool duration=key.EndsWith("Ms",StringComparison.Ordinal)&&sampleKey is not null;
            result[key]=duration&&(!runtime.TryGetValue(sampleKey!,out double samples)||!double.IsFinite(samples)||samples<=0)?null:value;
        }
        return result;
    }
}
