using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// A narrowly verified source contract for Rignetta's custom typewriter. This
// describes original display-source fields; it contains no translations and
// never changes a database, a variable, or the game's selected language.
internal static class WolfSentenceProfile
{
    internal const string ProfileId="rignetta-3.100-display-v1";
    internal const string FileName="sentence-profile.json";
    internal const string VerifiedExeSha256="87f7406acd7cb7df133c4e71a5a4556c2c393c86117457d2c083ea47d6c47d5e";
    internal const string CommonPath="Data/BasicData/CommonEvent.dat";
    internal const string DatabasePath="Data/BasicData/DataBase.dat";
    internal const string ProjectPath="Data/BasicData/DataBase.project";
    private static readonly Dictionary<string,string> VerifiedResources=new(StringComparer.OrdinalIgnoreCase){
        [CommonPath]="f660ed58a9f2cfc5ded72d870914cc11e964dab8d1d7d531fd04c20c4804d9ef",
        [DatabasePath]="3fffe3db34cbb21df6f161ff06439ff9c75f272053a509cbcbccabaef87ef3d4",
        [ProjectPath]="558cdc8a3756f8648cf3d76bffda1a9d05acfbf18dff98355d5c4a492fc86018"
    };
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private static readonly Regex Location=new(@"\Adatabase/type:([0-9]+)/row:([0-9]+)/field:([0-9]+)\z",RegexOptions.CultureInvariant|RegexOptions.NonBacktracking);
    private static readonly string[] Languages=["JP","EN","CN","EX"];

    internal sealed record ReadSite(int CommonId,int CommandIndex,int ByteOffset,int[] Command);
    internal sealed record Source(int Type,int Row,int Field,int StringSlot,int EncodedSourceId,[property:System.Text.Json.Serialization.JsonIgnore]string SourceText,string SourceSha256)
    {
        // Native's source member is deliberately the unmodified original text.
        [System.Text.Json.Serialization.JsonPropertyName("source")]
        public string RawSource=>SourceText;
        [System.Text.Json.Serialization.JsonIgnore]
        public string Original=>SourceText;
    }
    internal sealed record Document(int Version,string ProfileId,string Secret,string ExeSha256,
        Dictionary<string,string> ResourceHashes,ReadSite[] ReadSites,Source[] Sources);

    internal static ReadSite[] Sites()=>[
        new(605,12,935563,[250,1600032,1600033,1600034,4608,1600005]),
        new(606,11,938404,[250,1600032,1600033,1600034,4608,1600005])
    ];
    internal static bool AllowedType(int type)=>type is >=41 and <=48 or 51 or 52;
    internal static int Encode(int type,int row,int field)
    {
        if(!AllowedType(type)||row is <0 or >9999||field is <0 or >3)throw new ArgumentOutOfRangeException(nameof(type));
        return checked(1_000_000_000+type*1_000_000+row*100+field);
    }
    internal static bool MatchesReadSite(byte[] common,ReadSite site)
    {
        if(site.Command.Length!=6||site.ByteOffset<0||site.ByteOffset>common.Length-25||common[site.ByteOffset]!=6)return false;
        for(int i=0;i<6;i++)if(BinaryPrimitives.ReadInt32LittleEndian(common.AsSpan(site.ByteOffset+1+i*4,4))!=site.Command[i])return false;
        return true;
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string ExeHash(string exe)
    {AppDataPaths.RejectReparsePoints(exe);using var stream=File.OpenRead(exe);if(stream.Length>128L*1024*1024)return "";return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}

    internal static Source[] SelectSources(IEnumerable<WolfDatabaseCatalog.Field> fields)
    {
        var sources=new Dictionary<int,Source>();long bytes=0;
        foreach(var field in fields)
        {
            var match=Location.Match(field.Location);if(!match.Success)continue;
            if(!int.TryParse(match.Groups[1].Value,out int type)||!int.TryParse(match.Groups[2].Value,out int row)||!int.TryParse(match.Groups[3].Value,out int column)
                ||!AllowedType(type)||row is <0 or >9999||column is <0 or >3)continue;
            string text=field.Text;
            if(string.IsNullOrWhiteSpace(text))continue;
            if(text.Length>6000||text.Contains('\0'))throw new InvalidDataException("WOLF 整句显示来源超出已验证范围。");
            // The exact project hash fixes these labels and type layouts. Do
            // not accidentally treat its blank fifth column or numeric fields
            // as one of the four language-specific source columns.
            if(!field.Context.Contains("; field="+Languages[column]+";",StringComparison.Ordinal))throw new InvalidDataException("WOLF 整句显示来源的语言字段不符。");
            int id=Encode(type,row,column);bytes+=Encoding.UTF8.GetByteCount(text)+256;
            if(sources.Count>=4000||bytes>3L*1024*1024)throw new InvalidDataException("WOLF 整句来源表超过限制。");
            if(!sources.TryAdd(id,new(type,row,column,2000+column,id,text,Hash(Encoding.UTF8.GetBytes(text)))))throw new InvalidDataException("WOLF 整句来源身份重复。");
        }
        return sources.Values.OrderBy(s=>s.EncodedSourceId).ToArray();
    }

    internal static Document? FromVerifiedBytes(string exeSha256,IReadOnlyDictionary<string,byte[]> resources,string secret,Action check)
    {
        check();
        if(!string.Equals(exeSha256,VerifiedExeSha256,StringComparison.OrdinalIgnoreCase))return null;
        if(secret.Length!=64||secret.Any(c=>!Uri.IsHexDigit(c)))throw new ArgumentException("WOLF 会话标记无效。",nameof(secret));
        var hashes=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var expected in VerifiedResources)
        {
            check();if(!resources.TryGetValue(expected.Key,out var value)||value.Length>4*1024*1024)return null;
            string hash=Hash(value);if(hash!=expected.Value)return null;hashes.Add(expected.Key,hash);
        }
        var sites=Sites();if(sites.Any(site=>!MatchesReadSite(resources[CommonPath],site)))return null;
        var parsed=WolfDatabaseCatalog.Read(resources[ProjectPath],resources[DatabasePath],check);
        if(parsed.Encoding!="utf-8"||parsed.UnreadableStrings!=0)return null;
        var selected=SelectSources(parsed.Fields);
        // Opening JP is the real story source used to establish this profile.
        // Other allowed language columns retain their separate source IDs; no
        // active-locale switch or automatic built-in-CN selection is performed.
        if(!selected.Any(s=>s.Type==48&&s.Row==1&&s.Field==0&&s.Original.StartsWith("私です。",StringComparison.Ordinal)))return null;
        return new(1,ProfileId,secret,VerifiedExeSha256,hashes,sites,selected);
    }

    internal static Document? Read(string exe,string secret,CancellationToken token=default)
    {
        exe=Path.GetFullPath(exe);var timer=Stopwatch.StartNew();
        void Check(){token.ThrowIfCancellationRequested();if(timer.Elapsed.TotalSeconds>15)throw new IOException("WOLF 整句来源确认超时。");}
        Check();string before=ExeHash(exe);if(before!=VerifiedExeSha256)return null;
        using var resources=WolfResources.Open(exe,Check);
        if(resources.Issues.Any(issue=>issue.Status!="读取完成"))return null;
        var images=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach(string path in VerifiedResources.Keys)
        {
            Check();var matches=resources.Files.Where(resource=>resource.Path.Equals(path,StringComparison.OrdinalIgnoreCase)).ToArray();
            // Loose overrides or duplicate archive members are not guessed.
            if(matches.Length!=1)return null;images.Add(path,matches[0].Read());
        }
        var profile=FromVerifiedBytes(before,images,secret,Check);Check();
        return ExeHash(exe)==before?profile:null;
    }

    internal static bool WriteSession(string exe,string sessionFolder,string secret)
    {
        string folder=Path.GetFullPath(sessionFolder),path=Path.Combine(folder,FileName),temp=path+".tmp";
        string ownedRoot=Path.Combine(AppDataPaths.Root,"native-sessions","wolf")+Path.DirectorySeparatorChar;
        if(!folder.StartsWith(ownedRoot,StringComparison.OrdinalIgnoreCase)||!Directory.Exists(folder))return false;
        try
        {
            AppDataPaths.RejectReparsePoints(folder);AppDataPaths.RejectReparsePoints(path);
            var profile=Read(exe,secret);
            if(profile is null){if(File.Exists(path))File.Delete(path);return false;}
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(profile,JsonOptions);
            if(bytes.Length>4*1024*1024)throw new IOException("WOLF 整句来源配置超过限制。");
            AppDataPaths.RejectReparsePoints(temp);File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);return true;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or JsonException or DecoderFallbackException or ExperimentalRpgCatalog.LimitException)
        {
            // An unverified optional profile must never block existing game
            // startup, or leave a previous profile active in this session.
            try{AppDataPaths.RejectReparsePoints(path);if(File.Exists(path))File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}
            try{AppDataPaths.RejectReparsePoints(temp);if(File.Exists(temp))File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}
            AppLog.Write("wolf-sentence-profile","Optional display-source profile unavailable: "+error.GetType().Name);return false;
        }
    }
}
