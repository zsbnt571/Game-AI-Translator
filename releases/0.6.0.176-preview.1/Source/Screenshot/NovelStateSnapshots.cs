using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace ScreenshotTranslationUiTester;

internal sealed record NovelStateIdentity(string Engine,string GameKey,string Fingerprint);
internal sealed record NovelStateRecord(int Schema,string Id,NovelStateIdentity Identity,DateTimeOffset CreatedUtc,string Purpose,string Checksum,int Characters)
{
    public override string ToString()=>CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+(Purpose=="before-load"?" · 载入前备份":" · 手动快照");
}
internal static class NovelStateSnapshots
{
    internal const int Limit=4*1024*1024,ChunkSize=24576;
    private static string Digest(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Root(string exe)=>Path.Combine(WebGameAdapter.Storage(exe),"state-snapshots");
    private static string Folder(string exe,string id)
    {
        if(!Guid.TryParseExact(id,"N",out _))throw new IOException("状态快照编号无效。");
        string path=Path.Combine(Root(exe),id);AppDataPaths.RejectReparsePoints(path);return path;
    }
    // Include script/data contents, but exclude Fusion's generated entry points so an
    // adapter-only update does not invalidate a snapshot of an unchanged game.
    internal static NovelStateIdentity Identify(string exe)
    {
        var game=WebGameAdapter.Detect(exe);
        if(game?.Candidate!=true||game.Engine is not ("TyranoScript" or "Visual Novel Maker"))throw new IOException("此游戏尚未接入状态快照。");
        using var archive=new WebGameArchive(game.Archive);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value){byte[] b=Encoding.UTF8.GetBytes(value);hash.AppendData(BitConverter.GetBytes(b.Length));hash.AppendData(b);}
        Add(WebGameAdapter.Hash(exe));Add(game.Engine);
        long bytes=0;int count=0;
        foreach(string name in archive.Names.Where(n=>(n.StartsWith(game.Prefix+"tyrano/",StringComparison.Ordinal)||n.StartsWith(game.Prefix+"data/",StringComparison.Ordinal))&&!n.Split('/').Any(v=>v.StartsWith(".fusion-",StringComparison.Ordinal))&&
            (n.EndsWith(".ks",StringComparison.OrdinalIgnoreCase)||n.EndsWith(".js",StringComparison.OrdinalIgnoreCase)||n.EndsWith(".json",StringComparison.OrdinalIgnoreCase))&&n!= "package.json").OrderBy(n=>n,StringComparer.Ordinal))
        {
            if(++count>20000)throw new IOException("游戏状态版本校验超过文件数量限制。");
            byte[] content=archive.Read(name,32*1024*1024);bytes+=content.Length;if(bytes>256L*1024*1024)throw new IOException("游戏状态版本校验超过大小限制。");
            Add(name);hash.AppendData(BitConverter.GetBytes(content.Length));hash.AppendData(content);
        }
        if(count==0)throw new IOException("无法校验游戏状态版本。");
        return new(NovelWebCatalog.Id(game.Engine),Path.GetFileName(WebGameAdapter.Storage(exe)),Convert.ToHexString(hash.GetHashAndReset()));
    }
    private static void Validate(string raw)
    {
        if(raw.Length is <3 or >Limit)throw new IOException("状态快照为空或超过大小限制。");
        using var document=JsonDocument.Parse(raw,new JsonDocumentOptions{MaxDepth=80});
        if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new IOException("状态快照格式不符。");
        int count=0;
        void Walk(JsonElement v)
        {
            if(++count>250000)throw new IOException("状态快照超过结构限制。");
            if(v.ValueKind==JsonValueKind.Object){var keys=new HashSet<string>(StringComparer.Ordinal);foreach(var p in v.EnumerateObject()){if(p.Name is "__proto__" or "constructor" or "prototype"||!keys.Add(p.Name))throw new IOException("状态快照包含无效字段。");Walk(p.Value);}}
            else if(v.ValueKind==JsonValueKind.Array)foreach(var item in v.EnumerateArray())Walk(item);
            else if(v.ValueKind==JsonValueKind.Number&&(!v.TryGetDouble(out double number)||!double.IsFinite(number)))throw new IOException("状态快照包含无效数字。");
        }
        Walk(document.RootElement);
    }
    internal static NovelStateRecord Save(string exe,NovelStateIdentity identity,string raw,string purpose)
    {
        Validate(raw);if(identity.GameKey!=Path.GetFileName(WebGameAdapter.Storage(exe)))throw new IOException("状态快照不属于此游戏。");
        var record=new NovelStateRecord(1,Guid.NewGuid().ToString("N"),identity,DateTimeOffset.UtcNow,purpose,Digest(raw),raw.Length);
        string folder=Folder(exe,record.Id);Directory.CreateDirectory(folder);AppDataPaths.RejectReparsePoints(folder);
        PortableDataStorage.WriteJson(Path.Combine(folder,"state.json"),new{Schema=1,Data=raw});
        PortableDataStorage.WriteJson(Path.Combine(folder,"receipt.json"),record);return record;
    }
    internal static IReadOnlyList<NovelStateRecord> List(string exe)
    {
        string root=Root(exe);AppDataPaths.RejectReparsePoints(root);if(!Directory.Exists(root))return [];
        var records=new List<NovelStateRecord>();
        foreach(string directory in Directory.EnumerateDirectories(root).Take(2000))
        {
            try{string id=Path.GetFileName(directory),path=Path.Combine(Folder(exe,id),"receipt.json");AppDataPaths.RejectReparsePoints(path);
                if(!File.Exists(path)||new FileInfo(path).Length>8192)continue;
                var r=JsonSerializer.Deserialize<NovelStateRecord>(File.ReadAllText(path));
                if(r is {Schema:1}&&r.Id==id&&r.Identity?.GameKey==Path.GetFileName(WebGameAdapter.Storage(exe))&&r.Characters is >=3 and <=Limit&&File.Exists(Path.Combine(directory,"state.json")))records.Add(r);
            }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException){/* Invalid/incomplete backups are not offered for loading. */}
        }
        return records.OrderByDescending(r=>r.CreatedUtc).ToArray();
    }
    internal static string Read(string exe,NovelStateRecord record,NovelStateIdentity current)
    {
        if(record.Schema!=1||record.Identity!=current||current.GameKey!=Path.GetFileName(WebGameAdapter.Storage(exe)))throw new IOException("游戏或版本与此快照不符，未载入。");
        string path=Path.Combine(Folder(exe,record.Id),"state.json");AppDataPaths.RejectReparsePoints(path);
        if(!File.Exists(path)||new FileInfo(path).Length>32L*1024*1024)throw new IOException("状态快照文件缺失或过大。");
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        if(doc.RootElement.GetProperty("Schema").GetInt32()!=1)throw new IOException("状态快照格式不符。");
        string raw=doc.RootElement.GetProperty("Data").GetString()??"";
        if(raw.Length!=record.Characters||Digest(raw)!=record.Checksum)throw new IOException("状态快照校验失败，未载入。");Validate(raw);return raw;
    }
    private static async Task ClearAsync(IGameDataConnection connection,string? transfer)
    {
        if(transfer is null||!connection.Connected)return;
        try{using var timeout=new CancellationTokenSource(1500);await connection.RequestAsync(new(){["op"]="stateClear",["transfer"]=transfer},timeout.Token);}catch(Exception ex)when(ex is IOException or OperationCanceledException or ObjectDisposedException){}
    }
    internal static async Task<NovelStateRecord> CaptureAndSaveAsync(string exe,IGameDataConnection connection,NovelStateIdentity identity,string purpose,CancellationToken token=default)
    {
        string raw=await CaptureAsync(connection,identity.Engine,token);
        if(await Task.Run(()=>Identify(exe),token)!=identity)throw new IOException("备份期间游戏文件发生变化，未保存或载入快照。");
        return await Task.Run(()=>Save(exe,identity,raw,purpose),token);
    }
    internal static async Task<NovelStateRecord> RestoreAsync(string exe,NovelStateRecord selected,IGameDataConnection connection,Func<bool> current,Action<NovelStateRecord> backupSaved,CancellationToken token=default)
    {
        var identity=await Task.Run(()=>Identify(exe),token);
        string target=await Task.Run(()=>Read(exe,selected,identity),token);
        if(!current())throw new IOException("当前游戏选择或连接已变化，未载入快照。");
        var backup=await CaptureAndSaveAsync(exe,connection,identity,"before-load",token);backupSaved(backup);
        if(!current())throw new IOException("当前游戏选择或连接已变化，已保留备份并取消载入。");
        await LoadAsync(connection,target,identity.Engine,token);return backup;
    }
    internal static async Task<string> CaptureAsync(IGameDataConnection connection,string engine,CancellationToken token=default)
    {
        string? transfer=null;
        try
        {
            var capture=await connection.RequestAsync(new(){["op"]="stateCapture"},token);transfer=capture.GetProperty("transfer").GetString();
            int length=capture.GetProperty("length").GetInt32();
            if(string.IsNullOrEmpty(transfer)||length is <3 or >Limit||capture.GetProperty("engine").GetString()!=engine)throw new IOException("游戏返回的状态快照不符。");
            var raw=new StringBuilder(length);
            while(raw.Length<length){var part=await connection.RequestAsync(new(){["op"]="stateRead",["transfer"]=transfer,["offset"]=raw.Length},token);string text=part.GetProperty("text").GetString()??"";
                if(text.Length is <1 or >ChunkSize||raw.Length+text.Length>length)throw new IOException("状态快照传输不完整。");raw.Append(text);}
            string result=raw.ToString();Validate(result);return result;
        }
        finally{await ClearAsync(connection,transfer);}
    }
    internal static async Task LoadAsync(IGameDataConnection connection,string raw,string engine,CancellationToken token=default)
    {
        Validate(raw);string? transfer=null;
        try
        {
            var begin=await connection.RequestAsync(new(){["op"]="stateBegin",["length"]=raw.Length},token);transfer=begin.GetProperty("transfer").GetString();if(string.IsNullOrEmpty(transfer))throw new IOException("状态传输未建立。");
            for(int offset=0;offset<raw.Length;){int length=Math.Min(ChunkSize,raw.Length-offset);
                // Never split a UTF-16 surrogate pair across separately encoded JSON messages.
                if(offset+length<raw.Length&&char.IsHighSurrogate(raw[offset+length-1]))length--;
                var part=await connection.RequestAsync(new(){["op"]="stateWrite",["transfer"]=transfer,["offset"]=offset,["text"]=raw.Substring(offset,length)},token);offset+=length;
                if(part.GetProperty("received").GetInt32()!=offset)throw new IOException("状态传输未确认。");}
            var result=await connection.RequestAsync(new(){["op"]="stateLoad",["transfer"]=transfer},token);
            if(!result.GetProperty("submitted").GetBoolean()||result.GetProperty("engine").GetString()!=engine||result.GetProperty("completion").GetString()!="engine-call-returned")throw new IOException("游戏尚未确认载入请求。");
        }
        finally{await ClearAsync(connection,transfer);}
    }
}
