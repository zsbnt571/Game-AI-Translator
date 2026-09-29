using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace ScreenshotTranslationUiTester;

// Status is a product-generated state summary, never an API error, source text,
// translation, argument list, settings object, or profile identity.
internal sealed record GameCrashTranslationState(bool Requested,bool Connected,string Status);
internal sealed record GameCrashStateEvent(DateTime AtUtc,string Event,bool TranslationRequested,bool Connected,string Status);
internal sealed record GameCrashProcessExit(int Pid,string Executable,DateTime? StartedUtc,DateTime? EndedUtc,bool InitialProcess,bool Runtime,bool ExitObserved,int? ExitCode,string? ExitCodeHex);
internal sealed record GameExitReport
{
    public int Schema {get;init;}=1;
    public string SessionId {get;init;}="";
    public string GamePathHash {get;init;}="";
    public string GameExecutable {get;init;}="";
    public string GameName {get;init;}="";
    public string Engine {get;init;}="";
    public string FusionVersion {get;init;}="";
    public GameLaunchMode LaunchMode {get;init;}
    public DateTime StartedUtc {get;init;}
    public DateTime EndedUtc {get;init;}
    public DateTime RecordedUtc {get;init;}
    public bool Expected {get;init;}
    public string ExpectedReason {get;init;}="";
    public DateTime? LastExitRequestUtc {get;init;}
    public string LastExitRequestReason {get;init;}="";
    public string Outcome {get;init;}="";
    public string Summary {get;init;}="";
    public int? RuntimePid {get;init;}
    public int? ExitCode {get;init;}
    public string? ExitCodeHex {get;init;}
    public GameCrashProcessExit[] Processes {get;init;}=[];
    public GameCrashStateEvent[] Timeline {get;init;}=[];
    public JsonElement? Logs {get;init;}
    public bool LogsPending {get;init;}
    public bool ReportSaveFailed {get;init;}
    [JsonIgnore] public string JsonPath {get;init;}="";
    [JsonIgnore] public string MarkdownPath {get;init;}="";
}

// Owns only monitoring handles and its reports. It never stops a process or
// modifies a game's files. The worker runs independently of form visibility.
internal sealed class GameCrashMonitor : IDisposable
{
    private sealed class ProcessHandle : IDisposable
    {
        internal required SafeProcessHandle Handle;
        internal int Pid;internal string Path="";internal DateTime? StartedUtc,EndedUtc;
        internal bool Initial,Runtime,Exited;internal int? Code;
        public void Dispose()=>Handle.Dispose();
    }
    private sealed class Session
    {
        internal required GameInfo Game;internal required string Key;internal required string Id;
        internal GameLaunchMode Mode;internal DateTime StartedUtc,TrackedUtc;internal DateTime? QuietSince,DiscoveryEndUtc;
        internal string[]? Paths;internal bool Done,Expected,HadRuntime,ReadLimited;
        internal string ExpectedReason="",LastExitRequestReason="";internal DateTime? LastExitRequestUtc;
        internal GameCrashTranslationState State=new(false,false,"");
        internal readonly List<ProcessHandle> Processes=[];
        internal readonly List<GameCrashStateEvent> Timeline=[];
    }
    private readonly object gate=new(),reportGate=new();
    private readonly List<Session> sessions=[];
    private readonly Dictionary<string,Session> current=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,GameExitReport> latest=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> diskChecked=new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> reports=[];
    private readonly CancellationTokenSource lifetime=new();
    private readonly Func<GameInfo,DateTime,DateTime,object?>? collectLogs;
    private readonly Task worker;
    private bool disposed;
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    internal static string ReportsRoot=>Path.Combine(AppDataPaths.Root,"diagnostics","game-exits");
    internal GameCrashMonitor(Func<GameInfo,DateTime,DateTime,object?>? collectLogs=null)
    {this.collectLogs=collectLogs;worker=Task.Run(RunAsync);}

    internal string GetReportsDirectory(string gamePath)=>Path.Combine(ReportsRoot,Key(gamePath));
    internal bool IsTracking(string gamePath)
    {lock(gate)return current.TryGetValue(Key(gamePath),out var session)&&!session.Done;}
    internal void Track(GameInfo game,int pid,GameLaunchMode mode)
    {
        string key=Key(game.ExePath);var now=DateTime.UtcNow;
        // Acquire the limited-query/synchronize handle before a short-lived
        // launcher exits; resolving all known runtime paths happens off the UI.
        var initial=Open(pid,true);
        var session=new Session{Game=game,Key=key,Id=now.ToString("yyyyMMdd'T'HHmmssfff'Z'")+"-"+Guid.NewGuid().ToString("N"),Mode=mode,StartedUtc=initial?.StartedUtc??now,TrackedUtc=now};
        if(initial is not null)session.Processes.Add(initial);
        AddEvent(session,"开始监测",initial is null?"未能保有启动进程句柄；将尝试确认实际运行进程。":"已保有启动进程句柄。");
        lock(gate)
        {
            if(disposed){initial?.Dispose();return;}
            if(current.TryGetValue(key,out var previous)&&!previous.Done&&previous.Processes.Any(p=>p.Initial&&p.Pid==pid&&!p.Exited&&p.StartedUtc==initial?.StartedUtc))
            {initial?.Dispose();return;}
            // A rapid restart can happen before the previous run's quiet/handoff
            // grace expires. Keep its handles, but stop discovery at this run's
            // creation time and release candidates already adopted across it.
            foreach(var older in sessions.Where(s=>s.Key==key&&!s.Done))
            {
                if(older.StartedUtc<=session.StartedUtc)BoundDiscovery(older,session.StartedUtc);
                else BoundDiscovery(session,older.StartedUtc);
            }
            sessions.Add(session);current[key]=session;
        }
    }
    internal void Update(string gamePath,GameCrashTranslationState state)
    {
        var safe=new GameCrashTranslationState(state.Requested,state.Connected,Safe(state.Status,160));
        lock(gate)if(current.TryGetValue(Key(gamePath),out var session)&&!session.Done&&session.State!=safe)
        {
            bool disconnected=session.State.Connected&&!safe.Connected;
            session.State=safe;AddEvent(session,disconnected?"翻译连接断开":"状态变化",safe.Status);
        }
    }
    internal void MarkExpectedExit(string gamePath,string reason)
    {
        lock(gate)if(current.TryGetValue(Key(gamePath),out var session)&&!session.Done)
        {session.Expected=true;session.ExpectedReason=Safe(reason,80);session.LastExitRequestUtc=DateTime.UtcNow;session.LastExitRequestReason=session.ExpectedReason;AddEvent(session,"已请求主动退出",session.ExpectedReason);}
    }
    internal void ClearExpectedExit(string gamePath)
    {
        lock(gate)if(current.TryGetValue(Key(gamePath),out var session)&&!session.Done&&session.Expected)
        {session.Expected=false;session.ExpectedReason="";AddEvent(session,"取消主动退出标记","随后退出仍按实际退出码记录。");}
    }
    internal GameExitReport? Latest(string gamePath)
    {
        string key=Key(gamePath);
        lock(gate)
        {
            if(latest.TryGetValue(key,out var report))return report;
            if(!disposed&&diskChecked.Add(key))reports.Add(Task.Run(()=>LoadLatest(key)));
            return null;
        }
    }
    private async Task RunAsync()
    {
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                Session[] batch;lock(gate)batch=sessions.Where(s=>!s.Done).ToArray();
                foreach(var session in batch)
                    try{Poll(session);}catch(Exception ex)when(ex is not OutOfMemoryException)
                    {lock(gate)if(!session.Done&&!session.ReadLimited){session.ReadLimited=true;AddEvent(session,"监测读取受限",ex.GetType().Name);}}
                lock(gate){sessions.RemoveAll(s=>s.Done);reports.RemoveAll(t=>t.IsCompleted);}
                await Task.Delay(500,lifetime.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
    }
    private void Poll(Session session)
    {
        if(session.Paths is null)
        {
            string[] paths;
            try{paths=GameRuntimeExecutables.Resolve(session.Game.ExePath).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();}
            catch{paths=[Path.GetFullPath(session.Game.ExePath)];}
            lock(gate)
            {
                if(session.Done)return;session.Paths=paths;
                foreach(var p in session.Processes.Where(p=>p.StartedUtc is null||!paths.Contains(p.Path,StringComparer.OrdinalIgnoreCase)).ToArray())
                {p.Dispose();session.Processes.Remove(p);AddEvent(session,"启动进程身份未确认","初始 PID 与已知精确路径不匹配，未认领该进程。");}
                foreach(var p in session.Processes){p.Runtime=IsRuntime(session,p.Path);session.HadRuntime|=p.Runtime;}
            }
        }
        // Enumerate only expected executable basenames; accept a candidate only
        // after exact image path and process creation time are both confirmed.
        foreach(string name in session.Paths.Select(p=>Path.GetFileNameWithoutExtension(p)).Where(n=>!string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach(var candidate in Process.GetProcessesByName(name))using(candidate)
            {
                lock(gate){if(session.Done)return;if(session.Processes.Any(p=>p.Pid==candidate.Id&&!p.Exited))continue;}
                var opened=Open(candidate.Id,false);if(opened is null)continue;
                lock(gate)
                {
                    if(session.Done||opened.StartedUtc is null||opened.StartedUtc<session.StartedUtc||opened.StartedUtc>=session.DiscoveryEndUtc||!session.Paths.Contains(opened.Path,StringComparer.OrdinalIgnoreCase)
                        ||session.Processes.Any(p=>p.Pid==opened.Pid&&p.StartedUtc==opened.StartedUtc))
                    {opened.Dispose();continue;}
                    opened.Runtime=IsRuntime(session,opened.Path);session.Processes.Add(opened);session.HadRuntime|=opened.Runtime;
                    AddEvent(session,opened.Runtime?"确认实际运行进程":"确认启动进程","PID "+opened.Pid+"；"+Path.GetFileName(opened.Path));
                }
            }
        }
        GameExitReport? completed=null;
        lock(gate)
        {
            if(session.Done)return;
            if(session.Expected&&session.LastExitRequestUtc is {} requested&&(DateTime.UtcNow-requested).TotalMinutes>=2)
            {session.Expected=false;session.ExpectedReason="";AddEvent(session,"主动退出标记过期","此前请求仍保留记录；不把之后退出归因于该请求。");}
            foreach(var p in session.Processes.Where(p=>!p.Exited))
            {
                uint wait=WaitForSingleObject(p.Handle,0);
                if(wait==258)continue;
                p.Exited=true;p.EndedUtc=DateTime.UtcNow;
                if(wait==0&&GetExitCodeProcess(p.Handle,out uint exit))p.Code=unchecked((int)exit);
                if(wait==0&&GetProcessTimes(p.Handle,out _,out long exitTime,out _,out _)&&exitTime>0)p.EndedUtc=DateTime.FromFileTimeUtc(exitTime);
                AddEvent(session,p.Runtime?"运行进程退出":"启动进程退出","PID "+p.Pid+"；退出码 "+(Hex(p.Code)??"未取得"));
            }
            if(session.Processes.Any(p=>!p.Exited)){session.QuietSince=null;return;}
            session.QuietSince??=DateTime.UtcNow;
            // A launcher may exit before its runtime can be observed. A short
            // handoff window prevents treating the launcher's zero as game exit.
            double grace=session.HadRuntime?1:12;
            if((DateTime.UtcNow-session.QuietSince.Value).TotalSeconds<grace)return;
            completed=Complete(session,false);
        }
        if(completed is not null)QueueReport(session.Game,completed);
    }
    private static void BoundDiscovery(Session session,DateTime cutoff)
    {
        if(session.DiscoveryEndUtc is {} prior&&prior<=cutoff)return;
        session.DiscoveryEndUtc=cutoff;
        foreach(var process in session.Processes.Where(p=>!p.Initial&&p.StartedUtc>=cutoff).ToArray())
        {process.Dispose();session.Processes.Remove(process);}
        session.HadRuntime=session.Processes.Any(p=>p.Runtime);
        AddEvent(session,"下一次启动已登记","本次监测不再接纳下一次启动的进程。");
    }
    private static bool IsRuntime(Session session,string path)
    {
        var paths=session.Paths??[];
        bool hasSeparate=paths.Any(p=>!string.Equals(p,Path.GetFullPath(session.Game.ExePath),StringComparison.OrdinalIgnoreCase));
        return paths.Contains(path,StringComparer.OrdinalIgnoreCase)&&(!hasSeparate||!string.Equals(path,Path.GetFullPath(session.Game.ExePath),StringComparison.OrdinalIgnoreCase));
    }
    private GameExitReport Complete(Session session,bool monitoringStopped)
    {
        session.Done=true;DateTime recorded=DateTime.UtcNow;
        DateTime ended=monitoringStopped?recorded:session.Processes.Where(p=>p.Exited&&p.EndedUtc is not null).Select(p=>p.EndedUtc!.Value).DefaultIfEmpty(recorded).Max();
        // Names sort by the report's end time, even for a long-running game.
        session.Id=recorded.ToString("yyyyMMdd'T'HHmmssfff'Z'")+"-"+Guid.NewGuid().ToString("N");
        var exited=session.Processes.Where(p=>p.Runtime&&p.Exited).OrderBy(p=>p.EndedUtc).ToArray();
        var principal=exited.LastOrDefault(p=>p.Code is not null&&p.Code!=0)??exited.LastOrDefault();
        int? code=monitoringStopped?null:principal?.Code;
        string outcome=monitoringStopped?"monitoring-stopped":code is null?"exit-unconfirmed":code!=0?"nonzero-exit":"zero-exit";
        string summary=outcome switch
        {
            "monitoring-stopped"=>"软件监测已停止，未确认游戏退出。",
            "nonzero-exit"=>"检测到异常退出（"+Hex(code)+"）；仅凭退出码不能确定是否由翻译组件导致。",
            "zero-exit"=>"检测到退出码 0；不能据此确认属于正常退出。",
            _=>"未取得实际游戏进程的退出码，退出原因未确定。"
        };
        if(session.Expected)summary+=" 此前已请求主动退出（"+session.ExpectedReason+"）。";
        else if(session.LastExitRequestUtc is not null)summary+=" 监测期间曾请求退出，当前预期标记已取消或过期。";
        AddEvent(session,monitoringStopped?"监测停止":"生成退出记录",summary);
        string directory=Path.Combine(ReportsRoot,session.Key);
        var report=new GameExitReport{SessionId=session.Id,GamePathHash=session.Key,GameExecutable=Path.GetFileName(session.Game.ExePath),GameName=Safe(session.Game.Name,80),Engine=Safe(session.Game.Engine,40),FusionVersion=BuildIdentity.BuildVersion,LaunchMode=session.Mode,StartedUtc=session.StartedUtc,EndedUtc=ended,RecordedUtc=recorded,Expected=session.Expected,ExpectedReason=session.ExpectedReason,LastExitRequestUtc=session.LastExitRequestUtc,LastExitRequestReason=session.LastExitRequestReason,Outcome=outcome,Summary=summary,RuntimePid=principal?.Pid,ExitCode=code,ExitCodeHex=Hex(code),Processes=session.Processes.Select(p=>new GameCrashProcessExit(p.Pid,Path.GetFileName(p.Path),p.StartedUtc,p.EndedUtc,p.Initial,p.Runtime,p.Exited,p.Code,Hex(p.Code))).ToArray(),Timeline=session.Timeline.ToArray(),LogsPending=!monitoringStopped&&collectLogs is not null,JsonPath=Path.Combine(directory,session.Id+".json"),MarkdownPath=Path.Combine(directory,session.Id+".md")};
        foreach(var p in session.Processes)p.Dispose();
        return report;
    }
    private void QueueReport(GameInfo game,GameExitReport report)
    {
        // Persist the exit facts first. Enrich later so the engine can finish
        // writing its crash log, without blocking monitoring of another game.
        var task=Task.Run(async()=>
        {
            SaveReport(report);
            if(!report.LogsPending)return;
            await Task.Delay(2000).ConfigureAwait(false);
            try
            {
                bool newer=HasLaterLaunch(report);
                var logs=newer?null:collectLogs?.Invoke(game,report.StartedUtc,report.EndedUtc);
                // A new run may start while the collector reads. Discard that
                // result as well, rather than mixing generations in a report.
                if(newer||HasLaterLaunch(report))report=report with{Logs=JsonSerializer.SerializeToElement(new{Note="下一次启动已发生，仅保留退出事实，避免混入后次运行的日志。"}),LogsPending=false};
                else
                {
                    var data=JsonSerializer.SerializeToElement(logs);
                    if(data.GetRawText().Length<=512000)report=report with{Logs=data,LogsPending=false};
                    else report=report with{Logs=JsonSerializer.SerializeToElement(new{Note="日志摘要超过报告上限，未保存正文。"}),LogsPending=false};
                }
            }
            catch(Exception ex)when(ex is not OutOfMemoryException)
            {report=report with{Logs=JsonSerializer.SerializeToElement(new{Note="日志摘要读取失败："+ex.GetType().Name}),LogsPending=false};}
            SaveReport(report);
        });
        lock(gate)reports.Add(task);
    }
    private bool HasLaterLaunch(GameExitReport report)
    {lock(gate)return current.TryGetValue(report.GamePathHash,out var active)&&active.StartedUtc>report.StartedUtc;}
    private void SaveReport(GameExitReport report)
    {
        try
        {
            lock(reportGate)
            {
                string directory=Path.Combine(ReportsRoot,report.GamePathHash);EnsureReportDirectory(directory);
                Atomic(report.JsonPath,JsonSerializer.Serialize(report,JsonOptions));Atomic(report.MarkdownPath,Markdown(report));TrimReports();
            }
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {report=report with{ReportSaveFailed=true,Summary=report.Summary+" 诊断文件保存失败（"+ex.GetType().Name+"）；本次记录暂存内存。"};}
        lock(gate)if(!latest.TryGetValue(report.GamePathHash,out var prior)||prior.EndedUtc<=report.EndedUtc)latest[report.GamePathHash]=report;
    }
    private void LoadLatest(string key)
    {
        try
        {
            string directory=Path.Combine(ReportsRoot,key);if(!Directory.Exists(directory))return;ValidateReportPath(directory);
            foreach(var path in Directory.EnumerateFiles(directory,"*.json").Where(OwnedFile).OrderByDescending(Path.GetFileName).Take(5))
            {
                try
                {
                    ValidateReportPath(path);if(new FileInfo(path).Length>1024*1024)continue;
                    var report=JsonSerializer.Deserialize<GameExitReport>(File.ReadAllText(path));
                    if(report is null||report.Schema!=1||report.GamePathHash!=key||report.SessionId!=Path.GetFileNameWithoutExtension(path))continue;
                    if(report.LogsPending)report=report with{LogsPending=false,Logs=JsonSerializer.SerializeToElement(new{Note="上次软件退出时日志摘要尚未完成；现有退出事实已保留。"})};
                    report=report with{JsonPath=path,MarkdownPath=Path.ChangeExtension(path,".md")};
                    lock(gate)if(!latest.TryGetValue(key,out var prior)||prior.EndedUtc<report.EndedUtc)latest[key]=report;
                    return;
                }
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException){}
            }
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException){}
    }
    private static void TrimReports()
    {
        if(!Directory.Exists(ReportsRoot))return;ValidateReportPath(ReportsRoot,true);
        var files=Directory.EnumerateDirectories(ReportsRoot).Where(d=>Regex.IsMatch(Path.GetFileName(d),"^[A-F0-9]{64}$")&&!IsLink(d))
            .SelectMany(d=>Directory.EnumerateFiles(d,"*.json")).Where(p=>OwnedFile(p)&&!IsLink(p)).OrderByDescending(Path.GetFileName).Skip(50).ToArray();
        foreach(string file in files){ValidateReportPath(file);File.Delete(file);string markdown=Path.ChangeExtension(file,".md");if(File.Exists(markdown)&&!IsLink(markdown)){ValidateReportPath(markdown);File.Delete(markdown);}}
    }
    private static bool OwnedFile(string path)=>Regex.IsMatch(Path.GetFileName(path),@"^\d{8}T\d{9}Z-[a-f0-9]{32}\.json$");
    private static bool IsLink(string path)=>(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;
    private static void EnsureReportDirectory(string directory)
    {
        string full=Path.GetFullPath(directory);ValidateReportPath(full);
        Directory.CreateDirectory(full);
        ValidateReportPath(full);
    }
    private static void ValidateReportPath(string path,bool allowRoot=false)
    {
        string root=Path.GetFullPath(ReportsRoot),full=Path.GetFullPath(path);
        if(!(allowRoot&&string.Equals(full,root,StringComparison.OrdinalIgnoreCase))&&!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Report path outside owned directory.");
        for(string? parent=full;parent is not null;parent=Path.GetDirectoryName(parent))
        {
            try{if(IsLink(parent))throw new IOException("Report path contains a link.");}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
    }
    private static void Atomic(string path,string text)
    {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            ValidateReportPath(path);ValidateReportPath(temporary);
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(text);
            ValidateReportPath(path);ValidateReportPath(temporary);File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary)){ValidateReportPath(temporary);File.Delete(temporary);}}
    }
    internal static string Markdown(GameExitReport report)
    {
        var text=new StringBuilder().AppendLine("# 游戏退出记录").AppendLine().AppendLine(report.Summary).AppendLine()
            .AppendLine("- 游戏："+report.GameName+"（"+report.GameExecutable+"）").AppendLine("- 引擎："+report.Engine)
            .AppendLine("- Fusion 版本："+report.FusionVersion).AppendLine("- 启动方式："+(report.LaunchMode switch{GameLaunchMode.Ordinary=>"普通启动",GameLaunchMode.Translation=>"翻译启动",GameLaunchMode.Modification=>"修改功能启动",_=>"未确定"}))
            .AppendLine("- 监测开始（UTC）："+report.StartedUtc.ToString("O")).AppendLine("- 记录结束（UTC）："+report.EndedUtc.ToString("O"))
            .AppendLine("- 报告生成（UTC）："+(report.RecordedUtc==default?report.EndedUtc:report.RecordedUtc).ToString("O"))
            .AppendLine("- 实际运行 PID："+(report.RuntimePid?.ToString()??"未确认")).AppendLine("- 退出码："+(report.ExitCodeHex??"未取得"))
            .AppendLine("- 游戏路径标识："+report.GamePathHash).AppendLine().AppendLine("## 状态时间线").AppendLine();
        foreach(var entry in report.Timeline)text.AppendLine("- "+entry.AtUtc.ToString("O")+" · "+entry.Event+" · 请求翻译="+entry.TranslationRequested+"，组件连接="+entry.Connected+" · "+entry.Status);
        text.AppendLine().AppendLine("## 进程记录").AppendLine();
        foreach(var p in report.Processes)text.AppendLine("- PID "+p.Pid+" · "+p.Executable+" · "+(p.Runtime?"实际运行进程":"启动/待确认进程")+" · "+(p.ExitObserved?"退出码 "+(p.ExitCodeHex??"未取得"):"结束监测时未确认退出"));
        text.AppendLine().AppendLine("## 日志摘要").AppendLine();
        if(report.LogsPending)text.AppendLine("正在等待引擎完成退出日志写入。");
        else if(report.Logs is {} logs&&logs.ValueKind==JsonValueKind.Array)
        {
            if(logs.GetArrayLength()==0)text.AppendLine("没有找到本次会话的日志摘要。");
            foreach(var entry in logs.EnumerateArray())
            {
                static string Value(JsonElement item,string key)=>item.ValueKind==JsonValueKind.Object&&item.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
                text.AppendLine("### "+Safe(Value(entry,"Kind"),80)+" · "+Safe(Value(entry,"Name"),120)).AppendLine();
                string body=Value(entry,"Text"),note=Value(entry,"Note");
                if(body.Length>0)
                {
                    // Log text is already sanitized by the collector. A longer
                    // fence preserves literal backticks and multiline content.
                    int ticks=Regex.Matches(body,"`+").Select(m=>m.Length).DefaultIfEmpty(0).Max();string fence=new('`',Math.Max(3,ticks+1));
                    text.AppendLine(fence).AppendLine(body.TrimEnd('\r','\n')).AppendLine(fence).AppendLine();
                }
                if(note.Length>0)text.AppendLine(Safe(note,600)).AppendLine();
                if(entry.ValueKind==JsonValueKind.Object&&entry.TryGetProperty("Truncated",out var truncated)&&truncated.ValueKind==JsonValueKind.True)text.AppendLine("此摘要已截断，仅保留受限长度的内容。").AppendLine();
            }
        }
        else if(report.Logs is {} detail&&detail.ValueKind==JsonValueKind.Object&&detail.TryGetProperty("Note",out var note)&&note.ValueKind==JsonValueKind.String)text.AppendLine(Safe(note.GetString(),600));
        else text.AppendLine("本记录没有引擎日志摘要。");
        text.AppendLine().AppendLine("退出码、连接中断或日志片段单独不能证明故障由翻译组件导致。监测停止不表示游戏已关闭。");
        return text.ToString();
    }
    private static string Key(string path)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
    private static string? Hex(int? code)=>code is null?null:"0x"+unchecked((uint)code.Value).ToString("X8");
    private static string Safe(string? value,int limit)
    {
        string result=Regex.Replace(value??"",@"[\r\n\t]+"," ");
        result=Regex.Replace(result,@"(?i)(?:bearer\s+\S+|sk-[a-z0-9_-]{8,}|(?:api[-_ ]?key|authorization|secret|token|profilekey)\s*[:=]\s*\S+)","[已省略凭据]");
        result=Regex.Replace(result,@"(?i)(?:[a-z]:[\\/]|\\\\)[^,;，；\r\n]*","[已省略路径]");
        return result.Length<=limit?result:result[..limit]+"…";
    }
    private static void AddEvent(Session session,string name,string status)
    {
        session.Timeline.Add(new(DateTime.UtcNow,name,session.State.Requested,session.State.Connected,Safe(status,240)));
        if(session.Timeline.Count>64)session.Timeline.RemoveAt(1);
    }
    private static ProcessHandle? Open(int pid,bool initial)
    {
        if(pid<=0)return null;SafeProcessHandle handle=OpenProcess(0x101000,false,pid);
        if(handle.IsInvalid){handle.Dispose();return null;}
        try
        {
            var path=new StringBuilder(32768);uint length=32768;
            if(!QueryFullProcessImageName(handle,0,path,ref length)){handle.Dispose();return null;}
            DateTime? started=GetProcessTimes(handle,out long creation,out _,out _,out _)?DateTime.FromFileTimeUtc(creation):null;
            return new(){Handle=handle,Pid=pid,Path=Path.GetFullPath(path.ToString()),StartedUtc=started,Initial=initial};
        }
        catch{handle.Dispose();return null;}
    }
    public void Dispose()
    {
        List<GameExitReport> remaining=[];Task[] pending;
        lock(gate)
        {
            if(disposed)return;disposed=true;lifetime.Cancel();
            foreach(var session in sessions.Where(s=>!s.Done))remaining.Add(Complete(session,true));
            pending=reports.ToArray();
        }
        foreach(var report in remaining)SaveReport(report);
        try{Task.WaitAll(pending.Append(worker).ToArray(),1000);}catch(AggregateException){}
        // A delayed report task may still finish after Dispose. It has only
        // immutable report data; every process handle was already released.
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int id);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetExitCodeProcess(SafeProcessHandle handle,out uint code);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(SafeProcessHandle handle,out long creation,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageName(SafeProcessHandle handle,uint flags,StringBuilder path,ref uint length);
}
