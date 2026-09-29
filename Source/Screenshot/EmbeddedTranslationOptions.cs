namespace ScreenshotTranslationUiTester;

internal enum EmbeddedTextSyntax { Rpg, Renpy, Generic }

// The scheduler owns translation, priority and cache. Engine adapters only supply
// a text catalogue and a reversible display bridge; they never receive API keys.
internal sealed record EmbeddedTranslationOptions(
    EmbeddedTextSyntax Syntax,
    bool RemoteCatalog,
    Func<CancellationToken,Task<RpgTextCatalogResult>>? DesktopCatalog = null,
    int ForegroundBatchSize = 8,
    bool CatalogBoundOnly = false,
    Func<ApiSettings,CancellationToken,Task<RpgTextCatalogResult>>? ConfiguredDesktopCatalog = null)
{
    internal static EmbeddedTranslationOptions ForLegacy(string exe)
        => RenpyGameAdapter.Detect(exe) is not null
            ? new(EmbeddedTextSyntax.Renpy,true)
            : new(EmbeddedTextSyntax.Rpg,false,ForegroundBatchSize:1,
                ConfiguredDesktopCatalog:(settings,token)=>Task.Run(()=>RpgTextCatalog.Read(exe,token,settings.SourceLanguage),token));
}
