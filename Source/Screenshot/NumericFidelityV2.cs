using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

// Admission protects literal Arabic numbers in the source, not inferred
// quantities in prose. No English/Chinese number-word or idiom interpretation.
internal static class NumericFidelityV2
{
    // Keep spelling, leading zeroes, signs, decimals, percentages and compact
    // UI expressions (dates, times, ranges, fractions). Words remain translatable.
    private const string Scalar = @"[+\-−±]?\s*(?:[0-9]+(?:[.,][0-9]+)*|\.[0-9]+)(?:\s*[%％])?";
    private static readonly Regex Arabic = new(
        @"(?:(?<![A-Za-z0-9_])[xX×]\s*)?" + Scalar +
        @"(?:\s*[/：:]\s*" + Scalar + @")*" +
        @"(?:\s*(?:[-–—~～]|to|through|至|到)\s*" + Scalar + @")?",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // Spaces and the spelling of a range connector may follow target grammar;
    // numeric spelling and range direction/endpoints must remain unchanged.
    private static string LiteralKey(string text) => Regex.Replace(
        Regex.Replace(text,@"(?<=[0-9%％])\s*(?:[-–—~～]|to|through|至|到)\s*(?=[+\-−]?[0-9])","..",
            RegexOptions.CultureInvariant),@"\s+","");

    // A whole quantity marker has no natural-language body to translate. The
    // anchors are essential: Apple x1 / 20% damage must remain translatable.
    internal static bool IsPureQuantity(string text) => Regex.IsMatch(text,
        @"^\s*(?:[xX×]\s*)?\p{Sc}?" + Scalar +
        @"(?:\s*[/：:\-–—~～]\s*" + Scalar + @")*\s*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool Matches(string source,string translated)
    {
        var required = Arabic.Matches(source);
        if(required.Count == 0)return true;
        var available = new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(Match token in Arabic.Matches(translated))
            available[LiteralKey(token.Value)] = available.GetValueOrDefault(LiteralKey(token.Value)) + 1;
        foreach(Match token in required)
        {
            var key=LiteralKey(token.Value);
            if(!available.TryGetValue(key,out var count) || count == 0)return false;
            available[key] = count - 1;
        }
        // Target-only digits may translate written number words. Hard-rejecting
        // those would reintroduce the withdrawn word-quantity contract. Missing
        // or changed source literals still fail above; no semantic inference.
        return true;
    }

    internal sealed record QuantityToken(string Token,string Value,string Kind,string Relation,int Start,int Length);
    internal static IReadOnlyList<QuantityToken> Tokens(string text,string peer,bool sourceSide) =>
        Arabic.Matches(text).Select(token => new QuantityToken(token.Value,LiteralKey(token.Value),
            "ArabicLiteral","SourceText",token.Index,token.Length)).ToArray();
    internal static string Describe(string text,string peer,bool sourceSide) =>
        string.Join("|",Arabic.Matches(text).Select(token => $"ArabicLiteral:{token.Value}@{token.Index}"));
}
