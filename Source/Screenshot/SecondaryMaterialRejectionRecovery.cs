using System.Drawing;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static partial class CorePipelineCorpusRunner
{
    internal sealed record MaterialRejectionRecoveryResult(int BeforePixels,int OrdinaryRejectedPixels,
        int ReintroducedValidatedPrimaryPixels,int FinalRecoveredPrimaryPixels);

    internal static bool[,] ValidatePrimarySeedForSecondaryRecovery(bool[,] seed,Rectangle geometry)
    {
        // Validation uses a local copy: direct style recovery must not silently bypass
        // either structural rule before its pixels become eligible for later recovery.
        var primary=CopyMaskRegion(seed,geometry);
        var local=new Rectangle(0,0,geometry.Width,geometry.Height);
        RejectLongHorizontalStructure(primary,local);
        RejectContainerFillComponents(primary,local);
        return primary;
    }

    internal static MaterialRejectionRecoveryResult RejectContainerFillAfterSecondary(bool[,] seed,
        Rectangle geometry,bool[,] validatedPrimary)
    {
        if(validatedPrimary.GetLength(0)!=geometry.Width||validatedPrimary.GetLength(1)!=geometry.Height)
            throw new ArgumentException("Validated primary mask does not match its local geometry");
        var before=CountMaskPixels(seed,geometry);
        var eligible=new List<Point>();
        // A primary pixel removed by the intervening long-structure rule is not eligible.
        for(var y=geometry.Top;y<geometry.Bottom;y++)for(var x=geometry.Left;x<geometry.Right;x++)
            if(seed[x,y]&&validatedPrimary[x-geometry.Left,y-geometry.Top])eligible.Add(new(x,y));
        RejectContainerFillComponents(seed,geometry);
        var rejected=before-CountMaskPixels(seed,geometry);
        var restored=new List<Point>();
        foreach(var p in eligible)
            if(!seed[p.X,p.Y]){seed[p.X,p.Y]=true;restored.Add(p);}
        // The rejected secondary portion stays removed. Revalidate the resulting
        // components instead of unconditionally trusting a saved primary label.
        if(restored.Count>0)
        {
            RejectLongHorizontalStructure(seed,geometry);
            RejectContainerFillComponents(seed,geometry);
        }
        return new(before,rejected,restored.Count,restored.Count(p=>seed[p.X,p.Y]));
    }
}
