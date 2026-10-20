using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal enum ArtworkWebSource { Google, F95, DLsite, Steam }

// Public image transport and provenance. No account cookies, local endpoints,
// credentials, or verification workarounds are used by automatic artwork search.
internal static class ArtworkWebSources
{
    internal static string SearchTitle(string title) => Regex.Replace(Regex.Replace(GameWindowNames.WithoutEngineHints(title),@"\.exe$","",RegexOptions.IgnoreCase),
        @"(?i)\s*(?:[-_]\s*)?(?:ver(?:sion)?[.\s]*|v(?=\d))\d+(?:[._-]\d+)*\s*$","").Trim();
    internal static Uri SearchUri(ArtworkWebSource source,string title)
    {
        title=title.Trim();
        if(title.Length is <2 or >160)throw new ArgumentException("请输入 2—160 个字符的游戏名称。");
        string q=Uri.EscapeDataString(title);
        return source switch
        {
            ArtworkWebSource.F95=>new("https://www.google.com/search?udm=2&q="+Uri.EscapeDataString("site:f95zone.to "+title)),
            ArtworkWebSource.DLsite=>new("https://www.dlsite.com/home/fsr/=/keyword/"+q+"/"),
            ArtworkWebSource.Steam=>new("https://store.steampowered.com/search/?term="+q),
            _=>new("https://www.google.com/search?udm=2&q="+q)
        };
    }
    internal static bool IsPublicAddress(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
        if(IPAddress.IsLoopback(address))return false;
        var b=address.GetAddressBytes();
        if(b.Length==4)return b[0] is not (0 or 10 or 127) && b[0]<224 && !(b[0]==169&&b[1]==254) && !(b[0]==172&&b[1]>=16&&b[1]<=31) && !(b[0]==192&&b[1]==168) && !(b[0]==100&&b[1]>=64&&b[1]<=127);
        // Only global unicast IPv6. Reject unspecified, local, multicast,
        // mapped/translated and tunnel addresses before any connection.
        return b.Length==16&&(b[0]&0xe0)==0x20&&!(b[0]==0x20&&b[1]==0x02)&&!(b[0]==0x20&&b[1]==0x01&&b[2]==0&&b[3]==0);
    }
    internal static bool PublicUri(string? value,out Uri? uri)
    {
        return Uri.TryCreate(value,UriKind.Absolute,out uri)&&uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0&&uri.Host.Contains('.')&&
            !uri.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase)&&!uri.Host.EndsWith(".local",StringComparison.OrdinalIgnoreCase)&&
            (!IPAddress.TryParse(uri.Host.Trim('[',']'),out var ip)||IsPublicAddress(ip));
    }
    internal static string? SourcePage(string? value)
    {
        if(!PublicUri(value,out var uri))return null;
        // Provenance must not retain signed image queries or browser/session tokens.
        return new UriBuilder(uri!){Query="",Fragment=""}.Uri.AbsoluteUri;
    }
    internal static string? ClipboardSource(string? html)
    {
        if(string.IsNullOrEmpty(html))return null;
        var match=Regex.Match(html,@"(?m)^SourceURL:([^\r\n]+)",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        return match.Success?SourcePage(match.Groups[1].Value.Trim()):null;
    }
    internal static void Open(Uri uri)
    {
        if(!PublicUri(uri.AbsoluteUri,out _))throw new IOException("请输入公开网站的 HTTPS 图片地址。");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});
    }
    internal static void ValidateImage(Image image)
    {
        if(image.Width>8192||image.Height>8192||(long)image.Width*image.Height>32_000_000)throw new IOException("图片尺寸过大。");
    }
    internal static Task<Bitmap> DecodeImageAsync(byte[] bytes,CancellationToken token) => Task.Run(()=>
    {
        token.ThrowIfCancellationRequested();Bitmap result;
        if(bytes.Length>=12&&bytes.AsSpan(0,4).SequenceEqual("RIFF"u8)&&bytes.AsSpan(8,4).SequenceEqual("WEBP"u8))
        {
            // GDI+ does not decode WebP. Validate dimensions before allocating
            // pixels and keep the decoder bundled instead of relying on an OS codec.
            using var data=SkiaSharp.SKData.CreateCopy(bytes);using var codec=SkiaSharp.SKCodec.Create(data);
            if(codec is null)throw new IOException("无法读取这种图片。请选择其他候选图片。");
            var info=codec.Info.WithColorType(SkiaSharp.SKColorType.Bgra8888).WithAlphaType(SkiaSharp.SKAlphaType.Premul);
            if(info.Width<=0||info.Height<=0||info.Width>8192||info.Height>8192||(long)info.Width*info.Height>32_000_000)throw new IOException("图片尺寸过大。");
            using var decoded=new SkiaSharp.SKBitmap(info);
            if(codec.GetPixels(info,decoded.GetPixels())!=SkiaSharp.SKCodecResult.Success)throw new IOException("无法读取这种图片。请选择其他候选图片。");
            // A GDI+ bitmap wrapping SKBitmap memory may keep the external
            // buffer alive only logically. Copy rows into an owned bitmap before
            // disposing Skia; Clone can still alias that buffer on Windows.
            result=new Bitmap(info.Width,info.Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var bits=result.LockBits(new Rectangle(0,0,info.Width,info.Height),System.Drawing.Imaging.ImageLockMode.WriteOnly,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var row=new byte[info.Width*4];
                for(int y=0;y<info.Height;y++)
                {
                    token.ThrowIfCancellationRequested();
                    System.Runtime.InteropServices.Marshal.Copy(decoded.GetPixels()+y*decoded.RowBytes,row,0,row.Length);
                    System.Runtime.InteropServices.Marshal.Copy(row,0,bits.Scan0+y*bits.Stride,row.Length);
                }
            }
            catch{result.UnlockBits(bits);result.Dispose();throw;}
            result.UnlockBits(bits);
        }
        else
        {
            using var buffer=new MemoryStream(bytes);using var image=Image.FromStream(buffer,true,true);ValidateImage(image);
            result=new Bitmap(image);
        }
        if(token.IsCancellationRequested){result.Dispose();token.ThrowIfCancellationRequested();}return result;
    },token);
    internal static async Task<Bitmap> ReadImageAsync(string address,CancellationToken token)
    {
        var bytes=await ReadBytesAsync(address,20_000_000,true,token);
        try{return await DecodeImageAsync(bytes,token);}
        catch(ArgumentException){throw new IOException("无法读取这种图片。请选择其他候选图片。");}
    }
    internal static async Task<byte[]> ReadBytesAsync(string address,int limit,bool image,CancellationToken token)
    {
        if(!PublicUri(address.Trim(),out var uri))throw new IOException("请输入公开网站的 HTTPS 图片地址。");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(18));
        using var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false};
        handler.ConnectCallback=async(context,cancel)=>
        {
            var addresses=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,cancel);
            if(addresses.Length==0||addresses.Any(ip=>!IsPublicAddress(ip)))throw new IOException("请输入公开网站的 HTTPS 图片地址。");
            Exception? last=null;
            foreach(var ip in addresses)
            {
                var socket=new Socket(ip.AddressFamily,SocketType.Stream,ProtocolType.Tcp);
                try{await socket.ConnectAsync(ip,context.DnsEndPoint.Port,cancel);return new NetworkStream(socket,true);}
                catch(Exception ex){socket.Dispose();last=ex;if(cancel.IsCancellationRequested)throw;}
            }
            throw new IOException("网上图片暂时无法读取。",last);
        };
        using var client=new HttpClient(handler);
        for(int redirects=0;redirects<4;redirects++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);request.Headers.UserAgent.ParseAdd("FusionR1/0.6.0.179");
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if((int)response.StatusCode is >=300 and <400&&response.Headers.Location is {} location)
            {
                var next=location.IsAbsoluteUri?location:new Uri(uri!,location);
                if(!PublicUri(next.AbsoluteUri,out uri))throw new IOException("请输入公开网站的 HTTPS 图片地址。");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if(image&&response.Content.Headers.ContentType?.MediaType?.StartsWith("text/",StringComparison.OrdinalIgnoreCase)==true)
                throw new IOException("图片来源返回了网页，请选择其他候选。");
            if(response.Content.Headers.ContentLength>limit)throw new IOException("下载超过大小限制。");
            using var data=new MemoryStream();using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);var buffer=new byte[32768];int count;
            while((count=await stream.ReadAsync(buffer,timeout.Token))>0){if(data.Length+count>limit)throw new IOException("下载超过大小限制。");data.Write(buffer,0,count);}
            return data.ToArray();
        }
        throw new IOException("图片来源重定向过多。");
    }
}
