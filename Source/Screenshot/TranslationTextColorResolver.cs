namespace ScreenshotTranslationUiTester;

public sealed record TranslationTextColorResolution(
    Color ForegroundColor, int Alpha, double ContrastRatio, string Source,
    string Reason, float Confidence, Color BackgroundColor, double BackgroundVariance,
    Color? OriginalStyleHint);

public static class TranslationTextColorResolver
{
    public static TranslationTextColorResolution Resolve(Bitmap cleanBackground, RectangleF actualTextBounds,
        RegionRoleType role, Color? originalStyleHint = null)
    {
        var sample = Sample(cleanBackground, actualTextBounds);
        var white = Color.White;
        var black = Color.Black;
        var whiteContrast = Contrast(white, sample.Color);
        var blackContrast = Contrast(black, sample.Color);
        var best = whiteContrast >= blackContrast ? white : black;
        var bestContrast = Math.Max(whiteContrast, blackContrast);
        var minimum = role is RegionRoleType.Title or RegionRoleType.Header or RegionRoleType.Button or RegionRoleType.UILabel ? 3d : 4.5d;

        // A source color is only a style hint. It may survive when it is already readable and
        // materially as strong as the high-contrast choice; its old RGBA is never copied blindly.
        if (originalStyleHint is { } hint)
        {
            var styled = AdjustHint(hint, best, sample.Color, minimum);
            var styledContrast = Contrast(styled, sample.Color);
            // Preserve the source visual role (accent/secondary/metadata) whenever it is safe.
            // Requiring near-white/black contrast flattened every dark UI into the same white.
            if (styledContrast >= minimum)
                return new(styled, 255, styledContrast, "CleanBackground+StyleHint",
                    "Style hue retained after luminance adjustment; readability threshold satisfied", .92f,
                    sample.Color, sample.Variance, hint);
        }

        return new(best, 255, bestContrast, "CleanBackgroundHighContrast",
            $"Selected {(best == white ? "white" : "black")} from final clean-background contrast", .98f,
            sample.Color, sample.Variance, originalStyleHint);
    }

    public static double Contrast(Color foreground, Color background)
    {
        var a = RelativeLuminance(foreground); var b = RelativeLuminance(background);
        return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
    }

    private static Color AdjustHint(Color hint, Color target, Color background, double minimum)
    {
        var opaque=Color.FromArgb(255,hint);if(Contrast(opaque,background)>=minimum)return opaque;
        for(var step=1;step<=20;step++)
        {
            var t=step/20f;var mixed=Color.FromArgb(255,(int)(hint.R*(1-t)+target.R*t),(int)(hint.G*(1-t)+target.G*t),(int)(hint.B*(1-t)+target.B*t));
            if(Contrast(mixed,background)>=minimum)return mixed;
        }
        return target;
    }

    private static (Color Color,double Variance) Sample(Bitmap image, RectangleF area)
    {
        var b=Rectangle.Intersect(Rectangle.Round(area),new(0,0,image.Width,image.Height));
        if(b.Width<1||b.Height<1)return(Color.Black,0);
        var colors=new List<Color>();var step=Math.Max(1,Math.Min(b.Width,b.Height)/24);
        for(var y=b.Top;y<b.Bottom;y+=step)for(var x=b.Left;x<b.Right;x+=step)colors.Add(image.GetPixel(x,y));
        int Median(Func<Color,int> f)=>colors.Select(f).OrderBy(v=>v).ElementAt(colors.Count/2);
        var median=Color.FromArgb(Median(c=>c.R),Median(c=>c.G),Median(c=>c.B));
        var variance=colors.Average(c=>Math.Abs(c.R-median.R)+Math.Abs(c.G-median.G)+Math.Abs(c.B-median.B))/3d;
        return(median,variance);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Linear(byte v){var x=v/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}
        return .2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B);
    }
}
