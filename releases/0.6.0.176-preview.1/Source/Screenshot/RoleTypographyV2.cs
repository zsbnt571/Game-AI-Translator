namespace ScreenshotTranslationUiTester;

public sealed record RoleTypographyProfile(string Name,float Scale,float MinFont,float MaxFont,float LineSpacing,
    FontStyle Style,StringAlignment Alignment,bool VerticalCenter,float PaddingX,float PaddingY,string FitPolicy);

public static class RoleTypographyProfiles
{
    public static RoleTypographyProfile ResolveLegacy(RegionRoleType role)=>role switch
    {
        RegionRoleType.Title or RegionRoleType.Header=>new("LegacyHeading",1.12f,8,48,1.12f,FontStyle.Bold,StringAlignment.Near,false,.035f,.045f,"Legacy"),
        RegionRoleType.Metadata or RegionRoleType.UILabel=>new("LegacyMetadata",.90f,8,48,1.12f,FontStyle.Regular,StringAlignment.Near,false,.035f,.045f,"Legacy"),
        RegionRoleType.Button or RegionRoleType.CharacterName=>new("LegacyCentered",1.00f,8,48,1.12f,FontStyle.Bold,StringAlignment.Center,true,.035f,.045f,"Legacy"),
        _=>new("LegacyBody",1.00f,8,48,1.12f,FontStyle.Regular,StringAlignment.Near,false,.035f,.045f,"Legacy")
    };
    public static RoleTypographyProfile Resolve(RegionRoleType role)=>role switch
    {
        RegionRoleType.Title=>new("Title",1.18f,10,44,1.00f,FontStyle.Bold,StringAlignment.Near,false,.035f,.045f,"FewLinesPriority"),
        RegionRoleType.Dialogue=>new("Dialogue",1.00f,8,30,1.08f,FontStyle.Regular,StringAlignment.Near,false,.035f,.045f,"ReadableWrap"),
        RegionRoleType.Header=>new("Header",1.08f,8,28,1.00f,FontStyle.Bold,StringAlignment.Near,false,.035f,.04f,"HeaderHierarchy"),
        RegionRoleType.Metadata=>new("Metadata",.84f,7,20,1.00f,FontStyle.Regular,StringAlignment.Near,false,.03f,.04f,"CompactReadable"),
        RegionRoleType.Button=>new("Button",1.00f,8,30,1.00f,FontStyle.Bold,StringAlignment.Center,true,.04f,.06f,"CenteredSingleControl"),
        RegionRoleType.UILabel=>new("SmallLabel",.88f,7,22,1.00f,FontStyle.Regular,StringAlignment.Near,false,.03f,.04f,"CompactReadable"),
        _=>new("Body",1.00f,8,26,1.12f,FontStyle.Regular,StringAlignment.Near,false,.035f,.045f,"ComfortableReading")
    };

    public static RoleTypographyProfile Resolve(RecognitionRegion region)
    {
        var profile = Resolve(region.RoleType);
        if (region.RoleType is RegionRoleType.Button or RegionRoleType.CharacterName) return profile;
        var lines = region.SourceLinePolygons.Select(GeometryV2.Bounds).Where(x => x.Width > 1).ToArray();
        if (lines.Length == 0) return profile;
        var box = region.BoundingBox;
        var centered = lines.Count(line => Math.Abs((line.Left + line.Right) / 2F - (box.Left + box.Right) / 2F) <= Math.Max(4F, box.Width * .09F));
        var leftVariance = lines.Max(x => x.Left) - lines.Min(x => x.Left);
        // Preserve a clear source-centered visual role without guessing from translated text.
        if (centered >= Math.Max(1, (int)Math.Ceiling(lines.Length * .65)) && leftVariance > Math.Max(3F, box.Width * .025F))
            return profile with { Alignment = StringAlignment.Center };
        return profile;
    }
}
