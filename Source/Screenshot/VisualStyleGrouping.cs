using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester;

public sealed record VisualStyleMember(string RegionId,string Variant,float SourceLineHeight,
    float FontSize,StringAlignment Alignment,RectangleF ComponentBounds);
public sealed record VisualStyleGroup(string VisualStyleGroupId,RegionRoleType FamilyRole,float BaseFontSize,
    IReadOnlyList<VisualStyleMember> Members,string ForegroundProfile,string CleanupStrategy);

public static class VisualStyleGroupPlanner
{
    private static bool RepeatedUi(RecognitionRegion r)=>r.RoleType is RegionRoleType.Choice or RegionRoleType.Button or RegionRoleType.UILabel;

    public static IReadOnlyList<VisualStyleGroup> Build(IReadOnlyList<RecognitionRegion> regions,RenderSettings settings)
    {
        var candidates=regions.Where(r=>!r.IsIgnored&&!r.PreserveOriginal&&!string.IsNullOrWhiteSpace(r.TranslationText)&&RepeatedUi(r))
            .Select(r=>(Region:r,Height:MedianLineHeight(r))).Where(x=>x.Height>=6).OrderBy(x=>x.Region.ReadingOrder).ToArray();
        if(candidates.Length<2)return [];
        var groups=new List<VisualStyleGroup>();var remaining=candidates.ToList();var index=1;
        while(remaining.Count>0)
        {
            var seed=remaining[0];var family=remaining.Where(x=>Compatible(seed,x)).ToArray();
            foreach(var member in family)remaining.Remove(member);
            if(family.Length<2)continue;
            var heights=family.Select(x=>x.Height).OrderBy(x=>x).ToArray();var median=heights[(heights.Length-1)/2];
            var baseSize=Math.Clamp(median*.88f*settings.FontScale,Math.Max(10,settings.MinFontSize),42);
            var members=family.Select(x=>
            {
                var selected=x.Height>median*1.18f;var size=selected?Math.Min(baseSize*1.16f,x.Height*.9f):baseSize;
                return new VisualStyleMember(x.Region.RegionId,selected?"Selected":"Normal",x.Height,size,StringAlignment.Center,x.Region.BoundingBox);
            }).ToArray();
            groups.Add(new($"VSG-{index++:000}",seed.Region.RoleType,baseSize,members,"PerMemberSourceForeground","GlyphInkOnly"));
        }
        return groups;
    }

    private static bool Compatible((RecognitionRegion Region,float Height) a,(RecognitionRegion Region,float Height) b)
    {
        var roleCompatible=a.Region.RoleType==b.Region.RoleType||
            a.Region.RoleType is RegionRoleType.Choice or RegionRoleType.Button&&b.Region.RoleType is RegionRoleType.Choice or RegionRoleType.Button;
        if(!roleCompatible)return false;
        var ratio=Math.Max(a.Height,b.Height)/Math.Max(1,Math.Min(a.Height,b.Height));
        var widthRatio=Math.Max(a.Region.BoundingBox.Width,b.Region.BoundingBox.Width)/Math.Max(1,Math.Min(a.Region.BoundingBox.Width,b.Region.BoundingBox.Width));
        return ratio<=1.65f&&widthRatio<=3.5f;
    }

    private static float MedianLineHeight(RecognitionRegion r)
    {var h=r.SourceLinePolygons.Select(GeometryV2.Bounds).Select(x=>x.Height).Where(x=>x>0).OrderBy(x=>x).ToArray();return h.Length==0?r.BoundingBox.Height:h[(h.Length-1)/2];}
}
