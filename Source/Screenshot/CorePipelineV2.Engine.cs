using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static class CorePipelineEngine
{
    public static CorePipelineDocument Analyze(Size canvas, IReadOnlyList<RawOcrLine> raw,
        IReadOnlyList<VisualEvidenceRegion>? visualEvidence = null, Bitmap? sourceImage = null)
    {
        var normalized = raw.Select(Normalize).Where(x => !string.IsNullOrWhiteSpace(x.SourceText))
            .OrderBy(x => x.ReadingOrder).ThenBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left).ToArray();
        if(sourceImage is not null&&sourceImage.Size!=canvas)throw new ArgumentException("Source geometry mismatch",nameof(sourceImage));
        using var sourcePixels=sourceImage is null?null:ReadOnlyBitmapPixelBuffer.Create(sourceImage);
        if(sourcePixels is not null)normalized=SourceAdornmentEvidence.SeparateEmbeddedRailIcons(sourcePixels,normalized);
        var owners = InferRegionOwners(canvas, normalized);
        var sourceHeadings=SourceHeadingBoundary.ObserveGroups(normalized,sourcePixels);
        IReadOnlySet<string> uncertainIcons=new HashSet<string>();
        IReadOnlySet<string> sourceMarks=new HashSet<string>();
        IReadOnlySet<string> repeatedGraphicMarks=new HashSet<string>();
        var repeatedGraphicEvidence="";
        IReadOnlyDictionary<string,SourceCellGeometry.Cell> cells=new Dictionary<string,SourceCellGeometry.Cell>();
        if(sourceImage is not null)
        {
            if(sourceImage.Size!=canvas)throw new ArgumentException("Source geometry mismatch",nameof(sourceImage));
            owners=RefineSourceControlOwners(owners,normalized,sourceImage);
            var pixels=sourcePixels!;
            cells=SourceCellGeometry.ObserveGrid(pixels,normalized);
            uncertainIcons=SourceIconUncertainty.Observe(normalized,cells)
                .Concat(SourceIconUncertainty.ObserveContradictoryWordDirection(normalized))
                .Concat(SourceIconUncertainty.ObserveAmbiguousGlyphNotation(normalized,cells))
                .Concat(SourceIconUncertainty.ObserveRewardIcons(pixels,normalized))
                .Concat(SourceAdornmentEvidence.ObserveIconRail(pixels,normalized)).ToHashSet(StringComparer.Ordinal);
            sourceMarks=SourceAdornmentEvidence.ObserveInitialismMarks(pixels,normalized,cells);
            var repeated=SourceRepeatedGraphicMark.Observe(pixels,normalized,
                sourceHeadings.Keys.ToHashSet(StringComparer.Ordinal),cells);
            repeatedGraphicMarks=repeated.SourceIds;
            repeatedGraphicEvidence="REPEATED_CONNECTED_ORNAMENT_AT_DISTINCT_SOURCE_SCALES;scanned="+repeated.ScannedPixels+
                ";pairs="+string.Join("|",repeated.Pairs.Select(p=>$"{p.First}:{p.Second}:{p.Similarity:F3}"));
            owners=owners.Select(o=>cells.TryGetValue(o.SourceId,out var c)?o with{
                RegionOwnerId=$"SOURCE-CELL-{c.Bounds.X}-{c.Bounds.Y}-{c.Bounds.Width}-{c.Bounds.Height}",
                AnchorX=c.Bounds.Left,StrongOwner=true,RoleHint="NativeControl",Evidence=c.Proof}:o).ToArray();
        }
        owners=owners.Select(o=>sourceHeadings.TryGetValue(o.SourceId,out var headingOwner)&&o.RoleHint!="NativeControl"?o with
            {RegionOwnerId=headingOwner,StrongOwner=true,RoleHint="SourceHeading",
             Evidence="SOURCE_HEADING_SPACING_WIDTH_CASE_AND_FOLLOWING_PARAGRAPH"}:o).ToArray();
        var inclinedQuantities=normalized.Where(l=>IsPureNumber(l.SourceText)&&
            SourceOrientedText.Observe(new VisualBlock{BlockId=l.SourceId,Lines=[l],Bounds=l.Bounds,LayoutBehavior=BlockLayoutBehavior.Fixed}) is not null)
            .Select(l=>l.SourceId).ToHashSet(StringComparer.Ordinal);
        owners=owners.Select(o=>inclinedQuantities.Contains(o.SourceId)?o with
            {RegionOwnerId="SOURCE-QUANTITY-"+o.SourceId,StrongOwner=true,RoleHint="SourceQuantity",
             Evidence="INDEPENDENT_INCLINED_QUANTITY_POLYGON_PRESERVES_SOURCE_VALUE"}:o).ToArray();
        owners=owners.Select(o=>uncertainIcons.Contains(o.SourceId)||sourceMarks.Contains(o.SourceId)||repeatedGraphicMarks.Contains(o.SourceId)?o with
            {RegionOwnerId="SOURCE-ICON-"+o.SourceId,StrongOwner=true,RoleHint="IndependentIdentifier",
             Evidence="SOURCE_MULTICOLOR_SINGLE_OBJECT_CONTRADICTS_OCR_WORD"}:o).ToArray();
        var inlineControls=SourceInlineControlOwners.Observe(normalized);
        owners=owners.Select(o=>inlineControls.Contains(o.SourceId)?o with {RegionOwnerId="INLINE-CONTROL-"+o.SourceId,StrongOwner=true,RoleHint="InlineControl",Evidence="REPEATED_SEPARATE_SOURCE_LABELS_ON_ONE_BASELINE"}:o).ToArray();
        var grouping = BuildVisualBlocks(canvas, normalized, owners);
        var blocks = grouping.Blocks;
        var sourceMedian=Median(normalized.Select(l=>Math.Max(1,l.Bounds.Height)));
        foreach(var block in blocks)
        {
            if(block.Lines.All(l=>sourceHeadings.ContainsKey(l.SourceId)))block.RoleHint="PossibleTitle";
            if(block.Lines.Count==1&&inlineControls.Contains(block.Lines[0].SourceId))block.SourceControlCorridor=block.Bounds;
            var observed=block.Lines.Select(l=>cells.GetValueOrDefault(l.SourceId)).ToArray();
            if(observed.All(c=>c is not null)&&observed.Select(c=>c!.Bounds).Distinct().Count()==1)
            {block.SourceCell=observed[0];block.RoleHint="PossibleControl";block.LayoutBehavior=BlockLayoutBehavior.Fixed;}
            block.SourceRole=SourceTextRoleEvidence.Observe(block,sourceMedian,canvas);
            if(block.SourceRole.Role=="Prose"&&block.SourceRole.Alignment=="Center"&&block.Lines.Count==1)
            {
                var row=block.Lines[0];var h=row.Bounds.Height;
                var heading=normalized.Where(l=>sourceHeadings.ContainsKey(l.SourceId)&&
                    l.Bounds.Top<row.Bounds.Top-h*.5f&&row.Bounds.Top-l.Bounds.Bottom<=h&&
                    Math.Abs(l.Bounds.Left-row.Bounds.Left)<=h*.35f).OrderByDescending(l=>l.Bounds.Top).FirstOrDefault();
                if(heading is not null)block.SourceRole=new("Prose","Left",
                    "SOURCE_DESCRIPTION_SHARES_LOCAL_HEADING_START_OVERRIDES_COINCIDENT_CANVAS_AXIS");
            }
            if(block.SourceRole.Role=="Prose"){block.RoleHint="SourceProse";block.LayoutBehavior=BlockLayoutBehavior.Flow;}
        }
        SourceParallelColumns.AssignLayoutCorridors(canvas,blocks,owners);
        ApplyTextSelection(canvas, normalized, blocks, visualEvidence ?? []);
        foreach(var block in blocks.Where(b=>b.Lines.All(l=>uncertainIcons.Contains(l.SourceId))))
            Set(block,TextSelectionAction.Preserve,"UNREADABLE_OR_UNCERTAIN_SOURCE_ILLUSTRATION_WORD_GEOMETRY");
        foreach(var block in blocks.Where(b=>b.Lines.All(l=>sourceMarks.Contains(l.SourceId))))
        {
            block.SourceRole=new("GraphicIdentifier","Source","ISOLATED_STYLIZED_INITIALISM_SOURCE_MARK");
            Set(block,TextSelectionAction.Preserve,"SOURCE_INITIALISM_MARK_WITHOUT_EXPANSION_EVIDENCE");
        }
        foreach(var block in blocks.Where(b=>b.Lines.All(l=>repeatedGraphicMarks.Contains(l.SourceId))))
        {
            block.SourceRole=new("GraphicIdentifier","Source",repeatedGraphicEvidence);
            Set(block,TextSelectionAction.Preserve,"SOURCE_REPEATED_GRAPHIC_MARK_WITH_CONNECTED_ORNAMENT");
        }
        var translations = blocks.ToDictionary(x => x.BlockId, x => new BlockTranslation
        {
            BlockId = x.BlockId, SourceText = x.SourceText
        }, StringComparer.Ordinal);
        return new(canvas, raw, normalized, blocks, translations, visualEvidence ?? [], owners, grouping.Edges);
    }

    private static void ApplyTextSelection(Size canvas, IReadOnlyList<NormalizedOcrLine> lines,
        IReadOnlyList<VisualBlock> blocks, IReadOnlyList<VisualEvidenceRegion> visualEvidence)
    {
        if (blocks.Count == 0) return;
        var medianHeight = Median(lines.Select(x => Math.Max(1f, x.Bounds.Height)));
        var canvasArea = Math.Max(1d, canvas.Width * (double)canvas.Height);
        foreach (var block in blocks)
        {
            var text = block.SourceText.Trim();
            if (IsProtectedIdentifier(text)) Set(block, TextSelectionAction.Preserve, "PRESERVE_IDENTIFIER");
            else if (IsPerformanceOverlay(text)) Set(block, TextSelectionAction.Preserve, "PRESERVE_PERFORMANCE_OVERLAY");
            else if (IsPureNumber(text)) Set(block, TextSelectionAction.Preserve, "PURE_NUMBER");
            else if (IsPureSymbols(text)) Set(block, TextSelectionAction.Preserve, "PURE_SYMBOLS");
            else if (IsAlreadyTargetLanguage(text)) Set(block, TextSelectionAction.Preserve, "ALREADY_TARGET_LANGUAGE");
            else if (IsEmbeddedInImageRegion(block, visualEvidence, canvasArea))
                Set(block, TextSelectionAction.Preserve, "EMBEDDED_IMAGE_TEXT");
            else Set(block, TextSelectionAction.Translate, "ORDINARY_SCREEN_TEXT");
        }
    }

    private static void Set(VisualBlock block, TextSelectionAction action, string reason)
    { block.TextSelection = action; block.TextSelectionReason = reason; }

    private static bool IsPureNumber(string text) => NumericFidelityV2.IsPureQuantity(text);

    private static bool IsPureSymbols(string text) => text.Length > 0 &&
        text.All(c => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c));

    private static bool IsProtectedIdentifier(string text)
    {
        if(Regex.IsMatch(text,@"^@[A-Za-z0-9_.-]+$",RegexOptions.CultureInvariant))return true;
        return Uri.TryCreate(text,UriKind.Absolute,out var uri)&&
               (uri.Scheme==Uri.UriSchemeHttp||uri.Scheme==Uri.UriSchemeHttps);
    }

    private static bool IsPerformanceOverlay(string text)
    {
        var tokens=Regex.Matches(text,@"\b(?:FPS|GPU|CPU|G-SYNC|FRAME(?:TIME|\s+TIME)?)\b",
            RegexOptions.IgnoreCase|RegexOptions.CultureInvariant).Count;
        return tokens>=2||tokens>=1&&Regex.IsMatch(text,@"\d+\s*(?:%|ms|fps|Hz|\(1%L\))",
            RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    }

    private static bool IsAlreadyTargetLanguage(string text)
    {
        var han = text.Count(c => c is >= '\u3400' and <= '\u9fff');
        var latin = text.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        return han > 0 && latin == 0;
    }

    private static bool IsEmbeddedInImageRegion(VisualBlock block,
        IReadOnlyList<VisualEvidenceRegion> regions, double canvasArea)
    {
        foreach (var region in regions)
        {
            // An image enclosure or display font does not prove that readable
            // text is non-semantic. Only an explicit graphic-role observation
            // may protect the entire object at this boundary.
            if(region.Confidence<.9f||region.Role is not ("Logo" or "DecorativeGraphic" or "NonlinguisticSymbols"))continue;
            var area = region.Bounds.Width * (double)region.Bounds.Height;
            if (area <= block.Bounds.Width * block.Bounds.Height * 1.25 || area >= canvasArea * .38) continue;
            var intersection = RectangleF.Intersect(region.Bounds, block.Bounds);
            var coverage = intersection.Width * intersection.Height /
                Math.Max(1f, block.Bounds.Width * block.Bounds.Height);
            if (coverage >= .88f) return true;
        }
        return false;
    }

    internal static bool NumericTokensMatch(string source, string translated)
        => NumericFidelityV2.Matches(source, translated);

    public static void ApplyDeterministicTranslations(CorePipelineDocument document,
        Func<VisualBlock, (bool Accepted, string Text, string Error)> translator)
    {
        foreach (var block in document.VisualBlocks)
        {
            var result = translator(block);
            var state = document.Translations[block.BlockId];
            state.TranslatedText = result.Accepted ? result.Text.Trim() : "";
            state.State = result.Accepted && state.TranslatedText.Length > 0
                ? BlockTranslationState.Accepted : BlockTranslationState.Failed;
            state.FailureReason = state.State == BlockTranslationState.Failed
                ? (string.IsNullOrWhiteSpace(result.Error) ? "EMPTY_BLOCK_TRANSLATION" : result.Error) : "";
        }
    }

    private static NormalizedOcrLine Normalize(RawOcrLine line)
    {
        var source = string.IsNullOrWhiteSpace(line.CorrectedText) ? line.RawText : line.CorrectedText;
        var normalized = source.Normalize(NormalizationForm.FormKC)
            .Replace('\u00A0', ' ').Replace("\r", " ").Replace("\n", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        var trace = source == normalized ? "UNCHANGED" : "NFKC+WHITESPACE";
        return new(line.SourceId, normalized, line.Polygon.ToArray(), line.Bounds,
            line.Confidence, line.ReadingOrder, trace);
    }

    private sealed record VisualBlockGroupingResult(IReadOnlyList<VisualBlock> Blocks,
        IReadOnlyList<GroupingEdgeEvidence> Edges);

    private static IReadOnlyList<RegionOwnerEvidence> InferRegionOwners(Size canvas,
        IReadOnlyList<NormalizedOcrLine> lines)
    {
        if(lines.Count==0)return [];
        var medianHeight=Median(lines.Select(x=>Math.Max(1f,x.Bounds.Height)));
        var tolerance=Math.Max(18f,medianHeight*1.15f);
        var clusters=new List<List<NormalizedOcrLine>>();
        foreach(var line in lines.OrderBy(x=>x.Bounds.Left).ThenBy(x=>x.Bounds.Top))
        {
            var cluster=clusters
                .Select(x=>(Lines:x,Anchor:x.Average(y=>y.Bounds.Left)))
                .Where(x=>Math.Abs(x.Anchor-line.Bounds.Left)<=tolerance)
                .OrderBy(x=>Math.Abs(x.Anchor-line.Bounds.Left)).Select(x=>x.Lines).FirstOrDefault();
            if(cluster is null){cluster=[];clusters.Add(cluster);}
            cluster.Add(line);
        }
        var ordered=clusters.Select(x=>new{Lines=x,Anchor=x.Average(y=>y.Bounds.Left)})
            .OrderBy(x=>x.Anchor).ToArray();
        var result=new List<RegionOwnerEvidence>();
        for(var index=0;index<ordered.Length;index++)
        {
            var cluster=ordered[index];
            var localSupport=cluster.Lines.Count;
            var strong=localSupport>=2;
            var id=$"REG-{index+1:D3}-{Math.Round(cluster.Anchor):0000}";
            foreach(var line in cluster.Lines)
                result.Add(new(line.SourceId,id,(float)cluster.Anchor,localSupport,strong,
                    InferRegionRole(canvas,line),$"LEFT_ANCHOR_CLUSTER;tolerance={tolerance:F1};members={localSupport}"));
        }
        var parallel=SourceParallelColumns.Observe(canvas,lines);
        return result.Select(o=>parallel.TryGetValue(o.SourceId,out var owner)?o with
            {RegionOwnerId=owner,StrongOwner=true,RoleHint="ParallelCaptionColumn",
                Evidence="SOURCE_PARALLEL_REPEATED_CENTER_AXES_AND_SEPARATE_ROWS"}:o)
            .OrderBy(x=>lines.First(y=>y.SourceId==x.SourceId).ReadingOrder).ToArray();
    }

    private static IReadOnlyList<RegionOwnerEvidence> RefineSourceControlOwners(
        IReadOnlyList<RegionOwnerEvidence> original,IReadOnlyList<NormalizedOcrLine> lines,Bitmap source)
    {
        using var pixels=ReadOnlyBitmapPixelBuffer.Create(source);
        var result=original.ToDictionary(x=>x.SourceId,StringComparer.Ordinal);
        var controls=new List<(Rectangle Bounds,string Id)>();
        foreach(var line in lines)
        {
            var proof=SourceControlMaterial.Observe(pixels,Rectangle.Round(line.Bounds));
            if(proof is null)continue;
            // Enclosed glyph material can also be a local shadow around a word.
            // Splitting translation ownership needs independent container edges,
            // not merely a cleanup material component.
            var observed=new VisualBlock{BlockId=line.SourceId,Lines=[line],Bounds=line.Bounds,
                LayoutBehavior=BlockLayoutBehavior.Fixed};
            var interior=SourceTextPanel.ObserveControl(pixels,observed,proof.Surface);
            var independentIdentity=IsProtectedIdentifier(line.SourceText.Trim());
            if(interior is null&&!independentIdentity)continue;
            var controlBounds=interior is not null?Rectangle.Round(interior.Interior):proof.Bounds;
            var shared=controls.FirstOrDefault(x=>{
                var overlap=Rectangle.Intersect(x.Bounds,controlBounds);
                var union=x.Bounds.Width*x.Bounds.Height+controlBounds.Width*controlBounds.Height-overlap.Width*overlap.Height;
                return overlap.Width*overlap.Height/(double)Math.Max(1,union)>=.8;
            });
            var id=shared.Id;
            if(id is null)
            {
                id=$"SOURCE-CONTROL-{controlBounds.X}-{controlBounds.Y}-{controlBounds.Width}-{controlBounds.Height}";
                controls.Add((controlBounds,id));
            }
            result[line.SourceId]=new(line.SourceId,id,controlBounds.Left,1,true,
                independentIdentity?"IndependentIdentifier":"NativeControl",
                proof.Reason+";"+(independentIdentity?"SOURCE_IDENTIFIER_IDENTITY":interior!.Proof));
        }
        return original.Select(x=>result[x.SourceId]).ToArray();
    }

    private static string InferRegionRole(Size canvas,NormalizedOcrLine line)
    {
        var text=line.SourceText.Trim();var b=line.Bounds;
        if(IsPerformanceOverlay(text))return "DebugOverlay";
        if(b.Top>=canvas.Height*.87f)return "BottomNavigation";
        if(b.Left<=canvas.Width*.32f&&b.Top>=canvas.Height*.55f&&b.Width>=canvas.Width*.14f&&text.Length>=12)
            return "DialogueRegion";
        if(text.Contains(':')&&b.Width>=canvas.Width*.12f)return "ProfileMetadata";
        if(IsShortControl(text))return b.Width>=canvas.Width*.16f?"Choice":"Menu";
        if(b.Width>=canvas.Width*.12f)return "PanelBody";
        return "ToastStatus";
    }

    private static VisualBlockGroupingResult BuildVisualBlocks(Size canvas, IReadOnlyList<NormalizedOcrLine> lines,
        IReadOnlyList<RegionOwnerEvidence> ownerEvidence)
    {
        if (lines.Count == 0) return new([],[]);
        var medianHeight = Median(lines.Select(x => Math.Max(1f, x.Bounds.Height)));
        var owners=ownerEvidence.ToDictionary(x=>x.SourceId,StringComparer.Ordinal);
        var connectorPairs=SourceCenteredConnectorPairs(lines);
        var paragraphPairs=SourceParagraphPairs(lines,owners);
        var remaining = new HashSet<string>(lines.Select(x => x.SourceId), StringComparer.Ordinal);
        var result = new List<VisualBlock>();
        var edges=new Dictionary<string,GroupingEdgeEvidence>(StringComparer.Ordinal);
        foreach (var seed in lines)
        {
            if (!remaining.Remove(seed.SourceId)) continue;
            var members = new List<NormalizedOcrLine> { seed };
            var changed = true;
            while (changed)
            {
                changed = false;
                var union = Union(members.Select(x => x.Bounds));
                foreach (var next in lines.Where(x => remaining.Contains(x.SourceId)).ToArray())
                {
                    var nearest=members.OrderBy(x=>Distance(x.Bounds,next.Bounds)).First();
                    var edge=EvaluateGroupingEdge(nearest,next,medianHeight,owners);
                    if(connectorPairs.Contains(PairKey(nearest.SourceId,next.SourceId)))
                        edge=edge with{Admitted=true,BoundaryEvidence=false,FinalAdmissionReason="ADMIT_SOURCE_CENTERED_CONNECTOR_STACK"};
                    if(paragraphPairs.Contains(PairKey(nearest.SourceId,next.SourceId)))
                        edge=edge with{Admitted=true,BoundaryEvidence=false,FinalAdmissionReason="ADMIT_SOURCE_SENTENCE_GEOMETRY"};
                    var ownerBoundary=members.Any(member=>HasStrongOwnerBoundary(member,next,medianHeight,owners));
                    if(ownerBoundary&&edge.Admitted)
                        edge=edge with{Admitted=false,BoundaryEvidence=true,
                            FinalAdmissionReason="REJECT_COMPONENT_STRONG_REGION_BOUNDARY"};
                    var edgeKey=string.CompareOrdinal(edge.SourceId,edge.TargetId)<=0
                        ?$"{edge.SourceId}|{edge.TargetId}":$"{edge.TargetId}|{edge.SourceId}";
                    edges[edgeKey]=edge;
                    if (!edge.Admitted) continue;
                    remaining.Remove(next.SourceId); members.Add(next); changed = true;
                }
            }
            // OCR result ordering can put a short centered connector after the
            // following wider row. Within an admitted local block, read spatially.
            members.Sort((a,b)=>a.Bounds.Top!=b.Bounds.Top
                ?a.Bounds.Top.CompareTo(b.Bounds.Top):a.Bounds.Left.CompareTo(b.Bounds.Left));
            var bounds = Union(members.Select(x => x.Bounds));
            result.Add(new VisualBlock
            {
                BlockId = StableId(members.Select(x => x.SourceId)), Lines = members,
                Bounds = bounds, LayoutBehavior = InferLayout(canvas, members, bounds, medianHeight),
                RoleHint = AdvisoryRole(canvas, members, bounds, medianHeight)
            });
        }
        return new(result.OrderBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left).ToArray(),
            edges.Values.OrderBy(x=>x.SourceId,StringComparer.Ordinal).ThenBy(x=>x.TargetId,StringComparer.Ordinal).ToArray());
    }

    private static string PairKey(string a,string b)=>string.CompareOrdinal(a,b)<=0?a+"|"+b:b+"|"+a;
    private static HashSet<string> SourceParagraphPairs(IReadOnlyList<NormalizedOcrLine> lines,
        IReadOnlyDictionary<string,RegionOwnerEvidence> owners)
    {
        var pairs=new HashSet<string>(StringComparer.Ordinal);
        foreach(var first in lines)
        {
            var rows=new List<NormalizedOcrLine>{first};var current=first;
            for(var budget=0;budget<48;budget++)
            {
                var a=current.Bounds;var h=a.Height;
                var next=lines.Where(l=>l.Bounds.Top>a.Top+h*.65f&&l.Bounds.Top-a.Bottom<=h*1.15f&&
                    l.Bounds.Height>=h/1.4f&&l.Bounds.Height<=h*1.4f&&
                    (Math.Abs(l.Bounds.Left-a.Left)<=h*.22f||Math.Abs(l.Bounds.Left+l.Bounds.Width/2-a.Left-a.Width/2)<=h*.22f)&&
                    !HasStrongOwnerBoundary(current,l,h,owners))
                    .OrderBy(l=>l.Bounds.Top).ThenBy(l=>Math.Abs(l.Bounds.Left-a.Left)).FirstOrDefault();
                if(next is null||SourceParagraphBoundary.Separates(current,next,lines))break;
                // Sentence continuation must not erase the independently
                // observed compact heading/body boundary. This separates the
                // translation units; it never skips the heading as artwork.
                if(LooksLikeStructuralLabel(current.SourceText)&&
                    !current.SourceText.Any(char.IsLower)&&next.SourceText.Any(char.IsLower)&&
                    next.Bounds.Width>a.Width*1.65f&&next.Bounds.Top-a.Bottom>h*.35f)break;
                rows.Add(next);current=next;
                if(SourceTextRoleEvidence.Paragraph(rows) is not null)
                {
                    foreach(var (left,right) in rows.Zip(rows.Skip(1)))pairs.Add(PairKey(left.SourceId,right.SourceId));
                    break;
                }
            }
        }
        return pairs;
    }
    private static HashSet<string> SourceCenteredConnectorPairs(IReadOnlyList<NormalizedOcrLine> lines)
    {
        var result=new HashSet<string>(StringComparer.Ordinal);
        static bool Head(string text)
        {
            var words=text.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            return text.Length<=32&&words.Length is >0 and <=4&&
                words.All(x=>char.IsUpper(x[0])&&x.All(char.IsLetter));
        }
        static float Center(RectangleF b)=>b.Left+b.Width/2;
        foreach(var middle in lines.Where(x=>x.SourceText.Trim().ToLowerInvariant() is "of" or "and" or "&"))
        {
            var m=middle.Bounds;
            var near=lines.Where(x=>x.SourceId!=middle.SourceId&&Head(x.SourceText)&&
                m.Width<=x.Bounds.Width*.65f&&m.Height>=x.Bounds.Height*.45f&&m.Height<=x.Bounds.Height*1.5f&&
                Math.Abs(Center(m)-Center(x.Bounds))<=Math.Min(m.Height,x.Bounds.Height)*.65f).ToArray();
            var before=near.Where(x=>x.Bounds.Top<m.Top&&AxisGap(x.Bounds.Top,x.Bounds.Bottom,m.Top,m.Bottom)<=m.Height*.8f)
                .OrderByDescending(x=>x.Bounds.Top).FirstOrDefault();
            var after=near.Where(x=>x.Bounds.Top>m.Top&&AxisGap(x.Bounds.Top,x.Bounds.Bottom,m.Top,m.Bottom)<=m.Height*.8f)
                .OrderBy(x=>x.Bounds.Top).FirstOrDefault();
            if(before is null||after is null)continue;
            // Three aligned observations establish a heading with a connector;
            // two ordinary aligned menu entries alone never qualify.
            result.Add(PairKey(before.SourceId,middle.SourceId));
            result.Add(PairKey(middle.SourceId,after.SourceId));
        }
        return result;
    }

    private static bool HasStrongOwnerBoundary(NormalizedOcrLine a,NormalizedOcrLine b,float medianHeight,
        IReadOnlyDictionary<string,RegionOwnerEvidence> owners)
    {
        var oa=owners[a.SourceId];var ob=owners[b.SourceId];
        if((oa.RoleHint is "NativeControl" or "InlineControl" or "IndependentIdentifier" or "ParallelCaptionColumn" or "SourceHeading" or "SourceQuantity"||
            ob.RoleHint is "NativeControl" or "InlineControl" or "IndependentIdentifier" or "ParallelCaptionColumn" or "SourceHeading" or "SourceQuantity")&&oa.RegionOwnerId!=ob.RegionOwnerId)return true;
        if(!oa.StrongOwner||!ob.StrongOwner||oa.RegionOwnerId==ob.RegionOwnerId)return false;
        var sameRow=VerticalOverlap(a.Bounds,b.Bounds)>=.35f&&
                    AxisGap(a.Bounds.Left,a.Bounds.Right,b.Bounds.Left,b.Bounds.Right)<=medianHeight*1.35f;
        var separatedAnchors=Math.Abs(oa.AnchorX-ob.AnchorX)>Math.Max(medianHeight*3.5f,48f);
        return sameRow&&separatedAnchors;
    }

    private static GroupingEdgeEvidence EvaluateGroupingEdge(NormalizedOcrLine source,NormalizedOcrLine target,
        float medianHeight,IReadOnlyDictionary<string,RegionOwnerEvidence> owners)
    {
        var a=source.Bounds;var b=target.Bounds;
        var verticalGap = AxisGap(a.Top, a.Bottom, b.Top, b.Bottom);
        var horizontalGap = AxisGap(a.Left, a.Right, b.Left, b.Right);
        var hOverlap=HorizontalOverlap(a,b);var vOverlap=VerticalOverlap(a,b);
        var verticalStack = verticalGap <= Math.Max(medianHeight * 1.35f, Math.Min(a.Height, b.Height) * 1.6f)
            && hOverlap >= .28f;
        var sameRow = horizontalGap <= medianHeight * 1.1f && vOverlap >= .45f;
        var leftAligned = Math.Abs(a.Left - b.Left) <= Math.Max(8, medianHeight * .65f);
        var heightRatio = Math.Max(a.Height, b.Height) / Math.Max(1, Math.Min(a.Height, b.Height));
        var strongWhitespace = verticalGap > medianHeight * 2.1f || horizontalGap > medianHeight * 3.5f;
        var columnBreak = horizontalGap > medianHeight * 1.6f && hOverlap < .1f;
        var alignmentBoundary = verticalGap > 0 && Math.Abs(a.Left - b.Left) > Math.Max(24, medianHeight * 1.4f);
        var paragraphBoundary = verticalGap > medianHeight * .65f;
        var structuralTextBoundary = verticalGap > 0 &&
            (LooksLikeStructuralLabel(source.SourceText) || LooksLikeStructuralLabel(target.SourceText));
        var compactHeaderBoundary = verticalGap > medianHeight * .35f && source.SourceText.Length <= 48 &&
            b.Width > a.Width * 1.65f;
        var likelyIndependentControls = IsShortControl(source.SourceText) && IsShortControl(target.SourceText)
            && verticalGap > medianHeight * .45f;
        var strongOwnerBoundary=HasStrongOwnerBoundary(source,target,medianHeight,owners);
        // Separate observations of adjacent controls are not broken words when the
        // gap exceeds half a glyph-height. Preserve close same-baseline OCR fragments.
        var spacedControlBoundary=sameRow && IsShortControl(source.SourceText) &&
            IsShortControl(target.SourceText) && horizontalGap>Math.Min(a.Height,b.Height)*.5f;
        bool VerticalLatin(NormalizedOcrLine line)=>line.Bounds.Height>line.Bounds.Width*1.35f &&
            line.SourceText.Count(char.IsAsciiLetter)>=2 &&
            line.SourceText.Count(char.IsAsciiLetter)>=line.SourceText.Count(c=>!char.IsWhiteSpace(c))*.8f;
        var orientationBoundary=VerticalLatin(source)!=VerticalLatin(target);
        var continuation=sameRow||verticalStack||(leftAligned&&verticalGap<=medianHeight*1.15f);
        var boundary=orientationBoundary||spacedControlBoundary||strongOwnerBoundary||strongWhitespace||columnBreak||alignmentBoundary||paragraphBoundary||
                     structuralTextBoundary||compactHeaderBoundary||likelyIndependentControls||heightRatio>2.15f;
        var admitted=continuation&&!boundary;
        var reason=admitted?"ADMIT_LOCAL_CONTINUATION":strongOwnerBoundary?"REJECT_STRONG_REGION_COLUMN_BOUNDARY":
            orientationBoundary?"REJECT_SOURCE_ORIENTATION_BOUNDARY":
            spacedControlBoundary?"REJECT_SEPARATE_CONTROL_OBSERVATIONS":
            strongWhitespace?"REJECT_STRONG_WHITESPACE":columnBreak?"REJECT_COLUMN_BREAK":
            alignmentBoundary?"REJECT_ALIGNMENT_BOUNDARY":paragraphBoundary?"REJECT_PARAGRAPH_GAP":
            structuralTextBoundary?"REJECT_STRUCTURAL_LABEL_BOUNDARY":compactHeaderBoundary?"REJECT_COMPACT_HEADER_BOUNDARY":
            likelyIndependentControls?"REJECT_INDEPENDENT_CONTROLS":heightRatio>2.15f?"REJECT_HEIGHT_RATIO":"REJECT_NO_CONTINUATION";
        var oa=owners[source.SourceId];var ob=owners[target.SourceId];
        return new(source.SourceId,target.SourceId,oa.RegionOwnerId,ob.RegionOwnerId,
            !strongOwnerBoundary,oa.RegionOwnerId==ob.RegionOwnerId,vOverlap,hOverlap,verticalGap,horizontalGap,
            continuation,boundary,admitted,reason);
    }

    private static BlockLayoutBehavior InferLayout(Size canvas, IReadOnlyList<NormalizedOcrLine> lines,
        RectangleF bounds, float medianHeight)
    {
        if (lines.Count >= 3 && bounds.Height >= medianHeight * 2.4f) return BlockLayoutBehavior.Flow;
        if (bounds.Width < canvas.Width * .36f || bounds.Height < medianHeight * 1.7f) return BlockLayoutBehavior.Fixed;
        return BlockLayoutBehavior.Auto;
    }

    private static string AdvisoryRole(Size canvas, IReadOnlyList<NormalizedOcrLine> lines,
        RectangleF bounds, float medianHeight)
    {
        if (lines.Count == 1 && bounds.Height > medianHeight * 1.45f) return "PossibleTitle";
        if (lines.Count >= 3) return "PossibleFlowText";
        if (bounds.Width < canvas.Width * .32f && lines.All(x => IsShortControl(x.SourceText))) return "PossibleControl";
        return "Unknown";
    }

    private static bool IsShortControl(string text) => text.Length <= 28 && !text.Contains('.') && !text.Contains('!');
    private static bool LooksLikeStructuralLabel(string text)
    {
        if (text.Contains('|')) return true;
        var letters = text.Where(char.IsLetter).ToArray();
        return letters.Length >= 3 && letters.Length <= 32 && letters.All(char.IsUpper);
    }
    private static float AxisGap(float a0, float a1, float b0, float b1) => Math.Max(0, Math.Max(a0, b0) - Math.Min(a1, b1));
    private static float HorizontalOverlap(RectangleF a, RectangleF b) => Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) / Math.Max(1, Math.Min(a.Width, b.Width));
    private static float VerticalOverlap(RectangleF a, RectangleF b) => Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top)) / Math.Max(1, Math.Min(a.Height, b.Height));
    private static float Distance(RectangleF a, RectangleF b) => AxisGap(a.Left, a.Right, b.Left, b.Right) + AxisGap(a.Top, a.Bottom, b.Top, b.Bottom);
    private static float Median(IEnumerable<float> values) { var v = values.Order().ToArray(); return v[v.Length / 2]; }
    private static RectangleF Union(IEnumerable<RectangleF> values)
    {
        var a = values.ToArray(); var left = a.Min(x => x.Left); var top = a.Min(x => x.Top);
        return RectangleF.FromLTRB(left, top, a.Max(x => x.Right), a.Max(x => x.Bottom));
    }
    private static string StableId(IEnumerable<string> ids)
    {
        var data = Encoding.UTF8.GetBytes(string.Join("\n", ids.Order(StringComparer.Ordinal)));
        return "BLK-" + Convert.ToHexString(SHA256.HashData(data))[..12];
    }
}

internal sealed class AcceptedTranslationCache
{
    private readonly Dictionary<string, string> accepted = new(StringComparer.Ordinal);
    public bool TryGet(string blockId, out string translation) => accepted.TryGetValue(blockId, out translation!);
    public void Store(BlockTranslation result)
    {
        if (result.State == BlockTranslationState.Accepted && !string.IsNullOrWhiteSpace(result.TranslatedText))
            accepted[result.BlockId] = result.TranslatedText;
    }
}
