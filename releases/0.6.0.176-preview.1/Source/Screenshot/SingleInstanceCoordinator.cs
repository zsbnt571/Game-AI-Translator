using System.IO.Pipes;
namespace ScreenshotTranslationUiTester;
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private static string MutexName=>@"Local\"+AppDataPaths.InstanceId+".MainApplication";
    private static string PipeName=>AppDataPaths.InstanceId+".Activate";
    internal static string DiagnosticMutexName=>MutexName;
    internal static string DiagnosticPipeName=>PipeName;
    private readonly Mutex _mutex;private readonly CancellationTokenSource _stop=new();private Action? _activate;
    internal bool IsPrimary{get;}
    internal SingleInstanceCoordinator(){_mutex=new Mutex(true,MutexName,out var created);IsPrimary=created;}
    internal bool NotifyPrimary()
    {
        try{using var pipe=new NamedPipeClientStream(".",PipeName,PipeDirection.Out);pipe.Connect(1800);pipe.WriteByte(1);pipe.Flush();return true;}catch(Exception ex){AppLog.Write("single-instance","Activation signal failed",ex);return false;}
    }
    internal void Start(Action activate){_activate=activate;_ = Task.Run(ListenAsync);}
    private async Task ListenAsync()
    {
        while(!_stop.IsCancellationRequested)try{using var pipe=new NamedPipeServerStream(PipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);await pipe.WaitForConnectionAsync(_stop.Token);if(pipe.ReadByte()>=0)_activate?.Invoke();}catch(OperationCanceledException){break;}catch(Exception ex){AppLog.Write("single-instance","Activation listener error",ex);await Task.Delay(100);}
    }
    public void Dispose(){_stop.Cancel();if(IsPrimary)try{_mutex.ReleaseMutex();}catch{} _mutex.Dispose();_stop.Dispose();}
}
