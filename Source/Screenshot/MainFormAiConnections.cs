namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private AiConnectionProfiles? aiConnectionProfiles;
    private AiToolsController? sharedAiController;
    internal AiToolsController SharedAiController
    {
        get
        {
            if (sharedAiController is null)
            {
                sharedAiController = new(this);
                Disposed += (_, _) => sharedAiController?.Dispose();
            }
            return sharedAiController;
        }
    }
    private AiConnectionProfiles AiConnections => aiConnectionProfiles ??= new(Path.Combine(AiStoragePaths147.Root,"connections.json"));
    internal IReadOnlyList<WorkspaceProfileChoice> AiProfiles() => AiConnections.Choices();
    internal ApiSettings AiProfileSettings(string id) => AiConnections.Settings(id);
    internal AiAgentOptions AiProfileOptions(string id) => AiConnections.Options(id);
    internal void AiSaveAgentOptions(string id, AiAgentOptions options) => AiConnections.SaveOptions(id, options);
    internal void AiSaveAgentProfile(string id, string name, ApiSettings settings, AiAgentOptions options) => AiConnections.Save(id, name, settings, options);
    internal void AiOpenSettings() { WpfNavigate("设置"); NativeSelectSettings(10); }
    internal void AiSaveProfile(string id,string name,ApiSettings settings) => AiConnections.Save(id,name,settings);
    internal void AiDeleteProfile(string id) => AiConnections.Delete(id);
    internal IReadOnlyList<WorkspaceProfileChoice> AiTranslationProfiles() => _fusion is null ? [] :
        _fusion.State.Profiles.Select(p=>new WorkspaceProfileChoice(p.Key,_fusion.Name(p.Key))).ToArray();
    // Explicit copy is a detached connection snapshot. Never persist into _fusion.
    internal ApiSettings AiTranslationProfileSettings(string id)
    {
        if(_fusion is null || !_fusion.State.Profiles.TryGetValue(id,out var settings))throw new AiToolsException("select_profile");
        return AiConnectionProfiles.ConnectionOnly(settings);
    }
}
