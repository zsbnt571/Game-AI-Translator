namespace ScreenshotTranslationUiTester;

public sealed record TypographyContext(float BaseBodyFontSize, bool LongForm,
    IReadOnlyDictionary<string,float> RegionFontSizes, string Reason,
    string ContextId="",float SourceMedianHeight=0,float AllowedMinFontSize=0,float AllowedMaxFontSize=0,
    IReadOnlyList<VisualStyleGroup>? VisualStyleGroups=null)
{
    public bool TryGet(RecognitionRegion region,out float size)=>RegionFontSizes.TryGetValue(region.RegionId,out size);
}

public static class TypographyContextPlanner
{
    private static bool BodyLike(RecognitionRegion r)=>r.RoleType is RegionRoleType.BodyParagraph or RegionRoleType.Dialogue or RegionRoleType.Narration;

    public static TypographyContext Plan(IReadOnlyList<RecognitionRegion> regions,RenderSettings settings)
    {
        var styleGroups=VisualStyleGroupPlanner.Build(regions,settings);
        var styleSizes=styleGroups.SelectMany(x=>x.Members).ToDictionary(x=>x.RegionId,x=>x.FontSize,StringComparer.Ordinal);
        var body=regions.Where(r=>!r.IsIgnored&&!r.PreserveOriginal&&!string.IsNullOrWhiteSpace(r.TranslationText)&&BodyLike(r)).ToArray();
        if(body.Length<2)return new(0,false,styleSizes,"No continuous body context",VisualStyleGroups:styleGroups);
        var sourceSizes=body.Select(MedianHeight).Where(v=>v>0).OrderBy(v=>v).ToArray();
        if(sourceSizes.Length==0)return new(0,false,styleSizes,"No usable source line metrics",VisualStyleGroups:styleGroups);
        var totalCharacters=body.Sum(r=>(r.TranslationText??string.Empty).Length);
        var verticalSpan=body.Max(r=>r.BoundingBox.Bottom)-body.Min(r=>r.BoundingBox.Top);
        var longForm=body.Length>=3&&(totalCharacters>=180||verticalSpan>=480);
        if(!longForm)return new(0,false,styleSizes,"Body regions do not form a long-page typography context",VisualStyleGroups:styleGroups);
        // OCR line boxes include leading and antialiasing fringes and therefore are not a font
        // size.  A trimmed median plus a line-box-to-em conversion keeps short Chinese
        // translations from inflating to the old 26/28px ceiling.
        var trim=sourceSizes.Length>=5?Math.Max(1,sourceSizes.Length/10):0;
        var robust=sourceSizes.Skip(trim).Take(sourceSizes.Length-trim*2).ToArray();
        var median=robust[(robust.Length-1)/2];
        var baseSize=Math.Clamp(median*.78f*settings.FontScale,Math.Max(8,settings.MinFontSize),22);
        var allowedMin=Math.Max(Math.Max(8,settings.MinFontSize),baseSize*.88f);
        var allowedMax=Math.Min(24,baseSize*1.05f);
        // One page context owns one narrow scale band. Weight and italic style never select a
        // different tier, while an individual dense region may shrink slightly before preserve.
        var map=body.ToDictionary(r=>r.RegionId,_=>baseSize,StringComparer.Ordinal);foreach(var pair in styleSizes)map[pair.Key]=pair.Value;
        return new(baseSize,true,map,$"Robust shared scale from trimmed median source line height across {body.Length} long-page body regions",
            "BODY-CTX-001",median,allowedMin,allowedMax,styleGroups);
    }

    public static FontStyle ResolveStyle(RecognitionRegion region,TypographyContext context)
    {
        if(region.RoleType==RegionRoleType.Dialogue)return FontStyle.Bold;
        if(region.RoleType==RegionRoleType.Narration||context.LongForm&&region.RoleType==RegionRoleType.BodyParagraph)return FontStyle.Italic;
        return RoleTypographyProfiles.Resolve(region).Style;
    }

    private static float MedianHeight(RecognitionRegion r)
    {var v=r.SourceLinePolygons.Select(GeometryV2.Bounds).Select(x=>x.Height).Where(x=>x>0).OrderBy(x=>x).ToArray();return v.Length==0?Math.Max(10,r.BoundingBox.Height):v[(v.Length-1)/2];}
}
