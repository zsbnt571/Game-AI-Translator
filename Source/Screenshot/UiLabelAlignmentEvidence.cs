namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Read-only presentation evidence from repeated source label rows.
/// Neither role names nor a shared font make a control centered.</summary>
internal sealed record UiLabelAlignmentEvidence(string Alignment,string Reason,float SourceLeft,
    IReadOnlyList<string> PeerBlockIds,float LeftSpread,float CenterSpread,float Tolerance)
{
    internal static IReadOnlyDictionary<string,UiLabelAlignmentEvidence> Analyze(
        IReadOnlyList<VisualBlock> blocks,IReadOnlyDictionary<string,SourceStyleEvidenceR2> styles)
    {
        var result=new Dictionary<string,UiLabelAlignmentEvidence>(StringComparer.Ordinal);
        bool Label(VisualBlock b)=>b.Lines.Count==1 && b.RoleHint!="PossibleTitle" &&
            b.LayoutBehavior!=BlockLayoutBehavior.Flow && b.Bounds.Height>0 &&
            b.Bounds.Width>=b.Bounds.Height*1.3f && b.Bounds.Width<=b.Bounds.Height*30 &&
            b.SourceText.Length<=100 && b.SourceText.Any(char.IsLetter) &&
            !CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(b) &&
            styles.TryGetValue(b.BlockId,out var s) &&
            s.VisualRole is not (SourceVisualRoleR2.ArtisticTitle or SourceVisualRoleR2.Heading);
        var labels=blocks.Where(Label).ToArray();
        static int Distance(int x,int y)
        {
            var a=Color.FromArgb(x);var b=Color.FromArgb(y);
            return Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
        }
        foreach(var label in labels)
        {
            var b=label.Bounds;var own=styles[label.BlockId];
            var tolerance=Math.Max(2,b.Height*.3f);
            var column=labels.Where(peer=>
            {
                var p=peer.Bounds;var gap=Math.Abs(p.Top-b.Top);
                return p.Height>=b.Height*.65f && p.Height<=b.Height*1.5f &&
                    Math.Abs(p.Left-b.Left)<=tolerance &&
                    (peer.BlockId==label.BlockId || gap>=Math.Max(p.Height,b.Height)*1.2f && gap<=b.Height*14);
            }).ToArray();
            // A coherent paper gradient may change more over the full list than
            // between adjacent rows. Join compatible neighboring observations,
            // while retaining the same bounded geometric column.
            var connected=new HashSet<string>(StringComparer.Ordinal){label.BlockId};
            var pending=new Queue<VisualBlock>();pending.Enqueue(label);
            while(pending.Count>0)
            {
                var current=pending.Dequeue();var currentStyle=styles[current.BlockId];
                foreach(var peer in column)
                {
                    if(connected.Contains(peer.BlockId))continue;
                    var s=styles[peer.BlockId];
                    if(Distance(currentStyle.BackgroundArgb,s.BackgroundArgb)>28 ||
                        Distance(currentStyle.DirectForegroundArgb,s.DirectForegroundArgb)>55)continue;
                    connected.Add(peer.BlockId);pending.Enqueue(peer);
                }
            }
            var peers=column.Where(p=>connected.Contains(p.BlockId)).ToArray();
            if(peers.Length<2)continue;
            var leftSpread=peers.Max(p=>p.Bounds.Left)-peers.Min(p=>p.Bounds.Left);
            var centerSpread=peers.Max(p=>p.Bounds.Left+p.Bounds.Width/2)-peers.Min(p=>p.Bounds.Left+p.Bounds.Width/2);
            var widthSpread=peers.Max(p=>p.Bounds.Width)-peers.Min(p=>p.Bounds.Width);
            // Equal-width rows cannot distinguish a left list from centered
            // buttons. Require materially different text widths and stable lefts.
            if(leftSpread>tolerance || widthSpread<Math.Max(b.Height*.8f,peers.Max(p=>p.Bounds.Width)*.1f) ||
                centerSpread<=Math.Max(tolerance*2,leftSpread*2.5f))continue;
            result[label.BlockId]=new("Left","REPEATED_SOURCE_LABEL_LEFT_EDGES",b.Left,
                peers.Select(p=>p.BlockId).Order(StringComparer.Ordinal).ToArray(),leftSpread,centerSpread,tolerance);
        }
        return result;
    }
}
