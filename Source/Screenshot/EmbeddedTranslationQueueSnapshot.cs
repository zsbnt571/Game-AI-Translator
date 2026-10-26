using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

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
    int? ActiveRenderedBindings=null,int? ActiveCachedWaitingBindings=null,string[]? FailureCodes=null,string RuntimeNote="",
    int CatalogDiscoveredTexts=0,int CatalogDeferredTexts=0,bool CatalogLocaleUnconfirmed=false,
    int TerminologyPendingNames=0,bool TerminologyPreparing=false,
    int? FontDeferredComponents=null,string FontWarning="");

internal static class EmbeddedWritebackReceipt
{
    internal static int? Count(JsonElement value,string field)
        =>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(field,out var count)
        &&count.ValueKind==JsonValueKind.Number&&count.TryGetInt32(out int result)&&result>=0?result:null;

    internal static bool SourcePairsStored(JsonElement value,int epoch,int expected)
    {
        if(value.ValueKind!=JsonValueKind.Object)return false;
        if(expected<0)return false;
        foreach(string flag in new[]{"ok","success"})
            if(value.TryGetProperty(flag,out var state)&&state.ValueKind!=JsonValueKind.True)return false;
        if(value.TryGetProperty("error",out var error)&&error.ValueKind!=JsonValueKind.Null
            &&!(error.ValueKind==JsonValueKind.String&&string.IsNullOrWhiteSpace(error.GetString())))return false;
        if(value.TryGetProperty("stale",out var stale)&&stale.ValueKind!=JsonValueKind.False)return false;
        if(value.TryGetProperty("epoch",out var replyEpoch)&&
            (replyEpoch.ValueKind!=JsonValueKind.Number||!replyEpoch.TryGetInt32(out int number)||number!=epoch))return false;
        foreach(string field in new[]{"stored","accepted","applied"})
            if(value.TryGetProperty(field,out _))return Count(value,field)==expected;
        return false;
    }
    internal static string Fingerprint(JsonArray entries)
    {
        using var memory=new MemoryStream();using var writer=new BinaryWriter(memory,Encoding.UTF8,true);
        var pairs=entries.Select(e=>(Source:e!["source"]!.GetValue<string>(),Text:e["text"]!.GetValue<string>()))
            .OrderBy(p=>p.Source,StringComparer.Ordinal).ToArray();
        writer.Write(pairs.Length);
        foreach(var pair in pairs)foreach(string text in new[]{pair.Source,pair.Text})
        {byte[] bytes=Encoding.UTF8.GetBytes(text);writer.Write(bytes.Length);writer.Write(bytes);}
        writer.Flush();return Convert.ToHexString(SHA256.HashData(memory.ToArray())).ToLowerInvariant();
    }
    internal static bool SourcePairsStored(JsonElement value,int epoch,JsonArray entries,EmbeddedTextSyntax syntax,bool unreal=false)
    {
        var sources=new HashSet<string>(StringComparer.Ordinal);
        foreach(var entry in entries)
        {
            if(entry is not JsonObject row||row["source"] is not JsonValue sourceValue||!sourceValue.TryGetValue<string>(out var source)
                ||row["text"] is not JsonValue textValue||!textValue.TryGetValue<string>(out var text)
                ||source is null||text is null||!sources.Add(source)||!TranslationWorkspaceStore.Valid(source,text,syntax,unreal))return false;
        }
        if(!SourcePairsStored(value,epoch,entries.Count))return false;
        // Optional identity proof for newer bridges. Existing count-only bridges
        // remain count-only, never promoted to resource or pixel verification.
        if(value.TryGetProperty("sourcePairsHash",out var hash))
            return hash.ValueKind==JsonValueKind.String&&hash.GetString()==Fingerprint(entries);
        return true;
    }
}
