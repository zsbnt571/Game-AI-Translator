using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Language purpose and placement evidence, independent of editable pixels.</summary>
internal sealed record SourceTextRoleEvidence(string Role,string Alignment,string Reason,RectangleF? Cell=null)
{
    internal static SourceTextRoleEvidence Observe(VisualBlock block,float sourceMedian=0,Size canvas=default)
    {
        if(SourceCurvedText.Observe(block) is not null)
            return new("Heading","Center","SOURCE_CURVED_GLYPH_CORRIDOR_AXIS");
        if(block.SourceCell is {} cell)
        {
            var b=block.Bounds;var h=block.Lines.Min(l=>l.Bounds.Height);
            var left=b.Left-cell.Bounds.Left;var right=cell.Bounds.Right-b.Right;
            var centered=Math.Abs(left-right)<=Math.Max(2,h*.4f);
            return new("Control",centered?"Center":"Left",cell.Proof+";SOURCE_INSET_COMPARISON",cell.Bounds);
        }
        var paragraph=Paragraph(block.Lines,allowUnpunctuated:true);
        if(paragraph is not null)return paragraph;
        // A complete one-line sentence is still prose when it uses large or
        // outlined lettering. Font height alone must not dispatch it as a title.
        if(block.Lines.Count==1 && Regex.Matches(block.SourceText,@"[\p{L}]+(?:['’][\p{L}]+)?").Count>=4 &&
            Regex.IsMatch(block.SourceText,@"[.!?。！？][\"" '”’]*$"))
        {
            var center=canvas.Width>0&&block.Bounds.Width>=canvas.Width*.28f&&
                Math.Abs(block.Bounds.Left+block.Bounds.Width/2-canvas.Width/2)<=canvas.Width*.025f;
            return new("Prose",center?"Center":"Left",center?
                "COMPLETE_SOURCE_SENTENCE_ON_CANVAS_AXIS":"COMPLETE_SOURCE_SENTENCE_RETAINS_START");
        }
        var letters=block.SourceText.Where(char.IsLetter).ToArray();
        var upperCanvasHeading=block.Lines.Count==1&&canvas.Width>0&&canvas.Height>0&&
            block.Bounds.Top<canvas.Height*.2f&&block.Bounds.Width>=canvas.Width*.45f&&
            block.Bounds.Height>=canvas.Height*.03f&&block.Bounds.Width>=block.Bounds.Height*6&&
            Math.Abs(block.Bounds.Left+block.Bounds.Width/2-canvas.Width/2)<=canvas.Width*.025f&&
            letters.Length>=4&&letters.Count(char.IsUpper)>=letters.Length*.8f;
        if(block.RoleHint=="PossibleTitle" || upperCanvasHeading || sourceMedian>0&&block.Lines.Min(l=>l.Bounds.Height)>sourceMedian*1.5f)
        {
            var centered=canvas.Width>0&&block.Bounds.Width>=canvas.Width*.28f&&
                Math.Abs(block.Bounds.Left+block.Bounds.Width/2-canvas.Width/2)<=canvas.Width*.025f;
            return new("Heading",centered?"Center":"Source",centered?
                "SOURCE_HEADING_ON_CANVAS_AXIS":"SOURCE_RELATIVE_LINE_HIERARCHY_NOT_GRAPHIC_EXCLUSION");
        }
        return new(block.RoleHint=="PossibleTitle"?"Heading":"Unknown","Source", "NO_ROLE_OVERRIDE_WITHOUT_SOURCE_STRUCTURE");
    }
    internal static SourceTextRoleEvidence? Paragraph(IReadOnlyList<NormalizedOcrLine> lines,bool allowUnpunctuated=false)
    {
        if(lines.Count<2)return null;
        var rows=lines.OrderBy(l=>l.Bounds.Top).ToArray();
        var h=rows.Select(l=>l.Bounds.Height).Order().ElementAt(rows.Length/2);
        if(rows.Max(l=>l.Bounds.Height)>rows.Min(l=>l.Bounds.Height)*1.4f)return null;
        if(rows.Zip(rows.Skip(1),(a,b)=>(b.Bounds.Top+b.Bounds.Height/2-a.Bounds.Top-a.Bounds.Height/2)/Math.Max(1,h)).Any(g=>g<.65f||g>2.1f))return null;
        var text=string.Join(" ",rows.Select(l=>l.SourceText));
        var words=Regex.Matches(text,@"[\p{L}]+(?:['’][\p{L}]+)?").Count;
        var sentence=Regex.IsMatch(text,@"[.!?。！？][\""'”’]*$");
        var sustainedRows=words>=rows.Length*4 && rows.Max(l=>l.Bounds.Width)>=h*10;
        if(words<4||!sentence&&(!allowUnpunctuated||!sustainedRows))return null;
        // A sentence spans source rows. An independently punctuated label list
        // has no continuation; all-caps and short terminal rows remain valid.
        if(rows.Take(rows.Length-1).All(l=>Regex.IsMatch(l.SourceText,@"[.!?。！？][\""'”’]*$")))return null;
        var lefts=rows.Select(l=>l.Bounds.Left).ToArray();
        var centers=rows.Select(l=>l.Bounds.Left+l.Bounds.Width/2).ToArray();
        var left=lefts.Max()-lefts.Min();var center=centers.Max()-centers.Min();var tolerance=Math.Max(2,h*.22f);
        if(left<=tolerance&&center>tolerance*1.5f)return new("Prose","Left","SOURCE_SENTENCE_CONTINUATION_AND_LEFT_INSETS");
        if(center<=tolerance&&left>tolerance*1.5f)return new("Prose","Center","SOURCE_SENTENCE_CONTINUATION_AND_CENTER_AXIS");
        if(left<=tolerance&&center<=tolerance)return new("Prose","Left","SOURCE_SENTENCE_REGULAR_ROWS_RETAIN_START");
        return null;
    }
}
