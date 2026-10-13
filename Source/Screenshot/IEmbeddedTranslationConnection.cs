using System.Diagnostics;
namespace ScreenshotTranslationUiTester;

internal interface IEmbeddedTranslationConnection : IGameDataConnection
{
    EmbeddedConnectionStage ConnectionStage=>Connected?EmbeddedConnectionStage.Ready:EmbeddedConnectionStage.WaitingForBridge;
    void PrimeStartup(IReadOnlyDictionary<string,string> entries);
    ProcessStartInfo StartInfo(bool translationAtStartup=true);
    Task<Process> LaunchAsync(bool translationAtStartup=true,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(Process.Start(StartInfo(translationAtStartup))??throw new IOException("未能启动游戏。"));
    }
    void Launched(int pid) {}
    Task AcceptAsync(CancellationToken cancellation=default);
}

internal enum EmbeddedConnectionStage { WaitingForBridge, Authenticating, WaitingForHello, Ready }
