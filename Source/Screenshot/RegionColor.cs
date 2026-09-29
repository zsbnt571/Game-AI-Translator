namespace ScreenshotTranslationUiTester;

// A local raster sample needs four channels, not Color's name/state payload.
// Values remain byte-exact; this changes storage, not color arithmetic.
internal readonly struct RegionColor
{
    public readonly byte R,G,B,A;
    public RegionColor(Color c){R=c.R;G=c.G;B=c.B;A=c.A;}
    public static implicit operator RegionColor(Color c)=>new(c);
    public static implicit operator Color(RegionColor c)=>Color.FromArgb(c.A,c.R,c.G,c.B);
    internal static Color Median(RegionColor[] colors,bool[] selected)
    {
        Span<int> histogram=stackalloc int[768];histogram.Clear();int n=0;
        for(int i=0;i<colors.Length;i++)if(selected[i])
        {var c=colors[i];histogram[c.R]++;histogram[256+c.G]++;histogram[512+c.B]++;n++;}
        if(n==0)throw new ArgumentException("Empty trusted donor set");
        return FromHistogram(histogram,n);
    }
    internal static Color FromHistogram(ReadOnlySpan<int> histogram,int n)
    {
        return Color.FromArgb(Channel(histogram[..256],n/2),Channel(histogram.Slice(256,256),n/2),Channel(histogram.Slice(512,256),n/2));
    }
    private static int Channel(ReadOnlySpan<int> histogram,int rank)
    {int count=0;for(int i=0;i<256;i++){count+=histogram[i];if(count>rank)return i;}return 255;}
}
