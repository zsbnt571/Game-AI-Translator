namespace ScreenshotTranslationUiTester;

internal static class WolfModificationNames
{
    internal static Dictionary<string,string[]> Categories(IReadOnlyDictionary<string,string> names)
    {
        // Classification uses only schema labels. These synthetic zeros never
        // reach a write or a displayed current value; the bridge rereads every
        // requested cell from the live game before it becomes editable.
        var rows=names.Select(pair=>System.Text.Json.JsonSerializer.SerializeToElement(new
            {id=pair.Key,name=pair.Value,type="number",value=0,locked=false})).ToArray();
        var candidates=ModificationCommonCandidates.Build(rows,"wolf",names);
        var result=new Dictionary<string,string[]>(StringComparer.Ordinal);
        foreach(string scope in new[]{"common","characters","relationships"})
            result[scope]=candidates.Where(item=>scope=="common"?item.SectionKey is "" or "currency" or "recollection" or "recollection-registration":item.SectionKey==scope)
                .Select(item=>item.Source.GetProperty("id").GetString()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach(string scope in new[]{"items","weapons","armors"})
            result[scope]=names.Where(pair=>ModificationCommonCandidates.InventoryScope(pair.Value)==scope).Select(pair=>pair.Key).Order(StringComparer.Ordinal).ToArray();
        return result;
    }

    internal static Dictionary<string,string> Read(string exe)
    {
        using var resources=WolfResources.Open(exe,()=>{});
        var data=resources.Files.SingleOrDefault(f=>f.Path.Equals("Data/BasicData/CDataBase.dat",StringComparison.OrdinalIgnoreCase));
        var schema=resources.Files.SingleOrDefault(f=>f.Path.Equals("Data/BasicData/CDataBase.project",StringComparison.OrdinalIgnoreCase));
        if(data is null||schema is null)return [];
        var result=WolfDatabaseCatalog.Read(schema.Read(),data.Read(),()=>{},true,numericNameLimit:250000);
        if(result.NumericNamesTruncated)throw new ExperimentalRpgCatalog.LimitException("WOLF 数据库数值字段名称超过 250000。");
        return result.NumericNames;
    }
}
