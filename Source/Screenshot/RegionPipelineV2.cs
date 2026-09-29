using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

public interface IRegionOcrRetry
{
    OcrEngineKind Engine { get; }
    Task<IReadOnlyList<OcrEngineBlock>> RecognizeCropAsync(Bitmap nativeResolutionCrop, RectangleF sourceBounds, CancellationToken token);
}

public static class RegionProposalFusion
{
    public static List<RecognitionRegion> Fuse(IReadOnlyList<RectangleF> cv,
        IReadOnlyList<VisualRegion> visual, IReadOnlyList<OcrEngineBlock> ocr, long generation)
    {
        var result = new List<RecognitionRegion>();
        foreach (var block in ocr.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.CorrectedText.Length > 0 ? x.CorrectedText : x.RawText)))
        {
            var polygon = block.Polygon.Length >= 3 ? block.Polygon : GeometryV2.RectanglePolygon(block.BoundingBox);
            var match = result.FirstOrDefault(x => GeometryV2.IntersectionOverUnion(x.BoundingBox, block.BoundingBox) >= .45f);
            if (match is null) result.Add(new RecognitionRegion { Polygon = polygon, SourceBlockIds = [block.Id], DetectedBlocks = [block], SourceLinePolygons = [polygon],
                SourceSegments = [new(block.Id, string.IsNullOrWhiteSpace(block.CorrectedText) ? block.RawText : block.CorrectedText, polygon.ToArray(), RegionRoleType.Unknown, 0, block.ReadingOrder, block.Id)],
                OcrText = block.RawText, CorrectedText = string.IsNullOrWhiteSpace(block.CorrectedText) ? block.RawText : block.CorrectedText,
                StructuredText = string.IsNullOrWhiteSpace(block.CorrectedText) ? block.RawText : block.CorrectedText,
                ReadingOrder = block.ReadingOrder, RecognitionConfidence = block.Confidence ?? 0, RegionGeneration = generation,
                DetectionSources = [DetectionSource.OcrDetector], SourceImageRegion = block.BoundingBox, RendererTargetRegion = block.BoundingBox });
            else { match.SourceBlockIds.Add(block.Id); match.DetectedBlocks.Add(block); match.SourceLinePolygons.Add(polygon); match.SourceSegments.Add(new(block.Id, string.IsNullOrWhiteSpace(block.CorrectedText) ? block.RawText : block.CorrectedText, polygon.ToArray(), RegionRoleType.Unknown, 0, block.ReadingOrder, block.Id)); match.DetectionSources.Add(DetectionSource.OcrDetector); }
        }
        foreach (var vr in visual)
        {
            var match = result.OrderByDescending(x => GeometryV2.IntersectionOverUnion(x.BoundingBox, vr.BoundingBox)).FirstOrDefault();
            if (match is not null && GeometryV2.IntersectionOverUnion(match.BoundingBox, vr.BoundingBox) >= .25f)
            {
                match.DetectionSources.Add(DetectionSource.VisualModel);
                if ((match.ManualOverrideFlags & RegionManualOverrideFlags.Role) == 0 && vr.VisualRoleHint != RegionRoleType.Unknown)
                { match.RoleType = vr.VisualRoleHint; match.RoleConfidence = vr.Confidence; }
            }
            else result.Add(new RecognitionRegion { Polygon = vr.Polygon, RoleType = vr.VisualRoleHint, RoleConfidence = vr.Confidence,
                ReadingOrder = vr.ReadingOrderHint ?? int.MaxValue, RecognitionConfidence = vr.Confidence, RegionGeneration = generation,
                DetectionSources = [DetectionSource.VisualModel], ParentRegionId = vr.ParentContainerHint,
                SourceImageRegion = vr.BoundingBox, RendererTargetRegion = vr.BoundingBox });
        }
        foreach (var box in cv)
        {
            var match = result.FirstOrDefault(x => GeometryV2.IntersectionOverUnion(x.BoundingBox, box) >= .35f);
            if (match is not null) match.DetectionSources.Add(DetectionSource.TraditionalCv);
            else result.Add(new RecognitionRegion { Polygon = GeometryV2.RectanglePolygon(box), RegionGeneration = generation,
                DetectionSources = [DetectionSource.TraditionalCv], SourceImageRegion = box, RendererTargetRegion = box });
        }
        return result.Where(x => x.DetectionSources.Contains(DetectionSource.OcrDetector) ||
            x.DetectionSources.Contains(DetectionSource.VisualModel) && x.RoleType != RegionRoleType.Unknown)
            .OrderBy(x => x.ReadingOrder).ToList();
    }
}

public static class RegionRoleClassifierV2
{
    private static readonly Regex Date = new(@"\b(?:19|20)\d{2}[-/.\u5e74]|\b(?:April|January|February|March|May|June|July|August|September|October|November|December)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Metadata = new(
        @"^(?:P(?:A|R)GE\s*\d+|\d+[\u4e07\u842cKMB]?\s*(?:views?|likes?|comments?|\u6761)?|(?:about\s*)?\d+\s*(?:minutes?|hours?|days?)\s*ago|(?:\u5927\u7ea6\s*)?\d+\s*(?:\u5206\u949f|\u5c0f\u65f6|\u5929)\u524d|\d+\s*\u6761(?:\u6d88\u606f|\u8bc4\u8bba))$",
        RegexOptions.IgnoreCase);

    public static (RegionRoleType Role, float Confidence) Classify(RecognitionRegion region, IReadOnlyList<RecognitionRegion> neighbors)
    {
        if ((region.ManualOverrideFlags & RegionManualOverrideFlags.Role) != 0) return (region.RoleType, 1);
        var text = region.StructuredText.Trim(); var b = region.BoundingBox;
        if (text.Length == 0) return (region.RoleType, region.RoleConfidence);
        if (text.StartsWith('@')) return (RegionRoleType.CharacterName, .98f);
        if (Metadata.IsMatch(text)) return (RegionRoleType.Metadata, .94f);
        if (Date.IsMatch(text)) return (RegionRoleType.Header, .9f);
        if (Regex.IsMatch(text, @"^(continue|next|ok|cancel|\u7ee7\u7eed|\u786e\u5b9a|\u53d6\u6d88)$", RegexOptions.IgnoreCase)) return (RegionRoleType.Button, .94f);
        if (Regex.IsMatch(text, @"^(tell|lie|choose|\u9009\u62e9|\u9009\u9879)\b", RegexOptions.IgnoreCase)) return (RegionRoleType.Choice, .85f);
        var letters = text.Where(char.IsLetter).ToArray();
        if (letters.Length >= 2 && letters.All(char.IsUpper)) return (RegionRoleType.Header, .88f);
        var below = neighbors.Where(x => x.RegionId != region.RegionId && x.BoundingBox.Top >= b.Bottom && x.BoundingBox.Top - b.Bottom <= Math.Max(20, b.Height * 2)).ToArray();
        var aboveShort=neighbors.Where(x=>x.RegionId!=region.RegionId&&x.BoundingBox.Bottom<=b.Top&&b.Top-x.BoundingBox.Bottom<=Math.Max(20,b.Height*2)&&
            x.StructuredText.Trim().Length is >=2 and <=32).OrderBy(x=>b.Top-x.BoundingBox.Bottom).FirstOrDefault();
        // Decorative profile cards commonly overlap the large name glyphs and the smaller
        // species subtitle. Treat a smaller, horizontally nested subtitle beneath a larger
        // short label as species even when their OCR boxes overlap vertically.
        aboveShort??=neighbors.Where(x=>x.RegionId!=region.RegionId&&x.BoundingBox.Top<b.Top&&
            x.StructuredText.Trim().Length is >=2 and <=32&&x.BoundingBox.Height>=b.Height*1.25f&&
            Math.Max(0,Math.Min(x.BoundingBox.Right,b.Right)-Math.Max(x.BoundingBox.Left,b.Left))>=Math.Min(x.BoundingBox.Width,b.Width)*.55f)
            .OrderBy(x=>Math.Abs(x.BoundingBox.Bottom-b.Top)).FirstOrDefault();
        if(text.Length is >=3 and <=32&&aboveShort is not null&&below.Any(x=>x.StructuredText.Length>text.Length*1.8f))
            return (RegionRoleType.Species,.86f);
        if (text.Length <= 28 && below.Any(x => x.StructuredText.Length > text.Length * 1.8f)) return (RegionRoleType.CharacterName, .78f);
        if (text.StartsWith('"') || text.StartsWith('\u201c') || text.EndsWith('\u201d')) return (RegionRoleType.Dialogue, .85f);
        var population = neighbors.Any(x => x.RegionId == region.RegionId) ? neighbors : neighbors.Append(region).ToArray();
        var heights = population.Select(x => x.BoundingBox.Height).Where(x => x > 0).OrderBy(x => x).ToArray();
        var medianHeight = heights.Length == 0 ? b.Height : heights[(heights.Length - 1) / 2];
        var widths = population.Select(x => x.BoundingBox.Width).Where(x => x > 0).OrderBy(x => x).ToArray();
        var medianWidth = widths.Length == 0 ? b.Width : widths[(widths.Length - 1) / 2];
        var top = population.Min(x => x.BoundingBox.Top);
        if (region.SourceBlockIds.Count <= 3 && text.Length <= 100 && b.Top <= top + Math.Max(15, medianHeight) &&
            (b.Height >= medianHeight * 1.25f || (b.Height >= medianHeight * 1.1f && b.Width >= medianWidth * 1.25f) || b.Width >= medianWidth * 1.8f))
            return (RegionRoleType.Title, .84f);
        if (region.SourceBlockIds.Count <= 3 && b.Height >= neighbors.Select(x => x.BoundingBox.Height).DefaultIfEmpty(b.Height).Average() * 1.5f) return (RegionRoleType.Title, .75f);
        return (RegionRoleType.BodyParagraph, .62f);
    }
}

public static class TranslationUnitBuilderV2
{
    public static IReadOnlyList<TranslationUnitV2> Build(IReadOnlyList<RecognitionRegion> regions)
    {
        var output = new List<TranslationUnitV2>();
        foreach (var region in regions.Where(x => !x.IsIgnored).OrderBy(x => x.ReadingOrder))
        {
            var text = string.IsNullOrWhiteSpace(region.StructuredText) ? region.CorrectedText : region.StructuredText;
            if((region.ManualOverrideFlags&RegionManualOverrideFlags.PreserveOriginal)==0&&TranslationAuthorizationPolicy.ShouldPreserve(region,text))region.PreserveOriginal=true;
            if (string.IsNullOrWhiteSpace(text) && !region.PreserveOriginal) continue;
            // Keep the Settings 1.4 wire identity contract. TranslationUnit is
            // the V2 domain object, while GRPxxx remains the externally visible
            // request/recovery ID expected by the translation service.
            var id = $"GRP{output.Count + 1:000}"; region.TranslationUnitId = id;
            output.Add(new TranslationUnitV2(id, [region.RegionId], region.RoleType, text,
                region.ReadingOrder, region.PreserveOriginal, region.SourceBlockIds.ToArray()));
        }
        return output;
    }
}

public static class TranslationAuthorizationPolicy
{
    public static bool ShouldPreserve(RecognitionRegion region,string text)
    {
        var value=(text??"").Trim();if(value.Length==0)return true;
        if(region.RoleType is RegionRoleType.Image or RegionRoleType.Illustration)return true;
        if(value.StartsWith('@')||value.All(c=>char.IsDigit(c)||char.IsWhiteSpace(c)||c is '/' or '-' or ':' or '.'))return true;
        var words=System.Text.RegularExpressions.Regex.Matches(value,@"[\p{L}\p{N}]+",System.Text.RegularExpressions.RegexOptions.CultureInvariant).Select(m=>m.Value).ToArray();
        if(words.Length==1&&words[0].Length==1)return true;
        if(region.RoleType is RegionRoleType.CharacterName or RegionRoleType.Species)return false;
        if(words.Length==1&&words[0].Length<=4&&words[0].All(c=>!char.IsLetter(c)||char.IsUpper(c)))return true;
        return false;
    }
}

public static class TranslationMappingV2
{
    public static void Apply(RecognitionDocumentV2 document, TranslationBatchResult result, long generation)
    {
        if (!result.HasSourceAlignedAllocations)
        {
            Apply(document,result.Translations,generation);
            document.TranslationMappingMode="LEGACY_MAPPING";
            return;
        }
        var items=document.TranslationUnits.Select(x=>new TranslationItem(x.Id,x.Text,ToRole(x.RoleType),x.StableSourceIds)).ToArray();
        var validation=TranslationAllocationContractV1.Validate(items,result.Translations,result.Allocations);
        if(!validation.IsValid)throw new BatchJsonException($"ALLOCATION_INVALID: {validation.Reason}");
        if(generation!=document.TranslationGeneration)return;
        document.TranslationMappingMode="SOURCE_ALIGNED_MAPPING_V1";
        document.TranslationAllocations.Clear();
        foreach(var pair in validation.Allocations)document.TranslationAllocations[pair.Key]=pair.Value;
        foreach(var unit in document.TranslationUnits)
        foreach(var id in unit.RegionIds)
        {
            var region=document.Regions.Single(x=>x.RegionId==id);
            if(unit.PreserveOriginal){region.TranslationText=unit.Text;continue;}
            var owned=validation.Allocations[unit.Id].Where(x=>x.SourceIds.All(region.SourceBlockIds.Contains)).OrderBy(x=>x.Sequence).ToArray();
            region.TranslationText=owned.Length==0?"":FieldTranslationIntegrityV2.Normalize(unit.Text,string.Concat(owned.Select(x=>x.TranslatedText)));
        }
        RealPathDiagnosticTrace.Stage("POST_TRANSLATION_MAPPING",document.Regions.Select(RealPathDiagnosticTrace.RegionRow));
    }

    public static void Apply(RecognitionDocumentV2 document, IReadOnlyDictionary<string, string> translations, long generation)
    {
        if (generation != document.TranslationGeneration) return;
        var expected = document.TranslationUnits.Where(x=>!x.PreserveOriginal&&!string.IsNullOrWhiteSpace(x.Text)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var allowed = document.TranslationUnits.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
        var actual = translations.Keys.ToHashSet(StringComparer.Ordinal);
        var missing=expected.Except(actual).ToArray();var extra=actual.Except(allowed).ToArray();
        if (missing.Length>0||extra.Length>0) throw new BatchJsonException($"TranslationUnit ID 集合不完整。missing={string.Join(',', missing)}; extra={string.Join(',', extra)}");
        foreach (var unit in document.TranslationUnits)
            foreach (var id in unit.RegionIds)
            {
                var region = document.Regions.Single(x => x.RegionId == id);
                region.TranslationText = unit.PreserveOriginal ? unit.Text : FieldTranslationIntegrityV2.Normalize(unit.Text,translations[unit.Id]);
            }
        RealPathDiagnosticTrace.Stage("POST_TRANSLATION_MAPPING",document.Regions.Select(RealPathDiagnosticTrace.RegionRow));
    }
    private static StructuredTextRole ToRole(RegionRoleType role)=>Enum.TryParse<StructuredTextRole>(role.ToString(),out var value)?value:StructuredTextRole.Unknown;
}

public static class RegionDiagnosticsV2
{
    public static string Describe(RecognitionRegion r, OcrEngineKind engine, string visualModel) =>
        $"Region ID: {r.RegionId}\nRole: {r.RoleType}\nRole Confidence: {r.RoleConfidence:F2}\nVisual Model: {visualModel}\n" +
        $"Detection Sources: {string.Join(", ", r.DetectionSources.Distinct())}\nOCR Engine: {engine}\nOCR Confidence: {r.RecognitionConfidence:F2}\n" +
        $"SourceBlock Count: {r.SourceBlockIds.Count}\nReading Order: {r.ReadingOrder}\nTranslationUnitId: {r.TranslationUnitId}";
}
