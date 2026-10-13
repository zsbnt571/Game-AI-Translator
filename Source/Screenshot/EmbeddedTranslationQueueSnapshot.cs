using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Counts describe separate authorities. Stored source pairs are not rendering
// receipts; native readback remains unknown unless the bridge explicitly reports it.
internal sealed record EmbeddedTranslationQueueSnapshot(
    string SourceLanguage="正在识别",string LanguageEvidence="none",
    int DirectoryTexts=0,int DirectoryCachedTexts=0,
    int RuntimeCapturedTexts=0,int RuntimeEligibleTexts=0,int RuntimeExcludedTexts=0,
    int RuntimeCachedTexts=0,int RuntimeOutsideDirectory=0,int RuntimeOutsideDirectoryCached=0,
    int ReadOnlyTexts=0,int ActiveRequests=0,int BridgeStoredTexts=0,int PendingBridgeEntries=0,
    int? WritebacksConfirmed=null,int? WritebacksFailed=null,
    int RuntimeNonTextTexts=0,int? NativeCapturedTexts=null,int? NativeBindings=null,int? LastVerifiedBindings=null,
    int? NativeWritebackPending=null,int? FontBlockedBindings=null,int? RevealingBindings=null,
    string NativeCaptureSemantics="unknown",string FontStatus="unknown",int? FontsOwned=null,TranslationLatencySnapshot? Latency=null,
    bool CatalogReading=true,bool CatalogIncomplete=false,int CatalogIssueCount=0,int CatalogExcludedTexts=0,
    int RetryScheduledTexts=0,int RetryStoppedTexts=0,bool BridgePaused=false,bool CatalogRetryPending=false,
    int? ActiveRenderedBindings=null,int? ActiveCachedWaitingBindings=null,string[]? FailureCodes=null,string RuntimeNote="");

internal static class EmbeddedWritebackReceipt
{
    internal static int? Count(JsonElement value,string field)
        =>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(field,out var count)
        &&count.ValueKind==JsonValueKind.Number&&count.TryGetInt32(out int result)&&result>=0?result:null;

    internal static bool SourcePairsStored(JsonElement value,int epoch,int expected)
    {
        if(value.ValueKind!=JsonValueKind.Object)return false;
        if(value.TryGetProperty("stale",out var stale)&&stale.ValueKind!=JsonValueKind.False)return false;
        if(value.TryGetProperty("epoch",out var replyEpoch)&&
            (replyEpoch.ValueKind!=JsonValueKind.Number||!replyEpoch.TryGetInt32(out int number)||number!=epoch))return false;
        foreach(string field in new[]{"stored","accepted","applied"})
            if(value.TryGetProperty(field,out _))return Count(value,field)==expected;
        return false;
    }
}
