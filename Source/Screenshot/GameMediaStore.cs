using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed record GameMediaEntry(string FileName,string Kind,DateTimeOffset CreatedUtc,int Width,int Height,
    double DurationSeconds=0,string? ThumbnailFile=null,string FinishReason="manual");

internal sealed class GameMediaStore
{
    internal string DirectoryPath=>ScreenshotDirectory;
    internal string ScreenshotDirectory {get;}
    internal string VideoDirectory {get;}
    private readonly string legacyDirectory;
    private readonly Dictionary<string,string> knownFiles=new(StringComparer.OrdinalIgnoreCase);
    internal GameMediaStore(string gameId,string? dataRoot=null,string? gameName=null)
    {
        if(!Guid.TryParseExact(gameId,"N",out var id))throw new ArgumentException("游戏标识无效。",nameof(gameId));
        var root=Path.GetFullPath(dataRoot??AppDataPaths.Root);
        legacyDirectory=Path.Combine(root,"game-media",id.ToString("N"));CheckPath(legacyDirectory);
        ScreenshotDirectory=GameFolder(root,"screenshot",id,gameName);
        VideoDirectory=GameFolder(root,"video",id,gameName);
    }
    private static string GameFolder(string root,string kind,Guid id,string? gameName)
    {
        var parent=Path.Combine(root,kind);CheckPath(parent);Directory.CreateDirectory(parent);
        // The marker binds a readable folder to its library identity, including after rename/relocation.
        foreach(var folder in Directory.EnumerateDirectories(parent))
        {
            CheckPath(folder);var marker=Path.Combine(folder,".fusion-game-id");CheckPath(marker);
            if(File.Exists(marker)&&new FileInfo(marker).Length<=128&&File.ReadAllText(marker).Trim()==id.ToString("N"))return folder;
        }
        var name=new string((gameName??"未命名游戏").Select(c=>char.IsControl(c)||Path.GetInvalidFileNameChars().Contains(c)?'_':c).ToArray()).Trim().TrimEnd('.');
        if(name.Length>64)name=name[..64].TrimEnd('.',' ');
        if(string.IsNullOrWhiteSpace(name))name="未命名游戏";
        var stem=name.Split('.')[0].ToUpperInvariant();
        if(new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(stem))name="游戏_"+name;
        var selected=Path.Combine(parent,name);
        if(Directory.Exists(selected)||File.Exists(selected))selected=Path.Combine(parent,name+" ("+id.ToString("N")[..8]+")");
        for(int suffix=2;Directory.Exists(selected)||File.Exists(selected);suffix++)selected=Path.Combine(parent,name+" ("+id.ToString("N")[..8]+"-"+suffix+")");
        CheckPath(selected);Directory.CreateDirectory(selected);CheckPath(selected);
        using(var file=new FileStream(Path.Combine(selected,".fusion-game-id"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
        using(var writer=new StreamWriter(file)){writer.Write(id.ToString("N"));writer.Flush();file.Flush(true);}
        return selected;
    }
    internal static void CheckPath(string path)
    {
        for(var current=Path.GetFullPath(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("媒体目录不能指向其他位置的链接。");
    }
    internal string FilePath(string name)
    {
        if(string.IsNullOrWhiteSpace(name)||Path.GetFileName(name)!=name||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)
            throw new IOException("媒体文件名无效。");
        var parent=name.StartsWith("video-",StringComparison.Ordinal)||name.StartsWith("preview-",StringComparison.Ordinal)?VideoDirectory:ScreenshotDirectory;
        var path=Path.Combine(parent,name);CheckPath(path);return path;
    }
    internal string ExistingFilePath(string name)
    {
        _=FilePath(name);
        var path=knownFiles.GetValueOrDefault(name)??FilePath(name);CheckPath(path);return path;
    }
    internal string NewName(string kind,string extension)=>kind+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")[..8]+extension;
    internal IReadOnlyList<GameMediaEntry> Read()
    {
        var entries=new List<GameMediaEntry>();knownFiles.Clear();
        foreach(var folder in new[]{ScreenshotDirectory,VideoDirectory,legacyDirectory})
        {
        CheckPath(folder);if(!Directory.Exists(folder))continue;
        foreach(var path in Directory.EnumerateFiles(folder,"*.json").Take(10000))
        {
            try
            {
                CheckPath(path);if(new FileInfo(path).Length>8192)continue;
                var entry=JsonSerializer.Deserialize<GameMediaEntry>(File.ReadAllText(path));
                if(entry is null||!Valid(entry)||Path.GetFileName(path)!=entry.FileName+".json")continue;
                _=FilePath(entry.FileName);var media=Path.Combine(folder,entry.FileName);CheckPath(media);
                if(!File.Exists(media)||knownFiles.ContainsKey(entry.FileName))continue;
                entries.Add(entry);knownFiles[entry.FileName]=media;
                if(entry.ThumbnailFile is {} thumbnail){var thumb=Path.Combine(folder,thumbnail);CheckPath(thumb);if(File.Exists(thumb))knownFiles[thumbnail]=thumb;}
            }
            catch(Exception ex)when(ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException){}
        }
        }
        return entries.OrderByDescending(e=>e.CreatedUtc).ToArray();
    }
    private bool Valid(GameMediaEntry entry)=>!string.IsNullOrEmpty(entry.FileName)&&entry.Kind is "screenshot" or "video"&&entry.Width>0&&entry.Height>0&&
        entry.Width<=8192&&entry.Height<=8192&&double.IsFinite(entry.DurationSeconds)&&entry.DurationSeconds>=0&&
        Path.GetExtension(entry.FileName).Equals(entry.Kind=="video"?".mp4":".png",StringComparison.OrdinalIgnoreCase)&&
        (entry.ThumbnailFile is null||Path.GetExtension(entry.ThumbnailFile).Equals(".png",StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(entry.ThumbnailFile)==entry.ThumbnailFile);
    internal void Commit(GameMediaEntry entry)
    {
        if(!Valid(entry)||!File.Exists(FilePath(entry.FileName)))throw new IOException("媒体文件尚未保存完成。");
        var metadata=FilePath(entry.FileName+".json");var temp=FilePath(entry.FileName+".json.writing");
        using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {JsonSerializer.Serialize(stream,entry);stream.Flush(true);}
        File.Move(temp,metadata,false);
    }
    internal GameMediaEntry SaveScreenshot(Bitmap image)
    {
        var name=NewName("screenshot",".png");var file=FilePath(name);var temp=FilePath(name+".writing");
        using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {image.Save(stream,ImageFormat.Png);stream.Flush(true);}
        File.Move(temp,file,false);var entry=new GameMediaEntry(name,"screenshot",DateTimeOffset.UtcNow,image.Width,image.Height);
        Commit(entry);return entry;
    }
}
