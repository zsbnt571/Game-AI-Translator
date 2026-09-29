using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed record RecentGame(string Id,string Name,string ExePath,DateTimeOffset SelectedAt,
    string? Icon=null,string? Cover=null,string? IconStamp=null,
    bool Saved=false,DateTimeOffset? SavedAt=null,DateTimeOffset? RecentAt=null,
    bool IconManual=false,bool CoverManual=false,string NameSource="file",string IconOrigin="automatic",string CoverOrigin="automatic",
    ArtworkSource? IconPreference=null,ArtworkSource? CoverPreference=null,long ArtworkVersion=0);

// Separate from OCR history, settings, credentials and installation ledgers.
internal sealed class RecentGameStore
{
    private sealed record Document(int Version,List<RecentGame> Games,int RecentLimit=20);
    private readonly object gate=new();
    private readonly string root,file;
    private List<RecentGame> games=[];
    private sealed record QualityCache(string Path,long Length,long Written,CoverQualityResult Result);
    private readonly Dictionary<string,QualityCache> coverQuality=new(StringComparer.Ordinal);
    private readonly Dictionary<string,long> identityVersions=new(StringComparer.Ordinal);
    internal string Notice {get;private set;}="";
    internal bool Writable {get;private set;}=true;
    internal int RecentLimit {get;private set;}=20;
    internal event Action? Changed;
    internal RecentGameStore()
    {
        root=Path.Combine(AppDataPaths.Root,"recent-games-v1");file=Path.Combine(root,"games.json");
        if(!File.Exists(file))return;
        try
        {
            if(new FileInfo(file).Length>8_000_000)throw new InvalidDataException();
            var doc=JsonSerializer.Deserialize<Document>(File.ReadAllText(file));
            if(doc is null||doc.Version is not (1 or 2 or 3)||doc.Games is null||doc.Games.Count>10000||doc.RecentLimit is <1 or >1000)throw new InvalidDataException();
            var ids=new HashSet<string>(StringComparer.Ordinal);var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var item in doc.Games)
            {
                if(item is null||!Guid.TryParseExact(item.Id,"N",out _)||string.IsNullOrWhiteSpace(item.Name)||!Path.IsPathFullyQualified(item.ExePath)
                    ||!item.ExePath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||!ids.Add(item.Id)||!paths.Add(Normalize(item.ExePath))
                    ||!ValidImageName(item.Id,item.Icon,false)||!ValidImageName(item.Id,item.Cover,true))
                {Writable=false;continue;}
                var g=item with{ExePath=Normalize(item.ExePath)};
                // Older versions did not record manual ownership. Preserve every existing cover
                // until the user explicitly chooses an automatic source for that game.
                if(doc.Version<3&&g.Cover is not null)g=g with{CoverManual=true,CoverOrigin="preserved"};
                if(doc.Version==1)g=g with{Saved=false,SavedAt=null,RecentAt=g.SelectedAt==default?null:g.SelectedAt};
                games.Add(g);
            }
            RecentLimit=doc.Version==1?20:doc.RecentLimit;
            if(!Writable){Notice="部分游戏记录无效；原文件已保留，暂不覆盖。有效记录仍可选择。";return;}
            if(doc.Version==1)
            {
                var backup=Path.Combine(root,"games.v1-original.json");
                if(!File.Exists(backup))File.Copy(file,backup);
                Commit(Trim(games,RecentLimit),RecentLimit);
            }
        }
        catch{Writable=false;Notice="游戏记录无法读取或升级；原文件已保留，暂不覆盖。仍可直接选择 EXE。";}
    }
    private static bool ValidImageName(string id,string? name,bool cover)
    {
        if(name is null)return true;
        if(name==id+(cover?".cover.jpg":".icon.png"))return true;
        var prefix=id+".";var suffix=cover?".cover.jpg":".icon.png";
        return name.Length>prefix.Length+suffix.Length&&name.StartsWith(prefix,StringComparison.Ordinal)&&name.EndsWith(suffix,StringComparison.Ordinal)
            &&Guid.TryParseExact(name[prefix.Length..^suffix.Length],"N",out _);
    }
    internal static string Normalize(string path)=>Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar);
    internal RecentGame[] Snapshot(bool saved=false)
    {lock(gate)return saved?games.Where(x=>x.Saved).OrderBy(x=>x.SavedAt??DateTimeOffset.MinValue).ThenBy(x=>x.Id).ToArray():
        games.Where(x=>x.RecentAt.HasValue).OrderByDescending(x=>x.RecentAt).ThenBy(x=>x.Id).Take(RecentLimit).ToArray();}
    internal RecentGame[] SnapshotAll(){lock(gate)return games.OrderByDescending(x=>x.RecentAt??x.SavedAt??x.SelectedAt).ThenBy(x=>x.Id).ToArray();}
    internal RecentGame? Find(string path){var p=Normalize(path);lock(gate)return games.FirstOrDefault(x=>StringComparer.OrdinalIgnoreCase.Equals(x.ExePath,p));}
    private static List<RecentGame> Trim(List<RecentGame> list,int limit)
    {
        var keep=list.Where(x=>x.RecentAt.HasValue).OrderByDescending(x=>x.RecentAt).ThenBy(x=>x.Id).Take(limit).Select(x=>x.Id).ToHashSet();
        // Dormant identity/media can remain for current selection; never restore a trimmed recent timestamp.
        return list.Select(x=>x.RecentAt.HasValue&&!keep.Contains(x.Id)?x with{RecentAt=null}:x).ToList();
    }
    private void Commit(List<RecentGame> next,int? limit=null)
    {
        if(!Writable)throw new InvalidOperationException(Notice);
        Directory.CreateDirectory(root);var temp=Path.Combine(root,Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(stream,new Document(3,next,limit??RecentLimit),new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}
            if(File.Exists(file))File.Replace(temp,file,Path.Combine(root,"games.previous.json"));else File.Move(temp,file);
            games=next;RecentLimit=limit??RecentLimit;
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    internal RecentGame Select(string name,string path,string? relinkId=null,bool usage=true)
    {
        path=Normalize(path);
        lock(gate)
        {
            var same=games.FirstOrDefault(x=>StringComparer.OrdinalIgnoreCase.Equals(x.ExePath,path));
            var old=relinkId is null?same:games.FirstOrDefault(x=>x.Id==relinkId);
            if(relinkId is not null&&old is null)throw new InvalidOperationException("该游戏记录已不存在，请重新选择。");
            if(old is null&&same is null&&games.Count>=10000)throw new InvalidOperationException("游戏身份记录已达安全上限，原有记录仍保留。");
            var now=DateTimeOffset.UtcNow;RecentGame value;
            if(same is not null&&old is not null&&same.Id!=old.Id)
            {
                value=same with{Saved=same.Saved||old.Saved,SavedAt=new[]{same.SavedAt,old.SavedAt}.Min(),
                    RecentAt=usage?now:new[]{same.RecentAt,old.RecentAt}.Max()};
            }
            else
            {
                var changed=old is not null&&!StringComparer.OrdinalIgnoreCase.Equals(old.ExePath,path);
                value=new(old?.Id??Guid.NewGuid().ToString("N"),old is {NameSource:"window" or "manual"}?old.Name:name,path,usage?now:old?.SelectedAt??default,
                    changed&&old?.IconManual!=true?null:old?.Icon,old?.Cover,changed?null:old?.IconStamp,
                    old?.Saved??false,old?.SavedAt,usage?now:old?.RecentAt,
                    old?.IconManual??false,old?.CoverManual??false,old?.NameSource??"file",old?.IconOrigin??"automatic",old?.CoverOrigin??"automatic",old?.IconPreference,old?.CoverPreference,old?.ArtworkVersion??0);
            }
            Commit(Trim(games.Where(x=>x.Id!=value.Id&&(old is null||x.Id!=old.Id)).Append(value).ToList(),RecentLimit));
            if(old is not null&&!StringComparer.OrdinalIgnoreCase.Equals(old.ExePath,path))
                identityVersions[old.Id]=identityVersions.GetValueOrDefault(old.Id)+1;
            Changed?.Invoke();return value;
        }
    }
    internal void SetSaved(string id,bool saved)
    {lock(gate){Commit(games.Select(x=>x.Id==id?x with{Saved=saved,SavedAt=saved?(x.SavedAt??DateTimeOffset.UtcNow):null}:x).ToList());Changed?.Invoke();}}
    internal void RemoveRecent(string id)
    {lock(gate){Commit(games.Select(x=>x.Id==id?x with{RecentAt=null}:x).ToList());Changed?.Invoke();}}
    internal void RemoveFromLibrary(string id)
    {lock(gate){Commit(games.Where(x=>x.Id!=id).ToList());identityVersions[id]=identityVersions.GetValueOrDefault(id)+1;Changed?.Invoke();}}
    internal void RemoveFromLibrary(IEnumerable<string> ids)
    {lock(gate){var keys=ids.ToHashSet(StringComparer.Ordinal);Commit(games.Where(x=>!keys.Contains(x.Id)).ToList());foreach(var id in keys)identityVersions[id]=identityVersions.GetValueOrDefault(id)+1;Changed?.Invoke();}}
    internal void SetRecentLimit(int limit)
    {if(limit is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(limit));lock(gate){Commit(Trim(games,limit),limit);Changed?.Invoke();}}
    private string? ImagePath(RecentGame game,bool cover)=>(cover?game.Cover:game.Icon) is { } name?Path.Combine(root,name):null;
    internal Bitmap? ReadImage(RecentGame game,bool cover)
    {
        lock(gate)
        {
            var live=games.FirstOrDefault(x=>x.Id==game.Id&&StringComparer.OrdinalIgnoreCase.Equals(x.ExePath,game.ExePath));
            if(live is null)return null;
            var path=ImagePath(live,cover);if(path is null||!File.Exists(path)||new FileInfo(path).Length>4_000_000){if(cover)coverQuality.Remove(game.Id);return null;}
            try{
                var info=new FileInfo(path);
                var cached=cover&&!live.CoverManual&&coverQuality.TryGetValue(game.Id,out var found)&&found.Path==path&&found.Length==info.Length&&found.Written==info.LastWriteTimeUtc.Ticks?found:null;
                if(cached is {Result.Usable:false})return null;
                using var stream=File.OpenRead(path);using var image=Image.FromStream(stream);
                if(image.Width>1280||image.Height>1280){if(cover)coverQuality[game.Id]=new(path,info.Length,info.LastWriteTimeUtc.Ticks,new(false,"invalid-size",image.Width,image.Height));return null;}
                if(cover&&!live.CoverManual){
                    var quality=cached?.Result??CoverQuality.Analyze(image);
                    coverQuality[game.Id]=new(path,info.Length,info.LastWriteTimeUtc.Ticks,quality);
                    if(!quality.Usable)return null;
                }
                return new Bitmap(image);
            }
            catch{
                if(cover){try{var info=new FileInfo(path);coverQuality[game.Id]=new(path,info.Length,info.LastWriteTimeUtc.Ticks,new(false,"decode-failed",0,0));}catch{coverQuality.Remove(game.Id);}}
                return null;
            }
        }
    }
    internal string CoverDiagnostic(RecentGame game)
    {lock(gate)return coverQuality.TryGetValue(game.Id,out var cached)?cached.Result.Diagnostic:"quality="+CoverQuality.RuleVersion+" reason=missing-or-unreadable";}
    internal long CoverIdentityVersion(RecentGame game){lock(gate)return identityVersions.GetValueOrDefault(game.Id);}
    internal bool SaveImage(RecentGame expected,Bitmap image,bool cover,string? stamp,CancellationToken token,Func<bool>? stillCurrent=null,long? identityVersion=null,bool manual=false,string origin="automatic")
    {
        lock(gate)
        {
            token.ThrowIfCancellationRequested();
            var live=games.FirstOrDefault(x=>x.Id==expected.Id);
            if(live is null||!StringComparer.OrdinalIgnoreCase.Equals(live.ExePath,expected.ExePath))return false;
            if(!manual&&(cover?live.CoverManual:live.IconManual))return false;
            if((cover?live.Cover:live.Icon)!=(cover?expected.Cover:expected.Icon))return false;
            if((cover?live.CoverPreference:live.IconPreference)!=(cover?expected.CoverPreference:expected.IconPreference))return false;
            if(live.ArtworkVersion!=expected.ArtworkVersion||stillCurrent?.Invoke()==false)return false;
            // Compare the captured record version too: late work cannot replace a newer manual cover.
            if(cover&&(live.Cover!=expected.Cover||stillCurrent?.Invoke()==false))return false;
            if(cover&&identityVersion.HasValue&&identityVersion!=identityVersions.GetValueOrDefault(live.Id))return false;
            if(!Writable)throw new InvalidOperationException(Notice);
            Directory.CreateDirectory(root);var name=live.Id+"."+Guid.NewGuid().ToString("N")+(cover?".cover.jpg":".icon.png");
            var dest=Path.Combine(root,name);var temp=dest+".pending";var committed=false;
            try
            {
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {image.Save(stream,cover?ImageFormat.Jpeg:ImageFormat.Png);stream.Flush(true);}
                CoverQualityResult? quality=null;
                using(var check=Image.FromFile(temp)){
                    if(check.Width!=image.Width||check.Height!=image.Height)throw new InvalidDataException("封面写入后解码校验失败");
                    if(cover&&!manual){quality=CoverQuality.Analyze(check);if(!quality.Usable)throw new InvalidDataException("封面未提交："+quality.Diagnostic);}
                }
                token.ThrowIfCancellationRequested();
                if(stillCurrent?.Invoke()==false)return false;
                File.Move(temp,dest);
                Commit(games.Select(x=>x.Id==live.Id?(cover?x with{Cover=name,CoverManual=manual,CoverOrigin=origin}:x with{Icon=name,IconStamp=stamp,IconManual=manual,IconOrigin=origin}):x).ToList());
                committed=true;
                if(quality is not null){var info=new FileInfo(dest);coverQuality[live.Id]=new(dest,info.Length,info.LastWriteTimeUtc.Ticks,quality);}
                Changed?.Invoke();CleanupUnreferencedImages();return true;
            }
            finally{try{File.Delete(temp);}catch{}if(!committed)try{File.Delete(dest);}catch{}}
        }
    }
    private void CleanupUnreferencedImages()
    {
        // Only this store's small image files, and only after a successful commit.
        // Recovery documents count as references too; unknown/corrupt backup => no cleanup.
        try
        {
            var referenced=games.SelectMany(x=>new[]{x.Icon,x.Cover}).Where(x=>x is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var backup in new[]{"games.previous.json","games.v1-original.json"})
            {
                var path=Path.Combine(root,backup);if(!File.Exists(path))continue;
                if(new FileInfo(path).Length>8_000_000)return;
                var doc=JsonSerializer.Deserialize<Document>(File.ReadAllText(path));if(doc?.Games is null)return;
                foreach(var g in doc.Games){referenced.Add(g.Icon);referenced.Add(g.Cover);}
            }
            foreach(var path in Directory.EnumerateFiles(root).Where(x=>x.EndsWith(".cover.jpg",StringComparison.OrdinalIgnoreCase)||x.EndsWith(".icon.png",StringComparison.OrdinalIgnoreCase)).Take(1000))
            {
                var name=Path.GetFileName(path);if(referenced.Contains(name)||name.Length<33||!Guid.TryParseExact(name[..32],"N",out _))continue;
                if(!ValidImageName(name[..32],name,name.EndsWith(".cover.jpg",StringComparison.OrdinalIgnoreCase)))continue;
                try{File.Delete(path);}catch{}
            }
        }
        catch{} // Cache cleanup never changes the saved record or success state.
    }
    internal Bitmap? IconFor(RecentGame game,CancellationToken token)
    {
        var current=Find(game.ExePath);
        if(current is {IconManual:true}||current?.IconOrigin=="online")return ReadImage(current!,false);
        token.ThrowIfCancellationRequested();var f=new FileInfo(game.ExePath);
        if(!f.Exists)return ReadImage(game,false);
        var stamp=f.Length+":"+f.LastWriteTimeUtc.Ticks;
        var live=Find(game.ExePath);
        if(live?.IconStamp==stamp&&ReadImage(live,false) is { } cached)return cached;
        using var icon=System.Drawing.Icon.ExtractAssociatedIcon(game.ExePath);if(icon is null)return null;
        using var bitmap=icon.ToBitmap();using var small=GameWindowCover.Thumbnail(bitmap,64,64);
        if(Writable)SaveImage(game,small,false,stamp,token);return new Bitmap(small);
    }

    internal void SetImagePreference(string id,bool cover,ArtworkSource? preference)
    {
        lock(gate){Commit(games.Select(x=>x.Id==id?(cover?x with{CoverPreference=preference,CoverManual=false,CoverOrigin="automatic",ArtworkVersion=x.ArtworkVersion+1}:x with{IconPreference=preference,IconManual=false,IconOrigin="automatic",IconStamp=null,ArtworkVersion=x.ArtworkVersion+1}):x).ToList());identityVersions[id]=identityVersions.GetValueOrDefault(id)+1;Changed?.Invoke();}
    }
    internal void SetDisplayName(string id,string name,bool manual)
    {
        name=name.Trim();if(name.Length is <1 or >160)throw new ArgumentException("游戏名称需要 1—160 个字符。");
        lock(gate){var game=games.FirstOrDefault(x=>x.Id==id);if(game is null||!manual&&game.NameSource=="manual"||game.Name==name&&game.NameSource==(manual?"manual":"window"))return;
            Commit(games.Select(x=>x.Id==id?x with{Name=name,NameSource=manual?"manual":"window"}:x).ToList());Changed?.Invoke();}
    }
}
