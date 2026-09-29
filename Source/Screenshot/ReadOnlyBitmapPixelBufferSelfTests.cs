using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

/// <summary>Compares the snapshot against independent GDI+ GetPixel evidence.</summary>
internal static class ReadOnlyBitmapPixelBufferSelfTests
{
    internal static int Run(string output, string? sourceManifest = null)
    {
        Directory.CreateDirectory(output);
        var results = new List<object>();
        var failures = 0;
        var dpis = new (float X, float Y)[] { (96,96),(144,144),(300,300),(144,96),(96,300),(143.9926f,143.9926f),(72,72) };
        var formats = new[] { PixelFormat.Format24bppRgb, PixelFormat.Format32bppArgb, PixelFormat.Format32bppPArgb, PixelFormat.Format8bppIndexed };
        foreach (var format in formats)
        foreach (var dpi in dpis)
        {
            var id = $"{format}-{dpi.X}-{dpi.Y}";
            try
            {
                using var source = Pattern(format);
                source.SetResolution(dpi.X, dpi.Y);
                var before = Pixels(source);
                using var buffer = ReadOnlyBitmapPixelBuffer.Create(source);
                var actual = BufferPixels(buffer);
                var different = Differences(before, actual);
                var unchanged = before.SequenceEqual(Pixels(source));
                var sizeEqual = source.Size == buffer.Size;
                // The snapshot must not borrow the source's lifetime or storage.
                if (format != PixelFormat.Format8bppIndexed) source.SetPixel(0,0,Color.Magenta);
                source.Dispose();
                var independent = actual.SequenceEqual(BufferPixels(buffer));
                var pass = different == 0 && unchanged && sizeEqual && independent;
                if (!pass) failures++;
                results.Add(new { Id=id, Pass=pass, DifferentPixels=different, SourceUnchanged=unchanged, SizeEqual=sizeEqual, IndependentAfterSourceDispose=independent, SourcePixelSHA256=Hash(before), BufferPixelSHA256=Hash(actual) });
            }
            catch (Exception ex) { failures++; results.Add(new { Id=id, Pass=false, Error=ex.ToString() }); }
        }
        if (sourceManifest is not null)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(sourceManifest));
            foreach (var fixture in manifest.RootElement.EnumerateArray())
            {
                var id = fixture.GetProperty("Id").GetString()!;
                var path = fixture.GetProperty("SourcePath").GetString()!;
                try
                {
                    using var source = new Bitmap(path);
                    var before = Pixels(source);
                    using var buffer = ReadOnlyBitmapPixelBuffer.Create(source);
                    var actual = BufferPixels(buffer);
                    var different = Differences(before, actual);
                    var unchanged = before.SequenceEqual(Pixels(source));
                    var pass = different == 0 && unchanged && source.Size == buffer.Size;
                    if (!pass) failures++;
                    using var stream = File.OpenRead(path);
                    var row = new { Id=id, Pass=pass, SourcePath=path, SourceSHA256=Convert.ToHexString(SHA256.HashData(stream)), source.Width, source.Height, DpiX=source.HorizontalResolution, DpiY=source.VerticalResolution, PixelFormat=source.PixelFormat.ToString(), DifferentPixels=different, SourceUnchanged=unchanged, SourcePixelSHA256=Hash(before), BufferPixelSHA256=Hash(actual) };
                    results.Add(row);
                    File.WriteAllText(Path.Combine(output,id+"-EXACT-SOURCE-BUFFER.json"),JsonSerializer.Serialize(row,new JsonSerializerOptions { WriteIndented=true }));
                }
                catch (Exception ex) { failures++; results.Add(new { Id=id, Pass=false, Error=ex.ToString() }); }
            }
        }
        File.WriteAllText(Path.Combine(output,"PIXEL-BUFFER-SELFTEST.json"),JsonSerializer.Serialize(new { Utc=DateTime.UtcNow, Pass=results.Count-failures, Fail=failures, Reference="Source Bitmap.GetPixel per pixel; no production buffer in reference", Results=results },new JsonSerializerOptions { WriteIndented=true }));
        return failures == 0 ? 0 : 1;
    }

    private static Bitmap Pattern(PixelFormat format)
    {
        var bitmap = new Bitmap(173,91,format);
        if (format == PixelFormat.Format8bppIndexed)
        {
            var palette = bitmap.Palette;
            for (var i=0;i<palette.Entries.Length;i++) palette.Entries[i] = Color.FromArgb(i%7==0?0:i%5==0?47:255,(i*31)%256,(i*73)%256,(i*117)%256);
            bitmap.Palette=palette;
            var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.WriteOnly,format);
            try
            {
                var row=new byte[bitmap.Width];
                for (var y=0;y<bitmap.Height;y++) { for(var x=0;x<bitmap.Width;x++) row[x]=(byte)((x*7+y*13)%256); Marshal.Copy(row,0,data.Scan0+y*data.Stride,row.Length); }
            }
            finally { bitmap.UnlockBits(data); }
        }
        else
        {
            int[] alpha = [0,1,47,128,254,255];
            for(var y=0;y<bitmap.Height;y++) for(var x=0;x<bitmap.Width;x++) bitmap.SetPixel(x,y,Color.FromArgb(alpha[(x+y)%alpha.Length],(x*31+y*7)%256,(x*13+y*37)%256,(x*3+y*83)%256));
        }
        return bitmap;
    }

    private static int[] Pixels(Bitmap bitmap)
    {
        var pixels = new int[bitmap.Width*bitmap.Height];
        for(var y=0;y<bitmap.Height;y++) for(var x=0;x<bitmap.Width;x++) pixels[y*bitmap.Width+x]=bitmap.GetPixel(x,y).ToArgb();
        return pixels;
    }
    private static int[] BufferPixels(ReadOnlyBitmapPixelBuffer buffer)
    {
        var pixels = new int[buffer.Width*buffer.Height];
        for(var y=0;y<buffer.Height;y++) for(var x=0;x<buffer.Width;x++) pixels[y*buffer.Width+x]=buffer.GetPixel(x,y).ToArgb();
        return pixels;
    }
    private static int Differences(int[] a,int[] b)=>a.Length!=b.Length?Math.Max(a.Length,b.Length):a.Zip(b).Count(x=>x.First!=x.Second);
    private static string Hash(int[] values)=>Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));
}
