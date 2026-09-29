using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

/// <summary>
/// Immutable ARGB snapshot used by read-heavy renderer analysis.  It replaces
/// repeated GDI+ GetPixel transitions without changing source ownership or pixels.
/// </summary>
internal sealed class ReadOnlyBitmapPixelBuffer : IDisposable
{
    private readonly Bitmap _copy;
    private readonly int[] _argb;
    private readonly int _width;
    private readonly int _height;
    internal int Width=>_width;
    internal int Height=>_height;
    internal Size Size=>new(_width,_height);
    private ReadOnlyBitmapPixelBuffer(Bitmap copy,int[] argb)
    {_copy=copy;_argb=argb;_width=copy.Width;_height=copy.Height;}

    internal static ReadOnlyBitmapPixelBuffer Create(Bitmap source)
    {
        // Clone copies pixel coordinates directly. Drawing even an "unscaled"
        // image can apply its DPI metadata and silently resample this snapshot.
        var rect=new Rectangle(0,0,source.Width,source.Height);
        var copy=source.Clone(rect,PixelFormat.Format32bppArgb);
        try
        {
            var values=new int[copy.Width*copy.Height];
            var data=copy.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            try
            {
                for(var y=0;y<copy.Height;y++)Marshal.Copy(data.Scan0+y*data.Stride,values,y*copy.Width,copy.Width);
            }
            finally{copy.UnlockBits(data);}
            return new ReadOnlyBitmapPixelBuffer(copy,values);
        }
        catch{copy.Dispose();throw;}
    }

    internal Color GetPixel(int x,int y)=>Color.FromArgb(_argb[y*Width+x]);
    public void Dispose()=>_copy.Dispose();
}
