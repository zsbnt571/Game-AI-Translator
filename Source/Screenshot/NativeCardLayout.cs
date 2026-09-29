using System.Drawing.Drawing2D;
using System.Globalization;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Source-anchored paragraph and field drawing geometry; never owns cleanup pixels.</summary>
internal static class NativeCardLayout
{
    internal static NativeVisualLayoutPlan Plan(Graphics g, Bitmap source, VisualBlock block,
        IReadOnlyList<VisualBlock> blocks, string text, string family, FontStyle weight,
        float preferred, string alignment, SourceStyleBundle style, bool preserveFontSize = false)
    {
        if (block.Lines.Count == 2)
            return NativeCardCaptionLayout.Plan(g, source, block, blocks, text, family, weight,
                preferred, alignment, style);
        var fields = CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(block);var failure="";
        var kind = fields ? "IndependentFields" : "CardBody";
        NativeVisualLayoutPlan Reject(string why, bool candidate = true) => new(candidate, false, why, kind,
            block.Bounds, block.Bounds, block.Bounds, preferred, preferred, 0, 0, 0, alignment,
            "SourceTop", [], 0, true, true) { LayoutInput = text };
        if (block.Lines.Count < 3 || block.RoleHint is "PossibleTitle" or "PossibleControl" ||
            (!fields && (style.Role != RegionRoleType.BodyParagraph || block.LayoutBehavior != BlockLayoutBehavior.Flow)) ||
            block.Bounds.Width < block.Bounds.Height * (fields ? 1.8f : 1.1f) ||
            style.Confidence < .5f || preferred < 14 || string.IsNullOrWhiteSpace(text))
            return Reject("NOT_SOURCE_CARD_TEXT", false);
        var sourceLines = block.Lines.OrderBy(x => x.Bounds.Top).ToArray();
        var paragraphAlignment = fields ? null : ParagraphAlignmentEvidence.Observe(block,alignment);
        if(paragraphAlignment is not null) alignment=paragraphAlignment.Alignment;
        // Uniform baseline spacing alone does not prove a single paragraph: a
        // short name/author/header can share that spacing with the following prose.
        // Without a typed part contract preserve its accepted hard boundary.
        // Position near the image bottom alone is not evidence of a speaker row:
        // ordinary lower-page paragraphs must keep the same source-top policy.
        var followingWidths = sourceLines.Skip(1).Select(x => x.Bounds.Width).Order().ToArray();
        var followingHeights = sourceLines.Skip(1).Select(x => x.Bounds.Height).Order().ToArray();
        if (!fields && sourceLines[0].Bounds.Width <= followingWidths[followingWidths.Length / 2] * .48f &&
            sourceLines[0].Bounds.Height >= followingHeights[followingHeights.Length / 2] * .8f &&
            sourceLines[0].SourceText.Trim().Length <= 40)
            return Reject("DISTINCT_LEADING_ROW_RETAINS_HARD_BOUNDARY", false);
        var steps = sourceLines.Zip(sourceLines.Skip(1), (a,b) => b.Bounds.Top-a.Bounds.Top).Order().ToArray();
        var medianStep = steps[steps.Length/2];
        if (medianStep < sourceLines.Average(x=>x.Bounds.Height)*.5f)
            return Reject("SAME_BASELINE_GROUP_RETAINS_EXISTING_OWNER");
        if (!fields && steps.Any(x => x > medianStep*1.5f || x < medianStep*.65f))
            return Reject("SOURCE_PARAGRAPH_BOUNDARIES_REQUIRE_EXPLICIT_MAPPING");
        var normalized = text.Replace("\r", "");
        var rows = normalized.Split('\n');
        if (fields && (rows.Length != sourceLines.Length || rows.Any(x => !Field(x))))
            return Reject("FIELD_ROW_MAPPING_NOT_ONE_TO_ONE");
        // A uniform source paragraph has soft visual wraps. Keep explicit blank
        // paragraphs, and never apply this operation to label:value fields.
        var layout = fields || block.PreserveExplicitLineBreaks ? normalized : SoftWraps(normalized);
        var wordBreaks=PreferredWordBreaks.Create(layout);
        var other = blocks.Where(x=>x.BlockId!=block.BlockId).Select(x=>RectangleF.Inflate(x.Bounds,3,3)).ToArray();
        var maxRight = block.Bounds.Right;
        if (fields)
        {
            maxRight = Math.Min(source.Width-2, maxRight + Math.Min(block.Bounds.Width*.3f, preferred*4));
            foreach (var neighbor in other.Where(x=>x.Left>=block.Bounds.Right && x.Top<block.Bounds.Bottom && x.Bottom>block.Bounds.Top))
                maxRight = Math.Min(maxRight,neighbor.Left-3);
        }
        var search=RectangleF.FromLTRB(block.Bounds.Left,block.Bounds.Top,maxRight,block.Bounds.Bottom);
        var surface=new Surface(source,block,style,other);
        // The original paragraph remains the safe area. When the accepted Chinese
        // uses substantially fewer rows, spend some of that unused area on legibility
        // before considering shrink-to-fit. Field baselines and all erase masks stay put.
        var originalPreferred=preferred;
        if(fields)preferred=Math.Max(preferred,Math.Min(30,sourceLines.Select(x=>x.Bounds.Height).Order().ElementAt(sourceLines.Length/2)*.96f));
        if(!fields && !preserveFontSize)
        {
            using var probe=FontManager.CreatePixel(family,preferred,weight);
            var estimatedRows=Math.Max(1,Math.Ceiling(Measure(g,probe,layout.Replace("\n",""))/Math.Max(1,search.Width-4)));
            var occupied=estimatedRows*Math.Max(Ink(probe,"国Ag").Height+2,probe.GetHeight(g));
            var capacityScale=(float)Math.Sqrt(search.Height*.78/Math.Max(1,occupied));
            preferred*=Math.Clamp(capacityScale,1f,1.35f);
        }
        var floor = preserveFontSize?preferred:Math.Max(14,originalPreferred*(fields?.72f:.82f));
        for (var attempt=0;attempt<=(int)Math.Ceiling((preferred-floor)/.5f);attempt++)
        {
            var size=Math.Max(floor,preferred-attempt*.5f);
            using var font=FontManager.CreatePixel(family,size,weight);
            var nominal=Ink(font,"国Ag");
            var step=Math.Max(nominal.Height+2,Math.Min(font.GetHeight(g)*1.04f,medianStep));
            var planned=new List<NativeVisualLine>();var spans=new List<RectangleF>();var inkRects=new List<RectangleF>();
            var pending=Elements(layout);var cursor=0;var failed=false;var y=block.Bounds.Top+1;
            var elementOffsets=PreferredWordBreaks.ElementOffsets(pending);
            if(!fields)
            {
                var maxInset=Math.Min(medianStep*.25f,sourceLines[0].Bounds.Height*.35f);
                // An adjacent heading's OCR/antialias guard can overlap the first
                // prose row by a few pixels. Move ink down within a bounded source
                // inset; never drop its protection or move another owner.
                foreach(var neighbor in other.Where(n=>n.Right>block.Bounds.Left && n.Left<maxRight &&
                    n.Top<block.Bounds.Top && n.Bottom>=block.Bounds.Top && n.Bottom+2<=block.Bounds.Top+maxInset))
                    y=Math.Max(y,(float)Math.Ceiling(neighbor.Bottom)+2);
            }
            while(cursor<pending.Length)
            {
                if(planned.Count>=sourceLines.Length+2){failed=true;break;}
                if(!fields && pending[cursor]=="\n") {y+=step*.6f;cursor++;continue;}
                var index=planned.Count;
                if(fields && index>=sourceLines.Length){failed=true;break;}
                // The union's left edge may belong to a later slanted/indented
                // source row. Anchor prose to its first row, not that union minimum.
                var xAnchor=fields?sourceLines[index].Bounds.Left:
                    paragraphAlignment?.Alignment=="Left"?paragraphAlignment.LeftAt(y):sourceLines[0].Bounds.Left;
                if(fields)
                {
                    y=sourceLines[index].Bounds.Top+1;
                    if(inkRects.Count>0)y=Math.Max(y,inkRects[^1].Bottom+2);
                    if(y-sourceLines[index].Bounds.Top-1>Math.Max(3,medianStep*.3f))
                    {failure="FIELD_BASELINE_ADJUSTMENT_LIMIT";failed=true;break;}
                }
                var center=!fields && alignment.Equals("Center",StringComparison.OrdinalIgnoreCase);
                var target=center?(paragraphAlignment?.CenterAt(y)??block.Bounds.Left+block.Bounds.Width/2):xAnchor;
                var span=surface.Span(xAnchor,maxRight,y,nominal.Height+2,target,center,
                    fields?3:Math.Max(3,Math.Min(16,sourceLines.Select(l=>l.Bounds.Height).Order().ElementAt(sourceLines.Length/2)*.25f)));
                if(span.Width<size*2){failure=$"SPAN_TOO_NARROW;y={y};span={span};pixel={surface.Failure}";failed=true;break;}
                string lineText;
                if(fields) lineText=rows[index];
                else
                {
                    var end=cursor;
                    while(end<pending.Length && pending[end]!="\n" &&
                        Measure(g,font,string.Concat(pending.Skip(cursor).Take(end-cursor+1)))<=span.Width-2)end++;
                    if(end==cursor){failed=true;break;}
                    // Do not split a Latin word/name or start a line with closing punctuation.
                    if(end<pending.Length && pending[end]!="\n")
                    {
                        var cut=end;
                        while(cut>cursor && Latin(pending[cut-1]) && Latin(pending[cut]))cut--;
                        if(cut>cursor)end=cut;
                        if(end>cursor+1 && Closing(pending[end]))end--;
                        // Avoid leaving a single CJK character before punctuation
                        // at the next row start. This uses punctuation structure,
                        // not a dictionary of fixture-specific words.
                        if(end>cursor+1 && end+1<pending.Length && Closing(pending[end+1]) &&
                            Cjk(pending[end]) && Cjk(pending[end-1]))end--;
                        // Prefer a nearby dictionary word boundary, but never
                        // create a half-width row or make a long word unrenderable.
                        var limit=Math.Max(cursor+1,end-Math.Min(4,Math.Max(1,(end-cursor)/4)));
                        for(var candidate=end;candidate>=limit;candidate--)
                            if(wordBreaks.Offsets.Contains(elementOffsets[candidate]) && !Closing(pending[candidate]))
                            {end=candidate;break;}
                    }
                    lineText=string.Concat(pending.Skip(cursor).Take(end-cursor));cursor=end;
                }
                var advance=Measure(g,font,lineText);var ink=Ink(font,lineText);
                if(advance>span.Width-2 || ink.Height<=0){failure=$"LINE_WIDTH;advance={advance};span={span.Width}";failed=true;break;}
                var x=center?target-advance/2:span.Left+1;
                var origin=new PointF(x,y-ink.Top);
                var inkRect=new RectangleF(origin.X+ink.Left,origin.Y+ink.Top,ink.Width,ink.Height);
                if(inkRect.Bottom>block.Bounds.Bottom-1 || (inkRects.Count>0 && inkRects[^1].Bottom+1>inkRect.Top) ||
                    !surface.Safe(RectangleF.Inflate(inkRect,1,1)))
                {failure=$"INK_GEOMETRY_OR_SURFACE;ink={inkRect};previous={(inkRects.Count>0?inkRects[^1]:RectangleF.Empty)};pixel={surface.Failure}";failed=true;break;}
                planned.Add(new(lineText,new RectangleF(origin.X,origin.Y,advance,font.GetHeight(g))) {AdvanceWidth=advance});
                inkRects.Add(inkRect);
                spans.Add(span);
                if(fields){cursor+=Elements(rows[index]).Length;if(index<rows.Length-1)cursor++;}
                else y+=step;
            }
            if(failed || planned.Count==0)continue;
            if(!fields && !block.PreserveExplicitLineBreaks && !layout.Contains("\n\n"))BalanceTail(g,font,planned,spans,block.Bounds,alignment,surface);
            var originalVisible=WithoutWhitespace(layout);
            if(WithoutWhitespace(string.Concat(planned.Select(x=>x.Text)))!=originalVisible)
                return Reject("CARD_CONTENT_PRESERVATION_FAILED");
            var union=planned.Select(x=>x.Bounds).Aggregate(RectangleF.Union);
            return new(true,true,fields?"SOURCE_FIELD_ROWS_WITH_GLYPH_METRICS":"SOURCE_BODY_REFLOW_WITH_TOP_ANCHOR",kind,
                block.Bounds,union,search,preferred,size,step,planned.Max(x=>x.Bounds.Width),union.Height,
                fields?"SourceRowLeft":alignment,"SourceTop",planned,surface.CheckedPixels,true,true)
                {LayoutInput=layout,SafeLineRects=spans,ParagraphAlignment=paragraphAlignment,WordBoundaryEngine=wordBreaks.Engine,
                    BreakPolicy=fields?"HardFieldRows":"UniformSourceSoftWraps_ExplicitBlankParagraphsPreserved"};
        }
        return Reject("NO_READABLE_SOURCE_CARD_FIT;"+failure);
    }

    private static void BalanceTail(Graphics g,Font font,List<NativeVisualLine> lines,List<RectangleF> spans,
        RectangleF sourceBounds,string alignment,Surface surface)
    {
        if(lines.Count<2)return;
        var center=alignment.Equals("Center",StringComparison.OrdinalIgnoreCase);
        var minimumTail=Math.Min(spans[^1].Width*.25f,Measure(g,font,"国国国国国国"));
        if(center ? lines[^1].AdvanceWidth>=lines[^2].AdvanceWidth*.35f :
            lines[^1].AdvanceWidth>=minimumTail)return;
        var first=lines[^2];var last=lines[^1];var parts=Elements(first.Text+last.Text);
        var wordBreaks=PreferredWordBreaks.Create(first.Text+last.Text);var offsets=PreferredWordBreaks.ElementOffsets(parts);
        var firstTop=first.Bounds.Top+Ink(font,first.Text).Top;var lastTop=last.Bounds.Top+Ink(font,last.Text).Top;
        (NativeVisualLine First,NativeVisualLine Last,float Cost)? best=null;
        for(var cut=1;cut<parts.Length;cut++)
        {
            if(Closing(parts[cut]) || (Latin(parts[cut-1])&&Latin(parts[cut])))continue;
            var a=string.Concat(parts.Take(cut));var b=string.Concat(parts.Skip(cut));
            var aw=Measure(g,font,a);var bw=Measure(g,font,b);
            if(aw>spans[^2].Width-2||bw>spans[^1].Width-2)continue;
            // A left paragraph only moves the minimum needed to prevent a tiny
            // orphan tail. Never equalize two ordinary prose rows.
            if(!center && (bw<minimumTail || aw<spans[^2].Width*.65f))continue;
            var ai=Ink(font,a);var bi=Ink(font,b);
            var ax=center?sourceBounds.Left+sourceBounds.Width/2-aw/2:first.Bounds.Left;
            var bx=center?sourceBounds.Left+sourceBounds.Width/2-bw/2:last.Bounds.Left;
            var ar=new RectangleF(ax,firstTop-ai.Top,aw,first.Bounds.Height);
            var br=new RectangleF(bx,lastTop-bi.Top,bw,last.Bounds.Height);
            var aInk=new RectangleF(ax+ai.Left,firstTop,ai.Width,ai.Height);
            var bInk=new RectangleF(bx+bi.Left,lastTop,bi.Width,bi.Height);
            if(aInk.Bottom+1>bInk.Top||bInk.Bottom>sourceBounds.Bottom-1||
                !surface.Safe(RectangleF.Inflate(aInk,1,1))||!surface.Safe(RectangleF.Inflate(bInk,1,1)))continue;
            var cost=(center?Math.Abs(aw-bw):first.AdvanceWidth-aw)+
                (wordBreaks.Offsets.Contains(offsets[cut])?0:spans[^2].Width);
            if(best is null||cost<best.Value.Cost)best=(new(a,ar){AdvanceWidth=aw},new(b,br){AdvanceWidth=bw},cost);
        }
        if(best is {} chosen){lines[^2]=chosen.First;lines[^1]=chosen.Last;}
    }

    private static string SoftWraps(string text)
    {
        var result=new System.Text.StringBuilder();
        for(var i=0;i<text.Length;i++)
        {
            if(text[i]!='\n'){result.Append(text[i]);continue;}
            if((i>0&&text[i-1]=='\n')||(i+1<text.Length&&text[i+1]=='\n')){result.Append('\n');continue;}
            if(i>0&&i+1<text.Length&&char.IsAsciiLetterOrDigit(text[i-1])&&char.IsAsciiLetterOrDigit(text[i+1]))result.Append(' ');
        }
        return result.ToString();
    }
    private static bool Field(string s){var at=s.IndexOfAny([':', '：']);return at>0&&at<=24;}
    private static bool Latin(string s)=>s.Length==1&&char.IsAsciiLetterOrDigit(s[0]);
    private static bool Cjk(string s)=>s.Length==1&&s[0]>='\u3400'&&s[0]<='\u9fff';
    private static bool Closing(string s)=>s.Length==1&&"，。！？：；、,.!?:;)）】》」』".Contains(s[0]);
    private static string WithoutWhitespace(string s)=>string.Concat(s.Where(x=>!char.IsWhiteSpace(x)));
    private static string[] Elements(string s){var a=new List<string>();var e=StringInfo.GetTextElementEnumerator(s);while(e.MoveNext())a.Add(e.GetTextElement());return a.ToArray();}
    private static float Measure(Graphics g,Font font,string s)=>g.MeasureString(s,font,PointF.Empty,StringFormat.GenericTypographic).Width;
    private static RectangleF Ink(Font font,string s){using var path=new GraphicsPath();path.AddString(s,font.FontFamily,(int)font.Style,font.Size,PointF.Empty,StringFormat.GenericTypographic);return path.PointCount==0?RectangleF.Empty:path.GetBounds();}

    internal sealed class Surface(Bitmap source,VisualBlock block,SourceStyleBundle style,RectangleF[] protectedText)
    {
        private readonly Dictionary<int,Color?> rowBackground=[];
        public int CheckedPixels {get;private set;}
        public string Failure {get;private set;}="";
        private Color? Background(int y)
        {
            if(rowBackground.TryGetValue(y,out var known))return known;
            var near=block.Lines.OrderBy(x=>Math.Abs(x.Bounds.Top+x.Bounds.Height/2-y)).First().Bounds;
            var colors=new List<Color>();
            foreach(var sampleY in new[]{y,(int)near.Top,(int)near.Bottom-1}.Distinct())
            for(var x=(int)Math.Ceiling(near.Left);x<Math.Min(source.Width,near.Right);x+=3)
            {
                var c=source.GetPixel(x,Math.Clamp(sampleY,0,source.Height-1));
                if(Distance(c,style.FillColor)>70)colors.Add(c);
            }
            if(colors.Count<8)return rowBackground[y]=null;
            // Dense glyph rows contain many antialiased mixtures even after the
            // solid fill is excluded. Select the background-side quantile, not
            // the median mixture, independently for each channel.
            int M(Func<Color,int> f,int fill){var a=colors.Select(f).Order().ToArray();var q=fill>a[a.Length/2]?.2:.8;return a[(int)((a.Length-1)*q)];}
            var median=Color.FromArgb(M(c=>c.R,style.FillColor.R),M(c=>c.G,style.FillColor.G),M(c=>c.B,style.FillColor.B));
            // Restrict the owner to midtone/light card material. The per-pixel
            // corridor check still rejects illustration and edge departures.
            return rowBackground[y]=TranslationTextColorResolver.Contrast(median,style.FillColor)>=1.2?median:null;
        }
        private bool Pixel(int x,int y)
        {
            CheckedPixels++;
            if(x<0||y<0||x>=source.Width||y>=source.Height||protectedText.Any(r=>r.Contains(x+.5f,y+.5f)))return false;
            var background=Background(y);if(background is null)return false;
            var color=source.GetPixel(x,y);
            if(Distance(color,background.Value)<=85 && MaxChannelDistance(color,background.Value)<=42)return true;
            // OCR boxes can omit the outer 1-2 antialiased source-glyph pixels.
            // Admit only colors on this text/background segment in that bounded halo;
            // this is a layout safety observation, not cleanup/mutation authority.
            return block.Lines.Any(l=>RectangleF.Inflate(l.Bounds,2,2).Contains(x+.5f,y+.5f))&&
                (Distance(color,style.FillColor)<=85 || OnAntialiasSegment(color,background.Value,style.FillColor));
        }
        public bool Safe(RectangleF rectangle)
        {
            for(var y=(int)Math.Floor(rectangle.Top);y<(int)Math.Ceiling(rectangle.Bottom);y++)
            for(var x=(int)Math.Floor(rectangle.Left);x<(int)Math.Ceiling(rectangle.Right);x++)if(!Pixel(x,y)){Failure=$"{x},{y}";return false;}
            return true;
        }
        public RectangleF Span(float left,float right,float top,float height,float target,bool centered,float allowedInset=3)
        {
            var columns=new List<(int Left,int Right)>();var start=-1;
            for(var x=(int)Math.Ceiling(left);x<(int)Math.Floor(right);x++)
            {
                var safe=true;for(var y=(int)Math.Floor(top-1);y<(int)Math.Ceiling(top+height+1);y++)if(!Pixel(x,y))
                {if(Failure.Length==0||x==(int)Math.Ceiling(target))Failure=x>=0&&y>=0&&x<source.Width&&y<source.Height ? $"{x},{y};source={source.GetPixel(x,y)};bg={Background(y)};fill={style.FillColor}" : $"{x},{y};outside-source";safe=false;break;}
                if(safe){if(start<0)start=x;}
                else if(start>=0){columns.Add((start,x));start=-1;}
            }
            if(start>=0)columns.Add((start,(int)Math.Floor(right)));
            var chosen=columns.Where(p=>centered?p.Left<=target&&p.Right>=target:p.Left<=target+allowedInset&&p.Right>target)
                .OrderByDescending(p=>p.Right-p.Left).FirstOrDefault();
            if(chosen.Right<=chosen.Left)return RectangleF.Empty;
            if(centered){var half=Math.Min(target-chosen.Left,chosen.Right-target)-1;return new(target-half,top,Math.Max(0,half*2),height);}
            return new(chosen.Left+1,top,Math.Max(0,chosen.Right-chosen.Left-2),height);
        }
        private static int Distance(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
        private static int MaxChannelDistance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
        private static bool OnAntialiasSegment(Color value,Color background,Color fill)
        {
            var dr=fill.R-background.R;var dg=fill.G-background.G;var db=fill.B-background.B;
            var norm=dr*dr+dg*dg+db*db;if(norm<100)return false;
            var t=((value.R-background.R)*dr+(value.G-background.G)*dg+(value.B-background.B)*db)/(float)norm;
            // The observed fill can be a shaded/antialiased median. A brighter
            // plateau on the same ink-colour ray is still source text inside
            // its OCR halo, not an illustration blocking the body corridor.
            float gamut=float.PositiveInfinity;
            void Limit(int baseValue,int delta){if(delta!=0)gamut=Math.Min(gamut,
                (delta>0?255-baseValue:-baseValue)/(float)delta);}
            Limit(background.R,dr);Limit(background.G,dg);Limit(background.B,db);
            if(t<0||t>gamut+.01f)return false;
            return Math.Abs(value.R-background.R-t*dr)+Math.Abs(value.G-background.G-t*dg)+Math.Abs(value.B-background.B-t*db)<=50;
        }
    }
}
