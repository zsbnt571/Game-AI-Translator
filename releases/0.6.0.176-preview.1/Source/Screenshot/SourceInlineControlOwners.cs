using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Repeated independent labels on one baseline establish separate owners.
/// No command vocabulary, screen position or translation text participates.</summary>
internal static class SourceInlineControlOwners
{
    internal static IReadOnlySet<string> Observe(IReadOnlyList<NormalizedOcrLine> lines)
    {
        var result=new HashSet<string>(StringComparer.Ordinal);
        bool Label(NormalizedOcrLine l)=>l.SourceText.Length<=18 &&
            Regex.IsMatch(l.SourceText,@"^[A-Z][A-Za-z]*(?:\.[A-Z][A-Za-z]*)?$",RegexOptions.CultureInvariant) &&
            l.SourceText.Count(char.IsLetter)>=2 && l.Bounds.Width>l.Bounds.Height*1.2f;
        var candidates=lines.Where(Label).ToArray();
        foreach(var anchor in candidates)
        {
            var h=anchor.Bounds.Height;
            var row=candidates.Where(l=>l.Bounds.Height>=h*.7f&&l.Bounds.Height<=h*1.35f&&
                Math.Abs(l.Bounds.Top+l.Bounds.Height/2-anchor.Bounds.Top-h/2)<=h*.30f)
                .OrderBy(l=>l.Bounds.Left).ToArray();
            var run=new List<NormalizedOcrLine>();
            void Admit()
            {
                if(run.Count>=4&&run.Zip(run.Skip(1)).Count(p=>
                    p.Second.Bounds.Left-p.First.Bounds.Right>=Math.Min(p.First.Bounds.Height,p.Second.Bounds.Height)*.5f)>=3)
                    foreach(var l in run)result.Add(l.SourceId);
            }
            foreach(var l in row)
            {
                if(run.Count>0)
                {
                    var previous=run[^1];var gap=l.Bounds.Left-previous.Bounds.Right;
                    var local=Math.Min(l.Bounds.Height,previous.Bounds.Height);
                    if(gap<local*.15f||gap>local*1.35f){Admit();run.Clear();}
                }
                run.Add(l);
            }
            Admit();
        }
        return result;
    }
}
