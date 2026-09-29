using System.Drawing.Drawing2D;
using System.Text.Json.Serialization;

namespace ScreenshotTranslationUiTester;

public enum BackgroundSurfaceType { FlatColor, HorizontalGradient, VerticalGradient, BilinearGradient, Texture, ComplexImage, SemiTransparentUi, UnknownUnsafe }
public enum BackgroundReconstructionStrategy { RobustFlatFill, LinearGradient, EdgePropagation, LocalInpaint, PreserveOriginal, HorizontalSurfaceContinuation }

public sealed class BackgroundIntegrationPlan
{
    public required string RegionId { get; init; }
    public required RegionRoleType Role { get; init; }
    public required Rectangle CleanupBounds { get; init; }
    public required IReadOnlyList<PointF[]> SourceTextPolygons { get; init; }
    public required IReadOnlyList<PointF[]> ProtectedGeometry { get; init; }
    public required BackgroundSurfaceType BackgroundType { get; init; }
    public required BackgroundReconstructionStrategy ReconstructionStrategy { get; init; }
    public required float Confidence { get; init; }
    public required bool SafeToCommit { get; init; }
    public required string FailureReason { get; init; }
    public required string MaskBasis { get; init; }
    [JsonIgnore] public required bool[,] CleanupMask { get; init; }
    [JsonIgnore] public required bool[,] PrimaryMask { get; init; }
    [JsonIgnore] public required bool[,] PunctuationMask { get; init; }
}

public sealed record BackgroundIntegrationResult(Bitmap Bitmap, BackgroundIntegrationPlan Plan,
    bool Committed, string ValidationFailure, int ChangedPixels, int ChangedOutsideMask) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

public sealed record BackgroundIntegrationRoiResult(Bitmap Bitmap, Rectangle Bounds,
    BackgroundIntegrationPlan Plan,bool Committed,string ValidationFailure,int ChangedPixels,int ChangedOutsideMask) : IDisposable
{ public void Dispose()=>Bitmap.Dispose(); }

public static class BackgroundIntegrationPlanner
{
    public static BackgroundIntegrationPlan Plan(Bitmap source, RecognitionRegion region,
        RectangleF safeTarget, IReadOnlyList<RecognitionRegion> allRegions)
    {
        var polygons = (region.SourceLinePolygons.Count > 0 ? region.SourceLinePolygons : [region.Polygon])
            .Where(p => p.Length >= 3).Select(p => p.ToArray()).ToArray();
        var protectedPolygons = region.LayoutExclusionPolygons.Select(p => p.ToArray())
            .Concat(allRegions.Where(r => r.RegionId != region.RegionId)
                .SelectMany(r => r.SourceLinePolygons.Count > 0 ? r.SourceLinePolygons : [r.Polygon])
                .Select(p => p.ToArray())).ToArray();
        if (polygons.Length == 0)
            return Unsafe(region, polygons, protectedPolygons, "Missing source text polygon", source.Size);

        var union = polygons.Select(GeometryV2.Bounds).Aggregate(RectangleF.Union);
        var bounds = Rectangle.Round(RectangleF.Intersect(RectangleF.Inflate(union, 3, 2), new(PointF.Empty, source.Size)));
        if (bounds.Width < 2 || bounds.Height < 2)
            return Unsafe(region, polygons, protectedPolygons, "Empty cleanup bounds", source.Size);
        var mask = new bool[source.Width, source.Height];
        using (var raster = new Bitmap(source.Width, source.Height))
        using (var g = Graphics.FromImage(raster))
        {
            g.Clear(Color.Black); g.SmoothingMode = SmoothingMode.None;
            foreach (var polygon in polygons)
            {
                var pb=GeometryV2.Bounds(polygon);var overlap=region.LayoutExclusionPolygons.Select(GeometryV2.Bounds).Select(x=>RectangleF.Intersect(x,pb)).Where(x=>x.Width>0&&x.Height>0).Sum(x=>x.Width*x.Height)/Math.Max(1,pb.Width*pb.Height);
                RasterizeInkMask(source,raster,polygon,overlap>.55f);
            }
            var primary=ReadMask(raster,bounds,source.Size);
            var punctuation=RecoverPunctuation(source,raster,polygons,primary,bounds,
                string.IsNullOrWhiteSpace(region.StructuredText)?region.OcrText:region.StructuredText);
            bounds=MaskBounds(primary,punctuation,bounds,source.Size);
            using var ownInk = new Bitmap(raster);
            // Protection is absolute outside legitimate source ink geometry. This prevents a padded
            // cleanup mask from damaging an image/UI neighbor while retaining real source overlap.
            foreach (var p in protectedPolygons)
            {
                using var protect = new SolidBrush(Color.Black); g.FillPolygon(protect, p);
            }
            for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
                if(ownInk.GetPixel(x,y).R>127)raster.SetPixel(x,y,Color.White);
            for (var y = bounds.Top; y < bounds.Bottom; y++)
            for (var x = bounds.Left; x < bounds.Right; x++) mask[x, y] = raster.GetPixel(x, y).R > 127;
            var sourceLineHeight=polygons.Select(GeometryV2.Bounds).Select(x=>x.Height).Where(x=>x>2).DefaultIfEmpty(union.Height).OrderBy(x=>x).ElementAt(polygons.Length/2);
            var strokeEnvelope=Rectangle.Intersect(Rectangle.Round(RectangleF.Inflate(union,Math.Clamp(sourceLineHeight*.20f,4,10),Math.Clamp(sourceLineHeight*.16f,3,8))),new Rectangle(Point.Empty,source.Size));
            for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)if(!strokeEnvelope.Contains(x,y))mask[x,y]=false;
            // Protection can remove a neighbouring UI component that was discovered in the
            // bounded ink search. The commit bounds must follow the final owned mask, not the
            // earlier unprotected component envelope.
            bounds=MaskBounds(mask,new bool[source.Width,source.Height],bounds,source.Size);
            return FinishPlan(source,region,allRegions,polygons,protectedPolygons,bounds,mask,primary,punctuation);
        }
    }
    private static BackgroundIntegrationPlan FinishPlan(Bitmap source,RecognitionRegion region,IReadOnlyList<RecognitionRegion> allRegions,PointF[][] polygons,PointF[][] protectedPolygons,Rectangle bounds,bool[,] mask,bool[,] primary,bool[,] punctuation)
    {
        var samples = RingSamples(source, bounds, mask);
        if (samples.Count < 12) return Unsafe(region, polygons, protectedPolygons, "Insufficient uncontaminated background samples", source.Size, mask, bounds);
        var classification = Classify(samples, bounds);
        int MedianSample(Func<Color,int> f)=>samples.Select(x=>f(x.Color)).OrderBy(x=>x).ElementAt(samples.Count/2);
        var sampleBackground=Color.FromArgb(MedianSample(c=>c.R),MedianSample(c=>c.G),MedianSample(c=>c.B));
        {
            // Residual pass for thin/italic/anti-aliased glyphs. The local dominant color is
            // derived inside each source line, so a colored button is preserved while every
            // low-opacity text fringe is admitted to the glyph mask.
            foreach(var polygon in polygons){var pb=Rectangle.Intersect(Rectangle.Round(GeometryV2.Bounds(polygon)),new Rectangle(Point.Empty,source.Size));if(pb.Width<2||pb.Height<2)continue;var colors=new List<Color>();var step=Math.Max(1,Math.Min(pb.Width,pb.Height)/24);for(var y=pb.Top;y<pb.Bottom;y+=step)for(var x=pb.Left;x<pb.Right;x+=step)colors.Add(source.GetPixel(x,y));if(colors.Count<8)continue;var dominant=colors.GroupBy(c=>(c.R/12,c.G/12,c.B/12)).OrderByDescending(g=>g.Count()).First().ToArray();if(dominant.Length<colors.Count*.68f)continue;int Med(Func<Color,int> f)=>dominant.Select(f).OrderBy(v=>v).ElementAt(dominant.Length/2);var localBg=Color.FromArgb(Med(c=>c.R),Med(c=>c.G),Med(c=>c.B));for(var y=pb.Top;y<pb.Bottom;y++)for(var x=pb.Left;x<pb.Right;x++){var c=source.GetPixel(x,y);var delta=Math.Abs(c.R-localBg.R)+Math.Abs(c.G-localBg.G)+Math.Abs(c.B-localBg.B);if(delta<20)continue;for(var yy=Math.Max(pb.Top,y-2);yy<=Math.Min(pb.Bottom-1,y+2);yy++)for(var xx=Math.Max(pb.Left,x-2);xx<=Math.Min(pb.Right-1,x+2);xx++)mask[xx,yy]=true;}}
            // A second, slightly expanded identity band handles OCR boxes that start a few
            // pixels inside italic ascenders or punctuation. Admission still requires a highly
            // dominant local surface, so photographic/image regions cannot become rectangles.
            var identityText=string.IsNullOrWhiteSpace(region.StructuredText)?region.OcrText:region.StructuredText;
            var initialMaskPixels=Count(mask,bounds);var identityChars=identityText.Count(c=>!char.IsWhiteSpace(c));var identityHeight=polygons.Select(GeometryV2.Bounds).Select(x=>x.Height).DefaultIfEmpty(bounds.Height).OrderBy(x=>x).ElementAt(polygons.Length/2);
            var needsExpandedIdentityBand=initialMaskPixels<Math.Max(12,identityChars*identityHeight*identityHeight*.035f);
            foreach(var polygon in needsExpandedIdentityBand?polygons:[])
            {
                var raw=Rectangle.Round(GeometryV2.Bounds(polygon));
                var pad=Math.Clamp((int)MathF.Round(raw.Height*.28f),3,14);
                var scan=Rectangle.Intersect(Rectangle.Inflate(raw,pad,pad),new Rectangle(Point.Empty,source.Size));
                if(scan.Width<2||scan.Height<2)continue;
                var samplesLocal=new List<Color>();var stepLocal=Math.Max(1,Math.Min(scan.Width,scan.Height)/24);
                for(var y=scan.Top;y<scan.Bottom;y+=stepLocal)for(var x=scan.Left;x<scan.Right;x+=stepLocal)samplesLocal.Add(source.GetPixel(x,y));
                if(samplesLocal.Count<8)continue;
                var dominantLocal=samplesLocal.GroupBy(c=>(c.R/12,c.G/12,c.B/12)).OrderByDescending(g=>g.Count()).First().ToArray();
                if(dominantLocal.Length<samplesLocal.Count*.68f)continue;
                int MedianLocal(Func<Color,int> f)=>dominantLocal.Select(f).OrderBy(v=>v).ElementAt(dominantLocal.Length/2);
                var localBackground=Color.FromArgb(MedianLocal(c=>c.R),MedianLocal(c=>c.G),MedianLocal(c=>c.B));
                for(var y=scan.Top;y<scan.Bottom;y++)for(var x=scan.Left;x<scan.Right;x++)
                {var c=source.GetPixel(x,y);var delta=Math.Abs(c.R-localBackground.R)+Math.Abs(c.G-localBackground.G)+Math.Abs(c.B-localBackground.B);if(delta<18)continue;for(var yy=Math.Max(scan.Top,y-2);yy<=Math.Min(scan.Bottom-1,y+2);yy++)for(var xx=Math.Max(scan.Left,x-2);xx<=Math.Min(scan.Right-1,x+2);xx++)mask[xx,yy]=true;}
            }
            bounds=MaskBounds(mask,punctuation,bounds,source.Size);
        }
        var sourceForeground=TextStyleHintExtractor.EstimateForeground(source,region,sampleBackground);
        var sourceContrast=sourceForeground is null?0:TranslationTextColorResolver.Contrast(sourceForeground.Value,sampleBackground);
        // A uniform panel with no detectable source ink is still a valid empty draw target (and
        // occurs in editor-created Regions). Low-contrast separation is a veto only when the
        // surface itself is not a high-confidence flat panel.
        var lowContrastCompactInk=classification.Type!=BackgroundSurfaceType.FlatColor
            &&(sourceForeground is null||sourceContrast<3.2||sampleBackground.GetBrightness()>.72f&&sourceContrast<4.5);
        var masked = Count(mask, bounds);
        var independentDecorativeGeometry=region.RoleType is RegionRoleType.CharacterName or RegionRoleType.Species
            &&region.SourceBlockIds.Count==1&&region.SourceLinePolygons.Count==1;
        if(independentDecorativeGeometry&&lowContrastCompactInk)
        {
            // Low-contrast ornamental letters may have no separable foreground component.
            // In that narrow case the exact, separately-owned OCR polygon is the cleanup owner;
            // neighbouring Name/Species polygons remain protected and a Header union is forbidden.
            for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
                if(polygons.Any(p=>PointInPolygon(new PointF(x+.5f,y+.5f),p))&&!protectedPolygons.Any(p=>PointInPolygon(new PointF(x+.5f,y+.5f),p)))mask[x,y]=true;
            masked=Count(mask,bounds);
        }
        var sourceText=string.IsNullOrWhiteSpace(region.StructuredText)?region.OcrText:region.StructuredText;
        var textCharacters=sourceText.Count(c=>!char.IsWhiteSpace(c));
        var lineHeights=polygons.Select(GeometryV2.Bounds).Select(x=>x.Height).Where(x=>x>2).OrderBy(x=>x).ToArray();
        var medianLineHeight=lineHeights.Length==0?bounds.Height:lineHeights[(lineHeights.Length-1)/2];
        var expectedInk=Math.Min(bounds.Width*bounds.Height*.65f,textCharacters*medianLineHeight*medianLineHeight*.10f);
        var incompleteInk=textCharacters>=8&&masked<expectedInk*.28f;
        var imageOverlapRatio=polygons.Select(GeometryV2.Bounds).Sum(pb=>region.LayoutExclusionPolygons.Select(GeometryV2.Bounds).Select(x=>RectangleF.Intersect(x,pb)).Where(x=>x.Width>0&&x.Height>0).Sum(x=>x.Width*x.Height))/Math.Max(1,polygons.Select(GeometryV2.Bounds).Sum(x=>x.Width*x.Height));
        var visualComplexity=polygons.Select(p=>VisualComplexity(source,GeometryV2.Bounds(p))).DefaultIfEmpty(0).Max();
        var reliablePanel=classification.Type==BackgroundSurfaceType.FlatColor&&classification.Confidence>=.72f
            ||classification.Type is BackgroundSurfaceType.HorizontalGradient or BackgroundSurfaceType.VerticalGradient or BackgroundSurfaceType.BilinearGradient
                &&classification.Confidence>=.75f&&visualComplexity<.22f;
        if(reliablePanel)
        {
            // A reliable panel authorizes reconstruction, never a rectangular erase. Keep the
            // glyph-shaped mask so an existing button/choice surface remains byte-for-byte
            // untouched away from the source ink.
            masked=Count(mask,bounds);
            incompleteInk=textCharacters>=8&&masked<expectedInk*.28f;
        }
        // Complexity of the enclosing OCR rectangle is not itself a veto. A title may span a
        // continuous UI panel while its rectangle touches an image exclusion. Only a background
        // actually classified as texture/complex image uses image overlap or visual complexity
        // as the unsafe signal; flat/gradient panel continuation remains reconstructable.
        // Runtime trace 0.5.0.6.12: a semi-transparent game Choice can legitimately expose a
        // textured scene through its pill background. Both OCR lines and SourceIds were present,
        // and the glyph mask was complete, but the generic texture veto preserved every English
        // pixel while the translated label was still drawn. A complete, glyph-shaped Choice mask
        // is safe for local inpaint; this does not authorize a component/rectangle erase.
        var polygonArea=Math.Max(1,polygons.Select(GeometryV2.Bounds).Sum(x=>x.Width*x.Height));
        static float MedianLineHeight(RecognitionRegion r){var values=r.SourceLinePolygons.Select(GeometryV2.Bounds).Select(x=>x.Height).Where(x=>x>2).OrderBy(x=>x).ToArray();return values.Length==0?r.BoundingBox.Height:values[(values.Length-1)/2];}
        var ownLineHeight=MedianLineHeight(region);
        var repeatedCompactUi=region.BoundingBox.Width<source.Width*.45f&&region.BoundingBox.Height<source.Height*.16f&&sourceText.Length<=48&&
            allRegions.Count(r=>r.RegionId!=region.RegionId&&r.BoundingBox.Width<source.Width*.45f&&r.BoundingBox.Height<source.Height*.16f&&
                (string.IsNullOrWhiteSpace(r.StructuredText)?r.OcrText:r.StructuredText).Length<=48&&Math.Max(ownLineHeight,MedianLineHeight(r))/Math.Max(1,Math.Min(ownLineHeight,MedianLineHeight(r)))<=1.55f)>=2;
        var structuredIdentityHeader=region.RoleType==RegionRoleType.Header&&region.SourceBlockIds.Count==1&&polygons.Length==1&&sourceText.Contains('|');
        if(repeatedCompactUi||region.RoleType==RegionRoleType.Choice||structuredIdentityHeader)
        {
            if(sourceForeground is { } repeatedForeground)RecoverResidualOutlineComponents(source,polygons,mask,repeatedForeground,sampleBackground);
            var allowedUnion=polygons.Select(GeometryV2.Bounds).Aggregate(RectangleF.Union);var allowedPad=Math.Clamp(ownLineHeight*.32f,5,14);
            var allowed=Rectangle.Intersect(Rectangle.Round(RectangleF.Inflate(allowedUnion,allowedPad,Math.Clamp(ownLineHeight*.24f,4,12))),new Rectangle(Point.Empty,source.Size));
            // OCR polygons often stop inside bold/outlined terminal glyphs (the real selected
            // choice left "name?" behind). Dilate only already-discovered glyph ink and keep it
            // constrained to the source-line neighbourhood; the game button itself is untouched.
            DilateGlyphMask(mask,allowed,structuredIdentityHeader
                ?Math.Clamp((int)MathF.Round(ownLineHeight*.16f),2,4)
                :Math.Clamp((int)MathF.Round(ownLineHeight*.10f),1,3));
            var clearScan=Rectangle.Intersect(Rectangle.Inflate(Rectangle.Round(allowedUnion),120,48),new Rectangle(Point.Empty,source.Size));
            for(var y=clearScan.Top;y<clearScan.Bottom;y++)for(var x=clearScan.Left;x<clearScan.Right;x++)if(mask[x,y]&&!allowed.Contains(x,y))mask[x,y]=false;
            bounds=MaskBounds(mask,new bool[source.Width,source.Height],bounds,source.Size);masked=Count(mask,bounds);
            incompleteInk=textCharacters>=8&&masked<expectedInk*.28f;
        }
        var completeChoiceInk=(region.RoleType==RegionRoleType.Choice||repeatedCompactUi||structuredIdentityHeader)&&!incompleteInk&&masked/polygonArea<.72f;
        var unsafeImage=classification.Type is BackgroundSurfaceType.Texture or BackgroundSurfaceType.ComplexImage
            && !completeChoiceInk&&(imageOverlapRatio>.55f||visualComplexity>.78f);
        var complexBodySurface=region.RoleType==RegionRoleType.BodyParagraph&&bounds.Width>200&&visualComplexity>.22f;
        // A separately owned decorative header often uses textured, low-contrast ink by design.
        // Its exact source polygon is still authoritative; do not preserve the readable English
        // underneath its translation merely because foreground contrast is low.
        var independentDecorativeOwner=independentDecorativeGeometry&&masked>0;
        var safe = independentDecorativeOwner||classification.Type != BackgroundSurfaceType.UnknownUnsafe&&!unsafeImage&&!complexBodySurface&&!incompleteInk&&!lowContrastCompactInk;
        return new BackgroundIntegrationPlan { RegionId = region.RegionId, Role = region.RoleType,
            CleanupBounds = bounds, SourceTextPolygons = polygons, ProtectedGeometry = protectedPolygons,
            BackgroundType = classification.Type, ReconstructionStrategy = completeChoiceInk?BackgroundReconstructionStrategy.HorizontalSurfaceContinuation:classification.Strategy,
            Confidence = classification.Confidence, SafeToCommit = safe,
            FailureReason = safe ? "" : lowContrastCompactInk?"Low-contrast source ink cannot be separated safely; preserve original":incompleteInk?$"Source ink coverage is incomplete ({masked}/{expectedInk:0}); preserve original to prevent residual glyphs":complexBodySurface?"Continuous panel model is unavailable for this complex body surface; preserve original":unsafeImage?"Complex image reconstruction confidence is insufficient; preserve original":"Background classification is unsafe",
            MaskBasis = "SourceLinePolygons + detected ink + constrained punctuation residual pass; never TranslationRenderRect", CleanupMask = mask,PrimaryMask=primary,PunctuationMask=punctuation };
    }

    private static bool[,] ReadMask(Bitmap raster,Rectangle bounds,Size size){var result=new bool[size.Width,size.Height];for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)result[x,y]=raster.GetPixel(x,y).R>127;return result;}
    private static void DilateGlyphMask(bool[,] mask,Rectangle allowed,int iterations)
    {
        for(var pass=0;pass<iterations;pass++)
        {
            var add=new List<Point>();
            for(var y=allowed.Top;y<allowed.Bottom;y++)for(var x=allowed.Left;x<allowed.Right;x++)
            {if(mask[x,y])continue;for(var yy=Math.Max(allowed.Top,y-1);yy<=Math.Min(allowed.Bottom-1,y+1);yy++)for(var xx=Math.Max(allowed.Left,x-1);xx<=Math.Min(allowed.Right-1,x+1);xx++)if(mask[xx,yy]){add.Add(new(x,y));goto next; } next:;}
            foreach(var p in add)mask[p.X,p.Y]=true;
        }
    }
    private static Rectangle MaskBounds(bool[,] primary,bool[,] punctuation,Rectangle fallback,Size size)
    {var l=size.Width;var t=size.Height;var r=-1;var b=-1;for(var y=0;y<size.Height;y++)for(var x=0;x<size.Width;x++)if(primary[x,y]||punctuation[x,y]){l=Math.Min(l,x);t=Math.Min(t,y);r=Math.Max(r,x);b=Math.Max(b,y);}return r<l?fallback:Rectangle.Intersect(Rectangle.FromLTRB(l,t,r+1,b+1),new(0,0,size.Width,size.Height));}
    private static bool[,] RecoverPunctuation(Bitmap source,Bitmap raster,IReadOnlyList<PointF[]> polygons,bool[,] primary,Rectangle overall,string sourceText)
    {
        var recovered=new bool[source.Width,source.Height];
        foreach(var polygon in polygons)
        {
            var line=Rectangle.Intersect(Rectangle.Round(GeometryV2.Bounds(polygon)),new(0,0,source.Width,source.Height));if(line.Width<2||line.Height<3)continue;
            var scan=Rectangle.Intersect(Rectangle.Inflate(line,Math.Clamp((int)(line.Height*1.05f),5,24),2),new(0,0,source.Width,source.Height));
            var border=new List<Color>();for(var x=line.Left;x<line.Right;x++){border.Add(source.GetPixel(x,line.Top));border.Add(source.GetPixel(x,line.Bottom-1));}
            Color Median(IEnumerable<Color> input){var a=input.ToArray();int V(Func<Color,int> f)=>a.Select(f).OrderBy(v=>v).ElementAt(a.Length/2);return Color.FromArgb(V(c=>c.R),V(c=>c.G),V(c=>c.B));}
            var bg=Median(border);int Delta(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
            var ink=new List<Color>();for(var y=line.Top;y<line.Bottom;y++)for(var x=line.Left;x<line.Right;x++)if(primary[x,y]&&Delta(source.GetPixel(x,y),bg)>45)ink.Add(source.GetPixel(x,y));if(ink.Count<2)continue;var fg=Median(ink);
            // Candidate/visited state is local to this line scan. Full-image arrays here made
            // April Long allocate image-sized buffers once per line and triggered repeated full GCs.
            var candidate=new bool[scan.Width,scan.Height];for(var y=scan.Top;y<scan.Bottom;y++)for(var x=scan.Left;x<scan.Right;x++)
            {if(primary[x,y])continue;var trimmed=sourceText.Trim();var leadingPunctuation=trimmed.FirstOrDefault() is '"' or '\'' or '(' or '[';var trailingPunctuation=trimmed.LastOrDefault() is '"' or '\'' or '.' or ',' or ':' or ';' or '!' or '?' or '…' or '-' or '—' or ')' or ']';var leftEndpoint=x<line.Left;var rightEndpoint=x>=line.Right;var trailingBand=trailingPunctuation&&x>=line.Right-Math.Clamp(line.Height,8,24);var endpoint=rightEndpoint||trailingBand||(leftEndpoint&&leadingPunctuation);if(!endpoint)continue;var c=source.GetPixel(x,y);candidate[x-scan.Left,y-scan.Top]=Delta(c,bg)>22&&Delta(c,fg)<240;}
            var seen=new bool[scan.Width,scan.Height];for(var sy=scan.Top;sy<scan.Bottom;sy++)for(var sx=scan.Left;sx<scan.Right;sx++)
            {if(!candidate[sx-scan.Left,sy-scan.Top]||seen[sx-scan.Left,sy-scan.Top])continue;var q=new Queue<Point>();var component=new List<Point>();q.Enqueue(new(sx,sy));seen[sx-scan.Left,sy-scan.Top]=true;while(q.Count>0){var p=q.Dequeue();component.Add(p);for(var yy=Math.Max(scan.Top,p.Y-1);yy<=Math.Min(scan.Bottom-1,p.Y+1);yy++)for(var xx=Math.Max(scan.Left,p.X-1);xx<=Math.Min(scan.Right-1,p.X+1);xx++)if(candidate[xx-scan.Left,yy-scan.Top]&&!seen[xx-scan.Left,yy-scan.Top]){seen[xx-scan.Left,yy-scan.Top]=true;q.Enqueue(new(xx,yy));}}
                var cb=Rectangle.FromLTRB(component.Min(p=>p.X),component.Min(p=>p.Y),component.Max(p=>p.X)+1,component.Max(p=>p.Y)+1);var endpointDistance=cb.Right<=line.Left?line.Left-cb.Right:cb.Left>=line.Right?cb.Left-line.Right:0;
                var clippedAtOuterEdge=cb.Left<=scan.Left||cb.Right>=scan.Right;var small=!clippedAtOuterEdge&&component.Count<=Math.Max(18,line.Height*line.Height/3)&&cb.Width<=Math.Max(8,line.Height*1.05)&&cb.Height<=Math.Max(10,line.Height*.85);var aligned=cb.Top>=line.Top-2&&cb.Bottom<=line.Bottom+2;var near=endpointDistance<=Math.Max(10,line.Height);
                if(!small||!aligned||!near)continue;foreach(var p in component)for(var yy=Math.Max(scan.Top,p.Y-1);yy<=Math.Min(scan.Bottom-1,p.Y+1);yy++)for(var xx=Math.Max(scan.Left,p.X-1);xx<=Math.Min(scan.Right-1,p.X+1);xx++){recovered[xx,yy]=true;raster.SetPixel(xx,yy,Color.White);}
            }
        }
        return recovered;
    }
    private static float VisualComplexity(Bitmap source,RectangleF area)
    {var b=Rectangle.Intersect(Rectangle.Round(area),new(0,0,source.Width,source.Height));if(b.Width<2||b.Height<2)return 0;var bins=new Dictionary<(int,int,int),int>();var step=Math.Max(1,Math.Min(b.Width,b.Height)/24);var total=0;for(var y=b.Top;y<b.Bottom;y+=step)for(var x=b.Left;x<b.Right;x+=step){var c=source.GetPixel(x,y);var k=(c.R/16,c.G/16,c.B/16);bins[k]=bins.GetValueOrDefault(k)+1;total++;}if(total==0)return 0;var dominant=bins.Values.Max()/(float)total;var diversity=Math.Min(1,bins.Count/80f);return Math.Clamp(diversity*(1-dominant)*1.5f,0,1);}

    private static void RecoverResidualOutlineComponents(Bitmap source,IReadOnlyList<PointF[]> polygons,bool[,] mask,Color foreground,Color background)
    {
        int Delta(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
        foreach(var polygon in polygons)
        {
            var line=Rectangle.Intersect(Rectangle.Round(GeometryV2.Bounds(polygon)),new(0,0,source.Width,source.Height));if(line.Width<2||line.Height<3)continue;
            var scan=Rectangle.Intersect(Rectangle.Inflate(line,Math.Clamp((int)(line.Height*.38f),5,24),Math.Clamp((int)(line.Height*.20f),3,12)),new(0,0,source.Width,source.Height));
            var candidate=new bool[scan.Width,scan.Height];for(var y=scan.Top;y<scan.Bottom;y++)for(var x=scan.Left;x<scan.Right;x++)
            {if(mask[x,y])continue;var c=source.GetPixel(x,y);candidate[x-scan.Left,y-scan.Top]=Delta(c,background)>28&&Delta(c,foreground)<Delta(c,background)*1.35+35;}
            var seen=new bool[scan.Width,scan.Height];for(var sy=0;sy<scan.Height;sy++)for(var sx=0;sx<scan.Width;sx++)
            {
                if(!candidate[sx,sy]||seen[sx,sy])continue;var q=new Queue<Point>();var component=new List<Point>();q.Enqueue(new(sx,sy));seen[sx,sy]=true;
                while(q.Count>0){var p=q.Dequeue();component.Add(p);for(var yy=Math.Max(0,p.Y-1);yy<=Math.Min(scan.Height-1,p.Y+1);yy++)for(var xx=Math.Max(0,p.X-1);xx<=Math.Min(scan.Width-1,p.X+1);xx++)if(candidate[xx,yy]&&!seen[xx,yy]){seen[xx,yy]=true;q.Enqueue(new(xx,yy));}}
                var cb=Rectangle.FromLTRB(component.Min(p=>p.X)+scan.Left,component.Min(p=>p.Y)+scan.Top,component.Max(p=>p.X)+scan.Left+1,component.Max(p=>p.Y)+scan.Top+1);
                var nearIdentity=Rectangle.Intersect(Rectangle.Inflate(line,4,3),cb) is {Width:>0,Height:>0};var glyphSized=component.Count<=Math.Max(900,line.Width*line.Height/3)&&cb.Height<=line.Height*1.2f&&cb.Width<=Math.Max(line.Height*2.2f,line.Width*.55f);
                if(!nearIdentity||!glyphSized)continue;foreach(var p in component)for(var yy=Math.Max(scan.Top,p.Y+scan.Top-2);yy<=Math.Min(scan.Bottom-1,p.Y+scan.Top+2);yy++)for(var xx=Math.Max(scan.Left,p.X+scan.Left-2);xx<=Math.Min(scan.Right-1,p.X+scan.Left+2);xx++)mask[xx,yy]=true;
            }
        }
    }

    private static void RasterizeInkMask(Bitmap source,Bitmap raster,PointF[] polygon,bool strictForeground)
    {
        var geometry=Rectangle.Intersect(Rectangle.Round(GeometryV2.Bounds(polygon)),new(0,0,source.Width,source.Height));if(geometry.Width<2||geometry.Height<2)return;
        // OCR polygons frequently describe the text baseline/body and omit the outer stroke,
        // shadow, glow, or even clipped end glyphs. Discover connected glyph ink in a bounded
        // neighbourhood of the source line instead of treating the polygon as a hard clip.
        // The vertical band remains tight, which prevents nearby rows from being absorbed.
        var b=Rectangle.Intersect(Rectangle.Inflate(geometry,
            Math.Clamp((int)MathF.Round(geometry.Height*2.35f),8,120),
            Math.Clamp((int)MathF.Round(geometry.Height*.80f),4,48)),new(0,0,source.Width,source.Height));
        var border=new List<Color>();for(var x=b.Left;x<b.Right;x++){border.Add(source.GetPixel(x,b.Top));border.Add(source.GetPixel(x,b.Bottom-1));}for(var y=b.Top;y<b.Bottom;y++){border.Add(source.GetPixel(b.Left,y));border.Add(source.GetPixel(b.Right-1,y));}
        Color Median(IEnumerable<Color> input){var a=input.ToArray();int V(Func<Color,int> f)=>a.Select(f).OrderBy(v=>v).ElementAt(a.Length/2);return Color.FromArgb(V(c=>c.R),V(c=>c.G),V(c=>c.B));}
        var bg=Median(border);int Delta(Color a,Color c)=>Math.Abs(a.R-c.R)+Math.Abs(a.G-c.G)+Math.Abs(a.B-c.B);
        var candidates=new List<Color>();for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++){var c=source.GetPixel(x,y);if(Delta(c,bg)>75)candidates.Add(c);}
        if(candidates.Count==0)return;
        // Pick a compact high-contrast color cluster, not the median of the strongest third.
        // On pill/ellipse controls the surface owns many more pixels than the label and used to
        // win that median, merging the label into the panel component.
        var foregroundCluster=candidates.GroupBy(c=>(c.R/20,c.G/20,c.B/20))
            .Select(group=>new{Colors=group.ToArray(),Score=Math.Pow(group.Average(c=>Delta(c,bg)),2)*Math.Sqrt(group.Count())})
            .OrderByDescending(x=>x.Score).First().Colors;
        var fg=Median(foregroundCluster);
        var fgBrighter=(fg.R+fg.G+fg.B)>=(bg.R+bg.G+bg.B);var bgLuma=bg.R+bg.G+bg.B;var fgLuma=fg.R+fg.G+fg.B;
        var seed=new bool[b.Width,b.Height];for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++){var c=source.GetPixel(x,y);var luma=c.R+c.G+c.B;var direction=fgBrighter?luma>bgLuma:luma<bgLuma;var brightText=fgLuma>bgLuma+120&&luma>bgLuma+75;
            // Foreground similarity is mandatory even on ordinary panels. Without it, a blue
            // button surface and its white label become one connected component; the component
            // is then rejected as panel-sized and none of the source letters are removed.
            seed[x-b.Left,y-b.Top]=Delta(c,bg)>(strictForeground?48:26)&&(direction||brightText)&&Delta(c,fg)<(strictForeground?150:185);}
        // A visual Region can be much larger than its OCR ink (Faranna's title Region is
        // 595x159). Reject large connected background structures before dilation; an ink mask
        // is a collection of glyph-sized components, never a panel-sized island.
        var seen=new bool[b.Width,b.Height];var accepted=new List<Point>();
        for(var sy=b.Top;sy<b.Bottom;sy++)for(var sx=b.Left;sx<b.Right;sx++)
        {
            if(!seed[sx-b.Left,sy-b.Top]||seen[sx-b.Left,sy-b.Top])continue;var queue=new Queue<Point>();var component=new List<Point>();queue.Enqueue(new(sx,sy));seen[sx-b.Left,sy-b.Top]=true;
            while(queue.Count>0){var p=queue.Dequeue();component.Add(p);for(var yy=Math.Max(b.Top,p.Y-1);yy<=Math.Min(b.Bottom-1,p.Y+1);yy++)for(var xx=Math.Max(b.Left,p.X-1);xx<=Math.Min(b.Right-1,p.X+1);xx++)if(seed[xx-b.Left,yy-b.Top]&&!seen[xx-b.Left,yy-b.Top]){seen[xx-b.Left,yy-b.Top]=true;queue.Enqueue(new(xx,yy));}}
            var cb=Rectangle.FromLTRB(component.Min(p=>p.X),component.Min(p=>p.Y),component.Max(p=>p.X)+1,component.Max(p=>p.Y)+1);
            var glyphSized=component.Count<=Math.Max(180,b.Width*b.Height/16)&&cb.Width<=Math.Max(22,geometry.Height*1.8f)&&cb.Height<=Math.Max(20,geometry.Height*1.20f);
            // Bold/outlined UI words can become one connected component. Accept that component
            // when it crosses the OCR identity box and remains a thin, line-aligned ink band;
            // this is the case where per-letter size gates otherwise leave the English title.
            var crossesIdentity=Rectangle.Intersect(cb,geometry) is {Width:>1,Height:>1};
            var lineLike=crossesIdentity&&cb.Height<=Math.Max(22,geometry.Height*1.25f)
                &&cb.Width>cb.Height*1.35f&&component.Count<=Math.Max(600,b.Width*b.Height/2);
            var lineAligned=cb.Bottom>=geometry.Top-2&&cb.Top<=geometry.Bottom+2;
            if((glyphSized||lineLike)&&lineAligned)accepted.AddRange(component);
        }
        // Stroke-aware controlled dilation captures bold outlines, glow and anti-alias fringes.
        // It follows connected glyph pixels and is capped well below the line rectangle scale.
        var radius=Math.Clamp((int)MathF.Round(geometry.Height*.15f),3,7);
        foreach(var p in accepted)for(var yy=Math.Max(b.Top,p.Y-radius);yy<=Math.Min(b.Bottom-1,p.Y+radius);yy++)for(var xx=Math.Max(b.Left,p.X-radius);xx<=Math.Min(b.Right-1,p.X+radius);xx++)
            if((xx-p.X)*(xx-p.X)+(yy-p.Y)*(yy-p.Y)<=radius*radius+1)raster.SetPixel(xx,yy,Color.White);
    }

    private static BackgroundIntegrationPlan Unsafe(RecognitionRegion r, IReadOnlyList<PointF[]> source,
        IReadOnlyList<PointF[]> protectedGeometry, string reason, Size size, bool[,]? mask = null, Rectangle bounds = default) =>
        new() { RegionId = r.RegionId, Role = r.RoleType, CleanupBounds = bounds,
            SourceTextPolygons = source, ProtectedGeometry = protectedGeometry,
            BackgroundType = BackgroundSurfaceType.UnknownUnsafe,
            ReconstructionStrategy = BackgroundReconstructionStrategy.PreserveOriginal,
            Confidence = 0, SafeToCommit = false, FailureReason = reason,
            MaskBasis = "SourceLinePolygons; unsafe plan commits zero pixels", CleanupMask = mask ?? new bool[size.Width, size.Height],PrimaryMask=mask??new bool[size.Width,size.Height],PunctuationMask=new bool[size.Width,size.Height] };

    private sealed record Sample(int X, int Y, Color Color);
    private static List<Sample> RingSamples(Bitmap image, Rectangle b, bool[,] mask)
    {
        var result = new List<Sample>(); var ring = Rectangle.Intersect(Rectangle.Inflate(b, 5, 5), new(0, 0, image.Width, image.Height));
        for (var y = ring.Top; y < ring.Bottom; y++) for (var x = ring.Left; x < ring.Right; x++)
        {
            if (mask[x, y]) continue;
            var inInner = x >= b.Left + 3 && x < b.Right - 3 && y >= b.Top + 3 && y < b.Bottom - 3;
            if (!inInner) result.Add(new(x, y, image.GetPixel(x, y)));
        }
        return result;
    }
    private static (BackgroundSurfaceType Type, BackgroundReconstructionStrategy Strategy, float Confidence) Classify(List<Sample> s, Rectangle b)
    {
        var dominant=s.GroupBy(x=>(x.Color.R/16,x.Color.G/16,x.Color.B/16)).OrderByDescending(g=>g.Count()).First();
        if(dominant.Count()>=s.Count*.28)return(BackgroundSurfaceType.FlatColor,BackgroundReconstructionStrategy.RobustFlatFill,Math.Min(.98f,.72f+dominant.Count()/(float)s.Count));
        int Median(Func<Color, int> f) => s.Select(x => f(x.Color)).OrderBy(x => x).ElementAt(s.Count / 2);
        var m = Color.FromArgb(Median(c => c.R), Median(c => c.G), Median(c => c.B));
        double Delta(Color c) => Math.Abs(c.R - m.R) + Math.Abs(c.G - m.G) + Math.Abs(c.B - m.B);
        var variation = s.Average(x => Delta(x.Color));
        var left = s.Where(x => x.X <= b.Left + 2).Select(x => Luma(x.Color)).DefaultIfEmpty(Luma(m)).Average();
        var right = s.Where(x => x.X >= b.Right - 3).Select(x => Luma(x.Color)).DefaultIfEmpty(Luma(m)).Average();
        var top = s.Where(x => x.Y <= b.Top + 2).Select(x => Luma(x.Color)).DefaultIfEmpty(Luma(m)).Average();
        var bottom = s.Where(x => x.Y >= b.Bottom - 3).Select(x => Luma(x.Color)).DefaultIfEmpty(Luma(m)).Average();
        if (variation < 18) return (BackgroundSurfaceType.FlatColor, BackgroundReconstructionStrategy.RobustFlatFill, .96f);
        if (variation < 48 && Math.Abs(left - right) >= Math.Abs(top - bottom) * 1.35)
            return (BackgroundSurfaceType.HorizontalGradient, BackgroundReconstructionStrategy.LinearGradient, .88f);
        if (variation < 48 && Math.Abs(top - bottom) >= Math.Abs(left - right) * 1.35)
            return (BackgroundSurfaceType.VerticalGradient, BackgroundReconstructionStrategy.LinearGradient, .88f);
        if (variation < 62) return (BackgroundSurfaceType.BilinearGradient, BackgroundReconstructionStrategy.LinearGradient, .76f);
        if (variation < 105) return (BackgroundSurfaceType.Texture, BackgroundReconstructionStrategy.EdgePropagation, .68f);
        return (BackgroundSurfaceType.ComplexImage, BackgroundReconstructionStrategy.LocalInpaint, .58f);
    }
    private static double Luma(Color c) => .2126 * c.R + .7152 * c.G + .0722 * c.B;
    private static bool PointInPolygon(PointF point,IReadOnlyList<PointF> polygon)
    {var inside=false;for(int i=0,j=polygon.Count-1;i<polygon.Count;j=i++){var a=polygon[i];var b=polygon[j];if((a.Y>point.Y)!=(b.Y>point.Y)&&point.X<(b.X-a.X)*(point.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;}return inside;}
    private static int Count(bool[,] mask, Rectangle b) { var n = 0; for (var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)if(mask[x,y])n++;return n; }
}

public static class BackgroundIntegrationExecutor
{
    public static BackgroundIntegrationRoiResult RestoreMaskLocal(Bitmap current,BackgroundIntegrationPlan plan,Rectangle requestedBounds)
    {
        var bounds=Rectangle.Intersect(requestedBounds,new(0,0,current.Width,current.Height));
        if(bounds.Width<1||bounds.Height<1)return new(new Bitmap(1,1),bounds,plan,false,"Empty ROI",0,0);
        var roi=current.Clone(bounds,current.PixelFormat);if(!HasMask(plan))return new(roi,bounds,plan,true,"",0,0);
        var exclusion=DilateMask(plan.CleanupMask,plan.CleanupBounds,Math.Clamp((int)MathF.Round(MathF.Sqrt(Math.Max(1,plan.CleanupBounds.Height))*.7f),2,5));
        var edge=EdgeColors(current,plan.CleanupBounds,exclusion);var changed=0;var outside=0;var radius=Math.Max(10,Math.Min(72,Math.Max(plan.CleanupBounds.Width,plan.CleanupBounds.Height)/2));
        var b=Rectangle.Intersect(plan.CleanupBounds,new(0,0,current.Width,current.Height));
        for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)
        {
            if(!plan.CleanupMask[x,y])continue;var lx=x-bounds.Left;var ly=y-bounds.Top;if(lx<0||ly<0||lx>=roi.Width||ly>=roi.Height){outside++;continue;}
            var c=LocalSurfaceContinuation(current,exclusion,x,y,radius,edge.Median);
            if(roi.GetPixel(lx,ly).ToArgb()!=c.ToArgb()){roi.SetPixel(lx,ly,c);changed++;}
        }
        if(outside>0)return new(roi,bounds,plan,false,$"Atomic ROI validation: {outside} cleanup pixels outside rollback bounds",changed,outside);
        return new(roi,bounds,plan,true,"",changed,0);
    }

    public static BackgroundIntegrationRoiResult ExecuteRoi(Bitmap current,BackgroundIntegrationPlan plan,Rectangle requestedBounds)
    {
        var bounds=Rectangle.Intersect(requestedBounds,new(0,0,current.Width,current.Height));
        if(bounds.Width<1||bounds.Height<1)return new(new Bitmap(1,1),bounds,plan,false,"Empty ROI",0,0);
        var roi=current.Clone(bounds,current.PixelFormat);
        if(!plan.SafeToCommit)return new(roi,bounds,plan,false,plan.FailureReason,0,0);
        if(!HasMask(plan))return new(roi,bounds,plan,true,"",0,0);
        if(plan.BackgroundType!=BackgroundSurfaceType.FlatColor&&!HasGeometryBackedPanelMask(plan)&&HasResidualGlyphRisk(current,plan))return new(roi,bounds,plan,false,"Residual source glyph risk outside CleanupMask; atomic preserve",0,0);
        var changed=0;var outside=0;var edge=EdgeColors(current,plan.CleanupBounds,plan.CleanupMask);
        var b=plan.CleanupBounds;
        for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)
        {
            if(!plan.CleanupMask[x,y])continue;
            var fx=b.Width<=1?0:(x-b.Left)/(float)(b.Width-1);var fy=b.Height<=1?0:(y-b.Top)/(float)(b.Height-1);
            Color c;
            // 0.5.0.6.4 replaced local surface continuation with one median color per OCR
            // region. On Faranna those independent regions became giant, mismatched patches.
            // Restore the 0.5.0.6.3 safe baseline until a panel boundary (not an ink mask) is
            // available: reconstruct only real ink from nearby unmasked pixels.
            if(plan.Role==RegionRoleType.CharacterName)
            {
                var left=current.GetPixel(Math.Max(0,b.Left-1),Math.Clamp(y,0,current.Height-1));
                var right=current.GetPixel(Math.Min(current.Width-1,b.Right),Math.Clamp(y,0,current.Height-1));
                c=Mix(left,right,fx);
            }
            else if(plan.ReconstructionStrategy==BackgroundReconstructionStrategy.RobustFlatFill)
                c=plan.Confidence<.72f?NearestUnmaskedFast(current,plan.CleanupMask,x,y,Math.Max(6,Math.Min(18,Math.Max(b.Width,b.Height)/4)),edge.Median):edge.Median;
            else if(plan.ReconstructionStrategy==BackgroundReconstructionStrategy.LinearGradient)
            {var h=Mix(edge.Left,edge.Right,fx);var v=Mix(edge.Top,edge.Bottom,fy);c=Mix(h,v,.5f);}
            else if(plan.ReconstructionStrategy==BackgroundReconstructionStrategy.HorizontalSurfaceContinuation)
                c=HorizontalContinuation(current,plan.CleanupMask,x,y,Math.Max(24,Math.Min(120,b.Width/2)),edge.Median);
            else c=NearestUnmasked(current,plan.CleanupMask,x,y,Math.Max(6,Math.Min(24,Math.Max(b.Width,b.Height)/3)),edge.Median);
            var lx=x-bounds.Left;var ly=y-bounds.Top;if(lx<0||ly<0||lx>=roi.Width||ly>=roi.Height){outside++;continue;}
            if(roi.GetPixel(lx,ly).ToArgb()!=c.ToArgb()){roi.SetPixel(lx,ly,c);changed++;}
        }
        var validation=outside>0?$"Atomic ROI validation: {outside} cleanup pixels outside rollback bounds":"";
        if(validation.Length>0)return new(roi,bounds,plan,false,validation,changed,outside);
        return new(roi,bounds,plan,true,"",changed,0);
    }

    public static BackgroundIntegrationResult Execute(Bitmap current, BackgroundIntegrationPlan plan)
    {
        var unchanged = new Bitmap(current);
        if (!plan.SafeToCommit) return new(unchanged, plan, false, plan.FailureReason, 0, 0);
        if (!HasMask(plan)) return new(unchanged, plan, true, "", 0, 0);
        if(plan.BackgroundType!=BackgroundSurfaceType.FlatColor&&!HasGeometryBackedPanelMask(plan)&&HasResidualGlyphRisk(current,plan))return new(unchanged,plan,false,"Residual source glyph risk outside CleanupMask; atomic preserve",0,0);
        using var temp = new Bitmap(current);
        Reconstruct(temp, current, plan);
        var outside = 0; var changed = 0;
        // Reconstruction is contractually confined to CleanupBounds. Validating the whole
        // bitmap once per Region made long pages scale as Regions x ImagePixels.
        var bounds=plan.CleanupBounds;
        for (var y = bounds.Top; y < bounds.Bottom; y++) for (var x = bounds.Left; x < bounds.Right; x++)
        {
            if (temp.GetPixel(x,y).ToArgb() == current.GetPixel(x,y).ToArgb()) continue;
            changed++; if (!plan.CleanupMask[x,y]) outside++;
        }
        var validation = outside > 0 ? $"Atomic validation: {outside} pixels changed outside CleanupMask" : "";
        if (validation.Length > 0) return new(unchanged, plan, false, validation, changed, outside);
        unchanged.Dispose(); return new(new Bitmap(temp), plan, true, "", changed, outside);
    }
    private static bool HasMask(BackgroundIntegrationPlan p){var b=p.CleanupBounds;for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)if(p.CleanupMask[x,y])return true;return false;}
    private static bool HasGeometryBackedPanelMask(BackgroundIntegrationPlan p)
    {var area=p.SourceTextPolygons.Select(GeometryV2.Bounds).Sum(x=>x.Width*x.Height);if(area<=0)return false;var masked=0;var b=p.CleanupBounds;for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)if(p.CleanupMask[x,y])masked++;return masked/area>.62f;}

    private static bool HasResidualGlyphRisk(Bitmap image,BackgroundIntegrationPlan plan)
    {
        var b=plan.CleanupBounds;var edge=EdgeColors(image,b,plan.CleanupMask);var bg=edge.Median;
        int Delta(Color c)=>Math.Abs(c.R-bg.R)+Math.Abs(c.G-bg.G)+Math.Abs(c.B-bg.B);
        var candidate=new bool[b.Width,b.Height];for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)if(!plan.CleanupMask[x,y]&&Delta(image.GetPixel(x,y))>75)candidate[x-b.Left,y-b.Top]=true;
        var seen=new bool[b.Width,b.Height];var glyphPixels=0;
        for(var sy=0;sy<b.Height;sy++)for(var sx=0;sx<b.Width;sx++)
        {
            if(!candidate[sx,sy]||seen[sx,sy])continue;var q=new Queue<Point>();var c=new List<Point>();q.Enqueue(new(sx,sy));seen[sx,sy]=true;
            while(q.Count>0){var p=q.Dequeue();c.Add(p);for(var yy=Math.Max(0,p.Y-1);yy<=Math.Min(b.Height-1,p.Y+1);yy++)for(var xx=Math.Max(0,p.X-1);xx<=Math.Min(b.Width-1,p.X+1);xx++)if(candidate[xx,yy]&&!seen[xx,yy]){seen[xx,yy]=true;q.Enqueue(new(xx,yy));}}
            var cb=Rectangle.FromLTRB(c.Min(p=>p.X),c.Min(p=>p.Y),c.Max(p=>p.X)+1,c.Max(p=>p.Y)+1);
            if(c.Count>=2&&c.Count<=700&&cb.Width<=Math.Max(18,b.Width*.35f)&&cb.Height<=Math.Max(18,b.Height*.85f))glyphPixels+=c.Count;
        }
        var masked=0;for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)if(plan.CleanupMask[x,y])masked++;
        return glyphPixels>Math.Max(10,masked*.03f);
    }

    private static void Reconstruct(Bitmap target, Bitmap source, BackgroundIntegrationPlan p)
    {
        var b = p.CleanupBounds;
        var edge = EdgeColors(source, b, p.CleanupMask);
        for (var y=b.Top;y<b.Bottom;y++) for(var x=b.Left;x<b.Right;x++)
        {
            if(!p.CleanupMask[x,y])continue;
            var fx=b.Width<=1?0:(x-b.Left)/(float)(b.Width-1);var fy=b.Height<=1?0:(y-b.Top)/(float)(b.Height-1);
            Color c;
            if(p.ReconstructionStrategy==BackgroundReconstructionStrategy.RobustFlatFill)
                c=p.Confidence>=.72f?edge.Median:NearestUnmaskedFast(source,p.CleanupMask,x,y,Math.Max(6,Math.Min(18,Math.Max(b.Width,b.Height)/4)),edge.Median);
            else if(p.ReconstructionStrategy==BackgroundReconstructionStrategy.LinearGradient)
            {
                var h=Mix(edge.Left,edge.Right,fx);var v=Mix(edge.Top,edge.Bottom,fy);c=Mix(h,v,.5f);
            }
            else if(p.ReconstructionStrategy==BackgroundReconstructionStrategy.HorizontalSurfaceContinuation)
                c=HorizontalContinuation(source,p.CleanupMask,x,y,Math.Max(24,Math.Min(120,b.Width/2)),edge.Median);
            else c=NearestUnmasked(source,p.CleanupMask,x,y,Math.Max(6,Math.Min(24,Math.Max(b.Width,b.Height)/3)),edge.Median);
            target.SetPixel(x,y,c);
        }
    }
    private sealed record Edges(Color Left,Color Right,Color Top,Color Bottom,Color Median);
    private static Edges EdgeColors(Bitmap image,Rectangle b,bool[,] mask)
    {
        Color M(IEnumerable<Color> input){var a=input.ToArray();if(a.Length==0)return Color.Black;var dominant=a.GroupBy(c=>(c.R/16,c.G/16,c.B/16)).OrderByDescending(g=>g.Count()).First().ToArray();int V(Func<Color,int> f)=>dominant.Select(f).OrderBy(v=>v).ElementAt(dominant.Length/2);return Color.FromArgb(V(c=>c.R),V(c=>c.G),V(c=>c.B));}
        var all=new List<Color>();var l=new List<Color>();var r=new List<Color>();var t=new List<Color>();var d=new List<Color>();
        for(var y=Math.Max(0,b.Top-3);y<Math.Min(image.Height,b.Bottom+3);y++)for(var x=Math.Max(0,b.Left-3);x<Math.Min(image.Width,b.Right+3);x++){if(mask[x,y])continue;var c=image.GetPixel(x,y);all.Add(c);if(x<=b.Left+1)l.Add(c);if(x>=b.Right-2)r.Add(c);if(y<=b.Top+1)t.Add(c);if(y>=b.Bottom-2)d.Add(c);}
        var m=M(all);return new(l.Count>0?M(l):m,r.Count>0?M(r):m,t.Count>0?M(t):m,d.Count>0?M(d):m,m);
    }
    private static Color NearestUnmasked(Bitmap image,bool[,] mask,int x,int y,int radius,Color fallback)
    {for(var d=1;d<=radius;d++){var values=new List<Color>();for(var yy=Math.Max(0,y-d);yy<=Math.Min(image.Height-1,y+d);yy++)for(var xx=Math.Max(0,x-d);xx<=Math.Min(image.Width-1,x+d);xx++){if(Math.Abs(xx-x)!=d&&Math.Abs(yy-y)!=d||mask[xx,yy])continue;values.Add(image.GetPixel(xx,yy));}if(values.Count>0){int V(Func<Color,int> f)=>values.Select(f).OrderBy(v=>v).ElementAt(values.Count/2);return Color.FromArgb(V(c=>c.R),V(c=>c.G),V(c=>c.B));}}return fallback;}
    private static Color NearestUnmaskedFast(Bitmap image,bool[,] mask,int x,int y,int radius,Color fallback)
    {
        // Four axis probes preserve the local 0.5.0.6.3 surface continuation without its
        // O(radius squared) ring scan for every ink pixel.
        for(var d=1;d<=radius;d++)
        {
            Span<Point> points=stackalloc Point[4]{new(x-d,y),new(x+d,y),new(x,y-d),new(x,y+d)};
            var colors=new List<Color>(4);foreach(var p in points)if(p.X>=0&&p.Y>=0&&p.X<image.Width&&p.Y<image.Height&&!mask[p.X,p.Y])colors.Add(image.GetPixel(p.X,p.Y));
            if(colors.Count==0)continue;int V(Func<Color,int> f)=>colors.Select(f).OrderBy(v=>v).ElementAt(colors.Count/2);return Color.FromArgb(V(c=>c.R),V(c=>c.G),V(c=>c.B));
        }
        return fallback;
    }
    private static Color HorizontalContinuation(Bitmap image,bool[,] mask,int x,int y,int radius,Color fallback)
    {
        var left=-1;var right=-1;for(var d=1;d<=radius&&(left<0||right<0);d++){if(left<0&&x-d>=0&&!mask[x-d,y])left=x-d;if(right<0&&x+d<image.Width&&!mask[x+d,y])right=x+d;}
        if(left>=0&&right>=0)return Mix(image.GetPixel(left,y),image.GetPixel(right,y),(x-left)/(float)Math.Max(1,right-left));
        if(left>=0)return image.GetPixel(left,y);if(right>=0)return image.GetPixel(right,y);return fallback;
    }
    private static Color Mix(Color a,Color b,float t)=>Color.FromArgb((int)(a.R*(1-t)+b.R*t),(int)(a.G*(1-t)+b.G*t),(int)(a.B*(1-t)+b.B*t));
    private static bool[,] DilateMask(bool[,] source,Rectangle bounds,int radius)
    {
        var result=(bool[,])source.Clone();var clipped=Rectangle.Intersect(Rectangle.Inflate(bounds,radius,radius),new(0,0,source.GetLength(0),source.GetLength(1)));
        for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)if(source[x,y])
            for(var yy=Math.Max(clipped.Top,y-radius);yy<=Math.Min(clipped.Bottom-1,y+radius);yy++)for(var xx=Math.Max(clipped.Left,x-radius);xx<=Math.Min(clipped.Right-1,x+radius);xx++)
                if((xx-x)*(xx-x)+(yy-y)*(yy-y)<=radius*radius+1)result[xx,yy]=true;
        return result;
    }
    private static Color LocalSurfaceContinuation(Bitmap image,bool[,] exclusion,int x,int y,int radius,Color fallback)
    {
        Point? left=null,right=null,top=null,bottom=null;
        for(var distance=1;distance<=radius&&(left is null||right is null||top is null||bottom is null);distance++)
        {
            if(left is null&&x-distance>=0&&!exclusion[x-distance,y])left=new(x-distance,y);if(right is null&&x+distance<image.Width&&!exclusion[x+distance,y])right=new(x+distance,y);
            if(top is null&&y-distance>=0&&!exclusion[x,y-distance])top=new(x,y-distance);if(bottom is null&&y+distance<image.Height&&!exclusion[x,y+distance])bottom=new(x,y+distance);
        }
        Color Axis(Point? a,Point? b,bool horizontal){if(a is { } pa&&b is { } pb){var t=horizontal?(x-pa.X)/(float)Math.Max(1,pb.X-pa.X):(y-pa.Y)/(float)Math.Max(1,pb.Y-pa.Y);return Mix(image.GetPixel(pa.X,pa.Y),image.GetPixel(pb.X,pb.Y),t);}if(a is { } one)return image.GetPixel(one.X,one.Y);if(b is { } two)return image.GetPixel(two.X,two.Y);return fallback;}
        var h=Axis(left,right,true);var v=Axis(top,bottom,false);var hd=(left is { } l?x-l.X:radius)+(right is { } r?r.X-x:radius);var vd=(top is { } t?y-t.Y:radius)+(bottom is { } d?d.Y-y:radius);
        if(left is not null&&right is not null&&top is not null&&bottom is not null)return Mix(h,v,hd/(float)Math.Max(1,hd+vd));return hd<=vd?h:v;
    }
}
