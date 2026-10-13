using System.Diagnostics;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed record TranslationStageTiming(long Samples,double LastMs,double MaxMs,double MeanMs,long Failures,int Active,double OldestActiveMs);
internal sealed record TranslationLatencySnapshot(TranslationStageTiming ForegroundQueue,TranslationStageTiming Provider,
    TranslationStageTiming BridgePoll,TranslationStageTiming BridgeApply,int WaitingForeground,double OldestForegroundMs,
    long UntrackedForeground,IReadOnlyDictionary<string,double> Runtime,TranslationStageTiming? CacheSave=null);

// Fixed-size aggregate counters and at most 2048 pending visible source keys.
// No prompt, source, translation, path, credential or object address is exported.
internal sealed class TranslationLatency
{
    internal enum Stage { ForegroundQueue,Provider,BridgePoll,BridgeApply,CacheSave }
    private sealed class Measure
    {
        internal long Count,Failures;internal double Last,Max,Total;
        internal long Next;internal int Active;internal readonly Dictionary<long,long> Starts=new();
        internal long Begin(){long id=++Next;Active++;if(Starts.Count<128)Starts[id]=Stopwatch.GetTimestamp();return id;}
        internal void End(long id){Active--;Starts.Remove(id);}
        internal void Add(double ms,bool failed){Count++;Last=ms;Max=Math.Max(Max,ms);Total+=ms;if(failed)Failures++;}
        internal TranslationStageTiming Snapshot()=>new(Count,Last,Max,Count==0?0:Total/Count,Failures,Active,Starts.Count==0?0:Ms(Starts.Values.Min()));
    }
    private readonly object gate=new();private readonly Measure[] measures=[new(),new(),new(),new(),new()];
    private readonly LinkedList<(string Source,long At)> order=new();
    private readonly Dictionary<string,LinkedListNode<(string Source,long At)>> pending=new(StringComparer.Ordinal);
    private long untracked;
    private IReadOnlyDictionary<string,double> runtime=new Dictionary<string,double>();
    private static double Ms(long since)=>(Stopwatch.GetTimestamp()-since)*1000d/Stopwatch.Frequency;
    internal void ObserveVisible(string source,bool cached)
    {
        lock(gate)
        {
            if(cached){Remove(source,false);return;}
            if(pending.ContainsKey(source))return;
            if(pending.Count==2048){Remove(order.First!.Value.Source,false);untracked++;}
            pending.Add(source,order.AddLast((source,Stopwatch.GetTimestamp())));
        }
    }
    private void Remove(string source,bool record)
    {
        if(!pending.Remove(source,out var node))return;
        if(record)measures[(int)Stage.ForegroundQueue].Add(Ms(node.Value.At),false);
        order.Remove(node);
    }
    internal void Dispatched(IEnumerable<string> sources){lock(gate)foreach(var source in sources)Remove(source,true);}
    internal async Task<T> MeasureAsync<T>(Stage stage,Func<Task<T>> action)
    {
        long start=Stopwatch.GetTimestamp(),id;bool failed=true;
        lock(gate)id=measures[(int)stage].Begin();
        try{var value=await action().ConfigureAwait(false);failed=false;return value;}
        finally{lock(gate){measures[(int)stage].End(id);measures[(int)stage].Add(Ms(start),failed);}}
    }
    internal void ReadRuntime(JsonElement receipt)
    {
        if(receipt.ValueKind!=JsonValueKind.Object||!receipt.TryGetProperty("latency",out var value)||value.ValueKind!=JsonValueKind.Object)return;
        var clean=new Dictionary<string,double>();
        foreach(string key in new[]{"observedToCacheSamples","observedToCacheLastMs","observedToCacheMaxMs","cacheToReadbackSamples","cacheToReadbackLastMs","cacheToReadbackMaxMs","glyphWaitSamples","glyphWaitLastMs","glyphWaitMaxMs","waitingForCache","waitingForFont","waitingForReadback","oldestObservedMs","maxDiscoveryMs","discoverySnapshots","lastPumpAgeMs"})
            if(value.TryGetProperty(key,out var number)&&number.ValueKind==JsonValueKind.Number&&number.TryGetDouble(out double result)&&double.IsFinite(result)&&result>=0)clean[key]=result;
        lock(gate)runtime=clean;
    }
    internal TranslationLatencySnapshot Snapshot()
    {
        lock(gate)return new(measures[0].Snapshot(),measures[1].Snapshot(),measures[2].Snapshot(),measures[3].Snapshot(),pending.Count,order.First is {} first?Ms(first.Value.At):0,untracked,runtime,measures[4].Snapshot());
    }
}
