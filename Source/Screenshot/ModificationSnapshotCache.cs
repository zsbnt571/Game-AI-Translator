using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// A connection instance is a launch identity: a restarted process cannot inherit
// a snapshot even if Windows has reused its PID or the executable path is equal.
internal sealed class ModificationSnapshotCache(Func<long>? clock=null)
{
    private readonly Func<long> now=clock??(()=>Environment.TickCount64);
    private readonly ConditionalWeakTable<IGameDataConnection, Bucket> buckets = new();
    private const int EntryLimit = 12, ByteLimit = 16 * 1024 * 1024;
    private sealed record Entry(JsonElement Value, long Time, int Bytes);
    private sealed class Bucket
    {
        internal readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, Task<JsonElement>> Flights = new(StringComparer.Ordinal);
        internal int Epoch, Bytes;
        internal int? Generation;
        internal long Sequence,GenerationSequence;
    }
    private static string Key(string path, JsonObject request) => path.ToUpperInvariant() + "\n" + request.ToJsonString();
    internal bool TryGet(string path, IGameDataConnection connection, JsonObject request, out JsonElement value, out bool fresh)
    {
        value = default; fresh = false;
        if (!connection.Connected || !buckets.TryGetValue(connection, out var bucket)) return false;
        lock (bucket)
        {
            if (!bucket.Entries.TryGetValue(Key(path, request), out var entry)) return false;
            value = entry.Value; fresh = now() - entry.Time < 5000; return true;
        }
    }
    internal void Invalidate(IGameDataConnection connection)
    {
        if (!buckets.TryGetValue(connection, out var bucket)) return;
        lock (bucket) { bucket.Epoch++; bucket.Entries.Clear(); bucket.Flights.Clear(); bucket.Bytes = 0; bucket.Generation = null; }
    }
    internal bool IsGenerationCurrent(IGameDataConnection connection,int generation)
    {
        if(!connection.Connected||!buckets.TryGetValue(connection,out var bucket))return false;
        lock(bucket)return bucket.Generation==generation&&bucket.Entries.Count>0;
    }
    internal bool IsLatestGeneration(IGameDataConnection connection,int generation)
    {
        if(!connection.Connected||!buckets.TryGetValue(connection,out var bucket))return false;
        // Large snapshots need not be retained to establish ordering.
        lock(bucket)return bucket.Generation==generation;
    }
    internal Task<JsonElement> ReadAsync(string path, IGameDataConnection connection, JsonObject request, Func<JsonElement,Task<JsonElement>>? transform = null)
    {
        var bucket = buckets.GetValue(connection,_=>new()); string key = Key(path, request);
        lock (bucket)
        {
            if (bucket.Flights.TryGetValue(key, out var pending)) return pending;
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            bucket.Flights[key] = completion.Task;
            _ = ReadCoreAsync(bucket, bucket.Epoch, ++bucket.Sequence,key, connection, request, completion, transform,now);
            return completion.Task;
        }
    }
    private static async Task ReadCoreAsync(Bucket bucket, int epoch,long sequence, string key, IGameDataConnection connection, JsonObject request, TaskCompletionSource<JsonElement> completion, Func<JsonElement,Task<JsonElement>>? transform,Func<long> now)
    {
        try
        {
            var result = (await connection.RequestAsync(request)).Clone();
            if (transform is not null) result = await transform(result);
            // Retain only bounded read-only snapshots; never cache a write result.
            int bytes = checked(result.GetRawText().Length * 2);
            lock (bucket)
            {
                if (epoch == bucket.Epoch && connection.Connected)
                {
                    bool accepted=true;
                    if (result.TryGetProperty("generation", out var value) && value.TryGetInt32(out int generation))
                    {
                        if (bucket.Generation is {} old && old != generation)
                        {
                            if(sequence<bucket.GenerationSequence)accepted=false;
                            else { bucket.Entries.Clear(); bucket.Bytes = 0; }
                        }
                        if(accepted){bucket.Generation = generation;bucket.GenerationSequence=Math.Max(sequence,bucket.GenerationSequence);}
                    }
                    if(accepted&&bytes<=ByteLimit)
                    {
                        if (bucket.Entries.Remove(key, out var previous)) bucket.Bytes -= previous.Bytes;
                        while (bucket.Entries.Count >= EntryLimit || bucket.Bytes + bytes > ByteLimit)
                        {
                            var oldest = bucket.Entries.MinBy(pair => pair.Value.Time);
                            bucket.Entries.Remove(oldest.Key); bucket.Bytes -= oldest.Value.Bytes;
                        }
                        bucket.Entries[key] = new(result, now(), bytes); bucket.Bytes += bytes;
                    }
                }
            }
            completion.TrySetResult(result);
        }
        catch (Exception error) { completion.TrySetException(error); }
        finally
        {
            lock (bucket)
                if (bucket.Flights.TryGetValue(key, out var pending) && ReferenceEquals(pending, completion.Task)) bucket.Flights.Remove(key);
        }
    }
}
