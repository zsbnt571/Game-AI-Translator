using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public enum TranslationFailureKind
{
    None, ProviderFirstByteTimeout, ProviderTransportError, MalformedJson,
    PartialGroupResponse, UnexpectedGroupId, DuplicateGroupId,
    MissingGroupAfterRecovery, TranslationCancelled, TranslationParseFailure, ContentValidationFailure
}

public sealed record TranslationRecoveryStats(
    int InitialRequestCount=0, int RetryRequestCount=0, int MissingOnlyRequestCount=0,
    int SplitRequestCount=0, int SingleGroupRequestCount=0, int FirstByteTimeoutCount=0,
    int PartialResponseCount=0, int RecoveredGroupCount=0, int FinalMissingGroupCount=0,
    int TotalRequestCount=0, int MaxRetryBudget=2, int MaxSplitDepth=3,
    int MaxTotalRecoveryRequests=8, TranslationFailureKind LastFailureType=TranslationFailureKind.None);

internal sealed record RecoveryRequest(
    IReadOnlyList<TranslationItem> Items, int AttemptNumber, int SplitDepth,
    bool IsInitial, bool IsMissingOnly, bool IsSplit)
{
    internal IReadOnlyDictionary<string,ContentRecoveryFeedback> ContentFeedback {get;init;} =
        new Dictionary<string,ContentRecoveryFeedback>(StringComparer.Ordinal);
}

internal sealed record ContentRecoveryFeedback(string Reason,string RejectedTranslation,
    string SourceQuantities,string RejectedQuantities);

internal sealed record RecoveryExecution(
    Dictionary<string,string> Translations, TranslationRecoveryStats Stats,
    Dictionary<string,TranslationAllocationSegment[]> Allocations);
internal sealed record RecoveredTranslationEntry(string GroupId,string TranslatedText,string SourceHash,
    int AttemptNumber,string RequestId,string ValidationStatus);

public sealed class TranslationRecoveryFailedException : BatchJsonException
{
    public IReadOnlyDictionary<string,string> CompletedTranslations { get; }
    public IReadOnlyList<string> RemainingMissingIds { get; }
    public TranslationRecoveryStats RecoveryStats { get; }
    public TranslationRecoveryFailedException(string message, IReadOnlyDictionary<string,string> completed,
        IReadOnlyList<string> missing, TranslationRecoveryStats stats, Exception? inner=null) : base(message,inner)
    { CompletedTranslations=completed;RemainingMissingIds=missing;RecoveryStats=stats; }
}

internal static class TranslationRecoveryCoordinator
{
    internal const int MaxRetryBudget=2;
    internal const int MaxSplitDepth=3;
    internal const int MaxTotalRecoveryRequests=8;
    private sealed record Work(List<TranslationItem> Items,int Attempt,int Depth,bool Initial,bool MissingOnly,bool Split);

    internal static async Task<RecoveryExecution> RunAsync(IReadOnlyList<TranslationItem> items,
        IReadOnlyDictionary<string,string>? existing,
        Func<RecoveryRequest,CancellationToken,Task<string>> request,
        IProgress<TranslationProgress>? progress,CancellationToken cancellationToken,
        IReadOnlyDictionary<string,TranslationAllocationSegment[]>? existingAllocations=null,string targetLanguage="")
    {
        var expected=items.Where(x=>!string.IsNullOrWhiteSpace(x.Text)).ToDictionary(x=>x.Id,StringComparer.Ordinal);
        var coreV2=expected.Values.All(x=>x.IdentityContract==TranslationIdentityContract.CoreV2Block);
        var completed=new Dictionary<string,string>(StringComparer.Ordinal);
        var completedAllocations=new Dictionary<string,TranslationAllocationSegment[]>(StringComparer.Ordinal);
        var recoveredBuffer=new Dictionary<string,RecoveredTranslationEntry>(StringComparer.Ordinal);
        var contentFeedback=new Dictionary<string,ContentRecoveryFeedback>(StringComparer.Ordinal);
        var rejectedFingerprints=new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
        var exhaustedContent=new HashSet<string>(StringComparer.Ordinal);
        if(existing is not null)foreach(var pair in existing)
            if(expected.TryGetValue(pair.Key,out var cachedItem)&&!string.IsNullOrWhiteSpace(pair.Value))
            {
                var checkedCache=coreV2?CoreTranslationContentValidator.Validate(cachedItem,pair.Value,"recovery-cache",targetLanguage):
                    new CoreContentValidation(true,pair.Value,false,"NON_CORE_CACHE");
                if(!checkedCache.Accepted)continue;
                completed[pair.Key]=checkedCache.Text;recoveredBuffer[pair.Key]=Entry(cachedItem,checkedCache.Text,0,"validated-cache",checkedCache.Reason);
                if(existingAllocations?.TryGetValue(pair.Key,out var cachedAllocation)==true)
                    completedAllocations[pair.Key]=cachedAllocation;
            }
        var queue=new Queue<Work>();var initialMissing=expected.Values.Where(x=>!completed.ContainsKey(x.Id)).ToList();
        if(initialMissing.Count>0)queue.Enqueue(new(initialMissing,0,0,true,false,false));
        var initial=0;var retries=0;var missingOnly=0;var splits=0;var singles=0;var timeouts=0;var partials=0;var recovered=0;var total=0;
        var last=TranslationFailureKind.None;Exception? lastError=null;

        void EnqueueSplit(IReadOnlyList<TranslationItem> unresolved,int depth)
        {
            if(unresolved.Count==0)return;
            if(unresolved.Count==1||depth>=MaxSplitDepth){queue.Enqueue(new(unresolved.ToList(),0,Math.Min(depth,MaxSplitDepth),false,true,depth>0));return;}
            var midpoint=(unresolved.Count+1)/2;
            queue.Enqueue(new(unresolved.Take(midpoint).ToList(),0,depth,false,true,true));
            queue.Enqueue(new(unresolved.Skip(midpoint).ToList(),0,depth,false,true,true));
        }

        while(queue.Count>0&&total<MaxTotalRecoveryRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var work=queue.Dequeue();
            var unresolved=work.Items.Where(x=>!completed.ContainsKey(x.Id)&&!exhaustedContent.Contains(x.Id)).ToList();
            if(unresolved.Count==0)continue;
            total++;if(work.Initial)initial++;else retries++;if(work.MissingOnly)missingOnly++;if(work.Split)splits++;if(unresolved.Count==1)singles++;
            progress?.Report(new(TranslationStage.RecoveringResponse,total,0,
                work.Initial?"正在翻译":$"正在恢复 {unresolved.Count} 个缺失翻译组……"));
            try
            {
                var nextRequest=new RecoveryRequest(unresolved,work.Attempt,work.Depth,work.Initial,work.MissingOnly,work.Split)
                {ContentFeedback=unresolved.Where(x=>contentFeedback.ContainsKey(x.Id))
                    .ToDictionary(x=>x.Id,x=>contentFeedback[x.Id],StringComparer.Ordinal)};
                var body=await request(nextRequest,cancellationToken).ConfigureAwait(false);
                var expectedIds=unresolved.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
                var outcome=coreV2
                    ?TranslationService.ParseCoreV2PartialResponseForRecovery(body,expectedIds,$"recovery-{total}")
                    :TranslationService.ParsePartialResponseForRecovery(body,expectedIds,$"recovery-{total}");
                var contentRejected=false;
                foreach(var pair in outcome.Translations)
                {
                    if(completed.ContainsKey(pair.Key))continue;
                    var content=coreV2?CoreTranslationContentValidator.Validate(expected[pair.Key],pair.Value,$"recovery-{total}",targetLanguage):
                        new CoreContentValidation(true,pair.Value,false,"LEGACY_UNCHANGED");
                    if(!content.Accepted)
                    {
                        contentRejected=true;
                        // One targeted request receives the exact rejection and
                        // quantity evidence. Repeating the same invalid answer
                        // after that feedback adds no information. Stop only this
                        // item; other missing or newly corrected items still run.
                        if(!rejectedFingerprints.TryGetValue(pair.Key,out var seen))
                            rejectedFingerprints[pair.Key]=seen=new(StringComparer.Ordinal);
                        var fingerprint=content.Reason+"\n"+pair.Value.Trim().Replace("\r\n","\n");
                        if(!seen.Add(fingerprint))
                        {
                            exhaustedContent.Add(pair.Key);
                            TranslationBoundaryDiagnostics.Record("ContentRecoveryExhausted",$"recovery-{total}",pair.Key,
                                "REPEATED_IDENTICAL_REJECTION_AFTER_FEEDBACK; "+content.Reason);
                        }
                        var source=expected[pair.Key].Text;
                        contentFeedback[pair.Key]=new(content.Reason,pair.Value,
                            CorePipelineV2.NumericFidelityV2.Describe(source,pair.Value,true),
                            CorePipelineV2.NumericFidelityV2.Describe(pair.Value,source,false));
                        continue;
                    }
                    contentFeedback.Remove(pair.Key);
                    completed[pair.Key]=content.Text;recoveredBuffer[pair.Key]=Entry(expected[pair.Key],content.Text,total,$"recovery-{total}",content.Reason);recovered++;
                    if(outcome.Allocations.TryGetValue(pair.Key,out var allocation))completedAllocations[pair.Key]=allocation;
                }
                var still=unresolved.Where(x=>!completed.ContainsKey(x.Id)&&!exhaustedContent.Contains(x.Id)).ToList();
                if(still.Count==0)
                {
                    if(contentRejected)last=TranslationFailureKind.ContentValidationFailure;
                    continue;
                }
                partials++;last=contentRejected?TranslationFailureKind.ContentValidationFailure:outcome.ConflictingDuplicateIds.Count>0?TranslationFailureKind.DuplicateGroupId:TranslationFailureKind.PartialGroupResponse;
                if(!work.Initial||work.Attempt>0)EnqueueSplit(still,work.Depth+1);
                else queue.Enqueue(new(still,0,work.Depth,false,true,false));
            }
            catch(UnexpectedGroupIdException ex)
            {
                // A provider can occasionally copy a nearby-looking identifier incorrectly.
                // Never remap or accept the unknown identifier.  Retry only the unresolved
                // expected IDs within the existing bounded recovery budget, then split so a
                // repeated error remains isolated and explicitly rejected.
                last=TranslationFailureKind.UnexpectedGroupId;lastError=ex;
                if(work.Attempt+1<MaxRetryBudget)
                    queue.Enqueue(work with{Attempt=work.Attempt+1,Initial=false,MissingOnly=true});
                else if(unresolved.Count>1)
                    EnqueueSplit(unresolved,work.Depth+1);
                else break;
            }
            catch(TranslationFirstByteTimeoutException ex)
            {
                timeouts++;last=TranslationFailureKind.ProviderFirstByteTimeout;lastError=ex;
                progress?.Report(new(TranslationStage.RecoveringResponse,total,0,"服务器响应较慢，正在有限重试……"));
                await Task.Delay(TimeSpan.FromMilliseconds(200*Math.Min(3,timeouts)),cancellationToken).ConfigureAwait(false);
                if(work.Attempt+1<MaxRetryBudget)queue.Enqueue(work with{Attempt=work.Attempt+1,Initial=false,MissingOnly=true});
                else EnqueueSplit(unresolved,work.Depth+1);
            }
            catch(BatchJsonException ex)
            {
                last=TranslationFailureKind.MalformedJson;lastError=ex;
                if(work.Attempt+1<MaxRetryBudget&&unresolved.Count==1)queue.Enqueue(work with{Attempt=work.Attempt+1,Initial=false,MissingOnly=true});
                else EnqueueSplit(unresolved,work.Depth+1);
            }
            catch(Exception ex)when(ex is HttpRequestException or IOException)
            {
                last=TranslationFailureKind.ProviderTransportError;lastError=ex;
                if(work.Attempt+1<MaxRetryBudget)queue.Enqueue(work with{Attempt=work.Attempt+1,Initial=false,MissingOnly=true});
                else EnqueueSplit(unresolved,work.Depth+1);
            }
        }

        var missing=expected.Keys.Where(x=>!completed.ContainsKey(x)).ToArray();
        var stats=new TranslationRecoveryStats(initial,retries,missingOnly,splits,singles,timeouts,partials,recovered,missing.Length,total,
            MaxRetryBudget,MaxSplitDepth,MaxTotalRecoveryRequests,missing.Length==0?TranslationFailureKind.None:last==TranslationFailureKind.None?TranslationFailureKind.MissingGroupAfterRecovery:last);
        if(missing.Length>0)
        {
            var message=$"Translation Recovery Failed\nRequested: {expected.Count}\nCompleted: {completed.Count}\nMissing: {string.Join(',',missing)}\n"+
                        $"Attempts: {total}\nFirstByteTimeouts: {timeouts}\nPartialResponses: {partials}\nSplits: {splits}\nLastFailureType: {stats.LastFailureType}";
            throw new TranslationRecoveryFailedException(message,completed,missing,stats,lastError);
        }
        if(!expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(completed.Keys))
            throw new InvalidOperationException("Final expected/completed group set validation failed.");
        var contracts=expected.Values.Select(x=>x.IdentityContract).Distinct().ToArray();
        if(contracts.Length!=1)
            throw new TranslationRecoveryFailedException("IDENTITY_CONTRACT_MIXED",completed,[],stats);
        if(contracts[0]==TranslationIdentityContract.CoreV2Block)
        {
            var emptyIds=completed.Where(x=>string.IsNullOrWhiteSpace(x.Value)).Select(x=>x.Key).ToArray();
            if(emptyIds.Length>0)
                throw new TranslationRecoveryFailedException($"EMPTY_TRANSLATION: {string.Join(',',emptyIds)}",completed,[],stats);
            TranslationAcceptanceTrace.WriteCoreV2(items,completed,completedAllocations,stats);
            return new(completed,stats,new(StringComparer.Ordinal));
        }
        var validation=TranslationAllocationContractV1.Validate(items,completed,completedAllocations);
        TranslationAcceptanceTrace.Write(items,completed,completedAllocations,validation,stats);
        if(validation.Status==TranslationAllocationStatus.Invalid)
            throw new TranslationRecoveryFailedException($"ALLOCATION_INVALID: {validation.Reason}",completed,[],stats);
        return new(completed,stats,validation.IsValid?validation.Allocations:new(StringComparer.Ordinal));

        static RecoveredTranslationEntry Entry(TranslationItem item,string text,int attempt,string requestId,string validation)
        {var bytes=System.Text.Encoding.UTF8.GetBytes($"{item.Id}|{item.RoleType}|{item.Text}");var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));return new(item.Id,text,hash,attempt,requestId,validation);}
    }
}

internal static class TranslationAcceptanceTrace
{
    internal static void WriteCoreV2(
        IReadOnlyList<TranslationItem> items,
        IReadOnlyDictionary<string,string> completed,
        IReadOnlyDictionary<string,TranslationAllocationSegment[]> providerAllocations,
        TranslationRecoveryStats stats)
    {
        var validation=new TranslationAllocationValidation(TranslationAllocationStatus.Valid,
            "CORE_V2_BLOCK_IDENTITY_ACCEPTED; provider allocation ignored",new(StringComparer.Ordinal));
        Write(items,completed,providerAllocations,validation,stats);
    }
    internal static void Write(
        IReadOnlyList<TranslationItem> items,
        IReadOnlyDictionary<string,string> completed,
        IReadOnlyDictionary<string,TranslationAllocationSegment[]> allocations,
        TranslationAllocationValidation validation,
        TranslationRecoveryStats stats)
    {
        try
        {
            var expectedIds=items.Where(x=>!string.IsNullOrWhiteSpace(x.Text)).Select(x=>x.Id)
                .OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            var parsedIds=completed.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            var allocationIds=allocations.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            var missing=expectedIds.Except(parsedIds,StringComparer.Ordinal).ToArray();
            var extra=parsedIds.Except(expectedIds,StringComparer.Ordinal).ToArray();
            var empty=completed.Where(x=>string.IsNullOrWhiteSpace(x.Value)).Select(x=>x.Key)
                .OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            var exactSet=expectedIds.SequenceEqual(parsedIds,StringComparer.Ordinal);
            var allocationValid=validation.Status!=TranslationAllocationStatus.Invalid;
            var firstFalse=!exactSet?"ID_SET_COMPLETE":empty.Length>0?"NO_EMPTY_TRANSLATIONS":
                !allocationValid?"LEGACY_ALLOCATION_VALID":"NONE";
            var trace=new
            {
                Timestamp=DateTimeOffset.Now,
                Counts=new
                {
                    CoreV2RequestItems=expectedIds.Length,
                    ExpectedResponses=expectedIds.Length,
                    ParsedResponses=parsedIds.Length,
                    AcceptedResponses=allocationValid?parsedIds.Length:0,
                    RecoveryRequested=stats.RetryRequestCount,
                    FinalCommittedBlocks=0
                },
                IdSets=new
                {
                    CoreV2BlockIds=expectedIds,
                    TranslationRequestIds=expectedIds,
                    ProviderResponseIds=parsedIds,
                    ParsedResponseIds=parsedIds,
                    AcceptanceExpectedIds=expectedIds,
                    AcceptanceMatchedIds=expectedIds.Intersect(parsedIds,StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal),
                    MissingIds=missing,
                    ExtraIds=extra,
                    DuplicateIds=Array.Empty<string>(),
                    RecoveryTargetIds=missing,
                    LegacyAllocationUnitIds=allocationIds
                },
                Equality=new
                {
                    RequestIdSetEqualsParsedIdSet=exactSet,
                    ParsedIdSetEqualsAcceptanceExpectedIdSet=exactSet
                },
                Predicates=new
                {
                    ParseComplete=true,
                    ExpectedCountComplete=expectedIds.Length==parsedIds.Length,
                    IdSetComplete=exactSet,
                    NoDuplicateIds=true,
                    NoUnknownIds=extra.Length==0,
                    NoEmptyTranslations=empty.Length==0,
                    LegacyAllocationValid=allocationValid,
                    RecoveryRequired=!allocationValid,
                    AcceptanceFinal=allocationValid
                },
                Allocation=new
                {
                    Status=validation.Status.ToString(),
                    validation.Reason,
                    AllocationIds=allocationIds,
                    SourceIdsByRequest=items.ToDictionary(x=>x.Id,x=>x.StableSourceIds,StringComparer.Ordinal)
                },
                FirstFalsePredicate=firstFalse
            };
            var root=Path.Combine(RealPathDiagnosticTrace.Root,"translation-acceptance");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root,"TRANSLATION-ACCEPTANCE-TRACE.json"),
                JsonSerializer.Serialize(trace,new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(Path.Combine(root,"ID-SET-DIFF.json"),JsonSerializer.Serialize(new
            {
                RequestIds=expectedIds,ParsedIds=parsedIds,MissingIds=missing,ExtraIds=extra,
                DuplicateIds=Array.Empty<string>(),AllocationIds=allocationIds,ExactSetEquality=exactSet
            },new JsonSerializerOptions{WriteIndented=true}));
            TranslationService.DiagnosticLog($"translation acceptance firstFalse={firstFalse} expected={expectedIds.Length} parsed={parsedIds.Length} matched={expectedIds.Intersect(parsedIds,StringComparer.Ordinal).Count()} missing={missing.Length} extra={extra.Length} duplicate=0 empty={empty.Length} allocationStatus={validation.Status} allocationReason={validation.Reason}");
        }
        catch(Exception ex)
        {
            TranslationService.DiagnosticLog($"translation acceptance trace_error={ex.GetType().Name} message={ex.Message}");
        }
    }
}

public sealed class UnexpectedGroupIdException(string message,Exception? inner=null) : BatchJsonException(message,inner);
