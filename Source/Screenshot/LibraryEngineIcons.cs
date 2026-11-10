using System.Xml.Linq;
using W = System.Windows;
using WM = System.Windows.Media;
using WI = System.Windows.Media.Imaging;

namespace ScreenshotTranslationUiTester;

// Small, embedded engine marks; never fetch artwork while painting the library.
internal static class LibraryEngineIcons
{
    private static readonly Dictionary<(string,int,UiIconColorMode),WM.DrawingImage> images = new();
    private static readonly Dictionary<(string,int,UiIconColorMode),Bitmap> bitmaps = new();
    internal static string Key(string name)
    {
        if(name.StartsWith("WOLF",StringComparison.OrdinalIgnoreCase))return "wolf";
        if(name.StartsWith("RPG Maker",StringComparison.OrdinalIgnoreCase))return "rpgmaker";
        if(name.StartsWith("Construct",StringComparison.OrdinalIgnoreCase))return "construct3";
        if(name.StartsWith("Defold",StringComparison.OrdinalIgnoreCase))return "defold";
        if(name.StartsWith("Electron",StringComparison.OrdinalIgnoreCase))return "electron";
        if(name.StartsWith("GameMaker",StringComparison.OrdinalIgnoreCase))return "gamemaker";
        if(name.StartsWith("Godot",StringComparison.OrdinalIgnoreCase))return "godotengine";
        if(name.StartsWith("Ren",StringComparison.OrdinalIgnoreCase)&&name.Contains("Py",StringComparison.OrdinalIgnoreCase))return "renpy";
        if(name.StartsWith("Unity",StringComparison.OrdinalIgnoreCase))return "unity";
        if(name.StartsWith("Unreal",StringComparison.OrdinalIgnoreCase))return "unrealengine";
        return "unknown";
    }
    internal static WM.DrawingImage Image(string name,Color color,UiIconColorMode? colorMode=null)
    {
        var family=Key(name);
        var mode=colorMode??UiIconAppearance.Current;
        var key=(AssetKey(name),color.ToArgb(),mode);
        if(images.TryGetValue(key,out var image))return image;
        if(mode==UiIconColorMode.Original && family!="unrealengine" || family is "rpgmaker" or "wolf")
        {
            var raster=Raster(key.Item1,mode==UiIconColorMode.Monochrome);
            if(raster is not null)
            {
                double rasterScale=22d/Math.Max(raster.PixelWidth,raster.PixelHeight);
                double width=raster.PixelWidth*rasterScale,height=raster.PixelHeight*rasterScale;
                var artwork=new WM.DrawingGroup();
                using(var context=artwork.Open())
                {
                    context.DrawRectangle(WM.Brushes.Transparent,null,new W.Rect(0,0,24,24));
                    context.DrawImage(raster,new W.Rect((24-width)/2,(24-height)/2,width,height));
                }
                artwork.Freeze();image=new(artwork);image.Freeze();images.Add(key,image);return image;
            }
        }
        using var stream=typeof(LibraryEngineIcons).Assembly.GetManifestResourceStream("Fusion.EngineIcons."+family+".svg")!;
        var svg=XDocument.Load(stream);
        var path=svg.Descendants().Single(e=>e.Name.LocalName=="path");
        var geometry=WM.Geometry.Parse((path.Attribute("fill-rule")?.Value=="evenodd"?"F0 ":"F1 ")+path.Attribute("d")!.Value).Clone();
        var bounds=geometry.Bounds;
        double scale=22/Math.Max(bounds.Width,bounds.Height);
        geometry.Transform=new WM.MatrixTransform(scale,0,0,scale,
            (24-bounds.Width*scale)/2-bounds.X*scale,(24-bounds.Height*scale)/2-bounds.Y*scale);
        geometry.Freeze();
        var drawing=new WM.DrawingGroup();
        using(var context=drawing.Open())
        {
            context.DrawRectangle(WM.Brushes.Transparent,null,new W.Rect(0,0,24,24));
            context.DrawGeometry(WpfDrawing.Brush(color),null,geometry);
        }
        drawing.Freeze();image=new(drawing);image.Freeze();images.Add(key,image);return image;
    }
    internal static Bitmap Bitmap(string name,Color color)
    {
        var key=(AssetKey(name),color.ToArgb(),UiIconAppearance.Current);
        if(bitmaps.TryGetValue(key,out var bitmap))return bitmap;
        var visual=new WM.DrawingVisual();
        using(var context=visual.RenderOpen())context.DrawImage(Image(name,color),new W.Rect(0,0,48,48));
        var rendered=new WI.RenderTargetBitmap(48,48,96,96,WM.PixelFormats.Pbgra32);rendered.Render(visual);
        var encoder=new WI.PngBitmapEncoder();encoder.Frames.Add(WI.BitmapFrame.Create(rendered));
        using var stream=new MemoryStream();encoder.Save(stream);stream.Position=0;
        using var temporary=new Bitmap(stream);bitmap=new Bitmap(temporary);bitmaps.Add(key,bitmap);return bitmap;
    }
    internal static string AssetKey(string name)
    {
        if(name.StartsWith("RPG Maker MZ",StringComparison.OrdinalIgnoreCase))return "rpgmz";
        if(name.StartsWith("RPG Maker MV",StringComparison.OrdinalIgnoreCase))return "rpgmv";
        if(name.StartsWith("RPG Maker VX Ace",StringComparison.OrdinalIgnoreCase))return "rpgvxace";
        if(name.StartsWith("RPG Maker XP",StringComparison.OrdinalIgnoreCase))return "rpgxp";
        return Key(name);
    }
    private static WI.BitmapSource? Raster(string key,bool monochrome)
    {
        using var stream=typeof(LibraryEngineIcons).Assembly.GetManifestResourceStream("Fusion.EngineIcons.Original."+key+".png");
        if(stream is null)return null;
        WI.BitmapSource source=WI.BitmapFrame.Create(stream,WI.BitmapCreateOptions.PreservePixelFormat,WI.BitmapCacheOption.OnLoad);
        if(monochrome)
        {
            var rgba=new WI.FormatConvertedBitmap(source,WM.PixelFormats.Bgra32,null,0);
            int stride=rgba.PixelWidth*4;var pixels=new byte[stride*rgba.PixelHeight];rgba.CopyPixels(pixels,stride,0);
            for(int i=0;i<pixels.Length;i+=4)
            {
                byte gray=(byte)((77*pixels[i+2]+150*pixels[i+1]+29*pixels[i]+128)>>8);
                pixels[i]=pixels[i+1]=pixels[i+2]=gray;
            }
            source=WI.BitmapSource.Create(rgba.PixelWidth,rgba.PixelHeight,96,96,WM.PixelFormats.Bgra32,null,pixels,stride);
        }
        source.Freeze();return source;
    }
}
