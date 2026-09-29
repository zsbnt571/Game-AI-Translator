namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Drawing-only center evidence from source peers; never joins their text or cleanup.</summary>
internal static class SourceCenteredAxis
{
    internal static IReadOnlyDictionary<string,UiLabelAlignmentEvidence> Observe(
        IReadOnlyList<VisualBlock> blocks,IReadOnlyDictionary<string,SourceStyleEvidenceR2> styles)
    {
        var result=new Dictionary<string,UiLabelAlignmentEvidence>(StringComparer.Ordinal);
        var candidates=blocks.Where(b=>b.Lines.Count==1 && b.Bounds.Height>=8 &&
            b.LayoutBehavior!=BlockLayoutBehavior.Flow && b.SourceRole?.Role is not ("Prose" or "GraphicIdentifier") &&
            b.SourceCell is null && b.SourceText.Length<=100 && b.SourceText.Any(char.IsLetter) &&
            b.Bounds.Width/b.Bounds.Height is >=1.25f and <=24f &&
            SourceOrientedText.Observe(b) is null && styles.ContainsKey(b.BlockId))
            .OrderByDescending(b=>b.Bounds.Height).Take(128).ToArray();
        static float Center(VisualBlock b)=>b.Bounds.Left+b.Bounds.Width/2;
        static int ColorDistance(int x,int y)
        {
            var a=Color.FromArgb(x);var b=Color.FromArgb(y);
            return Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
        }
        foreach(var block in candidates)
        {
            var b=block.Bounds;var h=b.Height;var tolerance=Math.Max(2,h*.22f);var style=styles[block.BlockId];
            var peers=candidates.Where(p=>p.Bounds.Height/h is >=.8f and <=1.25f &&
                Math.Abs(Center(p)-Center(block))<=tolerance &&
                (p.BlockId==block.BlockId || Math.Abs(p.Bounds.Top-b.Top)/h is >=1.1f and <=12f) &&
                ColorDistance(styles[p.BlockId].DirectForegroundArgb,style.DirectForegroundArgb)<=40 &&
                ColorDistance(styles[p.BlockId].BackgroundArgb,style.BackgroundArgb)<=30)
                .OrderBy(p=>p.Bounds.Top).ToArray();
            if(peers.Length>=3)
            {
                var left=peers.Max(p=>p.Bounds.Left)-peers.Min(p=>p.Bounds.Left);
                var centers=peers.Max(Center)-peers.Min(Center);
                var widths=peers.Max(p=>p.Bounds.Width)-peers.Min(p=>p.Bounds.Width);
                // Varying source widths distinguish a centered bank from identical
                // left-aligned fields. Every label keeps its own source center.
                if(centers<=tolerance && left>tolerance*2.5f && widths>h &&
                    peers.Zip(peers.Skip(1),(a,z)=>z.Bounds.Top-a.Bounds.Bottom).All(g=>g>=h*.2f))
                    result[block.BlockId]=new("Center","REPEATED_SOURCE_LABEL_CENTER_AXIS",b.Left,
                        peers.Select(p=>p.BlockId).ToArray(),left,centers,tolerance);
            }
            if(result.ContainsKey(block.BlockId))continue;
            // A short isolated page title may occupy much less width than its
            // paragraphs. Two independent broad prose blocks establish the page
            // axis without assuming that every short heading is centered.
            var prose=blocks.Where(p=>p.SourceRole?.Role=="Prose" && p.Lines.Count>=2 &&
                p.Lines.Max(l=>l.Bounds.Height)<=h*1.2f &&
                p.Bounds.Top>b.Bottom+h && p.Bounds.Width>=b.Width*2.5f &&
                Math.Abs(Center(p)-Center(block))<=Math.Max(tolerance,p.Bounds.Width*.018f) &&
                p.Bounds.Top-b.Bottom<=p.Bounds.Width*1.2f).OrderBy(p=>p.Bounds.Top).Take(8).ToArray();
            if(prose.Length>=2 && prose.Zip(prose.Skip(1),(a,z)=>a.Bounds.Bottom<=z.Bounds.Top).All(x=>x))
                result[block.BlockId]=new("Center","SHORT_HEADING_ON_INDEPENDENT_PROSE_AXIS",b.Left,
                    prose.Select(p=>p.BlockId).ToArray(),0,prose.Max(Center)-prose.Min(Center),tolerance);
        }
        foreach(var block in candidates.Where(c=>!result.ContainsKey(c.BlockId)).ToArray())
        {
            var b=block.Bounds;var h=b.Height;var style=styles[block.BlockId];
            // A display heading can share the independently established axis
            // of a smaller control bank. Its original ink must already sit on
            // that axis; a heading placed to one side is left alone.
            var controls=result.Where(p=>p.Value.Reason=="REPEATED_SOURCE_LABEL_CENTER_AXIS")
                .Select(p=>candidates.First(c=>c.BlockId==p.Key)).Where(p=>p.Bounds.Top>b.Bottom&&
                    p.Bounds.Height<h*.6f&&Math.Abs(Center(p)-Center(block))<=h*.15f&&
                    ColorDistance(styles[p.BlockId].DirectForegroundArgb,style.DirectForegroundArgb)<=60)
                .ToArray();
            if(controls.Length>=3)
                result[block.BlockId]=new("Center","DISPLAY_HEADING_ON_EXISTING_CONTROL_AXIS",b.Left,
                    controls.Select(p=>p.BlockId).ToArray(),0,controls.Max(Center)-controls.Min(Center),h*.15f);
        }
        return result;
    }
}
