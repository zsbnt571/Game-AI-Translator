using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class PostConfirmPerformanceTrace
{
    private static readonly object Gate=new();
    private static string? PathValue=>Environment.GetEnvironmentVariable("ST_06123_PERF_TRACE");
    internal static void Write(string stage,object value)
    {
        var path=PathValue;if(string.IsNullOrWhiteSpace(path))return;lock(Gate){Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);File.AppendAllText(path,JsonSerializer.Serialize(new{Timestamp=DateTimeOffset.Now,Stage=stage,ThreadId=Environment.CurrentManagedThreadId,IsUiThread=Application.MessageLoop,Value=value})+Environment.NewLine);}
    }
    internal static void FirstAsyncYield(double ms)=>Write("TimeToFirstAsyncYield",new{TimeToFirstAsyncYieldMs=ms});
    internal static void ImageHash(ImageHashDiagnostics d)=>Write("ImageHash",d);
}
