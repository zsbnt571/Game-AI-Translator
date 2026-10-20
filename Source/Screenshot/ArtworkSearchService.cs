using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

internal enum ArtworkProvider { Steam, DLsite, Google, F95, Bing }
internal sealed record ArtworkCandidate(string Name,string ImageUrl,string ThumbnailUrl,string PageUrl,ArtworkProvider Provider,bool PreviewOnly=false,ArtworkMatch? Steam=null,string? SearchIndex=null,int Width=0,int Height=0,bool TitleNeedsReview=false)
{
    internal string Identity => ImageUrl;
    internal ArtworkProvider SourceProvider => Source(PageUrl)??Provider;
    internal string SourceLabel => SourceProvider+(SearchIndex is null||SearchIndex==SourceProvider.ToString()?"":" · "+SearchIndex);
    internal bool BelongsTo(ArtworkProvider provider)=>provider==Provider||provider==SourceProvider||SearchIndex==provider.ToString();
    internal static ArtworkProvider? Source(string page)
    {
        if(!Uri.TryCreate(page,UriKind.Absolute,out var uri))return null;
        bool Host(string host)=>uri.Host.Equals(host,StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith("."+host,StringComparison.OrdinalIgnoreCase);
        return Host("dlsite.com")?ArtworkProvider.DLsite:Host("f95zone.to")?ArtworkProvider.F95:Host("steampowered.com")?ArtworkProvider.Steam:null;
    }
}
internal sealed record ArtworkSearchBatch(ArtworkProvider Provider,ArtworkCandidate[] Items,string Message);
internal sealed record ArtworkThumbnail(System.Windows.Media.Imaging.BitmapSource Image,bool PreviewOnly);
internal sealed class ArtworkSourceRestrictedException(string message) : IOException(message);

internal sealed class ArtworkSearchService
{
    private readonly Func<string,int,bool,CancellationToken,Task<byte[]>> read;
    private readonly GameArtworkService steam;
    private readonly Func<string,CancellationToken,Task<string>> google;
    private static readonly TimeSpan RegexLimit=TimeSpan.FromMilliseconds(500);
    private static readonly WpfCoverCache SharedThumbnails=new(24L*1024*1024,160);
    private readonly WpfCoverCache thumbnails;
    internal ArtworkSearchService(Func<string,int,bool,CancellationToken,Task<byte[]>>? read=null,GameArtworkService? steam=null,Func<string,CancellationToken,Task<string>>? google=null)
    {this.read=read??ArtworkWebSources.ReadBytesAsync;this.steam=steam??new();this.google=google??(read is null?GoogleArtworkPage.ReadAsync:Html);thumbnails=read is null?SharedThumbnails:new(24L*1024*1024,160);}
    internal static string Query(string name)
    {
        var title=ArtworkWebSources.SearchTitle(name).Trim();
        if(title.Length is <2 or >160)throw new ArgumentException("请输入 2—160 个字符的游戏名称。");return title;
    }
    internal static string Label(ArtworkProvider provider)=>provider==ArtworkProvider.F95?"F95（搜索索引）":provider.ToString();
    internal static (bool Unconfirmed,bool NonExact,bool Preview,int Shape,long Size) DisplayRank(ArtworkCandidate item,string query,bool cover)
    {
        double ratio=item.Height>0?(double)item.Width/item.Height:0;
        int shape=ratio<=0?1:ratio<(cover?.4:.25)||ratio>(cover?6:4)?2:0;
        return (item.TitleNeedsReview,GameArtworkService.MatchKey(ArtworkWebSources.SearchTitle(item.Name))!=GameArtworkService.MatchKey(ArtworkWebSources.SearchTitle(query)),
            item.PreviewOnly,shape,-Math.Min(4_000_000L,(long)Math.Max(0,item.Width)*Math.Max(0,item.Height)));
    }
    private async Task<string> OptionalGoogleIndex(string url,CancellationToken token)
    {
        try{return await google(url,token);}
        catch(HttpRequestException e)when(e.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)){return "";}
        catch(IOException e)when(e is not ArtworkSourceRestrictedException){return "";}
    }
    internal async Task<ArtworkSearchBatch> SearchAsync(ArtworkProvider provider,string title,CancellationToken token)
    {
        title=Query(title);var caller=token;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(provider is ArtworkProvider.DLsite or ArtworkProvider.F95?45:25));token=deadline.Token;
        try
        {
            ArtworkCandidate[] items;
            if(provider==ArtworkProvider.Steam)
                items=(await steam.SearchAsync(title,token)).Where(m=>Relevant(title,m.Name)).Select(m=>new ArtworkCandidate(m.Name,m.ImageUrl(true),m.SearchImage??m.ImageUrl(true),m.SourcePage.AbsoluteUri,provider,Steam:m)).ToArray();
            else if(provider==ArtworkProvider.DLsite)
            {
                var all=new List<ArtworkCandidate>();
                // Site attribution does not require scraping the product page.
                // Prefer the public Google index, then ordinary public lists.
                var indexed=await OptionalGoogleIndex(GoogleSearchUrl(title,provider),token);
                EnsureReadable(indexed);all.AddRange(ParseGoogle(indexed,title,provider));
                if(all.Count==0)foreach(var section in new[]{"home","maniax"})
                {
                    try
                    {
                        string html=await Html("https://www.dlsite.com/"+section+"/fsr/=/keyword/"+Uri.EscapeDataString(title)+"/",token);
                        EnsureReadable(html);all.AddRange(ParseDlsite(html,title));
                    }
                    catch(ArtworkSourceRestrictedException){throw;}
                    catch(HttpRequestException ex)when(ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests){throw;}
                    catch(Exception ex)when(ex is HttpRequestException or IOException){}
                }
                if(all.Count==0)
                {
                    indexed=await Html(SearchUrl(provider,title),token);
                    EnsureReadable(indexed);all.AddRange(ParseBing(indexed,title,provider));
                }
                items=all.DistinctBy(x=>x.Identity).Take(18).ToArray();
            }
            else if(provider==ArtworkProvider.Google)
            {
                var html=await google(SearchUrl(provider,title),token);
                EnsureReadable(html);items=ParseGoogle(html,title);
                if(items.Length==0&&!html.Contains("data-lpage=")&&(html.Contains("enablejs",StringComparison.OrdinalIgnoreCase)||html.Contains("Google Search",StringComparison.Ordinal)&&!html.Contains("imgres")))
                    return new(provider,[],"Google 暂时无法读取图片结果；其他来源仍可使用。");
            }
            else if(provider==ArtworkProvider.F95)
            {
                // Search the public index, not the restricted site or its attachments.
                var html=await OptionalGoogleIndex(GoogleSearchUrl(title,provider),token);
                EnsureReadable(html);items=ParseGoogle(html,title,provider);
                if(items.Length==0)
                {
                    html=await Html(SearchUrl(provider,title),token);EnsureReadable(html);items=ParseBing(html,title,provider);
                }
            }
            else
            {
                var html=await Html(SearchUrl(provider,title),token);
                EnsureReadable(html);items=ParseBing(html,title,provider);
                string shorter=CoreTitle(title);
                if(items.Length==0&&shorter!=title)
                {
                    html=await Html(SearchUrl(provider,shorter),token);EnsureReadable(html);items=ParseBing(html,title,provider);
                }
            }
            token.ThrowIfCancellationRequested();return new(provider,items,items.Length==0?"未找到与游戏名匹配的图片。":"已找到候选图片。");
        }
        catch(OperationCanceledException)when(!caller.IsCancellationRequested){return new(provider,[],"此来源请求超时；其他来源仍可使用。");}
        catch(HttpRequestException ex){return new(provider,[],ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests?"此来源暂时限制访问；其他来源仍可使用。":"此来源暂时无法连接；其他来源仍可使用。");}
        catch(ArtworkSourceRestrictedException){return new(provider,[],"此来源暂时限制访问；其他来源仍可使用。");}
        catch(Exception ex)when(ex is IOException or JsonException or RegexMatchTimeoutException){return new(provider,[],"此来源暂时无法读取；其他来源仍可使用。");}
    }
    internal static string SearchUrl(ArtworkProvider provider,string title)
    {
        // Search the actual title. Quoting the full name and appending English
        // keywords excluded Japanese store listings and shortened result titles.
        string q=(provider==ArtworkProvider.F95?"site:f95zone.to ":provider==ArtworkProvider.DLsite?"site:dlsite.com ":"")+title;
        if(title.Length<=4)q+=" game";
        return (provider==ArtworkProvider.Google?"https://www.google.com/search?udm=2&q=":"https://www.bing.com/images/search?q=")+Uri.EscapeDataString(q);
    }
    internal static string GoogleSearchUrl(string title,ArtworkProvider source)=>"https://www.google.com/search?udm=2&q="+Uri.EscapeDataString((source==ArtworkProvider.F95?"site:f95zone.to ":"site:dlsite.com ")+title);
    internal static string CoreTitle(string title)
    {
        var core=Regex.Split(title,@"\s*[~～〜｜|:：]\s*|\s+[—–-]\s+",RegexOptions.CultureInvariant,RegexLimit)[0].Trim();
        return GameArtworkService.MatchKey(core).Length>=6&&!title[core.Length..].Any(char.IsDigit)?core:title;
    }
    private async Task<string> Html(string url,CancellationToken token)=>Encoding.UTF8.GetString(await read(url,4_000_000,false,token));
    internal static void EnsureReadable(string html)
    {
        // Normal Google result scripts also mention /sorry/index. Inspect the
        // rendered page/form, not dormant script literals, for a challenge.
        var visible=Regex.Replace(html,@"<(script|style)\b[^>]*>.*?</\1>","",RegexOptions.IgnoreCase|RegexOptions.Singleline,RegexLimit);
        if(new[]{"unusual traffic","verify you are human","cf-chl-","captcha-delivery","/sorry/index"}.Any(s=>visible.Contains(s,StringComparison.OrdinalIgnoreCase)))
            throw new ArtworkSourceRestrictedException("此来源暂时限制访问；其他来源仍可使用。");
    }
    internal static bool Relevant(string query,string title)
    {
        string q=ArtworkWebSources.SearchTitle(query).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        string t=WebUtility.HtmlDecode(title).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        if(GameArtworkService.MatchKey(q)==GameArtworkService.MatchKey(t))return true;
        // Short titles must be whole words: REC must not match 'recommended'.
        if(q.Length<=4)return Regex.IsMatch(t,@"(?<![\p{L}\p{N}])"+Regex.Escape(q)+@"(?![\p{L}\p{N}])",RegexOptions.CultureInvariant,RegexLimit);
        var key=GameArtworkService.MatchKey(q);var target=GameArtworkService.MatchKey(t);
        if(key.Length<4)return false;
        string pattern=(char.IsDigit(key[0])?"(?<![0-9])":"")+Regex.Escape(key)+(char.IsDigit(key[^1])?"(?![0-9])":"");
        if(Regex.IsMatch(target,pattern,RegexOptions.CultureInvariant,RegexLimit))return true;
        string core=GameArtworkService.MatchKey(CoreTitle(q));
        if(core!=key&&core.Length>=6&&Regex.IsMatch(target,Regex.Escape(core)+(char.IsDigit(core[^1])?"(?![0-9])":""),RegexOptions.CultureInvariant,RegexLimit))return true;
        // A genuinely truncated long caption can still identify the game. Keep
        // sequel numbers and at least eight title characters; never loosen REC.
        if(key.Length>=12&&Regex.IsMatch(t,@"(?:…|\.{3})",RegexOptions.CultureInvariant,RegexLimit))
        {
            string prefix=GameArtworkService.MatchKey(Regex.Split(t,@"…|\.{3}",RegexOptions.CultureInvariant,RegexLimit)[0]);
            if(prefix.Length>=8&&key.StartsWith(prefix,StringComparison.Ordinal)&&!key[prefix.Length..].Any(char.IsDigit))return true;
        }
        return false;
    }
    private static MatchCollection Matches(string input,string pattern)=>Regex.Matches(input,pattern,RegexOptions.IgnoreCase|RegexOptions.Singleline|RegexOptions.CultureInvariant,RegexLimit);
    private static string Attr(string tag,string name)
    {
        var m=Regex.Match(tag,@"(?:^|\s)"+Regex.Escape(name)+"\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,RegexLimit);
        return WebUtility.HtmlDecode(m.Success?(m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value):"");
    }
    private static string Url(string s)=>s.StartsWith("//",StringComparison.Ordinal)?"https:"+s:s;
    private static bool Public(string s)=>ArtworkWebSources.PublicUri(s,out _);
    private static bool IsF95(string s)=>Uri.TryCreate(s,UriKind.Absolute,out var u)&&(u.Host.Equals("f95zone.to",StringComparison.OrdinalIgnoreCase)||u.Host.EndsWith(".f95zone.to",StringComparison.OrdinalIgnoreCase));
    private static ArtworkMatch? SteamCover(string page,string image,string name)
    {
        if(ArtworkCandidate.Source(page)!=ArtworkProvider.Steam||!Uri.TryCreate(image,UriKind.Absolute,out var asset)||!GameArtworkService.AllowedUri(asset))return null;
        var id=Regex.Match(new Uri(page).AbsolutePath,@"^/app/(\d+)(?:/|$)",RegexOptions.CultureInvariant,RegexLimit);
        if(!id.Success||!int.TryParse(id.Groups[1].Value,out int appId)||!GameArtworkService.AppImageUri(image,appId,out _))return null;
        if(!Regex.IsMatch(Path.GetFileName(asset.AbsolutePath),@"^(?:header(?:_2x)?|capsule_\d+x\d+|library_header(?:_2x)?)\.(?:jpg|png|webp)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,RegexLimit))return null;
        string path=asset.GetLeftPart(UriPartial.Path),segment="/apps/"+appId+"/";
        return new(appId,name,path[..(path.IndexOf(segment,StringComparison.Ordinal)+segment.Length)],image);
    }
    internal static ArtworkCandidate[] ParseBing(string html,string query,ArtworkProvider provider)
    {
        var items=new List<ArtworkCandidate>();
        foreach(Match tag in Matches(html,@"<a\b[^>]*>"))
        {
            var raw=Attr(tag.Value,"m");if(raw.Length==0)continue;
            try
            {
                using var doc=JsonDocument.Parse(raw);string Value(string key)=>doc.RootElement.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
                string name=Value("t"),page=Value("purl"),image=Value("murl"),thumb=Value("turl");
                if(!Relevant(query,name)||!Public(page)||!Public(thumb)||!Public(image))continue;
                if(query.Length<=4&&provider==ArtworkProvider.Bing)
                {
                    var host=new Uri(page).Host;
                    bool gameSource=new[]{"steampowered.com","f95zone.to","dlsite.com","itch.io","gamejolt.com","vndb.org"}.Any(d=>host==d||host.EndsWith("."+d,StringComparison.OrdinalIgnoreCase));
                    bool gameTitle=Regex.IsMatch(name,@"\bgame\b|ゲーム|游戏",RegexOptions.IgnoreCase,RegexLimit);
                    if(!gameSource&&!gameTitle)continue;
                }
                if(provider==ArtworkProvider.F95&&!IsF95(page))continue;
                if(provider==ArtworkProvider.DLsite&&new Uri(page).Host is not "www.dlsite.com" and not "dlsite.com")continue;
                // F95 cannot be fetched directly in this integration. Indexed thumbnails
                // are still useful and explicitly labelled; never retry blocked originals.
                bool indexed=IsF95(image);if(indexed)image=thumb;
                items.Add(new(name,image,thumb,page,provider,indexed,Steam:SteamCover(page,image,name),SearchIndex:"Bing"));
            }
            catch(JsonException){}
        }
        return items.DistinctBy(x=>x.Identity).Take(16).ToArray();
    }
    internal static ArtworkCandidate[] ParseDlsite(string html,string query)
    {
        var items=new List<ArtworkCandidate>();
        foreach(Match block in Matches(html,@"<li\b[^>]*data-list_item_product_id[^>]*>(.*?)</li>"))
        {
            var body=block.Value;var tag=Matches(body,@"<thumb-with-ng-filter-block\b[^>]*>").Cast<Match>().FirstOrDefault();if(tag is null)continue;
            string title=Attr(tag.Value,"alt"),page=Attr(tag.Value,"link");if(!Relevant(query,title)||!Public(page))continue;
            var original=Matches(body,@"//img\.dlsite\.jp/modpub/[^'""<>\s]+_img_main\.(?:jpg|png|webp)").Cast<Match>().FirstOrDefault()?.Value;
            var thumb=Matches(Attr(tag.Value,":thumb-candidates"),@"//img\.dlsite\.jp/[^'""\s]+\.jpg").Cast<Match>().FirstOrDefault()?.Value;
            if(original is not null)items.Add(new(title,Url(original),Url(thumb??original),page,ArtworkProvider.DLsite));
        }
        return items.DistinctBy(x=>x.Identity).Take(18).ToArray();
    }
    internal static bool IsIndexPreview(string address)
    {
        if(!Uri.TryCreate(address,UriKind.Absolute,out var uri))return true;
        return uri.Host.EndsWith(".gstatic.com",StringComparison.OrdinalIgnoreCase)&&uri.Host.StartsWith("encrypted-tbn",StringComparison.OrdinalIgnoreCase)
            ||(uri.Host.EndsWith(".bing.net",StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith(".bing.com",StringComparison.OrdinalIgnoreCase))&&uri.AbsolutePath.StartsWith("/th",StringComparison.OrdinalIgnoreCase);
    }
    internal static ArtworkCandidate[] ParseGoogle(string html,string query,ArtworkProvider? sourceFilter=null)
    {
        var items=new List<ArtworkCandidate>();
        var cards=Matches(html,@"<div\b[^>]*\bdata-lpage\s*=[^>]*>").Cast<Match>().ToArray();
        for(int i=0;i<cards.Length;i++)
        {
            string page=Attr(cards[i].Value,"data-lpage"),id=Attr(cards[i].Value,"data-docid");
            var end=i+1<cards.Length?cards[i+1].Index:Math.Min(html.Length,cards[i].Index+16000);
            var body=html.Substring(cards[i].Index,Math.Min(16000,end-cards[i].Index));
            var img=Matches(body,@"<img\b[^>]*>").Cast<Match>().FirstOrDefault();
            string name=img is null?"":Attr(img.Value,"alt"),thumb=img is null?"":Attr(img.Value,"src");
            if(id.Length is <1 or >100||!Public(page)||sourceFilter.HasValue&&ArtworkCandidate.Source(page)!=sourceFilter)continue;
            bool matched=Relevant(query,name);
            // A site-specific Japanese query can surface an English product title.
            // Keep these below exact matches and explicitly ask the user to check it.
            bool productPage=new Uri(page).AbsolutePath.Contains(sourceFilter==ArtworkProvider.F95?"/threads/":"/work/",StringComparison.OrdinalIgnoreCase);
            bool alternate=sourceFilter.HasValue&&productPage&&Regex.Matches(query,@"[\p{IsHiragana}\p{IsKatakana}]").Count>=6&&Regex.IsMatch(name,@"[A-Za-z]{4,}")&&!Regex.IsMatch(name,@"[\p{IsHiragana}\p{IsKatakana}]");
            if(!matched&&!alternate)continue;
            // Result metadata associates the original URL with the same card id.
            // Parse JSON strings only; never evaluate a site's script in .NET.
            string str="(\"(?:[^\"\\\\]|\\\\.)*\")";
            var data=Regex.Match(html,"\\[0,\""+Regex.Escape(id)+"\",\\["+str+",\\d+,\\d+\\],\\["+str+",(\\d+),(\\d+)\\]",RegexOptions.CultureInvariant,RegexLimit);
            string original="";int width=0,height=0;
            if(data.Success)try{thumb=JsonSerializer.Deserialize<string>(data.Groups[1].Value)??thumb;original=JsonSerializer.Deserialize<string>(data.Groups[2].Value)??"";}catch(JsonException){}
            if(!Public(thumb))continue;
            bool preview=!Public(original)||IsF95(original)||IsIndexPreview(original)||original==thumb;if(preview)original=thumb;
            if(!preview&&data.Success){int.TryParse(data.Groups[3].Value,out height);int.TryParse(data.Groups[4].Value,out width);}
            items.Add(new(name,original,thumb,page,sourceFilter??ArtworkProvider.Google,preview,Steam:preview?null:SteamCover(page,original,name),SearchIndex:"Google",Width:width,Height:height,TitleNeedsReview:!matched));
        }
        foreach(Match anchor in Matches(html,@"<a\b[^>]*>(.*?)</a>"))
        {
            var href=Attr(anchor.Value[..(anchor.Value.IndexOf('>')+1)],"href");if(!href.Contains("imgurl="))continue;
            var fields=href[(href.IndexOf('?')+1)..].Split('&').Select(s=>s.Split('=',2)).Where(s=>s.Length==2).GroupBy(s=>s[0]).ToDictionary(g=>g.Key,g=>Uri.UnescapeDataString(g.First()[1]));
            if(!fields.TryGetValue("imgurl",out var image)||!fields.TryGetValue("imgrefurl",out var page)||!Public(image)||!Public(page)||IsF95(image)||sourceFilter.HasValue&&ArtworkCandidate.Source(page)!=sourceFilter)continue;
            var img=Matches(anchor.Value,@"<img\b[^>]*>").Cast<Match>().FirstOrDefault();string name=img is null?"":Attr(img.Value,"alt");
            if(!Relevant(query,name))continue;string thumb=img is null?image:Attr(img.Value,"src");
            items.Add(new(name,image,Public(thumb)?thumb:image,page,sourceFilter??ArtworkProvider.Google,IsIndexPreview(image),Steam:SteamCover(page,image,name),SearchIndex:"Google"));
        }
        return items.DistinctBy(x=>x.Identity).Take(16).ToArray();
    }
    internal bool TryGetThumbnail(ArtworkCandidate item,out System.Windows.Media.Imaging.BitmapSource? image)=>thumbnails.TryGet(item.Identity,out image);
    internal async Task<ArtworkThumbnail> LoadThumbnailAsync(ArtworkCandidate item,CancellationToken token)
    {
        if(TryGetThumbnail(item,out var cached))return new(cached!,false);
        // Indexed thumbnails may be deliberately blurred or very small. Build
        // visible tiles from the permitted original, independently of selection.
        return await Task.Run(async()=>
        {
            if(!item.PreviewOnly)
            {
                try
                {
                    using var original=await LoadAsync(item,false,token).ConfigureAwait(false);
                    using var small=GameWindowCover.Thumbnail(original,640,440);
                    var source=WpfDrawing.Image(small);token.ThrowIfCancellationRequested();
                    thumbnails.Remember(item.Identity,source);return new ArtworkThumbnail(source,false);
                }
                catch(Exception ex)when(ex is IOException or HttpRequestException or ArgumentException){}
            }
            token.ThrowIfCancellationRequested();
            using var fallback=await LoadAsync(item,true,token).ConfigureAwait(false);
            return new ArtworkThumbnail(WpfDrawing.Image(fallback),true);
        },token).ConfigureAwait(false);
    }
    internal async Task<Bitmap> LoadAsync(ArtworkCandidate item,bool thumbnail,CancellationToken token,SteamArtworkKind? steamKind=null)
    {
        if(!thumbnail&&item.Steam is {} match)return (steamKind.HasValue?await steam.GetSelectedImageAsync(match,steamKind.Value,token):await steam.GetBestCoverAsync(match,token)).Image;
        string url=thumbnail?item.ThumbnailUrl:item.ImageUrl;
        var key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var path=Path.Combine(AppDataPaths.CacheRoot,"artwork-images",key+".img");byte[] bytes;bool downloaded=false;
        if(File.Exists(path)&&new FileInfo(path).Length<=20_000_000&&DateTime.UtcNow-File.GetLastWriteTimeUtc(path)<TimeSpan.FromDays(7))bytes=await File.ReadAllBytesAsync(path,token);
        else
        {
            bytes=await read(url,thumbnail?3_000_000:20_000_000,true,token);downloaded=true;
        }
        Bitmap? image=await ArtworkWebSources.DecodeImageAsync(bytes,token);
        try
        {
            if(downloaded)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{await File.WriteAllBytesAsync(temp,bytes,token);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
                // Bound only this cache. Reuse the validated decode instead of decoding twice.
                foreach(var old in new DirectoryInfo(Path.GetDirectoryName(path)!).EnumerateFiles("*.img").OrderByDescending(f=>f.LastWriteTimeUtc).Skip(180).Take(30))try{old.Delete();}catch(IOException){}
            }
            if(thumbnail)return GameWindowCover.Thumbnail(image,640,440);
            var result=image;image=null;return result;
        }
        finally{image?.Dispose();}
    }
}
