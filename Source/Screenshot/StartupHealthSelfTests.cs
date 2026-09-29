using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class StartupHealthSelfTests
{
    internal static async Task<int> RunAsync(string output, string applicationRoot)
    {
        Directory.CreateDirectory(output);
        await using var ocr = new OcrRuntimeManager(applicationRoot, new OcrService());
        var rapid = ocr.GetStatus(OcrEngineKind.Rapid);
        var rapidProbe = await ocr.CheckAsync(OcrEngineKind.Rapid, CancellationToken.None);
        var rapidPython = ModelManagerOperations.ResolveRapidPython(applicationRoot);
        var visionRoot = ModelManagerOperations.ResolveVisionRoot(applicationRoot);
        var ppModel = Path.Combine(visionRoot, "paddlex", "official_models", "PP-DocLayout-S");
        var ppPython = Path.Combine(visionRoot, "venv", "Scripts", "python.exe");
        var ppInstalled = Directory.Exists(ppModel) && File.Exists(ppPython);
        var startupReady = ModelManagerOperations.RequiredStartupComponentsReady(
            rapid.Ready && rapidProbe.Ready, hotKeyRegistered: true);
        var hotKeyFailureRejected = !ModelManagerOperations.RequiredStartupComponentsReady(
            rapidReady: true, hotKeyRegistered: false);
        var passed = startupReady && hotKeyFailureRejected && File.Exists(rapidPython);

        File.WriteAllText(Path.Combine(output, "startup-health.json"), JsonSerializer.Serialize(new
        {
            Status = passed ? "PASS" : "FAIL",
            ApplicationRoot = applicationRoot,
            RapidInstalled = rapid.Ready,
            RapidProbeReady = rapidProbe.Ready,
            RapidProbeDetail = rapidProbe.Detail,
            RapidPython = rapidPython,
            RapidPythonExists = File.Exists(rapidPython),
            PpDocLayoutSInstalled = ppInstalled,
            PpDocLayoutSRequired = false,
            PpDocLayoutSNormalState = "OFF",
            StartupReadyWithHotKey = startupReady,
            HotKeyFailureRejected = hotKeyFailureRejected
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 2;
    }
}
