using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static partial class CorePipelineCorpusRunner
{
    internal static bool PreserveIndependentFieldBreaks(VisualBlock block)
    {
        if(block.Lines.Count<2)return false;
        // A horizontal control group may contain colons too. Keep its existing layout
        // when all source boxes overlap one common baseline band.
        var commonTop=block.Lines.Max(x=>x.Bounds.Top);
        var commonBottom=block.Lines.Min(x=>x.Bounds.Bottom);
        var shortest=block.Lines.Min(x=>Math.Max(1,x.Bounds.Height));
        if((commonBottom-commonTop)/shortest>=.5f)return false;
        // An explicitly empty value remains a field, provided this vertical
        // group also contains populated fields. It must not change the entire
        // sibling column into a compact paragraph layout.
        return block.Lines.Any(line=>Regex.IsMatch(line.SourceText.Trim(),@"[:：]\s*\S"))&&block.Lines.All(line=>
        {
            var text=line.SourceText.Trim();
            if(Regex.IsMatch(text,@"^[A-Za-z]:[\\/]|^[A-Za-z]+://"))return false;
            return Regex.IsMatch(text,@"^[\p{L}][\p{L}\p{N} ./_-]{0,24}[:：]");
        });
    }
}
