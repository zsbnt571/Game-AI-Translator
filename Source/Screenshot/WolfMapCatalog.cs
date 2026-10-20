namespace ScreenshotTranslationUiTester;

internal static class WolfMapCatalog
{
    internal sealed record Entry(string Path,string Origin,int? MapId=null)
    {public override string ToString()=>(MapId is {} id?$"{id:000} · ":"")+System.IO.Path.GetFileNameWithoutExtension(Path);}
    internal static Entry[] List(string exe,CancellationToken token)
    {
        using var resources=WolfResources.Open(exe,token.ThrowIfCancellationRequested);
        var mapping=new Dictionary<int,string>();
        var data=resources.Files.Where(f=>f.Path.Equals("Data/BasicData/SysDatabase.dat",StringComparison.OrdinalIgnoreCase)).ToArray();
        var schema=resources.Files.Where(f=>f.Path.Equals("Data/BasicData/SysDatabase.project",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(data.Length==1&&schema.Length==1)
            try{mapping=WolfDatabaseCatalog.Read(schema[0].Read(),data[0].Read(),token.ThrowIfCancellationRequested,true).MapFiles;}
            catch(Exception e)when(e is IOException or InvalidDataException or NotSupportedException){/* Unproven map IDs remain read-only. */}
        return resources.Files.Where(f=>f.Path.EndsWith(".mps",StringComparison.OrdinalIgnoreCase)).SelectMany(f=>
        {
            var ids=mapping.Where(p=>f.Path.Equals("Data/"+p.Value.TrimStart('/'),StringComparison.OrdinalIgnoreCase)||f.Path.Equals(p.Value,StringComparison.OrdinalIgnoreCase)).Select(p=>(int?)p.Key).ToArray();
            return (ids.Length==0?new int?[]{null}:ids).Select(id=>new Entry(f.Path,f.Origin,id));
        }).Distinct().OrderBy(f=>f.MapId is null).ThenBy(f=>f.MapId).ThenBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).Take(5000).ToArray();
    }
    internal static WolfTextCatalog.MapStructure Read(string exe,Entry entry,CancellationToken token)
    {
        using var resources=WolfResources.Open(exe,token.ThrowIfCancellationRequested);
        var file=resources.Files.Single(f=>f.Path==entry.Path&&f.Origin==entry.Origin);
        WolfTextCatalog.MapStructure? map=null;
        WolfTextCatalog.Read(file.Read(),true,token.ThrowIfCancellationRequested,value=>map=value);
        return map??throw new IOException("该地图的结构尚未识别。");
    }
    internal static string[] VariableIds(WolfTextCatalog.MapEvent item)=>item.Pages.SelectMany(p=>p.Commands)
        .Where(c=>c.Code is 111 or 121 or 122 or 250 or 201 or 150)
        .SelectMany(c=>c.Arguments).Where(n=>n>=2000000&&n<8000000)
        .Select(n=>$"{(n-2000000)/100000}:{(n-2000000)%100000}").Distinct().ToArray();
}
