using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Web.Script.Serialization;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 1 && args[0] == "--languages")
            {
                foreach (var language in OcrEngine.AvailableRecognizerLanguages)
                    Console.WriteLine(language.LanguageTag);
                return 0;
            }

            if (args.Length != 2)
                throw new ArgumentException("用法：ScreenshotOcrHelper <图片路径> <OCR语言>");

            var file = StorageFile.GetFileFromPathAsync(Path.GetFullPath(args[0])).AsTask().GetAwaiter().GetResult();
            using (IRandomAccessStream stream = file.OpenAsync(FileAccessMode.Read).AsTask().GetAwaiter().GetResult())
            {
                var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
                var bitmap = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
                var engine = OcrEngine.TryCreateFromLanguage(new Language(args[1]));
                if (engine == null)
                    throw new InvalidOperationException("未安装OCR语言：" + args[1]);
                var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
                var regions = new List<object>();
                foreach (var line in result.Lines)
                {
                    if (line.Words.Count == 0) continue;
                    var left = line.Words.Min(w => w.BoundingRect.X);
                    var top = line.Words.Min(w => w.BoundingRect.Y);
                    var right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                    var bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                    regions.Add(new
                    {
                        text = line.Text, x = left, y = top,
                        width = right - left, height = bottom - top
                    });
                }

                var json = new JavaScriptSerializer().Serialize(new
                {
                    text = result.Text,
                    width = bitmap.PixelWidth,
                    height = bitmap.PixelHeight,
                    regions
                });
                Console.WriteLine(json);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetBaseException().Message);
            return 1;
        }
    }
}
