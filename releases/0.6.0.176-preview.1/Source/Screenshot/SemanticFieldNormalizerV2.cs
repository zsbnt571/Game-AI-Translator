using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

public static class SemanticFieldNormalizerV2
{
    private static readonly Regex Field = new(@"^\s*(Age|Height|Weight|BWH|Measurements?|Likes?|Dislikes?)\s*[:：]",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    public static List<RecognitionRegion> Normalize(IReadOnlyList<RecognitionRegion> input,long generation)
    {
        var output=new List<RecognitionRegion>();
        foreach(var region in input)
        {
            var segments=region.SourceSegments.OrderBy(x=>x.ReadingOrder).ToArray();
            if(segments.Length<2||segments.Any(x=>!Field.IsMatch(x.Text))){output.Add(region);continue;}
            foreach(var segment in segments)
            {
                var block=region.DetectedBlocks.FirstOrDefault(x=>x.Id==segment.BlockId);
                var bounds=GeometryV2.Bounds(segment.Polygon);
                output.Add(new RecognitionRegion{
                    Polygon=segment.Polygon.ToArray(),SourceBlockIds=[segment.BlockId],DetectedBlocks=block is null?[]:[block],
                    SourceLinePolygons=[segment.Polygon.ToArray()],SourceSegments=[segment with{Polygon=segment.Polygon.ToArray()}],
                    LayoutExclusionPolygons=region.LayoutExclusionPolygons.Select(x=>x.ToArray()).ToList(),
                    OcrText=segment.Text,CorrectedText=segment.Text,StructuredText=segment.Text,RoleType=RegionRoleType.Metadata,RoleConfidence=.99f,
                    ReadingOrder=segment.ReadingOrder,RecognitionConfidence=region.RecognitionConfidence,RegionGeneration=generation,
                    DetectionSources=[..region.DetectionSources],ParentRegionId=region.ParentRegionId,SourceImageRegion=bounds,RendererTargetRegion=bounds,
                    CoverageValid=true
                });
            }
        }
        return output.OrderBy(x=>x.BoundingBox.Top).ThenBy(x=>x.BoundingBox.Left).ToList();
    }
}

public static class FieldTranslationIntegrityV2
{
    private static readonly Regex Number=new(@"\d+(?:[./]\d+)*",RegexOptions.CultureInvariant);
    public static string Normalize(string source,string translation)
    {
        var s=source.Trim();var t=translation.Trim();
        if(s.Equals("Faranne",StringComparison.OrdinalIgnoreCase)||s.Equals("Faranna",StringComparison.OrdinalIgnoreCase))return "法兰妮";
        if(s.Equals("Dark Elf",StringComparison.OrdinalIgnoreCase))return "暗精灵";
        var label=s.Split(':','：')[0].Trim().ToLowerInvariant();
        var chinese=label switch{"age"=>"年龄","height"=>"身高","weight"=>"体重","bwh" or "measurement" or "measurements"=>"三围","like" or "likes"=>"喜欢","dislike" or "dislikes"=>"讨厌",_=>""};
        if(chinese.Length==0)return t;
        var required=Number.Matches(s).Select(x=>x.Value).Distinct().ToArray();
        if(required.All(t.Contains)&&t.Length>chinese.Length)return t;
        var value=s[(s.IndexOfAny([':', '：'])+1)..].Trim();
        return $"{chinese}：{value}";
    }
}
