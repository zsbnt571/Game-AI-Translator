using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Preserve the stored width field for older libraries; use four stable density levels.
internal static class LibraryCoverSizes
{
    internal static readonly int[] Widths=[120,158,220,300];
    internal static readonly int[] ShelfWidths=[172,212,260,308];
    internal static readonly int[] ListWidths=[240,300,360,440];
    internal static readonly string[] Labels=["小","标准","大","特大"];
    internal static int IndexFor(int width)=>Enumerable.Range(0,Widths.Length).MinBy(i=>Math.Abs((long)Widths[i]-width));
    internal static int Normalize(int width)=>Widths[IndexFor(width)];
}

internal enum ArtworkSource { Automatic, Online }
internal sealed record LibraryFolder(string Id,string Name,string? Root=null,string[]? Games=null,string[]? ExcludedGames=null);
internal sealed record LibraryPreferences(ArtworkSource IconSource=ArtworkSource.Automatic,
    ArtworkSource CoverSource=ArtworkSource.Automatic, bool Portrait=true, int CoverWidth=158,
    bool Compact=true, string[]? Categories=null, string RecentSection="recent", string MainSection="all",bool AutomaticChineseNames=true,LibraryFolder[]? Folders=null,int SidebarWidth=206);

internal static class LibraryPreferencesStore
{
    internal static string FilePath=>Path.Combine(AppDataPaths.Root,"library-preferences.json");
    internal static LibraryPreferences Load()
    {
        if(!File.Exists(FilePath))return new();
        if(new FileInfo(FilePath).Length>4194304)throw new InvalidDataException("游戏库设置文件过大，原文件已保留。");
        var value=JsonSerializer.Deserialize<LibraryPreferences>(File.ReadAllText(FilePath))??throw new InvalidDataException("游戏库设置无法读取。");
        return value with {SidebarWidth=Math.Clamp(value.SidebarWidth,190,460),CoverWidth=LibraryCoverSizes.Normalize(value.CoverWidth),Categories=(value.Categories??[]).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct().Take(100).ToArray(),RecentSection=LibrarySections.Normalize(value.RecentSection,"recent"),MainSection=LibrarySections.Normalize(value.MainSection,"all")};
    }
    internal static void Save(LibraryPreferences value)=>PortableDataStorage.WriteJson(FilePath,value);
}
