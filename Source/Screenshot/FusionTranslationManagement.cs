namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly Dictionary<string,NativeTranslationWorkspace> _nativeTranslationManagers=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,TranslationWorkspaceForm> _translationManagers=new(StringComparer.OrdinalIgnoreCase);
    private void OpenTranslationManager()
    {
        if(_selectedGame is not {} game||_fusion is null)return;
        try
        {
            if(!(EmbeddedGameAdapters.Handles(game)||game.AdapterId=="renpy"||game.AdapterId?.StartsWith("rpg-maker-")==true))
                throw new IOException("此游戏尚未接入通用翻译队列，暂没有可管理的内嵌译文。");
            var settings=_fusion.GameSettings(UiSettings,game.ExePath);
            string key=TranslationWorkspaceStore.For(game.ExePath,settings).FilePath;
            if(WpfWorkspace.Enabled)
            {
                if(_nativeTranslationManagers.TryGetValue(key,out var nativeExisting)&&!nativeExisting.IsDisposed){nativeExisting.Activate();return;}
                var native=new NativeTranslationWorkspace(game,settings);_nativeTranslationManagers[key]=native;native.Closed+=(_,_)=>_nativeTranslationManagers.Remove(key);native.Show(this);return;
            }
            if(_translationManagers.TryGetValue(key,out var existing)&&!existing.IsDisposed){existing.Activate();return;}
            var form=new TranslationWorkspaceForm(game,settings);_translationManagers[key]=form;
            form.FormClosed+=(_,_)=>_translationManagers.Remove(key);form.Show(this);
        }
        catch(Exception ex){SetGameActionFeedback(SafeDiagnosticOutput.ExceptionSummary(ex));}
    }
}
