using System.Drawing.Drawing2D;
namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static class NativeGlyphRowLayout
{
    internal static NativeVisualLayoutPlan? Plan(Graphics g,VisualBlock block,string text,string family,
        FontStyle weight,float size,string alignment,IReadOnlyDictionary<string,SourceGlyphRowEvidence> evidence)
    {
        if(block.RoleHint=="PossibleTitle" || !block.Lines.Any(l=>evidence.ContainsKey(l.SourceId)))return null;
        var rows=text.Replace("\r","").Split('\n');
        if(rows.Length!=block.Lines.Count || rows.Any(t=>t.Length>40 || string.IsNullOrWhiteSpace(t)))return null;
        var lines=new List<NativeVisualLine>();
        foreach(var pair in rows.Zip(block.Lines))
        {
            evidence.TryGetValue(pair.Second.SourceId,out var proof);
            var source=pair.Second.Bounds;var anchor=proof is null?source:proof.GlyphBounds;
            RectangleF ink=default;float advance=0;var chosen=size;var fits=false;
            for(;chosen>=Math.Max(9,size*.82f);chosen-=.5f)
            {
                using var candidateFont=FontManager.CreatePixel(family,chosen,weight);
                using var path=new GraphicsPath();path.AddString(pair.First,candidateFont.FontFamily,(int)candidateFont.Style,candidateFont.Size,PointF.Empty,StringFormat.GenericTypographic);
                ink=path.GetBounds();advance=g.MeasureString(pair.First,candidateFont,PointF.Empty,StringFormat.GenericTypographic).Width;
                if(advance<=source.Width-2 && ink.Height<=source.Height-2){fits=true;break;}
            }
            if(!fits)return null;
            using var font=FontManager.CreatePixel(family,chosen,weight);
            // Glyph recovery supplies color and safe bounds, not a new alignment
            // owner. Keep the proven source-left column selected by the text plan.
            var x=alignment=="Center"?anchor.Left+(anchor.Width-advance)/2:anchor.Left;
            var top=anchor.Top+(anchor.Height-ink.Height)/2;
            if(x<source.Left || x+advance>source.Right || top<source.Top || top+ink.Height>source.Bottom)return null;
            lines.Add(new(pair.First,new(x,top-ink.Top,advance,font.GetHeight(g))){AdvanceWidth=advance,ProvenFill=proof?.Fill,ProvenFontSize=chosen});
        }
        var union=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
        return new(true,true,"REPEATED_SOURCE_GLYPHS_EXCLUDE_ADJACENT_ICON","IndependentGlyphRows",block.Bounds,
            union,block.Bounds,size,size,lines.Max(l=>l.Bounds.Height),lines.Max(l=>l.Bounds.Width),union.Height,
            alignment,"SourceRow",lines,0,true,true){LayoutInput=text,BreakPolicy="OneAcceptedRowPerSourceRow"};
    }
}
