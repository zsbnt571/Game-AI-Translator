using System.Text.Json;
namespace ScreenshotTranslationUiTester;

// A reset starts a new per-game machine-cache generation. Old caches remain
// recoverable, but local, prepared, shared and legacy copies cannot refill it.
// Human translations, terms, game files, save data and other copies are separate.
internal static class TranslationCacheReset
{
    internal sealed record Record(int Version,string Generation,DateTimeOffset At,string Build);
    private static string Marker(string exe)
    {
        string root=AppDataPaths.ValidateExplicitRoot(RpgMakerDataAdapter.Storage(exe));
        string path=Path.Combine(root,"machine-cache-reset.json");
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new IOException("译文重置记录包含链接，未读取或覆盖。");
        return path;
    }
    internal static Record? Read(string exe)
    {
        string path=Marker(exe);if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>4096)throw new IOException("译文重置记录无效，未重新使用旧缓存。");
        Record? record;
        try{record=JsonSerializer.Deserialize<Record>(File.ReadAllText(path));}
        catch(JsonException ex){throw new IOException("译文重置记录无法读取，未重新使用旧缓存。",ex);}
        if(record is null||record.Version!=1||!Guid.TryParseExact(record.Generation,"N",out _)||string.IsNullOrEmpty(record.Build))
            throw new IOException("译文重置记录无效，未重新使用旧缓存。");
        return record;
    }
    internal static string DirectoryFor(string exe,TranslationCacheScope scope=TranslationCacheScope.Native)
    {
        string path=Path.Combine(TranslationRoute.DataRoot(exe),"translations");
        if(Read(exe) is {} reset)path=Path.Combine(path,"fresh-"+reset.Generation);
        if(scope==TranslationCacheScope.Screen)path=Path.Combine(path,"screen");
        return AppDataPaths.ValidateExplicitRoot(path);
    }
    internal static Record Reset(string exe)
    {
        _=Read(exe); // Refuse damaged/foreign markers instead of hiding them.
        var record=new Record(1,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,
            typeof(TranslationCacheReset).Assembly.GetName().Version?.ToString()??"unknown");
        PortableDataStorage.WriteJson(Marker(exe),record);
        if(Read(exe)!=record)throw new IOException("译文重置记录回读不一致。");
        return record;
    }
}
