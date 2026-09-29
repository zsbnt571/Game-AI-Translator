using System.Diagnostics;
namespace ScreenshotTranslationUiTester;

internal interface IEmbeddedTranslationConnection : IGameDataConnection
{
    void PrimeStartup(IReadOnlyDictionary<string,string> entries);
    ProcessStartInfo StartInfo(bool translationAtStartup=true);
    void Launched(int pid) {}
    Task AcceptAsync(CancellationToken cancellation=default);
}
