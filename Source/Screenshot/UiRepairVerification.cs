using System.Diagnostics;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    // Controlled regression of the same event handlers. Physical input/painting
    // is checked separately on the packaged executable with Computer Use.
    internal async Task RunUiRepairChecksAsync(string output)
    {
        if(!AppDataPaths.DisableGlobalInput || !File.Exists(Path.Combine(AppDataPaths.Root,"UI-TEST-COPY")))
            throw new InvalidOperationException("UI checks require a marked isolated data copy and disabled global input.");
        Directory.CreateDirectory(output);
        var rows=new List<object>();
        var saved=ApiSettingsSnapshot.Copy(CurrentSettings);
        var profileBefore=JsonSerializer.Serialize(_fusion!.State);
        var records=_session.SnapshotHistory();
        var initialFiles=Directory.GetFiles(AppDataPaths.HistoryRoot,"*",SearchOption.AllDirectories).Length;
        PreviewForm? preview=null;
        async Task Sample(string name,Action action)
        {
            UiPerformanceTrace.Write("case-start",new{name});
            var sw=Stopwatch.StartNew();action();var work=sw.Elapsed.TotalMilliseconds;
            await Task.Yield();var dispatch=sw.Elapsed.TotalMilliseconds;
            await Task.Delay(180);
            rows.Add(new{name,workMs=work,dispatchMs=dispatch});
        }
        try
        {
            await Task.Delay(1200);
            ShowPage("设置");_settingsNavigation.SelectedIndex=0;
            foreach(var family in new[]{"等线","黑体","华文仿宋"})
            {
                if(!FontManager.IsInstalled(family))throw new InvalidOperationException("Missing test font "+family);
                await Sample("ui-"+family,()=>{_uiFontModeBox.SelectedIndex=1;_uiFontBox.Text=family;_uiFontSizeBox.Value=14;SaveSettings(true);});
                await Sample("ui-repeat-"+family,()=>SaveSettings(true));
            }
            foreach(var size in new[]{10,18,14})await Sample("ui-size-"+size,()=>{_uiFontSizeBox.Value=size;SaveSettings(true);});
            if(records.Count>0) { preview=OpenHistoryItem(records[0]);await Task.Delay(600); }
            ShowPage("设置");_settingsNavigation.SelectedIndex=4;
            foreach(var family in new[]{"等线","黑体","华文仿宋"})
            {
                await Sample("preview-"+family,()=>{_previewFontModeBox.SelectedIndex=2;_previewFontBox.Text=family;_previewFontSizeBox.Value=14;SaveSettings(true);});
                await Sample("preview-repeat-"+family,()=>SaveSettings(true));
            }
            foreach(var size in new[]{10,18,14})await Sample("preview-size-"+size,()=>{_previewFontSizeBox.Value=size;SaveSettings(true);});
            await Sample("preview-follow",()=>{_previewFontModeBox.SelectedIndex=1;SaveSettings(true);});
            await Sample("preview-default",()=>{_previewFontModeBox.SelectedIndex=0;SaveSettings(true);});
            await Sample("rapid-font-choices",()=>{_uiFontBox.Text="等线";_uiFontBox.Text="黑体";_uiFontBox.Text="华文仿宋";SaveSettings(true);});
            preview?.Close();preview=null;
            ShowPage("内嵌翻译");_gameOperationResult="界面稳定性验证：只改变显示说明，未执行游戏操作。";
            RefreshGameState();await Task.Delay(150);
            var changes=0;EventHandler handler=(_,_)=>changes++;_gameStatus.TextChanged+=handler;
            var before=AllControls(_pages["内嵌翻译"]).ToDictionary(c=>c,c=>c.Bounds);
            var moved=0;
            for(var i=0;i<31;i++) { RefreshGameState();if(before.Any(p=>p.Key.Bounds!=p.Value))moved++;await Task.Delay(20); }
            _gameStatus.TextChanged-=handler;
            rows.Add(new{name="unchanged-game-state",polls=31,textChanges=changes,boundsChanged=moved});
            _gameOperationResult="";RefreshGameState();
            ShowPage("历史");await Task.Delay(100);
            rows.Add(new{name="history-preserved",records=records.Count,after=_session.HistoryCount,filesBefore=initialFiles,filesAfter=Directory.GetFiles(AppDataPaths.HistoryRoot,"*",SearchOption.AllDirectories).Length});
            rows.Add(new{name="schemes-preserved",unchanged=profileBefore==JsonSerializer.Serialize(_fusion!.State)});
            File.WriteAllText(Path.Combine(output,"controlled-results.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());}
        finally { preview?.Close();ApplyAppearanceControls(saved);SaveSettings(true);ExitApplication(); }
    }
}
