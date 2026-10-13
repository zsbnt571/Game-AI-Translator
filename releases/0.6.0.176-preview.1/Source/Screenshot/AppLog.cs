using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;
    public static string Root => AppDataPaths.LogsRoot;
    public static string LastError { get; private set; } = "";

    public static void Write(string category, string message, Exception? error = null)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, $"{category}.log"); Rotate(path);
            var safe = SafeDiagnosticOutput.Compose(message, error);
            if (error is not null) LastError = SafeDiagnosticOutput.ExceptionSummary(error);
            lock (Gate) File.AppendAllText(path, $"{DateTimeOffset.Now:O} {safe}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* Diagnostics must never block screenshot/OCR/translation. */ }
    }
    private static string Redact(string value) => SafeDiagnosticOutput.Redact(value);
    private static void Rotate(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxBytes) return;
        var backup = path + ".1"; if (File.Exists(backup)) File.Delete(backup); File.Move(path, backup);
    }
}
