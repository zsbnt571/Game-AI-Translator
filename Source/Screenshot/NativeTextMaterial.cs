using System.Drawing;
namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>
/// Source-pixel material evidence only. This owner does not choose a layout rectangle,
/// restore a background, reinterpret OCR text, or assign any final write authority.
/// </summary>
internal static class NativeTextMaterial
{
    internal sealed record Evidence(bool Accepted, string Reason, Rectangle Bounds, string Polarity,
        float RingBackground, float RingVariation, int Components, IReadOnlyList<Point> Primary,
        IReadOnlyList<Point> Antialias, IReadOnlyList<Point> Material)
    {
        internal int UncoveredOppositeOutlineComponents { get; init; }
        internal int UncoveredContrastComponents { get; init; }
        internal float SourceColorMinAlpha { get; init; }
        internal int SourceColorSupported { get; init; }
        internal int SourceColorMissing { get; init; }
        internal int SourceColorGrowth { get; init; }
        internal string SourceColorGate { get; init; } = "NO_VERIFIED_COLOR_CONTEXT";
    }

    internal static Evidence Observe(ReadOnlyBitmapPixelBuffer source, IReadOnlyList<PointF> polygon,
        bool[,]? protectedPixels = null, Color? sourceForeground = null, Color? sourceBackground = null, bool[,]? provenPrimary = null,
        int lightHaloPasses=2,float? intrinsicLineHeight=null)
    {
        Evidence Reject(string reason, Rectangle bounds, string polarity = "Uncertain",
            float background = 0, float variation = 0, int components = 0) =>
            new(false, reason, bounds, polarity, background, variation, components,
                Array.Empty<Point>(), Array.Empty<Point>(), Array.Empty<Point>());
        if (polygon.Count < 3) return Reject("NO_SOURCE_POLYGON", Rectangle.Empty);
        if (polygon.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y)))
            return Reject("NONFINITE_SOURCE_POLYGON", Rectangle.Empty);
        var bounds = Rectangle.Intersect(new Rectangle(0, 0, source.Width, source.Height),
            Rectangle.FromLTRB((int)MathF.Floor(polygon.Min(p => p.X)), (int)MathF.Floor(polygon.Min(p => p.Y)),
                (int)MathF.Ceiling(polygon.Max(p => p.X)), (int)MathF.Ceiling(polygon.Max(p => p.Y))));
        var letterHeight=intrinsicLineHeight??bounds.Height;
        if (bounds.Width < 4 || bounds.Height < 4 || bounds.Width > source.Width || letterHeight > 160 ||
            letterHeight<4 || bounds.Width*(long)bounds.Height>1_000_000)
            return Reject("UNSUPPORTED_TEXT_LINE_GEOMETRY", bounds);
        if (provenPrimary != null && (provenPrimary.GetLength(0) != source.Width || provenPrimary.GetLength(1) != source.Height))
            return Reject("INVALID_PRIMARY_MAP", bounds);
        if (protectedPixels != null && (protectedPixels.GetLength(0) != source.Width ||
            protectedPixels.GetLength(1) != source.Height))
            return Reject("INVALID_PROTECTED_MAP", bounds);
        var scan = Rectangle.Intersect(new Rectangle(0, 0, source.Width, source.Height),
            Rectangle.Inflate(bounds, 5, 5));
        int width = scan.Width, height = scan.Height;
        var gray = new byte[width * height];
        var inside = new bool[gray.Length];
        var protectedLocal = new bool[gray.Length];
        var ring = new List<byte>();
        var insideCount = 0;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var c = source.GetPixel(scan.X + x, scan.Y + y);
            var i = y * width + x;
            gray[i] = (byte)((299 * c.R + 587 * c.G + 114 * c.B + 500) / 1000);
            inside[i] = Contains(polygon, scan.X + x + .5f, scan.Y + y + .5f);
            protectedLocal[i] = protectedPixels != null && protectedPixels[scan.X + x, scan.Y + y];
            if (inside[i]) insideCount++; else ring.Add(gray[i]);
        }
        if (ring.Count < Math.Max(12, bounds.Width / 2) || insideCount < 12)
            return Reject("NO_EXTERNAL_LOCAL_BACKGROUND", bounds);
        ring.Sort();
        var background = ring[ring.Count / 2];
        var variationValues = ring.Select(v => Math.Abs(v - background)).OrderBy(v => v).ToArray();
        var variation = variationValues[(int)((variationValues.Length - 1) * .65)];
        var dark = background >= 135;
        var light = background <= 120;
        var polarity = dark ? "DarkOnLight" : "LightOnDark";
        if (!dark && !light) return Reject("AMBIGUOUS_LOCAL_POLARITY", bounds, "Uncertain", background, variation);
        var kernel = Math.Clamp((int)MathF.Round(letterHeight * .22f), 3, 19);
        if (kernel % 2 == 0) kernel++;
        var radius = kernel / 2;
        // Local close/open estimates a contrast trend, never a candidate background.
        var trend = Extremum(Extremum(gray, width, height, radius, dark), width, height, radius, !dark);
        var initial = new bool[gray.Length];
        var contrast = new int[gray.Length];
        var initialCount = 0;
        for (var i = 0; i < gray.Length; i++)
        {
            contrast[i] = dark ? trend[i] - gray[i] : gray[i] - trend[i];
            var extreme = dark ? gray[i] <= Math.Min(100, background - 55) :
                gray[i] >= Math.Max(175, background + 55);
            initial[i] = inside[i] && !protectedLocal[i] && extreme && contrast[i] >= 35;
            if (initial[i]) initialCount++;
        }
        var core = new bool[gray.Length]; var visited = new bool[gray.Length]; var components = 0;
        for (var i = 0; i < initial.Length; i++)
        {
            if (!initial[i] || visited[i]) continue;
            var points = new List<int>(); var queue = new Queue<int>();
            queue.Enqueue(i); visited[i] = true;
            int left = width, right = 0, top = height, bottom = 0;
            while (queue.Count > 0)
            {
                var p = queue.Dequeue(); points.Add(p); var x = p % width; var y = p / width;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                for (var yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++)
                for (var xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
                {
                    var n = yy * width + xx;
                    if (initial[n] && !visited[n]) { visited[n] = true; queue.Enqueue(n); }
                }
            }
            if (points.Count < 2 || right - left + 1 > Math.Max(letterHeight * 1.45f, 12) ||
                bottom - top + 1 > letterHeight * (intrinsicLineHeight is null?1.05f:1.45f)) continue;
            components++;
            foreach (var p in points) core[p] = true;
        }
        var coreCount = core.Count(v => v);
        var fraction = coreCount / (float)insideCount;
        if (components < 2 || fraction < .008f || fraction > .42f)
            return Reject("NO_CHARACTER_COMPONENT_CONTEXT", bounds, polarity, background, variation, components);
        // Do not silently clean only half of a source line when rules/art overwhelm it.
        if (coreCount < initialCount * .82f)
            return Reject("SIGNIFICANT_NON_GLYPH_STRUCTURE", bounds, polarity, background, variation, components);
        var material = (bool[])core.Clone();
        // Ring brightness variation includes gradients; estimate local AA noise instead.
        var residualNoise = contrast.Where((_, i) => !inside[i]).OrderBy(v => v).ToArray();
        var localNoise = residualNoise[(int)((residualNoise.Length - 1) * .85)];
        var minimumContrast = Math.Max(2, Math.Min(5, localNoise * .4f));
        for (var pass = 0; pass < (light?Math.Clamp(lightHaloPasses,2,10):2); pass++)
        {
            var next = (bool[])material.Clone();
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                if (material[i] || !inside[i] || protectedLocal[i] || contrast[i] < minimumContrast) continue;
                var adjacent = false;
                for (var yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1) && !adjacent; yy++)
                for (var xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
                    if (material[yy * width + xx]) { adjacent = true; break; }
                if (adjacent) next[i] = true;
            }
            material = next;
        }
        var colorSupported=0;var colorMissing=0;var colorGrowth=0;var colorMinAlpha=.12f;var colorGate="NO_VERIFIED_COLOR_CONTEXT";
        if(dark&&sourceForeground is { } foreground&&sourceBackground is { } backdrop)
        {
            var vr=backdrop.R-foreground.R;var vg=backdrop.G-foreground.G;var vb=backdrop.B-foreground.B;
            var length=vr*vr+vg*vg+vb*vb;
            var backdropLuma=.299f*backdrop.R+.587f*backdrop.G+.114f*backdrop.B;
            var foregroundLuma=.299f*foreground.R+.587f*foreground.G+.114f*foreground.B;
            if(length>0&&backdropLuma-foregroundLuma>=45)
            {
                var flatColorEvidence=variation<=4&&Math.Abs(backdropLuma-background)<=12;
                var family=new bool[gray.Length];var required=new bool[gray.Length];
                if(flatColorEvidence)
                {
                    var ringAlpha=new List<float>();
                    for(var i=0;i<gray.Length;i++)if(!inside[i]&&!protectedLocal[i])
                    {
                        var c=source.GetPixel(scan.X+i%width,scan.Y+i/width);
                        // Neighboring OCR lines and controls in the external ring are not background noise.
                        if(Math.Max(Math.Abs(c.R-backdrop.R),Math.Max(Math.Abs(c.G-backdrop.G),Math.Abs(c.B-backdrop.B)))<=Math.Max(4,variation*2+3))
                            ringAlpha.Add(((backdrop.R-c.R)*vr+(backdrop.G-c.G)*vg+(backdrop.B-c.B)*vb)/(float)length);
                    }
                    ringAlpha.Sort();
                    if(ringAlpha.Count>0)
                        colorMinAlpha=Math.Max(.008f,ringAlpha[(int)((ringAlpha.Count-1)*.99f)]+
                            1f/Math.Max(1,Math.Max(Math.Abs(vr),Math.Max(Math.Abs(vg),Math.Abs(vb)))));
                }
                for(var i=0;i<gray.Length;i++)
                {
                    if(!inside[i]||protectedLocal[i])continue;
                    var c=source.GetPixel(scan.X+i%width,scan.Y+i/width);
                    var alpha=((backdrop.R-c.R)*vr+(backdrop.G-c.G)*vg+(backdrop.B-c.B)*vb)/(float)length;
                    var error=Math.Max(Math.Abs(c.R-(backdrop.R-alpha*vr)),
                        Math.Max(Math.Abs(c.G-(backdrop.G-alpha*vg)),Math.Abs(c.B-(backdrop.B-alpha*vb))));
                    family[i]=alpha>=colorMinAlpha&&alpha<=1.3f&&error<=12;
                    // On textured/graded source, require both established primary and
                    // strong foreground color; paper brightness is not text material.
                    required[i]=family[i]&&((flatColorEvidence&&alpha>=.12f)||
                        (alpha>=.65f&&provenPrimary is not null&&provenPrimary[scan.X+i%width,scan.Y+i/width]));
                }
                if(flatColorEvidence)
                {
                    var queue=new Queue<int>();
                    for(var i=0;i<material.Length;i++)if(material[i])queue.Enqueue(i);
                    while(queue.Count>0)
                    {
                        var i=queue.Dequeue();var x=i%width;var y=i/width;
                        for(var yy=Math.Max(0,y-1);yy<=Math.Min(height-1,y+1);yy++)
                        for(var xx=Math.Max(0,x-1);xx<=Math.Min(width-1,x+1);xx++)
                        {
                            var j=yy*width+xx;
                            if(material[j]||!family[j])continue;
                            material[j]=true;colorGrowth++;queue.Enqueue(j);
                        }
                    }
                }
                for(var i=0;i<required.Length;i++)if(required[i])
                {colorSupported++;if(!material[i])colorMissing++;}
                colorGate=flatColorEvidence?"FLAT_SOURCE_COLOR_FAMILY_CONNECTED_COMPLETION":"SOURCE_COLOR_SUPPORTED_PRIMARY_COVERAGE";
                if(colorMissing>Math.Max(4,colorSupported*.005f)||material.Count(v=>v)>insideCount*.65f)
                    return Reject("SOURCE_COLOR_COVERAGE_INCOMPLETE",bounds,polarity,background,variation,components) with
                    {SourceColorMinAlpha=colorMinAlpha,SourceColorSupported=colorSupported,SourceColorMissing=colorMissing,SourceColorGrowth=colorGrowth,SourceColorGate=colorGate};
            }
        }
        // Completeness is independently checked against established primary pixels and
        // source local contrast. A foreground color fit cannot certify a differently lit
        // gray word that never matched that fit. Small texture noise is not a missing glyph.
        var uncoveredComponents=0;
        if(dark&&provenPrimary is not null)
        {
            var missed=new bool[gray.Length];var seen=new bool[gray.Length];
            for(var i=0;i<gray.Length;i++)
                missed[i]=inside[i]&&!protectedLocal[i]&&!material[i]&&gray[i]>100&&
                    gray[i]<=background-28&&contrast[i]>=22&&provenPrimary[scan.X+i%width,scan.Y+i/width];
            for(var i=0;i<missed.Length;i++)
            {
                if(!missed[i]||seen[i])continue;
                var queue=new Queue<int>();queue.Enqueue(i);seen[i]=true;
                int count=0,left=width,right=0,top=height,bottom=0;
                while(queue.Count>0)
                {
                    var j=queue.Dequeue();var x=j%width;var y=j/width;count++;
                    left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);
                    for(var yy=Math.Max(0,y-1);yy<=Math.Min(height-1,y+1);yy++)
                    for(var xx=Math.Max(0,x-1);xx<=Math.Min(width-1,x+1);xx++)
                    {
                        var k=yy*width+xx;if(missed[k]&&!seen[k]){seen[k]=true;queue.Enqueue(k);}
                    }
                }
                if(count>=Math.Max(12,bounds.Height*.5f)&&bottom-top+1>=bounds.Height*.18f&&
                    right-left+1<=Math.Max(12,bounds.Height*1.45f))uncoveredComponents++;
            }
            if(uncoveredComponents>0)
                return Reject("UNCOVERED_SOURCE_CONTRAST_COMPONENT",bounds,polarity,background,variation,components) with
                {UncoveredContrastComponents=uncoveredComponents,SourceColorMinAlpha=colorMinAlpha,
                    SourceColorSupported=colorSupported,SourceColorMissing=colorMissing,SourceColorGrowth=colorGrowth,SourceColorGate=colorGate};
        }
        // Dark-only material cannot certify the removal of a bright outline.
        // This is a refusal of replacement, not permission to erase new pixels.
        if(dark&&background<235&&colorGate=="FLAT_SOURCE_COLOR_FAMILY_CONNECTED_COMPLETION")
        {
            var opened=Extremum(Extremum(gray,width,height,radius,false),width,height,radius,true);
            var opposite=new bool[gray.Length];var seen=new bool[gray.Length];
            for(var i=0;i<gray.Length;i++)
                opposite[i]=inside[i]&&!protectedLocal[i]&&!material[i]&&gray[i]>=background+2&&gray[i]-opened[i]>=2;
            var oppositeComponents=0;
            for(var i=0;i<opposite.Length;i++)
            {
                if(!opposite[i]||seen[i])continue;
                var queue=new Queue<int>();queue.Enqueue(i);seen[i]=true;
                int count=0,left=width,right=0,top=height,bottom=0;var nearGlyph=false;
                while(queue.Count>0)
                {
                    var j=queue.Dequeue();var x=j%width;var y=j/width;count++;
                    left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);
                    for(var yy=Math.Max(0,y-3);yy<=Math.Min(height-1,y+3)&&!nearGlyph;yy++)
                    for(var xx=Math.Max(0,x-3);xx<=Math.Min(width-1,x+3);xx++)
                        if(core[yy*width+xx]){nearGlyph=true;break;}
                    for(var yy=Math.Max(0,y-1);yy<=Math.Min(height-1,y+1);yy++)
                    for(var xx=Math.Max(0,x-1);xx<=Math.Min(width-1,x+1);xx++)
                    {
                        var k=yy*width+xx;if(opposite[k]&&!seen[k]){seen[k]=true;queue.Enqueue(k);}
                    }
                }
                if(nearGlyph&&count>=Math.Max(12,bounds.Height*.5f)&&bottom-top+1>=bounds.Height*.18f&&
                    right-left+1<=Math.Max(12,bounds.Height*1.45f))oppositeComponents++;
            }
            if(oppositeComponents>0)
                return Reject("UNCOVERED_OPPOSITE_POLARITY_OUTLINE",bounds,polarity,background,variation,components) with
                {UncoveredOppositeOutlineComponents=oppositeComponents,SourceColorMinAlpha=colorMinAlpha,
                    SourceColorSupported=colorSupported,SourceColorMissing=colorMissing,SourceColorGrowth=colorGrowth,SourceColorGate=colorGate};
        }
        var primary = new List<Point>(); var antialias = new List<Point>(); var all = new List<Point>();
        for (var i = 0; i < material.Length; i++)
        {
            if (!material[i]) continue;
            var point = new Point(scan.X + i % width, scan.Y + i / width);
            all.Add(point); if (core[i]) primary.Add(point); else antialias.Add(point);
        }
        return new Evidence(true, "SOURCE_EXTREME_STROKES_AND_LOCAL_CONTRAST", bounds, polarity,
            background, variation, components, primary, antialias, all) with
            {SourceColorMinAlpha=colorMinAlpha,SourceColorSupported=colorSupported,SourceColorMissing=colorMissing,SourceColorGrowth=colorGrowth,SourceColorGate=colorGate};
    }

    internal static bool Contains(IReadOnlyList<PointF> polygon, float x, float y)
    {
        var result = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                result = !result;
        }
        return result;
    }

    private static byte[] Extremum(byte[] input, int width, int height, int radius, bool maximum)
    {
        var horizontal = new byte[input.Length]; var result = new byte[input.Length];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            byte v = maximum ? (byte)0 : (byte)255;
            for (var xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx++)
                v = maximum ? Math.Max(v, input[y * width + xx]) : Math.Min(v, input[y * width + xx]);
            horizontal[y * width + x] = v;
        }
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            byte v = maximum ? (byte)0 : (byte)255;
            for (var yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy++)
                v = maximum ? Math.Max(v, horizontal[yy * width + x]) : Math.Min(v, horizontal[yy * width + x]);
            result[y * width + x] = v;
        }
        return result;
    }
}
