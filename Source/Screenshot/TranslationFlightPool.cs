using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Only unfinished identical requests are shared. A cancellation belongs to its
// waiter; the underlying call is cancelled only after the last waiter leaves.
internal sealed class TranslationFlightPool
{
    internal static readonly TranslationFlightPool Shared=new();
    private sealed record Key(Delegate Provider,string Digest);
    private sealed class Flight
    {
        internal readonly CancellationTokenSource Cancellation=new();
        internal readonly TaskCompletionSource<string> Result=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Waiters;internal bool Finished;
    }
    private readonly object gate=new();private readonly Dictionary<Key,Flight> flights=new();
    internal async Task<string> Request(Func<string,ApiSettings,CancellationToken,Task<string>> provider,string prompt,ApiSettings settings,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var snapshot=ApiSettingsSnapshot.Copy(settings);
        // Include credentials and all request settings in a non-exported hash;
        // another account, glossary prompt or language never joins this request.
        var key=new Key(provider,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot)+"\n"+snapshot.RequestKind+"\n"+prompt))));
        Flight? flight;bool start=false;
        lock(gate)
        {
            if(!flights.TryGetValue(key,out flight)&&flights.Count<64){flight=new();flights.Add(key,flight);start=true;}
            if(flight is not null)flight.Waiters++;
        }
        if(flight is null)return await provider(prompt,snapshot,token).ConfigureAwait(false);
        if(start)_=Run();
        try{return await flight.Result.Task.WaitAsync(token).ConfigureAwait(false);}
        finally
        {
            bool cancel=false;
            lock(gate)
            {
                if(--flight.Waiters==0)
                {
                    if(flights.TryGetValue(key,out var current)&&ReferenceEquals(current,flight))flights.Remove(key);
                    if(flight.Finished)flight.Cancellation.Dispose();else cancel=true;
                }
            }
            if(cancel)try{flight.Cancellation.Cancel();}catch(ObjectDisposedException){}
        }
        async Task Run()
        {
            try{flight.Result.TrySetResult(await provider(prompt,snapshot,flight.Cancellation.Token).ConfigureAwait(false));}
            catch(OperationCanceledException){flight.Result.TrySetCanceled();}
            catch(Exception error){flight.Result.TrySetException(error);_ = flight.Result.Task.Exception;}
            finally
            {
                lock(gate)
                {
                    flight.Finished=true;
                    if(flights.TryGetValue(key,out var current)&&ReferenceEquals(current,flight))flights.Remove(key);
                    if(flight.Waiters==0)flight.Cancellation.Dispose();
                }
            }
        }
    }
}
