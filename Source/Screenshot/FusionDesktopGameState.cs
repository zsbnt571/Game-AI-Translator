using System.Diagnostics;
using System.Text;

namespace ScreenshotTranslationUiTester;

internal sealed record DesktopGameState(GameInfo Game,bool HasRecord,bool Installed,string Plugin,string Action,
    bool? ConfigurationMatches,string Detail,FusionGameProcessState Process,string ActionHint);

// Detection describes an adapter candidate; it does not prove that this game's
// current process has accepted the translation bridge. Keep these facts distinct.
internal sealed record GameTranslationPresentation(string Badge,string Font,string Rendering,bool CanToggle,
    bool Blocked,bool PendingVerification)
{
    internal static GameTranslationPresentation Resolve(GameInfo? game,bool connected,string? blockedReason)
    {
        bool supported=GameCapabilities.EmbeddedTranslation(game);
        if(connected&&supported)
            return new("组件已连接",game?.AdapterId=="renpy"?"保持游戏字号":"跟随游戏原有字体","游戏文字内替换",true,false,false);
        if(!string.IsNullOrWhiteSpace(blockedReason))
            return new("翻译未接入","未接入，暂不可用","翻译未接入",false,true,false);
        if(game?.Support==SupportLevel.Candidate)
            return new("待接入验证","接入后确认","接入后确认",false,false,true);
        return new(game?.Support==SupportLevel.Supported?"已有适配":"暂不支持内嵌翻译",
            supported?"由游戏适配自动控制":"当前游戏尚无内嵌适配",supported?"内嵌替换":"适配后可用",false,false,false);
    }
}

internal sealed partial class FusionAdapterInstaller
{
    // Invoked only by the desktop's single-flight background query. Cache keys
    // include all carried files, the ledger and config; actions always revalidate.
    private string? desktopStamp;
    private DesktopGameState? desktopState;
    private string? detectionStamp;
    private GameInfo? detectedGame;
    private static string DetectionStamp(GameInfo game)
    {
        var root=Path.GetDirectoryName(game.ExePath)!;
        var data=game.DataDirectory??Path.Combine(root,Path.GetFileNameWithoutExtension(game.ExePath)+"_Data");
        var paths=new[]{game.ExePath,Path.Combine(root,"UnityPlayer.dll"),Path.Combine(root,"GameAssembly.dll"),
            Path.Combine(root,"MonoBleedingEdge","EmbedRuntime","mono-2.0-bdwgc.dll"),
            Path.Combine(data,"app.info"),Path.Combine(data,"globalgamemanagers"),Path.Combine(data,"data.unity3d"),
            Path.Combine(data,"Managed","Assembly-CSharp.dll"),Path.Combine(data,"Managed","Game.dll"),
            Path.Combine(data,"Managed","Unity.TextMeshPro.dll"),Path.Combine(data,"Managed","mscorlib.dll"),
            Path.Combine(data,"Mono","mono.dll"),Path.Combine(data,"Mono","EmbedRuntime","mono.dll")};
        var stamp=new StringBuilder(game.ExePath).Append('|').Append(Directory.GetLastWriteTimeUtc(root).Ticks);
        foreach(var path in paths){var f=new FileInfo(path);stamp.Append('|').Append(f.Exists?f.Length:-1).Append(':').Append(f.Exists?f.LastWriteTimeUtc.Ticks:0);}
        return stamp.ToString();
    }
    internal (GameInfo Game,string Stamp) DetectDesktopSelection(string path)
    {
        var game=GameDetector.Detect(path);return (game,DetectionStamp(game));
    }
    internal DesktopGameState InspectDesktop(GameInfo game,ApiSettings settings,string key,bool force,string? selectionStamp=null)
    {
        var detection=DetectionStamp(game);
        if(selectionStamp==detection){detectedGame=game;detectionStamp=detection;}
        if(detection!=detectionStamp||detectedGame is null)
        {detectedGame=GameDetector.Detect(game.ExePath);detectionStamp=DetectionStamp(detectedGame);force=true;}
        game=detectedGame;
        var process=ProcessState(game);var cloud=Cloud(game);var supported=cloud||game.AdapterId=="mgi";
        if(EmbeddedGameAdapters.Handles(game))
        {
            string embeddedStamp="embedded|"+EmbeddedGameAdapters.InspectionStamp(game);
            if(!force&&desktopStamp==embeddedStamp&&desktopState is not null)return desktopState with{Process=process};
            bool embeddedInstalled=IsInstalled(game);desktopStamp=embeddedStamp;
            return desktopState=new(game,HasRecord(game),embeddedInstalled,embeddedInstalled?game.Engine+" 翻译组件":"未安装翻译组件","应用翻译",null,game.Details,process,"从软件启动后可随时开关翻译；已有译文优先显示，未译文本继续后台处理。");
        }
        if(game.AdapterId=="renpy")return new(game,HasRecord(game),IsInstalled(game),IsInstalled(game)?"Ren’Py 翻译组件":"未安装翻译组件","应用翻译",null,game.Details,process,"翻译并启动即可边玩边翻译；普通启动会移出本软件的组件。");
        if(!supported)return new(game,HasRecord(game),false,"此游戏尚无配套适配","安装翻译",null,game.Details,process,"未登记此构建的内嵌适配，可单独启动游戏。");
        var configPath=InRoot(game,cloud?FusionCloudPayload.Config:ConfigRelative);
        var paths=(cloud?(IEnumerable<string>)FusionCloudPayload.Files.Keys:new[]{PluginRelative}).Append(Ledger)
            .Append(cloud?FusionCloudPayload.Config:ConfigRelative).Select(x=>InRoot(game,x)).ToArray();
        var stamp=new StringBuilder(game.ExePath).Append('|').Append(FusionConfiguration.EditIdentity(settings)).Append('|').Append(key);
        foreach(var path in paths){var f=new FileInfo(path);stamp.Append('|').Append(path).Append(':').Append(f.Exists?f.Length:-1).Append(':').Append(f.Exists?f.LastWriteTimeUtc.Ticks:0);}
        var identity=stamp.ToString();
        if(!force&&desktopStamp==identity&&desktopState is not null)return desktopState with{Process=process};
        var record=HasRecord(game);var installed=IsInstalled(game);var pluginPath=InRoot(game,cloud?FusionCloudPayload.Plugin:PluginRelative);
        var exists=File.Exists(pluginPath);var action="安装翻译";var plugin="未安装";var details=new List<string>();var hint="";
        bool? matches=null;
        if(exists)
        {
            var hash=FileHash(pluginPath);var wanted=cloud?FusionCloudPayload.Files[FusionCloudPayload.Plugin]:FusionPluginIdentity.Sha256;
            var version=FileVersionInfo.GetVersionInfo(pluginPath).FileVersion;
            if(hash==wanted){plugin=cloud?"配套插件 0.6.0.21":"配套 MGI 插件";action="重新应用";}
            else
            {
                plugin="检测到插件 "+(string.IsNullOrWhiteSpace(version)?"（版本未确定）":version);
                action="切换配套插件";
                if(cloud&&Version.TryParse(version,out var actual)&&Version.TryParse(FusionCloudPayload.PluginVersion,out var carried))
                {if(actual<carried)action="更新插件";else if(actual>carried)hint="当前插件较新；应用将换回配套 0.6.0.21，气泡字号问题仍未解决。";}
                details.Add("安装的插件与本软件携带载荷不同。");
            }
            if(hash==wanted)
                try{Verify(game);details.Add("配套载荷完整性已核对。");}
                catch(Exception ex){plugin+=" · 文件需核对";action="修复配套插件";details.Add(SafeDiagnosticOutput.ExceptionSummary(ex));}
        }
        if(record&&!installed)hint+=(hint.Length>0?"\n":"")+"安装记录未完成或与所选游戏不匹配，请先修复安装或恢复。";
        if(exists&&!record)hint+=(hint.Length>0?"\n":"")+"已有插件文件，但缺少本软件安装记录；安装前会检查备份与冲突。";
        if(File.Exists(configPath))
        {
            try
            {
                var desired=ConfigValues(cloud?CloudConfiguration(settings,key):BuildLegacyConfiguration(settings,key));
                var actual=ConfigValues(File.ReadAllText(configPath));
                matches=desired.All(x=>actual.TryGetValue(x.Key,out var value)&&value==x.Value);
            }
            catch(Exception ex){details.Add("配置核对失败："+SafeDiagnosticOutput.ExceptionSummary(ex));}
        }
        else if(installed)matches=false;
        if(action=="重新应用"&&matches==false)action="应用更改";
        if(action is "更新插件" or "切换配套插件" or "修复配套插件")details.Add("应用时同时写入当前方案配置。");
        if(installed)hint+=(hint.Length>0?"\n":"")+"带翻译启动时应用当前方案；普通启动会先恢复本软件的安装。";
        if(hint.Length==0&&(action is "更新插件" or "切换配套插件" or "修复配套插件"))hint="应用时同时写入当前方案配置。";
        details.Add("桌面 "+BuildIdentity.BuildVersion+"；配套插件 "+(cloud?FusionCloudPayload.PluginVersion:"MGI 原始载荷")+"。运行中实际加载的配置未经运行侧确认。");
        desktopStamp=identity;
        return desktopState=new(game,record,installed,plugin,action,matches,string.Join("\n",details),process,hint);
    }
    private static Dictionary<string,string> ConfigValues(string text)
    {
        var values=new Dictionary<string,string>(StringComparer.Ordinal);var section="";
        foreach(var raw in text.Split('\n'))
        {
            var line=raw.Trim();if(line.Length==0||line.StartsWith('#')||line.StartsWith(';'))continue;
            if(line.StartsWith('[')&&line.EndsWith(']')){section=line;continue;}
            var split=line.IndexOf('=');if(split>=0)values[section+"|"+line[..split].Trim()]=line[(split+1)..].Trim();
        }
        return values;
    }
}

public sealed partial class MainForm
{
    private bool _gameQueryRunning,_gameQueryPending,_gameForceQuery,_gameDetecting;
    private int _gameQueryEpoch,_gameSelectionEpoch;
    private DesktopGameState? _desktopGameState;
    private readonly Label _gameTitle=new(){AutoSize=true};
    private readonly Label _gameConfiguration=new(){AutoSize=true};
    private readonly Label _gameActionHint=new(){AutoSize=true};
    private readonly Label _gameQueryDetails=new(){AutoSize=true};
    private readonly Label _gameSelectionHint=new(){AutoSize=true,Text="选择游戏主程序，也可拖入 EXE。"};
    private Button? _chooseGameButton;
    private string? _gamePlanIdentity;
    private string _gameQueryError="";
    private DateTime _launchFeedbackUntil;
    private readonly Label _gameFeedback=new(){AutoSize=true};

    private void InvalidateGameState()
    {
        _gameQueryEpoch++;_gameForceQuery=true;_desktopGameState=null;_gameQueryError="";
        RenderGameState();RefreshGameState();
    }
    private string? _selectedDetectionStamp;
    private string? _pendingGamePath;
    private readonly SemaphoreSlim _selectionDetectionGate=new(1,1);
    private Task _selectGameTask=Task.CompletedTask;
    private async void SelectFusionGame(string path,string? relinkId=null,bool recordSelection=true) => await SelectFusionGameAsync(path,relinkId,recordSelection);
    private Task SelectFusionGameAsync(string path,string? relinkId=null,bool recordSelection=true)
    {
        if(relinkId is null&&_gameDetecting&&SameGame(path,_pendingGamePath))return _selectGameTask;
        if(relinkId is null&&!_gameDetecting&&SameGame(path,_selectedGame?.ExePath)){RenderGameState();return Task.CompletedTask;}
        return _selectGameTask=SelectFusionGameCoreAsync(path,relinkId,recordSelection);
    }
    private async Task SelectFusionGameCoreAsync(string path,string? relinkId,bool recordSelection)
    {
        try{path=RecentGameStore.Normalize(path);}catch(Exception ex){ShowFusionError(ex);return;}
        if(_gameDetecting&&StringComparer.OrdinalIgnoreCase.Equals(path,_pendingGamePath))return;
        _pendingGamePath=path;
        CancelSelectedMedia();
        var token=_mediaSelectionCancellation!.Token;
        var selection=++_gameSelectionEpoch;_gameQueryEpoch++;_gameDetecting=true;_desktopGameState=null;RenderGameState();
        try
        {
            await _selectionDetectionGate.WaitAsync(token);
            (GameInfo Game,string Stamp) detected;
            try{detected=await Task.Run(()=>_adapter.DetectDesktopSelection(path),token);}
            finally{_selectionDetectionGate.Release();}
            token.ThrowIfCancellationRequested();
            var game=detected.Game;
            var displayRecord=(await RecentStore()).Find(game.ExePath);
            game=game with{Name=displayRecord is {NameSource:"window" or "manual"}?displayRecord.Name:GameWindowNames.Fallback(game.ExePath,game.DataDirectory)};
            if(IsDisposed||Disposing||selection!=_gameSelectionEpoch)return;
            _fusion!.PinGameProfile(game.ExePath);
            _selectedDetectionStamp=detected.Stamp;_selectedGame=game;_gamePath.Text=game.ExePath;
            _gameSessions.Get(game);PopulateProfiles();RefreshFusionChrome();
            SetLabelText(_gameTitle,game.Name);SetLabelText(_gameBadge,TranslationPresentation(game).Badge);
            SetLabelText(_gameInfo,game.Engine+" · "+game.Architecture+"\n"+game.Details);
            if(_fusion!.State.LastGame!=game.ExePath)_fusion.Change(next=>next.LastGame=game.ExePath);
            _gameOperationResult=_gameSessions.Get(game).Feedback;_launchFeedbackUntil=default;
            _rememberSelectedTask=RememberSelectedGameAsync(game,selection,relinkId,recordSelection);
        }
        catch(OperationCanceledException){}
        catch(Exception ex)
        {
            if(IsDisposed||selection!=_gameSelectionEpoch)return;
            _gameOperationResult="未更换游戏："+SafeDiagnosticOutput.ExceptionSummary(ex);ShowFusionError(ex);
            if(_selectedGame is { } previous)_rememberSelectedTask=RememberSelectedGameAsync(previous,selection,null,false);
        }
        finally
        {
            if(!IsDisposed&&selection==_gameSelectionEpoch){_pendingGamePath=null;_gameDetecting=false;InvalidateGameState();}
        }
    }
    private async void RefreshGameState()
    {
        if(!_desktopReady||IsDisposed||_gameActionRunning||_gameDetecting)return;
        if(_selectedGame is not { } game){RenderGameState();return;}
        if(_gameQueryRunning){_gameQueryPending=true;return;}
        _gameQueryRunning=true;_gameQueryPending=false;var epoch=_gameQueryEpoch;
        var settings=ApiSettingsSnapshot.Copy(EmbeddedSettings);var key=_fusion!.State.EmbeddedToggleKey;
        var selectionStamp=_selectedDetectionStamp;_selectedDetectionStamp=null;
        var force=_gameForceQuery;_gameForceQuery=false;
        try
        {
            var state=await Task.Run(()=>_adapter.InspectDesktop(game,settings,key,force,selectionStamp));
            if(IsDisposed||Disposing||epoch!=_gameQueryEpoch||_selectedGame!=game||_gameActionRunning)return;
            state=state with{Game=state.Game with{Name=game.Name}};
            _desktopGameState=state;_selectedGame=state.Game;_gameQueryError="";
            SetLabelText(_gameTitle,state.Game.Name);SetLabelText(_gameBadge,TranslationPresentation(state.Game).Badge);
            SetLabelText(_gameInfo,state.Game.Engine+" · "+state.Game.Architecture+"\n"+state.Game.Details);
            RenderGameState();
        }
        catch(Exception ex)
        {
            if(!IsDisposed&&epoch==_gameQueryEpoch){_desktopGameState=null;_gameQueryError="状态核对失败："+SafeDiagnosticOutput.ExceptionSummary(ex);RenderGameState();}
        }
        finally
        {
            _gameQueryRunning=false;
            if(!IsDisposed&&(_gameQueryPending||epoch!=_gameQueryEpoch)){_gameQueryPending=false;RefreshGameState();}
        }
    }
    private void RenderGameState()
    {
        if(_installGameButton is null)return;
        var game=_selectedGame;var s=_desktopGameState;
        var supported=game?.Engine=="Unity Mono"&&game.Support is SupportLevel.Supported or SupportLevel.Candidate;
        var stopped=s?.Process==FusionGameProcessState.Stopped;
        var busy=_gameActionRunning||_gameDetecting;
        _chooseGameButton!.Enabled=true;_chooseGameButton.Text=game is null?"选择游戏":"更换游戏";
        _gameSelectionHint.Visible=game is null;
        _folderButton.Enabled=game is not null&&!busy;_coverButton.Enabled=game is not null&&!busy;_saveGameButton.Enabled=game is not null&&!busy;
        _recentButton.Enabled=!_gameActionRunning&&!_recentOpening;
        _gamePath.Visible=_gameBadge.Visible=_folderButton.Visible=_coverButton.Visible=_saveGameButton.Visible=game is not null;
        _installGameButton.Visible=game is not null;
        if(_gameSessionPage is null)_launchGameButton!.Visible=game is not null;
        _restoreGameButton!.Visible=s?.HasRecord==true;
        _gameConfiguration.Visible=game is not null;
        if(_gameSessionPage is null)_installGameButton.Enabled=supported&&stopped&&!busy;
        _installGameButton.Text=s?.Action??"安装翻译";
        _installGameButton.FlatAppearance.BorderSize=1;
        if(_gameSessionPage is null)_launchGameButton!.Enabled=supported&&stopped&&!busy;
        if(_gameSessionPage is null&&_plainLaunchButton is not null){_plainLaunchButton.Enabled=game is not null&&stopped&&!busy;_plainLaunchButton.Visible=true;}
        if(_gameSessionPage is null)_restoreGameButton!.Enabled=s?.HasRecord==true&&stopped&&!busy;
        var status=game is null?"尚未选择游戏":s is null?"正在核对插件与配置…":(s.Installed?"已安装":s.HasRecord?"安装需核对":"未安装")+" · "+(s.Process switch{
            FusionGameProcessState.Stopped=>"未检测到运行",FusionGameProcessState.Running=>"游戏运行中",_=>"同名进程路径无法确认，暂不能安装、启动或恢复"});
        if(_gameDetecting)status="正在识别游戏…";else if(_gameActionRunning)status="正在处理，请稍候…";
        if(_launchFeedbackUntil!=default&&(DateTime.UtcNow>=_launchFeedbackUntil||s?.Process==FusionGameProcessState.Running)){_gameOperationResult=game is null?"":_gameSessions.Get(game).Feedback;_launchFeedbackUntil=default;}
        SetLabelText(_gameFeedback,_gameOperationResult);_gameFeedback.Visible=_gameOperationResult.Length>0;
        if(_gameQueryError.Length>0)status+="\n"+_gameQueryError;
        SetLabelText(_gameStatus,status);
        SetLabelText(_gameConfiguration,s?.ConfigurationMatches switch{
            true=>"方案已写入 · 运行时加载尚未确认。",
            false=>"有更改待应用 · 写入后在游戏下次启动时生效。",
            _=>game is null?"方案保存在软件中，选择游戏后可应用。":"方案保存在软件中；游戏配置尚未确认。"});
        SetLabelText(_gameQueryDetails,s is not null?"实际插件："+s.Plugin+"\n"+s.Detail:"尚无完整核对结果。");
        var hint=s?.ActionHint??"";
        var missing=_desktopReady?FusionConfiguration.Missing(EmbeddedSettings):new List<string>();
        if(missing.Count>0)
        {
            hint+=(hint.Length>0?"\n":"")+"方案尚缺："+string.Join("、",missing)+"。请在“编辑”中补全后应用。";
            if(_gameSessionPage is null){_installGameButton.Enabled=false;_launchGameButton!.Enabled=false;}
        }
        SetLabelText(_gameActionHint,hint);_gameActionHint.Visible=hint.Length>0;
        RenderGameSessionState();
    }
}

