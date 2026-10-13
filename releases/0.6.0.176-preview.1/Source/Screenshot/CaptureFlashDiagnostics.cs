using System.Diagnostics;
using System.Drawing.Imaging;

namespace ScreenshotTranslationUiTester;

internal enum CaptureFlashDiagnosticMode { Full, CaptureOnly, OverlayOnly, HideCapture, AffinityCapture }

internal sealed record CaptureFlashDiagnosticOptions(
    CaptureFlashDiagnosticMode Mode, bool NoActivate, bool NoShade, bool Requested = false)
{
    public bool Enabled => Requested || Mode != CaptureFlashDiagnosticMode.Full || NoActivate || NoShade;

    public static CaptureFlashDiagnosticOptions Parse(string[] args)
    {
        var mode = CaptureFlashDiagnosticMode.Full;
        var noActivate = false;
        var noShade = false;
        var requested = false;
        foreach (var argument in args)
        {
            const string prefix = "--capture-flash-diagnostic=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse(argument[prefix.Length..], true, out CaptureFlashDiagnosticMode parsed))
            {
                mode = parsed;
                requested = true;
            }
            else if (argument.Equals("--overlay-noactivate", StringComparison.OrdinalIgnoreCase))
                noActivate = true;
            else if (argument.Equals("--overlay-noshade", StringComparison.OrdinalIgnoreCase))
                noShade = true;
        }
        return new(mode, noActivate, noShade, requested);
    }
}

internal static class CaptureFlashDiagnosticLog
{
    private static readonly object Gate = new();
    private static Stopwatch _clock = Stopwatch.StartNew();
    private static string? _path;

    public static void Start(CaptureFlashDiagnosticOptions options)
    {
        if (!options.Enabled) return;
        var directory = AppDataPaths.ProductLogsRoot;
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory,
            $"capture-flash-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{options.Mode}.log");
        _clock = Stopwatch.StartNew();
        Write($"START mode={options.Mode} noActivate={options.NoActivate} noShade={options.NoShade}");
    }

    public static void Write(string message)
    {
        if (_path is null) return;
        lock (Gate)
            File.AppendAllText(_path,
                $"{DateTime.Now:O}|+{_clock.Elapsed.TotalMilliseconds:F1}ms|tid={Environment.CurrentManagedThreadId}|{message}{Environment.NewLine}");
    }

    public static string CreateArtifactPath(string name)
    {
        var directory = _path is null ? AppDataPaths.ProductLogsRoot :
            System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        return System.IO.Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{name}");
    }

    public static Bitmap CreateStaticDesktop(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var background = new System.Drawing.Drawing2D.LinearGradientBrush(
            new Rectangle(Point.Empty, bounds.Size), Color.FromArgb(38, 48, 68),
            Color.FromArgb(18, 22, 32), 25F);
        graphics.FillRectangle(background, 0, 0, bitmap.Width, bitmap.Height);
        using var font = new Font("Microsoft YaHei UI", 24F, FontStyle.Bold);
        graphics.DrawString("Overlay Only 静态诊断画面（右键退出）", font, Brushes.White, 50, 50);
        return bitmap;
    }
}
