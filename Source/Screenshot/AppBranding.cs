using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace ScreenshotTranslationUiTester;

// Transparent blue-cat assets share the same alpha-aware rendering across windows.
internal static class AppBranding
{
    private static readonly Lazy<Bitmap> Original = new(() =>
    {
        using var stream = Resource("BlueCat.Brand.png");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    });
    private static readonly Dictionary<Size, Bitmap> Logos = new();

    private static Stream Resource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException("Missing application brand asset: " + name);

    internal static Icon CreateIcon(Size? size = null)
    {
        using var stream = Resource("BlueCat.Brand.ico");
        using var source = new Icon(stream, size ?? SystemInformation.IconSize);
        return (Icon)source.Clone();
    }

    internal static void Apply(Form form)
    {
        var icon = CreateIcon();
        form.Icon = icon;
        form.Disposed += (_, _) => icon.Dispose();
    }

    internal static void DrawLogo(Graphics graphics, Rectangle bounds)
    {
        if (bounds.Width < 1 || bounds.Height < 1) return;
        Bitmap logo;
        lock (Logos)
        {
            if (!Logos.TryGetValue(bounds.Size, out logo!))
            {
                var source = Original.Value;
                logo = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using var drawing = Graphics.FromImage(logo);
                drawing.Clear(Color.Transparent);
                drawing.InterpolationMode = InterpolationMode.HighQualityBicubic;
                drawing.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var scale = Math.Min(bounds.Width / (float)source.Width, bounds.Height / (float)source.Height);
                var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                var height = Math.Max(1, (int)Math.Round(source.Height * scale));
                drawing.DrawImage(source, new Rectangle((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height));
                Logos.Add(bounds.Size, logo);
            }
        }
        graphics.DrawImageUnscaled(logo, bounds.Location);
    }
}
