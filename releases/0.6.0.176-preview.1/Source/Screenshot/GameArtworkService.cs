using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal sealed record ArtworkMatch(int AppId,string Name,string ImageBase,string? SearchImage=null)
{
    internal string ImageUrl(bool cover,bool portrait=false)=>ImageBase+(cover?(portrait?"library_600x900_2x.jpg":"header.jpg"):"logo.png");
}

// Public store artwork is an optional provider. Missing/changed endpoints are recoverable;
// no local paths, notes, credentials or screenshots are sent in a search request.
internal sealed class GameArtworkService
{
    private static readonly HttpClient Client=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(15)};
    private readonly Func<Uri,int,CancellationToken,Task<byte[]>> download;
    internal GameArtworkService(Func<Uri,int,CancellationToken,Task<byte[]>>? download=null)=>this.download=download??DownloadAsync;
    internal static string MatchKey(string text)=>Regex.Replace(text.ToLowerInvariant(),@"[^\p{L}\p{N}]","");
    internal static bool AllowedUri(Uri uri)=>uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0&&
        (uri.Host=="store.steampowered.com"||uri.Host.EndsWith(".steamstatic.com",StringComparison.OrdinalIgnoreCase));
    internal static ArtworkMatch[] ParseSearch(string json)
    {
        using var doc=JsonDocument.Parse(json);
        if(doc.RootElement.ValueKind!=JsonValueKind.Object||!doc.RootElement.TryGetProperty("items",out var items)||items.ValueKind!=JsonValueKind.Array)return [];
        var result=new List<ArtworkMatch>();
        foreach(var item in items.EnumerateArray().Take(30))
        {
            if(item.ValueKind!=JsonValueKind.Object||!item.TryGetProperty("id",out var id)||id.ValueKind!=JsonValueKind.Number||!id.TryGetInt32(out var appId)||appId<=0||!item.TryGetProperty("name",out var name)||name.ValueKind!=JsonValueKind.String||!item.TryGetProperty("tiny_image",out var tiny)||tiny.ValueKind!=JsonValueKind.String)continue;
            var text=name.GetString();if(string.IsNullOrWhiteSpace(text)||!Uri.TryCreate(tiny.GetString(),UriKind.Absolute,out var image)||!AllowedUri(image))continue;
            var path=image.GetLeftPart(UriPartial.Path);var expected="/apps/"+appId+"/";
            var end=path.IndexOf(expected,StringComparison.Ordinal);if(end<0)continue;
            result.Add(new(appId,text,path[..(end+expected.Length)],image.AbsoluteUri));
        }
        return result.DistinctBy(x=>x.AppId).Take(15).ToArray();
    }
    internal async Task<ArtworkMatch[]> SearchAsync(string name,CancellationToken token)
    {
        name=name.Trim();if(name.Length is <2 or >160)throw new ArgumentException("请输入 2—160 个字符的游戏名称。");
        var key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)));
        var path=Path.Combine(AppDataPaths.CacheRoot,"artwork-search",key+".json");
        if(File.Exists(path)&&new FileInfo(path).Length<1_000_000&&DateTime.UtcNow-File.GetLastWriteTimeUtc(path)<TimeSpan.FromDays(7))
            try{return ParseSearch(await File.ReadAllTextAsync(path,token));}catch(JsonException){}
        var bytes=await download(new("https://store.steampowered.com/api/storesearch/?term="+Uri.EscapeDataString(name)+"&l=english&cc=US"),1_000_000,token);
        var json=System.Text.Encoding.UTF8.GetString(bytes);var result=ParseSearch(json);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{await File.WriteAllTextAsync(temp,json,token);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
        return result;
    }
    internal async Task<Bitmap> GetImageAsync(ArtworkMatch match,bool cover,bool portrait,CancellationToken token)
    {
        var candidates=new List<string>();
        if(cover&&portrait)candidates.Add(match.ImageUrl(true,true));
        if(!cover)candidates.Add(match.ImageUrl(false));
        // Different assets can live in different hash directories. Use each full
        // URL returned by the store rather than deriving it from the search capsule.
        try
        {
            var details=await download(new("https://store.steampowered.com/api/appdetails?appids="+match.AppId+"&filters=basic&l=english&cc=US"),2_000_000,token);
            candidates.AddRange(ParseDetails(System.Text.Encoding.UTF8.GetString(details),match.AppId));
        }
        catch(HttpRequestException ex)when(ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone){}
        catch(JsonException){}
        candidates.Add(match.ImageUrl(true,false));
        if(match.SearchImage is { } tiny)candidates.Add(tiny);
        foreach(var url in candidates.Distinct(StringComparer.Ordinal))
        {
            if(!AppImageUri(url,match.AppId,out var uri))continue;
            byte[] data;
            try{data=await download(uri!,12_000_000,token);}
            catch(HttpRequestException ex)when(ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone){continue;}
            using var stream=new MemoryStream(data);using var source=Image.FromStream(stream,true,true);
            if(source.Width>8192||source.Height>8192||(long)source.Width*source.Height>32_000_000)throw new IOException("网络图片尺寸过大。");
            return GameWindowCover.Thumbnail(source,cover?1280:256,cover?1280:256);
        }
        throw new ArtworkUnavailableException();
    }
    internal static bool AppImageUri(string text,int appId,out Uri? uri)=>Uri.TryCreate(text,UriKind.Absolute,out uri)&&AllowedUri(uri)&&uri.Host.EndsWith(".steamstatic.com",StringComparison.OrdinalIgnoreCase)&&uri.AbsolutePath.Contains("/apps/"+appId+"/",StringComparison.Ordinal);
    internal static string[] ParseDetails(string json,int appId)
    {
        using var doc=JsonDocument.Parse(json);
        if(doc.RootElement.ValueKind!=JsonValueKind.Object||!doc.RootElement.TryGetProperty(appId.ToString(),out var entry)||entry.ValueKind!=JsonValueKind.Object||!entry.TryGetProperty("success",out var success)||success.ValueKind!=JsonValueKind.True||!entry.TryGetProperty("data",out var data)||data.ValueKind!=JsonValueKind.Object)return [];
        if(data.TryGetProperty("steam_appid",out var id)&&(id.ValueKind!=JsonValueKind.Number||!id.TryGetInt32(out var actual)||actual!=appId))return [];
        return new[]{"header_image","capsule_image","capsule_imagev5"}.Select(key=>data.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null)
            .Where(url=>url is not null&&AppImageUri(url,appId,out _)).Select(url=>url!).Distinct(StringComparer.Ordinal).ToArray();
    }
    private static async Task<byte[]> DownloadAsync(Uri uri,int limit,CancellationToken token)
    {
        for(int redirects=0;redirects<4;redirects++)
        {
            if(!AllowedUri(uri))throw new IOException("图片来源地址不受支持。");
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);request.Headers.UserAgent.ParseAdd("FusionR1/0.6.0.33");
            using var response=await Client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
            if((int)response.StatusCode is >=300 and <400&&response.Headers.Location is { } next){uri=next.IsAbsoluteUri?next:new Uri(uri,next);continue;}
            response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>limit)throw new IOException("图片下载超过大小限制。");
            using var stream=await response.Content.ReadAsStreamAsync(token);using var data=new MemoryStream();var buffer=new byte[32768];int count;
            while((count=await stream.ReadAsync(buffer,token))>0){if(data.Length+count>limit)throw new IOException("下载超过大小限制。");data.Write(buffer,0,count);}
            return data.ToArray();
        }
        throw new IOException("图片来源重定向过多。");
    }
}

internal sealed class ArtworkUnavailableException:IOException
{
    internal ArtworkUnavailableException():base("这个条目暂时没有可用图片，原图已保留。可以选择其他候选、本地图片或游戏截图。"){}
}
