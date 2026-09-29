using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class GameWindowNames
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
    internal static string WindowTitle(IntPtr window){var text=new StringBuilder(512);GetWindowText(window,text,text.Capacity);return text.ToString();}
    internal static string? Clean(string? title)
    {
        if(string.IsNullOrWhiteSpace(title))return null;title=title.Trim();
        if(title.Length is <2 or >160||title.Any(char.IsControl)||Regex.IsMatch(title,@"(^|\b)(loading|launcher|configuration|crash|error|console|setup|fps|BepInEx|ReiPatcher|Doorstop)(\b|$)",RegexOptions.IgnoreCase)||title.Contains("正在加载"))return null;
        return title;
    }
    internal static (string? Name,string Identity) Read(string exe)
    {
        var target=GameLibraryWindowIdentity.Find(exe).Target;if(target is null||!GameLibraryWindowIdentity.Matches(target))return(null,"");
        return(Clean(WindowTitle(target.Window)),$"{target.ProcessId}:{target.StartedUtcTicks}:{target.Window}");
    }
    internal static string Fallback(string exe,string? dataDirectory)
    {
        var folder=Path.GetDirectoryName(exe)!;var stem=Path.GetFileNameWithoutExtension(exe);
        try
        {
            if(dataDirectory is not null&&Path.Combine(dataDirectory,"app.info") is { } info&&File.Exists(info)&&new FileInfo(info).Length<16384)
            {var product=File.ReadAllLines(info).Skip(1).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x));if(Clean(product)is { } name)return name;}
            foreach(var file in new[]{Path.Combine(folder,"www","data","System.json"),Path.Combine(folder,"data","System.json")})
            {
                if(!File.Exists(file)||new FileInfo(file).Length>2_000_000)continue;
                using var json=JsonDocument.Parse(File.ReadAllText(file));if(json.RootElement.TryGetProperty("gameTitle",out var title)&&Clean(title.GetString())is { } name)return name;
            }
        }
        catch(IOException){}catch(JsonException){}catch(UnauthorizedAccessException){}
        return stem.Equals("game",StringComparison.OrdinalIgnoreCase)?Path.GetFileName(folder):stem;
    }
}

public sealed partial class MainForm
{
    private bool _namesPolling;
    private DateTimeOffset _namesLastPoll;
    private readonly Dictionary<string,(string Name,string Identity,int Seen)> _windowNames=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _acceptedWindowNames=new(StringComparer.Ordinal);
    private async Task RefreshLibraryWindowNamesAsync()
    {
        if(_namesPolling||AppDataPaths.DisableGlobalInput||DateTimeOffset.UtcNow-_namesLastPoll<TimeSpan.FromSeconds(4))return;
        _namesPolling=true;_namesLastPoll=DateTimeOffset.UtcNow;
        try
        {
            var store=await RecentStore();var records=store.SnapshotAll().Where(x=>x.NameSource!="manual").Take(200).ToArray();
            var found=await Task.Run(()=>{
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var process in System.Diagnostics.Process.GetProcesses())using(process){try{names.Add(process.ProcessName);}catch(InvalidOperationException){}}
                return records.Where(x=>names.Contains(Path.GetFileNameWithoutExtension(x.ExePath))||GameLibraryWindowIdentity.HasKnownIdentity(x.ExePath))
                    .Select(x=>(Record:x,Title:GameWindowNames.Read(x.ExePath))).ToArray();
            });
            if(IsDisposed)return;
            foreach(var (record,title) in found)
            {
                if(title.Name is null){_windowNames.Remove(record.ExePath);continue;}
                var key=record.Id+":"+title.Identity;if(_acceptedWindowNames.Contains(key))continue;
                var previous=_windowNames.GetValueOrDefault(record.ExePath);int seen=previous.Name==title.Name&&previous.Identity==title.Identity?previous.Seen+1:1;
                _windowNames[record.ExePath]=(title.Name,title.Identity,seen);if(seen<2)continue;
                store.SetDisplayName(record.Id,title.Name,false);_acceptedWindowNames.Add(key);
                if(_selectedGame is { } game&&SameGame(game.ExePath,record.ExePath)){_selectedGame=game with{Name=title.Name};_gameTitle.Text=title.Name;RenderGameSessionState();}
            }
        }
        catch(Exception ex){if(!IsDisposed)AppLog.Write("game-name",SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally{_namesPolling=false;}
    }
}
