using System.IO.Pipes;
using System.Text;
using Fusion.UnityEmbedded;

internal static class TransportTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        string Name()=>"fusion-unity-offline-"+Guid.NewGuid().ToString("N");
        string name=Name();
        using(var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous))
        {
            var accepting=server.WaitForConnectionAsync();
            using var client=UnityPipeStream.Connect(name,1000,()=>false);await accepting.WaitAsync(TimeSpan.FromSeconds(2));
            byte[] message=Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Neutral pipe payload 中文\n",24000)));
            var upload=Task.Run(()=>client.Write(message,0,message.Length));var received=new byte[message.Length];
            await server.ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(5));await upload.WaitAsync(TimeSpan.FromSeconds(5));
            check(message.SequenceEqual(received),"native client writes fragmented Unicode payload without framework Pipes");
            var download=Task.Run(()=>{var bytes=new byte[message.Length];int offset=0;while(offset<bytes.Length){int read=client.Read(bytes,offset,Math.Min(137,bytes.Length-offset));if(read==0)throw new EndOfStreamException();offset+=read;}return bytes;});
            await server.WriteAsync(message);check(message.SequenceEqual(await download.WaitAsync(TimeSpan.FromSeconds(5))),"native client reads fragmented payload exactly");
        }
        name=Name();
        using(var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous))
        {
            var accepting=server.WaitForConnectionAsync();var client=UnityPipeStream.Connect(name,1000,()=>false);await accepting;
            var pending=Task.Run(()=>{try{client.Read(new byte[32],0,32);return false;}catch(ObjectDisposedException){return true;}catch(IOException){return true;}});
            await Task.Delay(80);client.Dispose();check(await pending.WaitAsync(TimeSpan.FromSeconds(2)),"disposing native stream cancels a pending read");
        }
        name=Name();
        using(var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous))
        {
            var accepting=server.WaitForConnectionAsync();using var client=UnityPipeStream.Connect(name,1000,()=>false);await accepting;
            var stopped=new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread=new Thread(()=>{try{client.Read(new byte[32],0,32);stopped.TrySetResult(null);}catch(Exception error){stopped.TrySetResult(error);}}){IsBackground=true};
            thread.Start();
            for(int i=0;i<100&&(thread.ThreadState&ThreadState.WaitSleepJoin)==0;i++)await Task.Delay(10);
            thread.Interrupt();
            check(await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)) is ThreadInterruptedException,"interrupted native read drains cancellation before releasing its buffer");
            var received=new byte[2];var nextRead=Task.Run(()=>client.Read(received,0,received.Length));
            await server.WriteAsync(new byte[]{41,42}).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            check(await nextRead.WaitAsync(TimeSpan.FromSeconds(2))==2&&received.SequenceEqual(new byte[]{41,42}),"connection remains usable after interrupting a pending read");
        }
        name=Name();
        using(var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096))
        {
            var accepting=server.WaitForConnectionAsync();var client=UnityPipeStream.Connect(name,1000,()=>false);await accepting;
            var pending=Task.Run(()=>{try{client.Write(new byte[4*1024*1024],0,4*1024*1024);return false;}catch(ObjectDisposedException){return true;}catch(IOException){return true;}});
            await Task.Delay(80);client.Dispose();check(await pending.WaitAsync(TimeSpan.FromSeconds(2)),"disposing native stream cancels a blocked write");
        }
        name=Name();
        using(var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous))
        {
            var accepting=server.WaitForConnectionAsync();using var client=UnityPipeStream.Connect(name,1000,()=>false);await accepting;
            server.Dispose();check(client.Read(new byte[8],0,8)==0,"server closure becomes EOF");
        }
        var clock=System.Diagnostics.Stopwatch.StartNew();bool expired=false;
        try{using var missing=UnityPipeStream.Connect(Name(),80,()=>false);}catch(TimeoutException){expired=true;}
        check(expired&&clock.ElapsedMilliseconds<1000,"missing endpoint has a bounded connect timeout");
        bool cancelled=false;try{using var missing=UnityPipeStream.Connect(Name(),30000,()=>true);}catch(OperationCanceledException){cancelled=true;}
        check(cancelled,"cancelled connect does not wait for endpoint");

        var previous=new Dictionary<string,string>();
        void Set(string key,string value){previous[key]=Environment.GetEnvironmentVariable(key);Environment.SetEnvironmentVariable(key,value);}
        name=Name();var logs=new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var bridge=new UnityBridgeRuntime(logs.Enqueue);
        try
        {
            Set("FUSION_UNITY_PIPE",name);Set("FUSION_UNITY_SECRET",new string('a',64));Set("FUSION_UNITY_STARTUP_FILE",null);
            using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            var accepting=server.WaitForConnectionAsync();check(bridge.Start(),"bridge starts without a Unity game");await accepting.WaitAsync(TimeSpan.FromSeconds(2));
            using var reader=new StreamReader(server,new UTF8Encoding(false),false,4096,true);var writer=new StreamWriter(server,new UTF8Encoding(false),4096,true){AutoFlush=true};
            var hello=reader.ReadLineAsync();await Task.Delay(180);check(!hello.IsCompleted,"bridge does not acknowledge readiness before its first frame");
            bridge.Tick();var message=await hello.WaitAsync(TimeSpan.FromSeconds(2));
            check(message!=null&&message.Contains("\"hello\":1"),"first frame releases the handshake");
            await writer.WriteLineAsync("{\"id\":1,\"op\":\"translationPrepare\"}");var response=reader.ReadLineAsync();
            for(int i=0;i<50&&!response.IsCompleted;i++){bridge.Tick();await Task.Delay(10);}
            check((await response.WaitAsync(TimeSpan.FromSeconds(2)))!.Contains("\"ok\":true"),"real transport dispatches through the frame request queue");
            // Complete the test peer's writer while the connection is still open;
            // disposal would otherwise Flush after our deliberate peer shutdown.
            writer.Dispose();
            bridge.RestoreAndDispose();check(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2))==null,"plugin teardown closes the transport");
            check(logs.Any(s=>s.Contains("reason=plugin-destroyed"))&&!logs.Any(s=>s.Contains(new string('a',64))),"lifecycle diagnostics report teardown without the session secret");
        }
        finally{foreach(var pair in previous)Environment.SetEnvironmentVariable(pair.Key,pair.Value);}
    }
}
