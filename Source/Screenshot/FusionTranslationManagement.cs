namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly Dictionary<string,NativeTranslationWorkspace> _nativeTranslationManagers=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,TranslationWorkspaceForm> _translationManagers=new(StringComparer.OrdinalIgnoreCase);
    private WorkspaceMenu? _translationRouteMenu;
    private void OpenTranslationManager()
    {
        if(_selectedGame is not {} game||_fusion is null)return;
        try
        {
            if(game.AdapterId is "mgi" or "cloud-meadow")
                throw new IOException("当前选择原有专用适配，其缓存格式保持不变。请在新的独立游戏目录选择通用测试接入后使用译文管理。");
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
    private void ShowTranslationRoute(Control? anchor,System.Windows.FrameworkElement? nativeAnchor=null)
    {
        if(_selectedGame is not {} game)return;
        if(_translationRouteMenu is null)
        {
            // Auto-close can precede the mouse/key callback that opened this menu.
            // Keep it alive until its owning form is disposed, like the library menus.
            var ownedMenu=new WorkspaceMenu();
            _translationRouteMenu=ownedMenu;
            Disposed+=(_,_)=>ownedMenu.Dispose();
        }
        var menu=_translationRouteMenu;menu.ClearActions();menu.Font=Font;
        bool unity=game.Engine.StartsWith("Unity",StringComparison.Ordinal),selected=TranslationRoute.GenericTest(game.ExePath);
        async Task Select(bool generic)
        {
            try
            {
                if(_gameSessions.Find(game.ExePath)?.Busy==true||_gameSessions.Find(game.ExePath)?.LaunchPending==true)throw new IOException("当前游戏操作尚未完成，请稍后切换。");
                if(_translationManagers.Values.Any(f=>!f.IsDisposed)||_nativeTranslationManagers.Values.Any(f=>!f.IsDisposed))throw new IOException("请先关闭译文管理窗口，再切换接入方式。");
                await Task.Run(()=>TranslationRoute.Set(game,generic));await OpenLibraryGameAsync(game.ExePath);
                SetGameActionFeedback(generic?"已选择通用测试接入，使用独立译文缓存。翻译并启动后可检查是否接通。":"已恢复自动选择接入方式。");
            }
            catch(Exception ex){SetGameActionFeedback(SafeDiagnosticOutput.ExceptionSummary(ex));}
        }
        var auto=menu.AddAction("自动选择（保留原有专用适配）",!selected?"check":"",async(_,_)=>await Select(false),unity);auto.Checked=!selected;
        var test=menu.AddAction("通用测试（独立缓存）",selected?"check":"",async(_,_)=>await Select(true),unity);test.Checked=selected;
        if(!unity)menu.AddAction("当前引擎使用其现有通用接入方式","",(_,_)=>{},false);
        if(nativeAnchor is not null)WpfNativeMenus.Show(menu,nativeAnchor:nativeAnchor);else if(anchor is not null)menu.Show(anchor,new Point(0,anchor.Height));
    }
}
