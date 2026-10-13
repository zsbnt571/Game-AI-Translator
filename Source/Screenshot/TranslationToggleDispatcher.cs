namespace ScreenshotTranslationUiTester;

// Shared by the UI action and its presentation. A pending Unity restoration is
// an unfinished Close action; it must never be interpreted as a new Start.
internal static class TranslationToggleDispatcher
{
    internal static string ButtonText(RpgTranslationSession? translation)
        =>translation?.RestorePending==true?"重试恢复原文":translation?.Enabled==true?"关闭翻译":"开启翻译";

    internal static async Task<string> ToggleAsync(GameDataSession data,Func<RpgTranslationSession> create)
    {
        try
        {
            if(data.Translation is {} active&&(active.Enabled||active.RestorePending))
            {
                await active.StopAsync();
                string warning=active.CacheWarning;active.Dispose();data.Translation=null;return warning;
            }
            data.Translation?.Dispose();data.Translation=create();
            await data.Translation.StartAsync();return "";
        }
        catch(Exception ex)
        {
            string warning=data.Translation?.CacheWarning??"";
            // A false/pause receipt leaves the actual Unity ownership and its
            // live connection available for the next explicit Close attempt.
            if(data.Translation?.RestorePending!=true){data.Translation?.Dispose();data.Translation=null;}
            return SafeDiagnosticOutput.ExceptionSummary(ex)+(warning.Length==0?"":" · "+warning);
        }
    }
}
