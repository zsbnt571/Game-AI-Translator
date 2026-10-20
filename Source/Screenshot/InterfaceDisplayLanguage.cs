namespace ScreenshotTranslationUiTester;

// UI-generated names are display metadata. Never change the game translation target.
internal static class InterfaceDisplayLanguage
{
    internal static string Code(ApplicationLanguage language) => language switch
    { ApplicationLanguage.English => "en", ApplicationLanguage.Russian => "ru", _ => "zh" };
    internal static string Name(ApplicationLanguage language) => language switch
    { ApplicationLanguage.English => "English", ApplicationLanguage.Russian => "Russian", _ => "Simplified Chinese" };
    internal static ApiSettings Settings(ApiSettings source, ApplicationLanguage? language = null)
    {
        var copy = ApiSettingsSnapshot.Copy(source);
        copy.TargetLanguage = Code(language ?? UiLanguage.Current);
        copy.SourceLanguage = SourceLanguageMode.Auto;
        copy.SourceLanguages = [];
        return copy;
    }
    internal static bool Matches(string target, ApplicationLanguage? language = null) =>
        TranslationLanguagePolicy.Canonical(target) == Code(language ?? UiLanguage.Current);
}

public sealed partial class MainForm
{
    private void RefreshInterfaceDisplayNames()
    {
        if(IsDisposed||!_desktopReady)return;
        // Redraw names from the new language's cache; keep edits and selection.
        if(WpfWorkspace.Enabled)nativeRpg?.RefreshInterfaceNames();
        else if(SelectedDataSession() is {} data)
        {
            data.Labels?.Dispose();data.Labels=null;data.LabelIdentity="";
            RefreshDataLabels();
        }
        if(SelectedScriptTranslationSession() is {} script)
        {
            script.Labels?.Dispose();script.Labels=null;script.LabelIdentity="";
            RefreshRenpyDataNames();QueueRenpyDataNames();
        }
        ScheduleLibraryRefresh();
    }
}
