using System.Security.Cryptography;
using System.Text;
namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Independent nearby caption columns, established by repeated source axes and parallel rows.</summary>
internal static class SourceParallelColumns
{
    internal static void AssignLayoutCorridors(Size canvas,IReadOnlyList<VisualBlock> blocks,
        IReadOnlyList<RegionOwnerEvidence> owners)
    {
        var bySource=owners.ToDictionary(o=>o.SourceId);
        var captions=blocks.Where(b=>b.SourceCell is null&&b.Lines.Count is >=2 and <=5&&
            b.Lines.All(l=>bySource[l.SourceId].RoleHint=="ParallelCaptionColumn")&&
            b.Lines.Select(l=>bySource[l.SourceId].RegionOwnerId).Distinct().Count()==1).ToArray();
        foreach(var caption in captions)
        {
            var b=caption.Bounds;var h=caption.Lines.Select(l=>l.Bounds.Height).Order().ElementAt(caption.Lines.Count/2);
            var peers=blocks.Where(p=>p.BlockId!=caption.BlockId&&
                Math.Min(p.Bounds.Bottom,b.Bottom)-Math.Max(p.Bounds.Top,b.Top)>Math.Min(p.Bounds.Height,b.Height)*.45f&&
                (p.Bounds.Right<=b.Left||p.Bounds.Left>=b.Right)&&
                Math.Min(Math.Abs(p.Bounds.Left-b.Right),Math.Abs(b.Left-p.Bounds.Right))<h*2).ToArray();
            if(peers.Length==0)continue;
            // A source column may not borrow its neighbor's caption space.
            // This restricts layout only; no new pixels become erase targets.
            var left=Math.Max(0,b.Left-h*.15f);var right=Math.Min(canvas.Width,b.Right+h*.15f);
            foreach(var peer in peers)
            {
                if(peer.Bounds.Right<=b.Left)left=Math.Max(left,(peer.Bounds.Right+b.Left)/2+1);
                if(peer.Bounds.Left>=b.Right)right=Math.Min(right,(b.Right+peer.Bounds.Left)/2-1);
            }
            caption.SourceCaptionCorridor=RectangleF.FromLTRB(left,Math.Max(0,b.Top-1),right,Math.Min(canvas.Height,b.Bottom+1));
        }
    }
    internal static IReadOnlyDictionary<string,string> Observe(Size canvas,IReadOnlyList<NormalizedOcrLine> lines)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        var candidates=lines.Where(l=>l.Bounds.Width<canvas.Width*.22f && l.SourceText.Length<=64).ToArray();
        static float Center(NormalizedOcrLine l)=>l.Bounds.Left+l.Bounds.Width/2;
        var groups=new List<NormalizedOcrLine[]>();
        var unassigned=candidates.OrderBy(l=>l.SourceId,StringComparer.Ordinal).ToList();
        while(unassigned.Count>0)
        {
            var anchor=unassigned[0];var group=new List<NormalizedOcrLine>{anchor};unassigned.RemoveAt(0);
            // An immutable axis prevents a chain of near neighbours from drifting
            // into the next caption. Each source line belongs to exactly one group.
            for(var pass=0;pass<candidates.Length;pass++)
            {
                var next=unassigned.Where(b=>
                    Math.Abs(Center(anchor)-Center(b))<=Math.Max(2,Math.Min(anchor.Bounds.Height,b.Bounds.Height)*.3f)&&
                    Math.Max(anchor.Bounds.Height,b.Bounds.Height)<=Math.Min(anchor.Bounds.Height,b.Bounds.Height)*1.35f&&
                    group.Any(a=>Math.Abs(a.Bounds.Top-b.Bounds.Top)<=Math.Max(a.Bounds.Height,b.Bounds.Height)*4)).ToArray();
                if(next.Length==0)break;
                foreach(var line in next){group.Add(line);unassigned.Remove(line);}
            }
            if(group.Count>=2&&group.Max(l=>l.Bounds.Bottom)-group.Min(l=>l.Bounds.Top)<=anchor.Bounds.Height*8)
                groups.Add(group.ToArray());
        }
        foreach(var a in groups)
        {
            var ab=a.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
            // A repeated center is not a separate column if its bounding span
            // skips an intervening source row in the same horizontal corridor.
            // Ragged paragraph widths otherwise create interleaved owners.
            bool Interleaves(NormalizedOcrLine[] group)
            {
                var span=group.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
                return lines.Any(l=>!group.Any(g=>g.SourceId==l.SourceId)&&
                    l.Bounds.Top>group.Min(g=>g.Bounds.Top)&&l.Bounds.Top<group.Max(g=>g.Bounds.Top)&&
                    Math.Min(l.Bounds.Right,span.Right)-Math.Max(l.Bounds.Left,span.Left)>
                        Math.Min(l.Bounds.Width,span.Width)*.5f);
            }
            if(Interleaves(a))continue;
            var peers=groups.Where(b=>
            {
                if(Interleaves(b))return false;
                var bb=b.Select(l=>l.Bounds).Aggregate(RectangleF.Union);
                if(ab.IntersectsWith(bb)||Math.Min(Math.Abs(ab.Right-bb.Left),Math.Abs(bb.Right-ab.Left))>
                    Math.Max(ab.Height,bb.Height)*1.5f)return false;
                int Aligned(NormalizedOcrLine x)=>b.Count(y=>
                    Math.Min(x.Bounds.Bottom,y.Bounds.Bottom)-Math.Max(x.Bounds.Top,y.Bounds.Top)>=
                    Math.Min(x.Bounds.Height,y.Bounds.Height)*.25f);
                return a.Count(x=>Aligned(x)>0)>=2;
            }).ToArray();
            if(peers.Length==0)continue;
            var key="PARALLEL-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join("|",a.Select(l=>l.SourceId).Order()))))[..12];
            foreach(var line in a)result.TryAdd(line.SourceId,key);
        }
        return result;
    }
}
