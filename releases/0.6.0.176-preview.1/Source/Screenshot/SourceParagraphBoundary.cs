using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Local source line rhythm establishes a paragraph break independently of prose continuation.</summary>
internal static class SourceParagraphBoundary
{
    internal static bool Separates(NormalizedOcrLine before, NormalizedOcrLine after,
        IReadOnlyList<NormalizedOcrLine> lines)
    {
        var a=before.Bounds;var b=after.Bounds;
        var h=Math.Max(1,Math.Min(a.Height,b.Height));
        if(b.Top<=a.Top||b.Top-a.Bottom<h*.65f||
            !Regex.IsMatch(before.SourceText,@"[.!?。！？][\""'”’]*$"))return false;
        var nearby=lines.Where(l=>l.Bounds.Top>=a.Top-h*7&&l.Bounds.Top<=b.Top+h*7&&
            l.Bounds.Height>=h*.7f&&l.Bounds.Height<=h*1.4f&&
            Math.Abs(l.Bounds.Left-a.Left)<=h*.3f)
            .OrderBy(l=>l.Bounds.Top).Take(24).ToArray();
        var pitches=nearby.Zip(nearby.Skip(1),(x,y)=>
            y.Bounds.Top+y.Bounds.Height/2-x.Bounds.Top-x.Bounds.Height/2)
            .Where(step=>step>=h*.65f&&step<=h*2.5f).Order().ToArray();
        if(pitches.Length<3)return false;
        // A page may contain more one-line paragraphs than wrapped rows. In
        // that case the median pitch describes paragraph spacing, not line
        // spacing. Require multiple observed tight gaps before using their
        // ink clearance as the within-paragraph reference.
        var tight=nearby.Zip(nearby.Skip(1),(x,y)=>new {
            Gap=y.Bounds.Top-x.Bounds.Bottom,
            Pitch=y.Bounds.Top+y.Bounds.Height/2-x.Bounds.Top-x.Bounds.Height/2})
            .Where(x=>x.Gap>=0&&x.Gap<h*.65f&&x.Pitch>=h*.65f&&x.Pitch<=h*1.7f)
            .ToArray();
        if(tight.Length>=2)
        {
            var gap=tight.Select(x=>x.Gap).Order().ElementAt(tight.Length/2);
            var pitch=tight.Select(x=>x.Pitch).Order().ElementAt(tight.Length/2);
            var currentPitch=b.Top+b.Height/2-a.Top-a.Height/2;
            if(b.Top-a.Bottom>=gap*1.65f+h*.1f&&currentPitch>=pitch+h*.35f)return true;
        }
        var usual=pitches[pitches.Length/2];
        var current=b.Top+b.Height/2-a.Top-a.Height/2;
        return current>usual*1.5f;
    }
}
