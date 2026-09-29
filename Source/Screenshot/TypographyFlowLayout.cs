using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Final text-only plans. Shared style targets never share spatial ownership or translation identity.</summary>
internal static class TypographyFlowLayout
{
    internal sealed record Input(VisualBlock Block,string Text,string Family,FontStyle Weight,float Size,
        float Preferred,string Alignment,Color Fill,SourceStyleEvidenceR2 Evidence,NativeVisualLayoutPlan? Previous,
        bool AddedOutline=false,bool SourceStyleAllowed=true,RectangleF? ExistingSafe=null,bool SourcePixelScale=false);
    internal sealed record Decision(string BlockId,string Group,float Target,NativeVisualLayoutPlan? Plan,
        string Reason,RectangleF Safe,SourceTextPanel.Evidence? Panel)
    { public bool SuppressAddedOutline {get;init;} public FontStyle? ProvenWeight {get;init;} public bool RequiredSourceSpace {get;init;}
      public SourceGlyphTypography.Evidence? GlyphStyle {get;init;} public WeightPeerGroup? WeightPeers {get;init;} }
    internal sealed record WeightPeerMember(string BlockId,SourceVisualRoleR2 Role,FontStyle Chosen,
        FontStyle? Observed,IReadOnlyList<SourceGlyphTypography.Line> SourceLines);
    internal sealed record WeightPeerGroup(IReadOnlyList<WeightPeerMember> Members,FontStyle? Target,string Reason);
    internal static IReadOnlyList<Decision> Plan(Graphics g,ReadOnlyBitmapPixelBuffer pixels,IReadOnlyList<Input> inputs,
        IReadOnlyList<VisualBlock>? observedBlocks=null)
    {
        var result=new List<Decision>();
        var originalInputs=inputs;
        var sourceCells=inputs.Where(i=>i.Block.Lines.Count<=2&&i.Block.LayoutBehavior==BlockLayoutBehavior.Fixed&&
            i.Evidence.VisualRole!=SourceVisualRoleR2.ArtisticTitle).ToDictionary(i=>i.Block.BlockId,
                i=>SourceTextPanel.ObserveControl(pixels,i.Block,Color.FromArgb(i.Evidence.BackgroundArgb)));
        var glyphStyles=inputs.Where(i=>i.SourceStyleAllowed).ToDictionary(i=>i.Block.BlockId,
            i=>SourceGlyphTypography.Observe(pixels,i.Block,i.Evidence),StringComparer.Ordinal);
        var weightGroups=new Dictionary<string,WeightPeerGroup>();
        inputs=inputs.Select(i=>
        {
            if(!glyphStyles.TryGetValue(i.Block.BlockId,out var proof))return i;
            var weight=proof.Weight;
            if(i.Evidence.VisualRole==SourceVisualRoleR2.Control || sourceCells.GetValueOrDefault(i.Block.BlockId) is not null)
            {
                // Control peers must agree in observed stems, not in a previous
                // renderer's chosen weight. Keep isolated and genuinely different controls.
                bool Related(Input a,Input b)=>
                    (Math.Abs(a.Block.Bounds.Top-b.Block.Bounds.Top)<Math.Min(a.Evidence.MedianLineHeight,b.Evidence.MedianLineHeight)*.7f||
                     Math.Abs(a.Block.Bounds.Left+a.Block.Bounds.Width/2-b.Block.Bounds.Left-b.Block.Bounds.Width/2)<Math.Min(a.Evidence.MedianLineHeight,b.Evidence.MedianLineHeight)*.5f)&&
                    Math.Abs(a.Block.Bounds.Top-b.Block.Bounds.Top)<Math.Max(a.Block.Bounds.Height,b.Block.Bounds.Height)*8;
                var candidates=originalInputs.Where(p=>p.SourceStyleAllowed&&p.Family==i.Family&&
                    (p.Evidence.VisualRole==i.Evidence.VisualRole||sourceCells.GetValueOrDefault(p.Block.BlockId) is not null)&&
                    ColorDistance(p.Fill,i.Fill)<25&&
                    ColorDistance(Color.FromArgb(p.Evidence.BackgroundArgb),Color.FromArgb(i.Evidence.BackgroundArgb))<25&&
                    glyphStyles.TryGetValue(p.Block.BlockId,out var peer)&&peer.Lines.Count>0&&proof.Lines.Count>0&&
                    Math.Abs(peer.Lines.Average(l=>l.StrokePixels)-proof.Lines.Average(l=>l.StrokePixels))<=
                        Math.Max(.6f,Math.Min(peer.Lines.Average(l=>l.InkHeight),proof.Lines.Average(l=>l.InkHeight))*.025f)&&
                    peer.Lines.Concat(proof.Lines).All(l=>l.BaselineHeight>0)&&
                    peer.Lines.Concat(proof.Lines).Max(l=>l.BaselineHeight)<=peer.Lines.Concat(proof.Lines).Min(l=>l.BaselineHeight)*1.25f).ToArray();
                var connected=new HashSet<Input>{i};
                for(var pass=0;pass<candidates.Length;pass++)
                {
                    var added=candidates.Where(p=>!connected.Contains(p)&&connected.Any(m=>Related(m,p))).ToArray();
                    if(added.Length==0)break;
                    foreach(var peer in added)connected.Add(peer);
                }
                var matches=connected.ToArray();
                if(matches.Length<2)return i;
                var sourceLines=matches.SelectMany(p=>glyphStyles[p.Block.BlockId].Lines).ToArray();
                var capHeights=sourceLines.Select(l=>l.BaselineHeight).Where(v=>v>0).ToArray();
                if(capHeights.Length==sourceLines.Length&&capHeights.Max()<=capHeights.Min()*1.25f)
                {
                    var ratio=sourceLines.Select(l=>l.StrokePixels/l.BaselineHeight).Order().ElementAt(sourceLines.Length/2);
                    // Only geometrically related, equal source stems share a decision.
                    // A source emphasis with different stems remains a separate group.
                    if(ratio>=.115f)weight=FontStyle.Bold;
                    else if(ratio<=.095f)weight=FontStyle.Regular;
                    else
                    {
                        // Ambiguous short words may borrow a non-conflicting source
                        // observation from this same-stem grid, never the chosen font.
                        var observations=matches.Select(p=>glyphStyles[p.Block.BlockId].Weight)
                            .Where(w=>w is not null).Select(w=>w!.Value&FontStyle.Bold).Distinct().ToArray();
                        if(observations.Length==1)weight=observations[0];
                    }
                    if(proof.Weight is {} observed&&(observed&FontStyle.Italic)!=0&&weight is not null)
                        weight|=FontStyle.Italic;
                    weightGroups[i.Block.BlockId]=new(matches.Select(p=>new WeightPeerMember(p.Block.BlockId,
                        p.Evidence.VisualRole,p.Weight,glyphStyles[p.Block.BlockId].Weight,glyphStyles[p.Block.BlockId].Lines)).ToArray(),
                        weight,ratio is >.095f and <.115f?
                            weight is null?"SOURCE_PEER_WEIGHT_UNRESOLVED":"EQUAL_SOURCE_STEMS_NONCONFLICTING_OBSERVED_PEER":
                            "SOURCE_BASELINE_NORMALIZED_STEMS");
                }
            }
            return weight is { } chosen?i with{Weight=chosen}:i;
        }).ToArray();
        var panelCache=new Dictionary<string,SourceTextPanel.Evidence?>();
        SourceTextPanel.Evidence? Panel(Input i)
        {
            if(!panelCache.TryGetValue(i.Block.BlockId,out var known))
                panelCache[i.Block.BlockId]=known=SourceTextPanel.Observe(pixels,i.Block);
            return known;
        }
        var prose=inputs.Where(IsProse).ToArray();
        foreach(var input in prose)
        {
            var column=new HashSet<Input>{input};
            for(var pass=0;pass<prose.Length;pass++)
            {
                var added=prose.Where(other=>!column.Contains(other)&&column.Any(member=>ColumnAdjacent(member,other))).ToArray();
                if(added.Length==0)break;
                foreach(var peer in added)column.Add(peer);
            }
            var members=new HashSet<Input>{input};
            for(var pass=0;pass<prose.Length;pass++)
            {
                var added=prose.Where(other=>!members.Contains(other)&&members.Any(member=>Compatible(member,other))).ToArray();
                if(added.Length==0)break;
                foreach(var peer in added)members.Add(peer);
            }
            foreach(var peer in column.Where(p=>SameSourceStyle(input,p)))members.Add(peer);
            var peers=members.ToArray();
            var group=string.Join("+",peers.Select(p=>p.Block.BlockId).Order(StringComparer.Ordinal));
            var target=peers.Select(p=>p.Size).DefaultIfEmpty(input.Size).Max();
            // Source hierarchy caps grouping; a large peer cannot upscale a smaller source class.
            target=Math.Max(input.Size,Math.Min(target,peers.Min(p=>p.Evidence.MedianLineHeight)*1.25f));
            var panel=Panel(input);
            bool centeredBody=input.Alignment=="Center"&&input.Block.Lines.Count>=3&&
                input.Block.Lines.All(l=>Math.Abs(l.Bounds.Left+l.Bounds.Width/2-
                    (input.Block.Bounds.Left+input.Block.Bounds.Width/2))<input.Evidence.MedianLineHeight*.5f);
            if(centeredBody)target=Math.Max(target,input.Evidence.MedianLineHeight*.90f);
            if(panel is not null && input.Block.Lines.Count>=3)
                target=Math.Max(target,Math.Min(input.Size*1.4f,input.Evidence.MedianLineHeight*.82f));
            var mixed=MixedHeading(input);
            var sourceLeftRows=SourceLeftParagraph(input);
            var reflow=((input.Block.Lines.Count>=3||input.Block.SourceRole?.Role=="Prose"||ParagraphAlignmentEvidence.ObserveShortSentence(input.Block) is not null)&&input.Previous is not {Applied:true}||sourceLeftRows&&input.Alignment!="Left") && !input.Block.PreserveExplicitLineBreaks;
            var newSize=target>input.Size+.3f;
            var changedWeight=originalInputs.First(i=>i.Block.BlockId==input.Block.BlockId).Weight!=input.Weight;
            if(!mixed && !reflow && !newSize && panel is null && !changedWeight && !(sourceLeftRows&&input.Alignment!="Left"))continue;
            var safe=input.Block.Bounds;
            var alignment=sourceLeftRows?"Left":input.Alignment;
            var text=input.Text.Replace("\r","");
            var top=safe.Top;
            // The first short centred row is not the paragraph's left margin.
            var left=alignment=="Left"?SourceLeft(input.Block):safe.Left;
            if(panel is not null)
            {
                var padding=Math.Max(3,target*.5f);
                safe=RectangleF.Inflate(panel.Interior,-padding,-padding);
                left=Math.Max(left,safe.Left);top=Math.Max(top,safe.Top);
            }
            else
            {
                // Preserve the proven body corridor of cards. This excludes art and neighboring controls.
                if(input.Previous is {Applied:true,Kind:"CardBody",SafeLineRects.Count:>0} previous)
                {
                    var l=previous.SafeLineRects.Max(s=>s.Left);var r=previous.SafeLineRects.Min(s=>s.Right);
                    safe=RectangleF.FromLTRB(Math.Max(safe.Left,l),safe.Top,Math.Min(safe.Right,r),safe.Bottom);
                }
                safe=RectangleF.FromLTRB(Math.Max(safe.Left,left),safe.Top,safe.Right-2,safe.Bottom-1);
                if(column.Count>=3 && input.Alignment=="Left")
                {
                    var right=column.Max(c=>c.Block.Bounds.Right);
                    var next=inputs.Where(c=>c.Block.BlockId!=input.Block.BlockId &&
                        c.Block.Bounds.Top>=input.Block.Bounds.Bottom && c.Block.Bounds.Left<right &&
                        c.Block.Bounds.Right>safe.Left).Select(c=>c.Block.Bounds.Top).DefaultIfEmpty(safe.Bottom+target).Min();
                    var expanded=RectangleF.FromLTRB(safe.Left,safe.Top,right,
                        Math.Min(input.Block.Bounds.Bottom+target*.45f,next-target*.35f));
                    if(VerifiedColumnSpace(pixels,input,expanded,inputs))safe=expanded;
                }
            }
            // A failed row mapping never revokes a neighboring container's space.
            // Keep accepted paragraph boundaries without inventing source/target fields.
            foreach(var other in inputs.Where(i=>i.Block.BlockId!=input.Block.BlockId))
            {
                if(!safe.IntersectsWith(RectangleF.Inflate(other.Block.Bounds,target*3,target*3)))continue;
                var obstacle=Panel(other);if(obstacle is null||!safe.IntersectsWith(obstacle.Interior))continue;
                var center=input.Block.Bounds.Left+input.Block.Bounds.Width/2;
                var padding=Math.Max(3,target*.5f);
                if(center>obstacle.Interior.Right)
                    safe=RectangleF.FromLTRB(Math.Max(safe.Left,obstacle.Interior.Right+padding),safe.Top,safe.Right,safe.Bottom);
                else if(center<obstacle.Interior.Left)
                    safe=RectangleF.FromLTRB(safe.Left,safe.Top,Math.Min(safe.Right,obstacle.Interior.Left-padding),safe.Bottom);
            }
            var lines=new List<NativeVisualLine>();
            var spans=new List<RectangleF>();
            if(mixed)
            {
                // Only a reliable one-to-one source/accepted row mapping permits separating a heading.
                var rows=text.Split('\n');var sourceRows=input.Block.Lines.OrderBy(l=>l.Bounds.Top).ToArray();
                var titleSize=Math.Max(input.Size,Math.Min(target*1.3f,sourceRows[0].Bounds.Height*.72f));
                if(!Append(g,input,rows[0],titleSize,sourceRows[0].Bounds,sourceRows[0].Bounds.Top,
                    "Left",lines,spans,out _))
                {result.Add(new(input.Block.BlockId,group,target,null,"HEADING_MAPPING_NO_SAFE_FIT",safe,panel));continue;}
                var body=sourceRows.Skip(1).Select(l=>l.Bounds).Aggregate(RectangleF.Union);
                safe=RectangleF.FromLTRB(body.Left,body.Top,body.Right-2,body.Bottom-1);
                top=body.Top;alignment="Left";
                text=string.Join("\n",rows.Skip(1));
                // Keep the terminal row separate when source typography marks a distinct status.
                var terminal=input.Evidence.LineEvidence.Count==sourceRows.Length &&
                    ColorDistance(Color.FromArgb(input.Evidence.LineEvidence[^1].ForegroundArgb),
                        Color.FromArgb(input.Evidence.LineEvidence[1].ForegroundArgb))>45;
                if(!input.Block.PreserveExplicitLineBreaks)
                    text=terminal?SoftWraps(string.Join("\n",rows.Skip(1).Take(rows.Length-2)))+"\n"+rows[^1]:SoftWraps(text);
            }
            else if(!input.Block.PreserveExplicitLineBreaks && !HasSourceHeading(input))text=SoftWraps(text);
            if(alignment=="Left")safe=RectangleF.FromLTRB(Math.Max(left,safe.Left),safe.Top,safe.Right,safe.Bottom);
            var adopted=Append(g,input,text,target,safe,top,alignment,lines,spans,out var reason);
            if(!adopted && target>input.Size+.3f && lines.Count==0)
            {
                // An exceptional long member retains its own readable size, never shrinks its siblings.
                target=input.Size;
                adopted=Append(g,input,text,target,safe,top,alignment,lines,spans,out reason);
                reason="SHARED_TARGET_CONFLICT_OWN_SIZE;"+reason;
            }
            if(!adopted){result.Add(new(input.Block.BlockId,group,target,null,reason,safe,panel));continue;}
            if(centeredBody&&alignment=="Center")
            {
                // Preserve the observed body centre independently of the name
                // plate. Shorter Chinese may use fewer rows, not a higher anchor.
                var used=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
                float dy=safe.Top+(safe.Height-used.Height)/2-used.Top;
                for(int i=0;i<lines.Count;i++){var b=lines[i].Bounds;b.Offset(0,dy);lines[i]=lines[i] with{Bounds=b};}
                for(int i=0;i<spans.Count;i++){var b=spans[i];b.Offset(0,dy);spans[i]=b;}
            }
            var drawn=string.Concat(lines.Select(l=>l.Text));
            if(WithoutSpace(drawn)!=WithoutSpace(input.Text))
            {result.Add(new(input.Block.BlockId,group,target,null,"CONTENT_IDENTITY_REJECTED",safe,panel));continue;}
            var union=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
            var plan=new NativeVisualLayoutPlan(true,true,"FINAL_SOURCE_FLOW;"+reason,"TypographyFlow",
                input.Block.Bounds,union,safe,input.Preferred,target,target*1.2f,
                union.Width,union.Height,alignment,"SourceTop",lines,0,true,true)
                {LayoutInput=string.Join("\n",lines.Select(l=>l.Text)),SafeLineRects=spans,
                    WordBoundaryEngine="WINDOWS_ICU_WORD_DICTIONARY",BreakPolicy=mixed?
                        "SourceHeadingMapping_ProseSoftWraps":"ProseSoftWraps_ExplicitStructurePreserved"};
            result.Add(new(input.Block.BlockId,group,target,plan,reason,safe,panel));
        }
        // Single-row prose needs source/container asymmetry, not a character-count centering rule.
        foreach(var input in inputs.Where(i=>i.Block.Lines.Count==1 &&
            i.Block.SourceText.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Length>=2 &&
            Regex.IsMatch(i.Block.SourceText.TrimEnd(),@"[.!?]$")))
        {
            // The final column plan already establishes its source-left anchor.
            // A later short-dialogue correction must not restore the old per-block size.
            if(result.Any(d=>d.BlockId==input.Block.BlockId && d.Plan is {Applied:true,Alignment:"Left"}))continue;
            var panel=Panel(input)??SourceTextPanel.ObserveLeftInset(pixels,input.Block);
            if(panel is null)continue;
            var b=input.Block.Bounds;var leftGap=b.Left-panel.Interior.Left;var rightGap=panel.Interior.Right-b.Right;
            if(leftGap<0 || rightGap<leftGap*1.8f || leftGap>panel.Interior.Width*.12f)continue;
            var safe=RectangleF.FromLTRB(Math.Max(b.Left,panel.Interior.Left+input.Size*.5f),b.Top,
                Math.Min(b.Right,panel.Interior.Right-input.Size*.5f),
                Math.Min(b.Bottom+input.Size,panel.Interior.Bottom-input.Size*.5f));
            var lines=new List<NativeVisualLine>();var spans=new List<RectangleF>();
            if(!Append(g,input,SoftWraps(input.Text),input.Size,safe,b.Top,"Left",lines,spans,out var reason))continue;
            var union=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
            var plan=new NativeVisualLayoutPlan(true,true,"SOURCE_PANEL_LEFT_INSET","TypographyFlow",b,union,safe,
                input.Preferred,input.Size,input.Size*1.2f,union.Width,union.Height,"Left","SourceTop",lines,0,true,true)
                {LayoutInput=SoftWraps(input.Text),SafeLineRects=spans,BreakPolicy="SourceProseAnchor"};
            result.Add(new(input.Block.BlockId,input.Block.BlockId,input.Size,plan,reason,safe,panel));
        }
        foreach(var input in prose.Where(i=>i.AddedOutline && !i.Evidence.OutlineDetected &&
            i.Evidence.DirectConfidence>=.75f && i.Evidence.ContrastRatio>=3f && i.Size>=14 &&
            Math.Max(i.Fill.R,Math.Max(i.Fill.G,i.Fill.B))<215))
        {
            var at=result.FindIndex(d=>d.BlockId==input.Block.BlockId);
            if(at>=0)result[at]=result[at] with{SuppressAddedOutline=true};
            else result.Add(new(input.Block.BlockId,input.Block.BlockId,input.Size,null,
                "SOURCE_PLAIN_FILL_NO_ADDED_OUTLINE",input.Block.Bounds,null){SuppressAddedOutline=true});
        }
        result.AddRange(ControlRows(g,pixels,inputs,sourceCells,glyphStyles));
        result.AddRange(TableValues(g,inputs));
        result.AddRange(CardCaptions(g,pixels,inputs));
        result.AddRange(DialogueRows(g,pixels,inputs,glyphStyles));
        result.AddRange(RepeatedMenuRows(g,inputs,glyphStyles));
        result.AddRange(OptionRows(g,inputs));
        foreach(var input in inputs.Where(i=>i.Weight!=originalInputs.First(p=>p.Block.BlockId==i.Block.BlockId).Weight&&
            i.Evidence.VisualRole==SourceVisualRoleR2.Control&&!result.Any(d=>d.BlockId==i.Block.BlockId&&d.Plan?.Applied==true)))
        {
            var safe=input.Previous is {Applied:true} previous?previous.SearchBounds:input.ExistingSafe??input.Block.Bounds;
            var lines=new List<NativeVisualLine>();var spans=new List<RectangleF>();
            if(!Append(g,input,input.Text,input.Size,safe,safe.Top,input.Alignment,lines,spans,out _))continue;
            var shift=(safe.Height-(spans.Max(s=>s.Bottom)-spans.Min(s=>s.Top)))/2;
            lines=lines.Select(l=>l with{Bounds=new RectangleF(l.Bounds.X,l.Bounds.Y+shift,l.Bounds.Width,l.Bounds.Height)}).ToList();
            spans=spans.Select(s=>new RectangleF(s.X,s.Y+shift,s.Width,s.Height)).ToList();
            var union=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
            var plan=new NativeVisualLayoutPlan(true,true,"SOURCE_PEER_WEIGHT_IN_EXISTING_SPACE","TypographySourceWeight",
                input.Block.Bounds,union,safe,input.Preferred,input.Size,input.Size*1.2f,union.Width,union.Height,input.Alignment,"ExistingControl",lines,0,true,true)
                {LayoutInput=input.Text,SafeLineRects=spans,BreakPolicy="CompleteText_ExistingControlSpace"};
            result.Add(new(input.Block.BlockId,input.Block.BlockId,input.Size,plan,plan.Reason,safe,null){ProvenWeight=input.Weight});
        }
        foreach(var input in inputs.Where(i=>i.Block.SourceCell is not null||i.Block.SourceCaptionCorridor is not null||i.Block.SourceControlCorridor is not null))
        {
            var cellPlan=PlanSourceCell(g,input)!;
            if(cellPlan.Applied && input.SourceStyleAllowed && SourceCellGeometry.SmallTextFill(pixels,input.Block) is {} fill)
                cellPlan=cellPlan with {Lines=cellPlan.Lines.Select(l=>l with{ProvenFill=fill}).ToArray(),
                    Reason=cellPlan.Reason+";SOURCE_SMALL_CELL_CONTRAST_FILL"};
            result.Add(new(input.Block.BlockId,input.Block.BlockId,cellPlan.FontSize,cellPlan.Applied?cellPlan:null,
                cellPlan.Reason,cellPlan.SearchBounds,null){RequiredSourceSpace=true,ProvenWeight=input.Weight});
        }
        var final=result.GroupBy(d=>d.BlockId,StringComparer.Ordinal).Select(group=>
        {
            var chosen=group.LastOrDefault(d=>d.RequiredSourceSpace)??group.LastOrDefault(d=>d.Plan?.Applied==true)??group.Last();
            var input=inputs.First(i=>i.Block.BlockId==chosen.BlockId);
            return chosen with{SuppressAddedOutline=group.Any(d=>d.SuppressAddedOutline),
                ProvenWeight=chosen.Plan?.Applied==true?chosen.ProvenWeight??input.Weight:null,
                GlyphStyle=glyphStyles.GetValueOrDefault(chosen.BlockId),WeightPeers=weightGroups.GetValueOrDefault(chosen.BlockId)};
        }).ToList();
        // This last owner also inspects a retained old/native plan. Earlier fit or
        // structure failures cannot revoke a independently proved neighboring frame.
        foreach(var original in inputs)
        {
            var existing=final.LastOrDefault(d=>d.BlockId==original.Block.BlockId);
            var input=existing?.ProvenWeight is {} weight?original with{Weight=weight}:original;
            var prior=existing?.Plan is {Applied:true} selected?selected:input.Previous;
            if(prior?.SourcePixelScale==true)input=input with{SourcePixelScale=true};
            var safe=prior is {Applied:true}?prior.SearchBounds:input.Block.Bounds;
            var initial=safe;var padding=Math.Max(3,input.Evidence.OutlineWidth+2);
            foreach(var other in observedBlocks??inputs.Select(i=>i.Block).ToArray())
            {
                if(other.BlockId==input.Block.BlockId || !safe.IntersectsWith(RectangleF.Inflate(other.Bounds,input.Size*3,input.Size*3)))continue;
                if(!panelCache.TryGetValue(other.BlockId,out var obstacle))panelCache[other.BlockId]=obstacle=SourceTextPanel.Observe(pixels,other);
                if(obstacle is null||!safe.IntersectsWith(obstacle.Interior))continue;
                var center=input.Block.Bounds.Left+input.Block.Bounds.Width/2;
                if(center>obstacle.Interior.Right)
                    safe=RectangleF.FromLTRB(Math.Max(safe.Left,obstacle.Interior.Right+padding),safe.Top,safe.Right,safe.Bottom);
                else if(center<obstacle.Interior.Left)
                    safe=RectangleF.FromLTRB(safe.Left,safe.Top,Math.Min(safe.Right,obstacle.Interior.Left-padding),safe.Bottom);
            }
            if(safe==initial)continue;
            if(prior is {Applied:true}&&prior.Lines.All(l=>safe.Contains(RectangleF.Inflate(l.Bounds,1,1))))continue;
            var target=prior is {Applied:true}?prior.FontSize:input.Size;
            var minimum=Math.Max(12,target*.7f);NativeVisualLayoutPlan? replacement=null;string why="NO_COMPLETE_LAYOUT_INSIDE_SOURCE_SPACE";
            var text=input.Block.PreserveExplicitLineBreaks||HasSourceHeading(input)?input.Text.Replace("\r",""):SoftWraps(input.Text);
            for(var size=target;size>=minimum-.01f;size-=1)
            {
                var lines=new List<NativeVisualLine>();var spans=new List<RectangleF>();
                if(!Append(g,input,text,size,safe,safe.Top,input.Alignment,lines,spans,out why))continue;
                if(WithoutSpace(string.Concat(lines.Select(l=>l.Text)))!=WithoutSpace(input.Text))continue;
                var union=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
                replacement=new(true,true,"SOURCE_FRAME_CONSTRAINT_AFTER_ALL_FALLBACKS","TypographySourceConstraint",
                    input.Block.Bounds,union,safe,input.Preferred,size,size*1.2f,union.Width,union.Height,input.Alignment,"SourceTop",lines,0,true,true)
                    {LayoutInput=string.Join("\n",lines.Select(l=>l.Text)),SafeLineRects=spans,SourcePixelScale=input.SourcePixelScale,
                        BreakPolicy="CompleteAcceptedTextWithinProvedNeighborBoundary"};
                target=size;break;
            }
            final.RemoveAll(d=>d.BlockId==input.Block.BlockId);
            final.Add(new(input.Block.BlockId,input.Block.BlockId,target,replacement,why,safe,null)
                {RequiredSourceSpace=true,ProvenWeight=input.Weight,SuppressAddedOutline=existing?.SuppressAddedOutline??false,
                    GlyphStyle=existing?.GlyphStyle});
        }
        return final;
    }
    private static IEnumerable<Decision> TableValues(Graphics g,IReadOnlyList<Input> inputs)
    {
        var shortBlocks=inputs.Where(i=>i.Block.LayoutBehavior==BlockLayoutBehavior.Fixed && i.Block.Lines.Count<=2 &&
            i.Evidence.VisualRole is not SourceVisualRoleR2.ArtisticTitle && !i.Text.Contains("://",StringComparison.Ordinal)).ToArray();
        foreach(var value in shortBlocks)
        {
            var h=value.Evidence.MedianLineHeight;if(h<=0)continue;
            var rows=new List<(Input Value,Input Label)>();
            foreach(var candidate in shortBlocks.Where(i=>Math.Abs(i.Block.Bounds.Left-value.Block.Bounds.Left)<h*.3f &&
                Math.Abs(i.Block.Bounds.Top-value.Block.Bounds.Top)<h*7 && i.Block.Bounds.Height<=h*2.5f))
            {
                var b=candidate.Block.Bounds;
                var label=shortBlocks.Where(i=>i.Block.Lines.Count==1 && i.Block.Bounds.Right<b.Left-h*.4f &&
                    b.Left-i.Block.Bounds.Right<h*5 && i.Block.SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<=4 &&
                    Math.Min(i.Block.Bounds.Bottom,b.Bottom)-Math.Max(i.Block.Bounds.Top,b.Top)>=h*.45f)
                    .OrderByDescending(i=>i.Block.Bounds.Right).FirstOrDefault();
                if(label is not null)rows.Add((candidate,label));
            }
            if(rows.Count<3 || !rows.Any(x=>x.Value==value) || rows.Select(x=>x.Label.Block.BlockId).Distinct().Count()!=rows.Count ||
                rows.Max(x=>x.Label.Block.Bounds.Left)-rows.Min(x=>x.Label.Block.Bounds.Left)>h*.4f ||
                rows.Max(x=>x.Value.Block.Bounds.Width)-rows.Min(x=>x.Value.Block.Bounds.Width)<h*2)continue;
            var safe=value.Block.Bounds;var lines=new List<NativeVisualLine>();var spans=new List<RectangleF>();
            var target=Math.Max(value.Size,rows.Where(x=>SameSourceStyle(value,x.Value)).Select(x=>x.Value.Size).DefaultIfEmpty(value.Size).Max());
            if(!Append(g,value,value.Block.PreserveExplicitLineBreaks?value.Text:SoftWraps(value.Text),target,safe,safe.Top,"Left",lines,spans,out var reason))
            {yield return new(value.Block.BlockId,value.Block.BlockId,target,null,"TABLE_CELL_"+reason,safe,null);continue;}
            var union=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
            var plan=new NativeVisualLayoutPlan(true,true,"SOURCE_PAIRED_ROWS_VALUE_COLUMN","TypographyTableCell",safe,union,safe,
                value.Preferred,target,target*1.2f,union.Width,union.Height,"Left","OwnSourceRow",lines,0,true,true)
                {LayoutInput=value.Text,SafeLineRects=spans,BreakPolicy="OwnSourceCell_SoftWraps"};
            yield return new(value.Block.BlockId,string.Join("+",rows.Select(x=>x.Value.Block.BlockId).Order()),target,plan,
                "SOURCE_PAIRED_ROWS_VALUE_COLUMN",safe,null);
        }
    }
    private static IEnumerable<Decision> CardCaptions(Graphics g,ReadOnlyBitmapPixelBuffer pixels,IReadOnlyList<Input> inputs)
    {
        var cards=inputs.Where(i=>i.Previous is {Applied:true,Kind:"PaperCardCaptionEffect"} &&
            i.Text.Replace("\r","").Split('\n').Length==i.Block.Lines.Count).ToArray();
        foreach(var card in cards)
        {
            var peers=cards.Where(i=>SameSourceStyle(card,i)&&Math.Abs(i.Block.Bounds.Top-card.Block.Bounds.Top)<card.Evidence.MedianLineHeight).ToArray();
            if(peers.Length<3)continue;
            var target=Math.Max(card.Size,Math.Min(peers.Min(i=>i.Evidence.MedianLineHeight)*.98f,card.Size*1.3f));
            if(target<card.Size+.5f)continue;
            var safe=card.Previous!.SearchBounds;
            if(!VerifiedColumnSpace(pixels,card,safe,inputs))continue;
            var lines=new List<NativeVisualLine>();var spans=new List<RectangleF>();
            if(!Append(g,card,card.Text.Replace("\r",""),target,safe,safe.Top,"Center",lines,spans,out var reason))
            {yield return new(card.Block.BlockId,card.Block.BlockId,target,null,"CARD_CAPTION_"+reason,safe,null);continue;}
            var union=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
            var plan=new NativeVisualLayoutPlan(true,true,"VERIFIED_CAPTION_SHARED_SOURCE_SIZE","TypographyCardCaption",card.Block.Bounds,union,safe,
                card.Preferred,target,target*1.2f,union.Width,union.Height,"Center","SourceTop",lines,0,true,true)
                {LayoutInput=card.Text,SafeLineRects=spans,BreakPolicy="TitleAndEffectRetained"};
            yield return new(card.Block.BlockId,string.Join("+",peers.Select(i=>i.Block.BlockId).Order()),target,plan,
                "VERIFIED_CAPTION_SHARED_SOURCE_SIZE",safe,null);
        }
    }
    internal static NativeVisualLayoutPlan? PlanSourceCell(Graphics g,Input input)
    {
        var bounds=input.Block.SourceControlCorridor??input.Block.SourceCell?.Bounds??input.Block.SourceCaptionCorridor;
        if(bounds is not {} space)return null;
        var safe=RectangleF.Inflate(space,-Math.Min(3,space.Width*.035f),0);
        var alignment=(input.Block.SourceCaptionCorridor is not null||input.Block.SourceControlCorridor is not null)?"Center":input.Block.SourceRole?.Alignment??input.Alignment;
        var h=input.Evidence.MedianLineHeight;
        var preferred=Math.Min(input.Preferred,Math.Max(7,Math.Min(h*.95f,safe.Height*1.1f)));
        var minimum=Math.Min(preferred,Math.Max(7,Math.Min(h*.55f,safe.Height*.8f)));
        var text=input.Block.PreserveExplicitLineBreaks?input.Text:SoftWraps(input.Text.Replace("\r",""));
        for(float size=preferred,attempt=0;size>=minimum-.01f&&attempt<14;size-=.5f,attempt++)
        {
            var lines=new List<NativeVisualLine>();var spans=new List<RectangleF>();
            if(!Append(g,input with{SourcePixelScale=true},text,size,safe,safe.Top,alignment,lines,spans,out _))continue;
            var shift=Math.Max(0,(safe.Height-(spans.Max(s=>s.Bottom)-spans.Min(s=>s.Top)))/2);
            lines=lines.Select(l=>l with{Bounds=new(l.Bounds.X,l.Bounds.Y+shift,l.Bounds.Width,l.Bounds.Height)}).ToList();
            spans=spans.Select(s=>new RectangleF(s.X,s.Y+shift,s.Width,s.Height)).ToList();
            var inkBounds=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
            return new(true,true,input.Block.SourceControlCorridor is not null?"SOURCE_INLINE_CONTROL_COMPLETE_INK_LAYOUT":input.Block.SourceCaptionCorridor is null?"SOURCE_CELL_COMPLETE_INK_LAYOUT":"SOURCE_PARALLEL_CAPTION_COMPLETE_INK_LAYOUT",
                "SourceCell",input.Block.Bounds,inkBounds,safe,input.Preferred,size,
                size*1.2f,inkBounds.Width,inkBounds.Height,alignment,"SourceCellCenter",lines,0,true,true)
                {LayoutInput=text,SafeLineRects=spans,SourcePixelScale=true,BreakPolicy="CompleteAcceptedTextWithinIndependentSourceCell"};
        }
        return new(true,false,"SOURCE_CELL_HAS_NO_COMPLETE_READABLE_LAYOUT","SourceCell",input.Block.Bounds,safe,safe,input.Preferred,minimum,
            0,0,0,alignment,"SourceCell",[],0,true,true){LayoutInput=text,SourcePixelScale=true};
    }
    private static IEnumerable<Decision> ControlRows(Graphics g,ReadOnlyBitmapPixelBuffer pixels,IReadOnlyList<Input> inputs,
        IReadOnlyDictionary<string,SourceTextPanel.Evidence?> sourceCells,IReadOnlyDictionary<string,SourceGlyphTypography.Evidence> glyphStyles)
    {
        var controls=inputs.Where(i=>i.Block.LayoutBehavior==BlockLayoutBehavior.Fixed &&
            i.Block.Lines.Count is 1 or 2 && i.Evidence.DirectConfidence>=.7f &&
            (i.Evidence.VisualRole==SourceVisualRoleR2.Control || sourceCells.GetValueOrDefault(i.Block.BlockId) is not null) &&
            !i.Text.Contains('\\')).ToArray();
        foreach(var input in controls)
        {
            var own=input.Block.Bounds;var h=input.Evidence.MedianLineHeight;
            var peers=controls.Where(p=>p.Block.Lines.Count==input.Block.Lines.Count && SameSourceStyle(input,p) &&
                ColorDistance(Color.FromArgb(p.Evidence.BackgroundArgb),Color.FromArgb(input.Evidence.BackgroundArgb))<32 &&
                (Math.Abs(p.Block.Bounds.Top-own.Top)<h*.6f ||
                 (Math.Abs(p.Block.Bounds.Left+p.Block.Bounds.Width/2-own.Left-own.Width/2)<h*.35f &&
                  Math.Abs(p.Block.Bounds.Top-own.Top)<h*7))).ToArray();
            var cell=sourceCells.GetValueOrDefault(input.Block.BlockId);
            // A tightly enclosed multi-row block can be a header plus a message.
            // Without additional horizontal space, keep its existing row owner;
            // enclosure alone does not authorize joining and centering those rows.
            if(cell is not null && input.Block.Lines.Count>1 && cell.Interior.Width<=own.Width*1.1f)
                continue;
            if(cell is not null)
            {
                var cellSafe=RectangleF.Inflate(cell.Interior,-Math.Max(3,h*.2f),-1);
                if(inputs.Where(i=>i.Block.BlockId!=input.Block.BlockId).Any(i=>i.Block.Lines.Any(l=>l.Bounds.IntersectsWith(cellSafe))))
                    cell=null;
                else
                {
                    var cellLines=new List<NativeVisualLine>();var cellSpans=new List<RectangleF>();
                    var cellTarget=Math.Max(input.Size,Math.Min(input.Preferred,h*.9f));
                    if(peers.Length>1)cellTarget=Math.Max(cellTarget,peers.Max(p=>p.Size));
                    if(glyphStyles.TryGetValue(input.Block.BlockId,out var ownGlyph)&&ownGlyph.Lines.Count>0)
                    {
                        var inkHeight=ownGlyph.Lines.Average(l=>l.InkHeight);
                        var cellPeers=controls.Where(c=>sourceCells.GetValueOrDefault(c.Block.BlockId) is {} otherCell &&
                            Math.Abs(otherCell.Interior.Left-cell.Interior.Left)<h*.5f &&
                            Math.Abs(otherCell.Interior.Width-cell.Interior.Width)<h && ColorDistance(c.Fill,input.Fill)<25&&
                            glyphStyles.TryGetValue(c.Block.BlockId,out var shape)&&shape.Lines.Count>0&&
                            Math.Max(shape.Lines.Average(l=>l.InkHeight),inkHeight)<=Math.Min(shape.Lines.Average(l=>l.InkHeight),inkHeight)*1.4f).ToArray();
                        if(cellPeers.Length>=2)
                            cellTarget=(float)cellPeers.Select(c=>glyphStyles[c.Block.BlockId].Lines.Average(l=>l.InkHeight)).Order().ElementAt(cellPeers.Length/2)*1.12f;
                    }
                    var leftGap=own.Left-cell.Interior.Left;var rightGap=cell.Interior.Right-own.Right;
                    // The observed source edge wins over a previous chosen alignment.
                    // In particular, a right-edge field does not become a centered button.
                    var cellAlignment=Math.Abs(leftGap-rightGap)<Math.Max(h*.7f,cell.Interior.Width*.05f)
                        ?"Center":leftGap<rightGap?"Left":"Right";
                    // Source-side inset is part of the anchor. Wider available space
                    // may grow away from it, never push a value past its source edge.
                    if(cellAlignment=="Left")cellSafe=RectangleF.FromLTRB(Math.Max(cellSafe.Left,own.Left),cellSafe.Top,cellSafe.Right,cellSafe.Bottom);
                    else if(cellAlignment=="Right")cellSafe=RectangleF.FromLTRB(cellSafe.Left,cellSafe.Top,Math.Min(cellSafe.Right,own.Right),cellSafe.Bottom);
                    var text=input.Block.PreserveExplicitLineBreaks?input.Text:SoftWraps(input.Text.Replace("\r",""));
                    if(Append(g,input,text,cellTarget,cellSafe,cellSafe.Top,cellAlignment,cellLines,cellSpans,out var reason))
                    {
                        var inkTop=cellSpans.Min(s=>s.Top);var inkBottom=cellSpans.Max(s=>s.Bottom);
                        var shift=(cellSafe.Height-(inkBottom-inkTop))/2;
                        cellLines=cellLines.Select(l=>l with{Bounds=new RectangleF(l.Bounds.X,l.Bounds.Y+shift,l.Bounds.Width,l.Bounds.Height)}).ToList();
                        cellSpans=cellSpans.Select(s=>new RectangleF(s.X,s.Y+shift,s.Width,s.Height)).ToList();
                        var cellBounds=cellLines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
                        var cellPlan=new NativeVisualLayoutPlan(true,true,"PROVEN_CONTROL_INTERIOR","TypographyControlSpace",own,cellBounds,cellSafe,
                            input.Preferred,cellTarget,cellTarget*1.2f,cellBounds.Width,cellBounds.Height,cellAlignment,"ControlInterior",cellLines,0,true,true)
                            {LayoutInput=text,SafeLineRects=cellSpans,BreakPolicy="CompleteControlText_OwnInterior"};
                        yield return new(input.Block.BlockId,input.Block.BlockId,cellTarget,cellPlan,reason,cellSafe,cell);
                        continue;
                    }
                }
            }
            if(peers.Length<3 || input.Text.Replace("\r","").Split('\n').Length!=input.Block.Lines.Count)continue;
            var group=string.Join("+",peers.Select(p=>p.Block.BlockId).Order(StringComparer.Ordinal));
            var rowText=input.Text.Replace("\r","").Split('\n');
            var sourceRows=input.Block.Lines.OrderBy(l=>l.Bounds.Top).ToArray();
            var lines=new List<NativeVisualLine>();var spans=new List<RectangleF>();
            var sizes=new List<float>();var success=true;
            var sourceLeft=sourceRows.Length==2 && Math.Abs(sourceRows[0].Bounds.Left-sourceRows[1].Bounds.Left)<h*.2f &&
                Math.Abs(sourceRows[0].Bounds.Width-sourceRows[1].Bounds.Width)>h*.5f;
            for(var row=0;row<rowText.Length;row++)
            {
                var minHeight=peers.Min(p=>p.Block.Lines.OrderBy(l=>l.Bounds.Top).ElementAt(row).Bounds.Height);
                var target=Math.Max(input.Size,Math.Max(peers.Max(p=>p.Size),minHeight*.9f));
                var safe=sourceRows[row].Bounds;
                if(inputs.Where(p=>p.Block.BlockId!=input.Block.BlockId).Any(p=>p.Block.Lines.Any(l=>l.Bounds.IntersectsWith(safe))))
                {success=false;break;}
                // Reliable source rows retain their own position and optional hierarchy.
                // A shared style never enlarges a cell or borrows another row's space.
                using var font=FontManager.CreatePixel(input.Family,target,input.Weight);
                var ink=Ink(font,rowText[row]);
                if(g.MeasureString(rowText[row],font,PointF.Empty,StringFormat.GenericTypographic).Width>safe.Width-2 || ink.Height>safe.Height)
                {success=false;break;}
                var align=sourceLeft?"Left":input.Alignment;
                var top=safe.Top+(safe.Height-ink.Height)/2;
                if(!Append(g,input,rowText[row],target,safe,top,align,lines,spans,out _))
                {success=false;break;}
                sizes.Add(target);
            }
            if(!success)
            {
                yield return new(input.Block.BlockId,group,input.Size,null,"CONTROL_SHARED_TARGET_CONFLICT_RETAIN_OWN_COMPLETE_LAYOUT",own,null);
                continue;
            }
            if(sizes.All(size=>Math.Abs(size-input.Size)<.3f)&&!sourceLeft)continue;
            var bounds=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
            var plan=new NativeVisualLayoutPlan(true,true,"SOURCE_CONTROL_ROWS_SHARED_STYLE","TypographyControlRows",own,bounds,own,
                input.Preferred,sizes.Max(),sizes.Max()*1.2f,bounds.Width,bounds.Height,sourceLeft?"Left":input.Alignment,
                "SourceRow",lines,0,true,true){LayoutInput=input.Text,SafeLineRects=spans,BreakPolicy="OneToOneSourceControlRows"};
            yield return new(input.Block.BlockId,group,sizes.Max(),plan,"SOURCE_CONTROL_ROWS_SHARED_STYLE",own,null);
        }
    }
    private static bool IsProse(Input i)
    {
        if(i.Block.SourceRole?.Role=="Prose")return true;
        if(i.Block.SourceRole?.Role=="Control")return false;
        if(SourceOptionOrdinals(i.Block).Length>=2)return false;
        if(ParagraphAlignmentEvidence.ObserveShortSentence(i.Block) is not null)return true;
        // Fixed multi-row structures may contain a speaker, date and message.
        // Their hard row mapping is handled separately, never as prose continuation.
        if(i.Block.LayoutBehavior==BlockLayoutBehavior.Fixed && i.Block.Lines.Count>1)return false;
        // A standalone account handle is a structural header even when OCR
        // groups it with the following paragraph as Flow. Preserve its own row.
        if(i.Block.Lines.Count>1 && Regex.IsMatch(
            i.Block.Lines.OrderBy(l=>l.Bounds.Top).First().SourceText.Trim(),
            @"^[@＠][\p{L}\p{N}_][\p{L}\p{N}_.-]*$"))return false;
        // A heading and wrapped values do not turn a vertical field list into
        // prose. Retain the established row plan when multiple source rows
        // introduce explicit labels; continuations may lack a colon.
        if(i.Block.Lines.Count(l=>Regex.IsMatch(l.SourceText,
            @"^\s*(?:[•●▪-]\s*)?[\p{L}][\p{L}\p{N} _{}()/-]{0,38}\s*[:：](?![/\\])"))>=2)return false;
        if(i.Block.Lines.Count>=2 && !HasSourceHeading(i))
        {
            var rows=i.Block.Lines.OrderBy(l=>l.Bounds.Top).ToArray();
            if(rows[0].Bounds.Width<rows[1].Bounds.Width*.55f &&
                rows[0].SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<=5 &&
                rows[1].SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length>=6 &&
                !Regex.IsMatch(rows[0].SourceText.TrimEnd(),"[.!?。！？]$"))return false;
        }
        if(CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(i.Block) ||
            i.Block.SourceText.Contains("://",StringComparison.Ordinal) ||
            i.Previous?.Kind is "RightFootnote" or "VerticalSourceText" or "SourceGlyphRows" ||
            i.Evidence.VisualRole is SourceVisualRoleR2.ArtisticTitle or SourceVisualRoleR2.Control)return false;
        if(i.Block.LayoutBehavior==BlockLayoutBehavior.Flow && i.Block.Lines.Count>=3)
            return i.Block.Lines.Count(l=>l.SourceText.Count(char.IsLetter)>=4)>=3;
        // A short paragraph can share the source column's body style without
        // being mistaken for a label merely because its OCR occupies one row.
        return i.Block.SourceText.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Length>=3 &&
            Regex.IsMatch(i.Block.SourceText.TrimEnd(),"[.!?。！？][\"'”’]?$") &&
            !i.Block.SourceText.Contains("://",StringComparison.Ordinal);
    }
    private static bool SourceLeftParagraph(Input input)
    {
        if(input.Block.SourceRole?.Role=="Prose")return input.Block.SourceRole.Alignment=="Left";
        if(ParagraphAlignmentEvidence.ObserveShortSentence(input.Block) is not null)return true;
        var rows=input.Block.Lines.OrderBy(l=>l.Bounds.Top).ToArray();
        if(rows.Length<2||HasSourceHeading(input)||CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(input.Block))return false;
        var height=rows.Select(l=>l.Bounds.Height).Order().ElementAt(rows.Length/2);
        return rows.Max(l=>l.Bounds.Left)-rows.Min(l=>l.Bounds.Left)<=height*.25f&&
            rows[^1].Bounds.Width<=rows.Max(l=>l.Bounds.Width)*.7f;
    }
    private static string[] SourceOptionOrdinals(VisualBlock block)=>block.Lines.OrderBy(l=>l.Bounds.Top)
        .Select(l=>Regex.Match(l.SourceText,@"^\s*(\d{1,2})[.)]\s+")).Where(m=>m.Success).Select(m=>m.Groups[1].Value).ToArray();
    private static IEnumerable<Decision> RepeatedMenuRows(Graphics g,IReadOnlyList<Input> inputs,
        IReadOnlyDictionary<string,SourceGlyphTypography.Evidence> glyphStyles)
    {
        var candidates=inputs.Where(i=>i.SourceStyleAllowed&&i.Block.LayoutBehavior==BlockLayoutBehavior.Fixed&&
            i.Block.Lines.Count==1&&i.Evidence.VisualRole is SourceVisualRoleR2.Heading or SourceVisualRoleR2.Control&&
            i.Evidence.DirectConfidence>=.7f&&i.Block.SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<=4&&
            i.Block.SourceText.All(c=>char.IsLetter(c)||char.IsWhiteSpace(c)||c=='\'')&&
            glyphStyles.TryGetValue(i.Block.BlockId,out var shape)&&shape.Lines.Count==1).ToArray();
        foreach(var input in candidates)
        {
            var h=input.Block.Bounds.Height;var own=glyphStyles[input.Block.BlockId].Lines[0];
            var peers=candidates.Where(p=>p.Family==input.Family&&
                Math.Abs(p.Block.Bounds.Left-input.Block.Bounds.Left)<=h*.25f&&
                Math.Max(p.Block.Bounds.Height,h)<=Math.Min(p.Block.Bounds.Height,h)*1.15f&&
                Math.Abs(p.Block.Bounds.Top-input.Block.Bounds.Top)<=h*6&&
                Math.Abs(glyphStyles[p.Block.BlockId].Lines[0].StrokePixels-own.StrokePixels)<=Math.Max(1,own.StrokePixels*.2f))
                .OrderBy(p=>p.Block.Bounds.Top).ToArray();
            if(peers.Length<3)continue;
            var gaps=peers.Zip(peers.Skip(1),(a,b)=>b.Block.Bounds.Top-a.Block.Bounds.Top).ToArray();
            if(gaps.Min()<h*1.1f||gaps.Max()>h*2.8f||gaps.Max()>gaps.Min()*1.35f)continue;
            // Variable source widths with the same left edge prove a left menu.
            // Equal-width or centered rows retain their existing alignment.
            if(peers.Max(p=>p.Block.Bounds.Width)-peers.Min(p=>p.Block.Bounds.Width)<h*.8f)continue;
            var sourceHeight=peers.Select(p=>glyphStyles[p.Block.BlockId].Lines[0].InkHeight).Order().ElementAt(peers.Length/2);
            var target=Math.Max(input.Size,sourceHeight*1.12f);
            if(target<=input.Size+.5f)continue;
            var safe=RectangleF.Inflate(input.Block.Bounds,-2,-2);
            if(inputs.Any(p=>p.Block.BlockId!=input.Block.BlockId&&p.Block.Bounds.IntersectsWith(safe)))continue;
            var lines=new List<NativeVisualLine>();var spans=new List<RectangleF>();
            if(!Append(g,input with{SourcePixelScale=true},input.Text,target,safe,safe.Top,"Left",lines,spans,out _))continue;
            var inkBounds=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
            var plan=new NativeVisualLayoutPlan(true,true,"REPEATED_SOURCE_MENU_INK_HEIGHT","TypographySourceMenu",
                input.Block.Bounds,inkBounds,safe,input.Preferred,target,target*1.2f,inkBounds.Width,inkBounds.Height,
                "Left","SourceRow",lines,0,true,true){LayoutInput=input.Text,SafeLineRects=spans,SourcePixelScale=true,BreakPolicy="CompleteIndependentMenuWithinSourceBounds"};
            yield return new(input.Block.BlockId,string.Join("+",peers.Select(p=>p.Block.BlockId)),target,plan,plan.Reason,safe,null);
        }
    }
    private static IEnumerable<Decision> DialogueRows(Graphics g,ReadOnlyBitmapPixelBuffer pixels,IReadOnlyList<Input> inputs,
        IReadOnlyDictionary<string,SourceGlyphTypography.Evidence> glyphStyles)
    {
        var used=new HashSet<string>();
        foreach(var seed in inputs.Where(i=>i.Block.Lines.Count>=3&&SourceLeftParagraph(i)&&
            Math.Max(Color.FromArgb(i.Evidence.BackgroundArgb).R,Math.Max(Color.FromArgb(i.Evidence.BackgroundArgb).G,
                Color.FromArgb(i.Evidence.BackgroundArgb).B))<80))
        {
            var panel=SourceTextPanel.Observe(pixels,seed.Block);
            if(panel is null||panel.Coverage<.9f)continue;
            var h=seed.Evidence.MedianLineHeight;var sourceLeft=seed.Block.Lines.Min(l=>l.Bounds.Left);
            foreach(var input in inputs.Where(i=>i.Block.Lines.Count==1&&i.SourceStyleAllowed&&
                !CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(i.Block)&&
                Math.Abs(i.Block.Bounds.Left-sourceLeft)<h*.3f&&
                Math.Abs(i.Evidence.MedianLineHeight-h)<h*.25f&&ColorDistance(i.Fill,seed.Fill)<25&&
                panel.Interior.Contains(new PointF(i.Block.Bounds.Left+i.Block.Bounds.Width/2,i.Block.Bounds.Top+i.Block.Bounds.Height/2))))
            {
                if(!used.Add(input.Block.BlockId))continue;
                var bottom=inputs.Where(i=>i.Block.BlockId!=input.Block.BlockId&&i.Block.Bounds.Top>input.Block.Bounds.Top&&
                    i.Block.Bounds.Right>panel.Interior.Left&&i.Block.Bounds.Left<panel.Interior.Right)
                    .Select(i=>i.Block.Bounds.Top-2).DefaultIfEmpty(panel.Interior.Bottom-3).Min();
                var safe=RectangleF.FromLTRB(panel.Interior.Left+Math.Max(4,h*.34f),input.Block.Bounds.Top,
                    panel.Interior.Right-Math.Max(4,h*.34f),Math.Min(bottom,panel.Interior.Bottom-3));
                var weight=input.Weight;
                if(glyphStyles.TryGetValue(input.Block.BlockId,out var own)&&glyphStyles.TryGetValue(seed.Block.BlockId,out var parent)&&
                    own.Lines.Count>0&&parent.Lines.Count>0&&parent.Weight is {} sourceWeight&&
                    Math.Abs(own.Lines.Average(l=>l.StrokePixels)-parent.Lines.Average(l=>l.StrokePixels))<=1)
                    weight=sourceWeight;
                var adjusted=input with{Weight=weight};var target=Math.Max(input.Size,seed.Size);
                var lines=new List<NativeVisualLine>();var spans=new List<RectangleF>();
                if(!Append(g,adjusted,input.Text,target,safe,safe.Top,"Left",lines,spans,out _))continue;
                var union=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
                var plan=new NativeVisualLayoutPlan(true,true,"SOURCE_DIALOGUE_SIBLING_LEFT_INSET","TypographyDialogueRow",
                    input.Block.Bounds,union,safe,input.Preferred,target,target*1.2f,union.Width,union.Height,"Left","SourceRow",lines,0,true,true)
                    {LayoutInput=input.Text,SafeLineRects=spans,BreakPolicy="IndependentAcceptedUnitInsideSourceDialogue"};
                yield return new(input.Block.BlockId,seed.Block.BlockId,target,plan,plan.Reason,safe,panel){ProvenWeight=weight};
            }
        }
    }
    private static IEnumerable<Decision> OptionRows(Graphics g,IReadOnlyList<Input> inputs)
    {
        foreach(var input in inputs.Where(i=>SourceOptionOrdinals(i.Block).Length>=2))
        {
            var rows=input.Block.Lines.OrderBy(l=>l.Bounds.Top).ToArray();var text=input.Text.Replace("\r","").Split('\n');
            if(rows.Length!=text.Length)continue;
            if(rows.Where((l,index)=>Regex.Match(l.SourceText,@"^\s*\d{1,2}[.)]").Value.Trim()!=
                Regex.Match(text[index],@"^\s*\d{1,2}[.)]").Value.Trim()).Any())continue;
            var target=input.Size;var lines=new List<NativeVisualLine>();var spans=new List<RectangleF>();var ok=true;
            for(var i=0;i<rows.Length;i++)
            {
                var b=rows[i].Bounds;var bottom=i+1<rows.Length?rows[i+1].Bounds.Top-1:input.Block.Bounds.Bottom+2;
                var safe=RectangleF.FromLTRB(b.Left,b.Top,input.Block.Bounds.Right-1,bottom);
                var before=lines.Count;
                if(!Append(g,input,text[i],target,safe,b.Top,"Left",lines,spans,out _)){ok=false;break;}
                if(input.Evidence.LineEvidence.Count==rows.Length&&input.Evidence.LineEvidence[i].Confidence>=.5f)
                    for(var at=before;at<lines.Count;at++)lines[at]=lines[at] with{ProvenFill=Color.FromArgb(input.Evidence.LineEvidence[i].ForegroundArgb)};
            }
            if(!ok||WithoutSpace(string.Concat(lines.Select(l=>l.Text)))!=WithoutSpace(input.Text))continue;
            var union=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
            var plan=new NativeVisualLayoutPlan(true,true,"SOURCE_ENUMERATED_OPTIONS_RETAIN_OWN_ROWS","TypographyOptionRows",
                input.Block.Bounds,union,input.Block.Bounds,input.Preferred,target,target*1.2f,union.Width,union.Height,"Left","OwnSourceOption",lines,0,true,true)
                {LayoutInput=input.Text,SafeLineRects=spans,BreakPolicy="VerifiedSourceOrdinalAndAcceptedRowMapping"};
            yield return new(input.Block.BlockId,input.Block.BlockId,target,plan,plan.Reason,input.Block.Bounds,null);
        }
    }
    private static bool MixedHeading(Input i)
        => HasSourceHeading(i) && i.Text.Replace("\r","").Split('\n').Length==i.Block.Lines.Count;
    private static bool HasSourceHeading(Input i)
    {
        var rows=i.Block.Lines.OrderBy(l=>l.Bounds.Top).ToArray();
        if(rows.Length<3)return false;
        var median=rows.Skip(1).Select(l=>l.Bounds.Height).Order().ElementAt((rows.Length-1)/2);
        return rows[0].Bounds.Height>median*1.45f && rows[0].SourceText.Count(char.IsLetter)>=2 &&
            rows[0].SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<=4;
    }
    private static bool SameSourceStyle(Input a,Input b)
    {
        var h=Math.Min(a.Evidence.MedianLineHeight,b.Evidence.MedianLineHeight);
        return h>0 && a.Family==b.Family && a.Weight==b.Weight &&
            Math.Max(a.Evidence.MedianLineHeight,b.Evidence.MedianLineHeight)<=h*1.18f &&
            ColorDistance(Color.FromArgb(a.Evidence.DirectForegroundArgb),Color.FromArgb(b.Evidence.DirectForegroundArgb))<=24 &&
            ColorDistance(a.Fill,b.Fill)<=30;
    }
    private static bool ColumnAdjacent(Input a,Input b)
    {
        var h=Math.Min(a.Evidence.MedianLineHeight,b.Evidence.MedianLineHeight);
        return h>0 && Math.Abs(a.Block.Bounds.Left-b.Block.Bounds.Left)<=h*.5f &&
            Math.Max(a.Block.Bounds.Top,b.Block.Bounds.Top)-Math.Min(a.Block.Bounds.Bottom,b.Block.Bounds.Bottom)<h*5 &&
            ColorDistance(Color.FromArgb(a.Evidence.BackgroundArgb),Color.FromArgb(b.Evidence.BackgroundArgb))<24 &&
            Math.Max(a.Evidence.MedianLineHeight,b.Evidence.MedianLineHeight)<=h*1.25f;
    }
    private static bool VerifiedColumnSpace(ReadOnlyBitmapPixelBuffer p,Input i,RectangleF rect,IReadOnlyList<Input> inputs)
    {
        if(i.Evidence.BackgroundConfidence<.55f||rect.Width<=0||rect.Height<=0)return false;
        var bg=Color.FromArgb(i.Evidence.BackgroundArgb);var good=0;var count=0;
        foreach(var other in inputs.Where(x=>x.Block.BlockId!=i.Block.BlockId))
            if(other.Block.Lines.Any(l=>l.Bounds.IntersectsWith(rect)))return false;
        var step=Math.Max(3,(int)(i.Evidence.MedianLineHeight*.18f));
        for(var y=(int)rect.Top;y<rect.Bottom;y+=step)for(var x=(int)rect.Left;x<rect.Right;x+=step)
        {
            if(x<0||y<0||x>=p.Width||y>=p.Height)return false;
            if(i.Block.Lines.Any(l=>RectangleF.Inflate(l.Bounds,2,2).Contains(x,y)))continue;
            count++;if(ColorDistance(p.GetPixel(x,y),bg)<40)good++;
        }
        return count>=12&&good>=count*.96f;
    }
    private static bool Compatible(Input a,Input b)
    {
        if(a.Block.BlockId==b.Block.BlockId)return true;
        if(a.Family!=b.Family || a.Weight!=b.Weight || ColorDistance(a.Fill,b.Fill)>30)return false;
        var h=Math.Min(a.Evidence.MedianLineHeight,b.Evidence.MedianLineHeight);
        if(h<=0)return false;
        var x=BodyBounds(a);var y=BodyBounds(b);
        var sameColumn=Math.Abs(x.Left-y.Left)<h*.5f &&
            Math.Max(x.Top,y.Top)-Math.Min(x.Bottom,y.Bottom)<h*5 &&
            Math.Max(a.Evidence.MedianLineHeight,b.Evidence.MedianLineHeight)<=h*1.18f;
        var repeatedRow=Math.Abs(x.Top-y.Top)<h*.7f && Math.Abs(x.Width-y.Width)<Math.Max(x.Width,y.Width)*.12f &&
            Math.Max(x.Left,y.Left)-Math.Min(x.Right,y.Right)<Math.Min(x.Width,y.Width)*.65f &&
            Math.Max(a.Evidence.MedianLineHeight,b.Evidence.MedianLineHeight)<=h*1.25f;
        return sameColumn||repeatedRow;
    }
    private static RectangleF BodyBounds(Input i)=>HasSourceHeading(i)?
        i.Block.Lines.OrderBy(l=>l.Bounds.Top).Skip(1).Select(l=>l.Bounds).Aggregate(RectangleF.Union):i.Block.Bounds;
    private static float SourceLeft(VisualBlock b)=>b.Lines.OrderBy(l=>l.Bounds.Top).First().Bounds.Left;
    private static bool Append(Graphics g,Input input,string text,float size,RectangleF safe,float top,string align,
        List<NativeVisualLine> lines,List<RectangleF> spans,out string reason)
    {
        reason="SAFE_COMPLETE_REFLOW";
        using var font=FontManager.CreatePixel(input.Family,size,input.Weight,input.SourcePixelScale);
        var pending=new List<NativeVisualLine>();var pendingSpans=new List<RectangleF>();
        var step=Math.Max(size*1.2f,Ink(font,"国Ag").Height+size*.18f);
        foreach(var paragraph in text.Split('\n'))
        {
            if(paragraph.Length==0){top+=step*.55f;continue;}
            var elements=StringInfo.ParseCombiningCharacters(paragraph).Append(paragraph.Length).ToArray();
            var breaks=PreferredWordBreaks.Create(paragraph);var at=0;
            while(at<elements.Length-1)
            {
                var end=at+1;var best=at;
                for(;end<elements.Length;end++)
                {
                    var part=paragraph[elements[at]..elements[end]];
                    if(g.MeasureString(part,font,PointF.Empty,StringFormat.GenericTypographic).Width>safe.Width-2)break;
                    best=end;
                }
                if(best==at){reason="SAFE_WIDTH_TOO_SMALL";return false;}
                if(best<elements.Length-1)
                {
                    var preferred=Enumerable.Range(at+1,best-at).Where(e=>breaks.Offsets.Contains(elements[e])).LastOrDefault();
                    if(preferred>at && preferred>=best-5)best=preferred;
                    while(best>at+1 && "，。！？；：、,.!?:;)]）】》」』”’…".Contains(paragraph[elements[best]]))best--;
                    // Avoid a final row containing only one letter/ideograph and punctuation.
                    while(best>at+1 && paragraph[elements[best]..].Count(char.IsLetterOrDigit)<2)best--;
                    // A changed source-glyph style must not turn a whole Latin
                    // word into a split suffix. Use the same cohesion boundary
                    // as the existing native layout, including after tail fixes.
                    best=KeepLatinWord(paragraph,elements,at,best);
                    if(best<=at){reason="SAFE_LATIN_WORD_EXCEEDS_WIDTH";return false;}
                }
                var line=paragraph[elements[at]..elements[best]];var ink=Ink(font,line);
                var advance=g.MeasureString(line,font,PointF.Empty,StringFormat.GenericTypographic).Width;
                var left=align=="Center"?safe.Left+(safe.Width-advance)/2:
                    align=="Right"?safe.Right-advance:safe.Left;
                if(top+ink.Height>safe.Bottom+.1f){reason="SAFE_HEIGHT_CONFLICT";return false;}
                var bounds=new RectangleF(left,top-ink.Top,advance,font.GetHeight(g));
                pending.Add(new(line,bounds){ProvenFontSize=size,AdvanceWidth=advance});
                pendingSpans.Add(new(safe.Left,top,safe.Width,ink.Height));top+=step;at=best;
            }
        }
        lines.AddRange(pending);spans.AddRange(pendingSpans);return pending.Count>0;
    }
    private static RectangleF Ink(Font font,string text)
    {using var path=new GraphicsPath();path.AddString(text,font.FontFamily,(int)font.Style,font.Size,PointF.Empty,StringFormat.GenericTypographic);return path.GetBounds();}
    private static int ColorDistance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
    private static string WithoutSpace(string text)=>string.Concat(text.Where(c=>!char.IsWhiteSpace(c)));
    internal static int KeepLatinWord(string text,int[] offsets,int start,int cut)
    {
        while(cut>start && cut<offsets.Length-1 && char.IsAsciiLetterOrDigit(text[offsets[cut]-1]) && char.IsAsciiLetterOrDigit(text[offsets[cut]]))cut--;
        return cut;
    }
    private static string SoftWraps(string text)
    {
        var rows=text.Split('\n');var result=new System.Text.StringBuilder();
        bool Structural(string row)=>row.Contains("://",StringComparison.Ordinal)||row.TrimStart().StartsWith('#')||
            Regex.IsMatch(row.Trim(),@"^[\p{L}\p{N}_]+(?:[-_][\p{L}\p{N}_]+)+$");
        for(var n=0;n<rows.Length;n++)
        {
            result.Append(rows[n]);if(n+1==rows.Length)break;
            if(rows[n].Length==0||rows[n+1].Length==0||Structural(rows[n])||Structural(rows[n+1]))result.Append('\n');
            else if(char.IsAsciiLetterOrDigit(rows[n][^1])&&char.IsAsciiLetterOrDigit(rows[n+1][0]))result.Append(' ');
        }
        return result.ToString();
    }
}
