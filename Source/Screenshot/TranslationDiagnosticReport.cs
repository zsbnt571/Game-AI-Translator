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
        var lines=new List<string>{"Fusion 翻译状态报告",$"时间：{CapturedAt:yyyy-MM-dd HH:mm:ss zzz}",$"版本：{Version}；接入：{Engine}",
            $"游戏连接：{(Connected?"已连接":"未连接")}；翻译：{(Enabled?"开启":"关闭或已停止")}",""};
        if(MissingProfileFields>0)lines.Add("当前所选翻译方案尚未配置完整，请点击翻译方案旁的“编辑”补齐必填项。");
        if(RestorePending)lines.Add("游戏原文恢复尚未确认；请按游戏详情的提示重试关闭翻译。");
        if(Sample is {} s)
        {
            if(!Connected||!Enabled)lines.Add("以下是停止前保留的最近一次采样，不代表当前游戏页面。");
            var view=TranslationProgressPresentation.Describe(s);
            lines.Add(view.Summary);lines.Add(view.NextStep);lines.Add("");lines.AddRange(view.Details);
        }
        else lines.Add(Connected?"连接已建立。点击“开启翻译”后，在游戏中进入菜单或对话页面。":"尚无翻译采样。选择已配置的翻译方案，点击“翻译并启动”，再进入菜单或对话页面。");
        lines.Add("");lines.Add("反馈漏翻时，请一并提供当时的画面。此报告不含原文、译文、密钥、游戏路径或存档。");
        return string.Join(Environment.NewLine,lines);
    }
    internal string Json()
    {
        var s=Sample;
        object? counters=s is null?null:new
        {
            s.DirectoryTexts,s.DirectoryCachedTexts,s.RuntimeCapturedTexts,s.RuntimeEligibleTexts,s.RuntimeCachedTexts,
            s.RuntimeOutsideDirectory,s.RuntimeOutsideDirectoryCached,s.RuntimeExcludedTexts,s.RuntimeNonTextTexts,
            s.CatalogExcludedTexts,s.ReadOnlyTexts,s.ActiveRequests,s.RetryScheduledTexts,s.RetryStoppedTexts,
            s.BridgeStoredTexts,s.PendingBridgeEntries,s.ActiveRenderedBindings,s.ActiveCachedWaitingBindings,
            s.WritebacksConfirmed,s.WritebacksFailed,s.NativeWritebackPending,s.FontBlockedBindings,
            s.CatalogReading,s.CatalogIncomplete,s.CatalogIssueCount,s.CatalogRetryPending,s.BridgePaused,
            RuntimeNotePresent=s.RuntimeNote.Length>0,
            FailureReasons=(s.FailureCodes??[]).Select(TranslationAttemptStatus.Reason).Distinct().ToArray()
        };
        return JsonSerializer.Serialize(new{Schema=1,CapturedAt,Version,Engine,Connected,Enabled,RestorePending,MissingProfileFields,
            SampleIsCurrent=Connected&&Enabled,Counts=counters,Scope="observed-text-only; cache-and-control-receipts-are-not-pixel-proof"},new JsonSerializerOptions{WriteIndented=true});
    }
}
