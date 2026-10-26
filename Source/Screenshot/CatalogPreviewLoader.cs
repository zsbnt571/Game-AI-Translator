namespace ScreenshotTranslationUiTester;

// Opening the optional catalogue is passive. Expensive engine collection is an
// explicit action; cached/static results and language counts are prepared off UI.
internal sealed class CatalogPreviewLoader
{
    internal sealed record Preview(ExperimentalRpgCatalog.Result Catalog,Dictionary<string,string> Cached,string[] Sources,int Unique,int Ready);
    private readonly Func<GameInfo,ApiSettings,CancellationToken,bool,ExperimentalRpgCatalog.Result> read;
    private readonly Func<GameInfo,ApiSettings,Dictionary<string,string>> cache;
    internal CatalogPreviewLoader(
        Func<GameInfo,ApiSettings,CancellationToken,bool,ExperimentalRpgCatalog.Result>? read=null,
        Func<GameInfo,ApiSettings,Dictionary<string,string>>? cache=null)
    {
        this.read=read??((game,settings,token,collect)=>CatalogTranslationPreparation.ReadCatalog(game,settings,token,collect));
        this.cache=cache??((game,settings)=>RpgTranslationCache.Effective(game.ExePath,settings,CatalogTranslationPreparation.Syntax(game.Engine)));
    }
    internal async Task<Preview> LoadAsync(GameInfo game,ApiSettings settings,ExperimentalRpgCatalog.Result? initial=null,bool collect=false,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var profile=ApiSettingsSnapshot.Copy(settings);
        var catalogTask=Task.Run(()=>{token.ThrowIfCancellationRequested();return initial??read(game,profile,token,collect);},token);
        var cacheTask=Task.Run(()=>{var loaded=cache(game,profile);token.ThrowIfCancellationRequested();return loaded;},token);
        await Task.WhenAll(catalogTask,cacheTask).ConfigureAwait(false);
        return await Task.Run(()=>
        {
            token.ThrowIfCancellationRequested();
            var catalog=catalogTask.Result;var cached=cacheTask.Result;
            var sources=CatalogTranslationPreparation.Sources(catalog,profile,token);
            int unique=catalog.Entries.Select(e=>e.Text).Distinct(StringComparer.Ordinal).Count();
            token.ThrowIfCancellationRequested();
            return new Preview(catalog,cached,sources,unique,sources.Count(cached.ContainsKey));
        },token).ConfigureAwait(false);
    }
}
