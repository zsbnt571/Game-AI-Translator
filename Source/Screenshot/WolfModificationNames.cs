namespace ScreenshotTranslationUiTester;

internal static class WolfModificationNames
{
    internal static Dictionary<string,string> Read(string exe)
    {
        using var resources=WolfResources.Open(exe,()=>{});
        var data=resources.Files.SingleOrDefault(f=>f.Path.Equals("Data/BasicData/CDataBase.dat",StringComparison.OrdinalIgnoreCase));
        var schema=resources.Files.SingleOrDefault(f=>f.Path.Equals("Data/BasicData/CDataBase.project",StringComparison.OrdinalIgnoreCase));
        if(data is null||schema is null)return [];
        return WolfDatabaseCatalog.Read(schema.Read(),data.Read(),()=>{},true).NumericNames;
    }
}
