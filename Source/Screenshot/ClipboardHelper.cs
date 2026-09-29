using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

internal static class ClipboardHelper
{
    internal static Action<Image>? ImageWriterForDiagnostics { get; set; }
    public static void SetImage(Image image)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var copy = new Bitmap(image);
                if(ImageWriterForDiagnostics is {} writer)writer(copy);else Clipboard.SetImage(copy);
                return;
            }
            catch (ExternalException exception)
            {
                last = exception;
                Thread.Sleep(60 * (attempt + 1));
            }
        }
        throw new InvalidOperationException("Windows 剪贴板正被其他程序占用，请稍后重试。", last);
    }

    public static void SetText(string text)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text ?? "");
                return;
            }
            catch (ExternalException exception)
            {
                last = exception;
                Thread.Sleep(60 * (attempt + 1));
            }
        }
        throw new InvalidOperationException("Windows 剪贴板正被其他程序占用，请稍后重试。", last);
    }
}
