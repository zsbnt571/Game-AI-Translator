using System.Collections.Immutable;

namespace ScreenshotTranslationUiTester;

// One synchronous quality check repeatedly examines the same source and its
// passages. Memoize only pure parsing inside that check, never an acceptance
// decision or a game-wide result. Disposal releases all game text. Threads and
// nested checks own separate contexts, and cached collections are immutable.
internal sealed class TranslationAnalysisScope : IDisposable
{
    [ThreadStatic] private static TranslationAnalysisScope? current;
    private readonly TranslationAnalysisScope? previous;
    internal static TranslationAnalysisScope? Current=>current;
    internal const int Limit=128;
    internal Dictionary<string,string> Plain {get;}=new(StringComparer.Ordinal);
    internal Dictionary<string,IReadOnlySet<string>> Languages {get;}=new(StringComparer.Ordinal);
    internal Dictionary<(string Source,string Target,SourceLanguageMode Mode,string Selected),ImmutableArray<string>> Required {get;}=[];
    internal TranslationAnalysisScope(){previous=current;current=this;}
    public void Dispose()=>current=previous;
}
