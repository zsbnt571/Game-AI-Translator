using System.Security.Cryptography;
using System.Text;

namespace ScreenshotTranslationUiTester;

// Format adapters for existing translation stores. Game recognition and loader
// ownership remain with the installers; this exchanges text pairs only.
internal static class LegacyTranslationMemory
{
    internal enum Format { Mgi,Cloud }
    internal static string PathFor(string exe,ApiSettings s,Format format)
        =>PathForPrompt(exe,s,format,FusionConfiguration.PluginPrompt(s));
    internal static string PreviousPathFor(string exe,ApiSettings s,Format format)
        =>PathForPrompt(exe,s,format,FusionConfiguration.LegacyPluginPrompt(s));
    private static string PathForPrompt(string exe,ApiSettings s,Format format,string prompt)
    {
        string endpoint=s.ApiUrl.Trim().TrimEnd('/');
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https"))throw new ArgumentException("Invalid endpoint");
        if(format==Format.Mgi)
        {
            if(endpoint.EndsWith("api.deepseek.com",StringComparison.OrdinalIgnoreCase)||endpoint.EndsWith("/v1",StringComparison.OrdinalIgnoreCase))endpoint+="/chat/completions";
        }
        else if(!endpoint.EndsWith("/chat/completions",StringComparison.OrdinalIgnoreCase))
            endpoint+=uri.AbsolutePath is "" or "/"?(uri.Host.Equals("api.deepseek.com",StringComparison.OrdinalIgnoreCase)?"/chat/completions":"/v1/chat/completions"):"/chat/completions";
        string common=endpoint+"|"+s.Model.Trim()+"|"+FusionConfiguration.NormalizeLanguage(s.TargetLanguage)+"|"+prompt;
        string identity=format==Format.Cloud?"cloud-meadow|protected-json-v1|"+common+"|"+s.PreserveNumbers+"|"+s.PreserveVariables+"|"+s.PreserveIdentifiers:"fusion-cache-v1|"+common;
        string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        string directory=AppDataPaths.ValidateExplicitRoot(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!,"BepInEx","config"));
        string path=Path.Combine(directory,(format==Format.Cloud?"CloudMeadowTranslator":"MGITranslator")+".fusion."+hash+".tsv");
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("译文文件包含链接。");
        return path;
    }
    internal static Dictionary<string,string> Read(string path)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        if(!File.Exists(path)||new FileInfo(path).Length>64*1024*1024)return result;
        using var reader=File.OpenText(path);string? line;int count=0;
        while((line=reader.ReadLine()) is not null&&count++<100000)
        {
            if(line.Length>100000)continue;int tab=line.IndexOf('\t');if(tab<=0||line.IndexOf('\t',tab+1)>=0)continue;
            try
            {
                var utf8=new UTF8Encoding(false,true);
                string source=utf8.GetString(Convert.FromBase64String(line[..tab])),value=utf8.GetString(Convert.FromBase64String(line[(tab+1)..]));
                if(TranslationWorkspaceStore.Basic(source,value))result[source]=value;
            }
            catch(FormatException){}catch(DecoderFallbackException){}
        }
        return result;
    }
    internal static IEnumerable<KeyValuePair<string,string>> Read(string exe,ApiSettings settings)
    {
        foreach(var format in Enum.GetValues<Format>())
        {
            foreach(var pair in Read(PathFor(exe,settings,format)))yield return pair;
            if(settings.TranslationStyle is TranslationStyle.Natural or TranslationStyle.Literal or TranslationStyle.GameLocalization or TranslationStyle.Custom)
                foreach(var pair in Read(PreviousPathFor(exe,settings,format)))yield return pair;
        }
    }
    // Called only by a stopped-game installer. Keep existing TSV rows and append
    // validated missing entries atomically; never rewrite accepted plugin bytes.
    internal static void Seed(string exe,ApiSettings settings,Format format)
    {
        try
        {
            string path=PathFor(exe,settings,format);
            if(File.Exists(path)&&new FileInfo(path).Length>64*1024*1024)return;
            var existing=Read(path);var entries=RpgTranslationCache.Effective(exe,settings,display:true);
            foreach(var pair in RpgTranslationCache.Effective(exe,settings))entries.TryAdd(pair.Key,pair.Value);
            byte[] original=File.Exists(path)?File.ReadAllBytes(path):[];var suffix=new StringBuilder();
            foreach(var pair in entries)
            {
                if(existing.ContainsKey(pair.Key)||existing.Count>=100000)continue;
                string row=Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key))+"\t"+Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value))+"\n";
                if(original.Length+suffix.Length+row.Length>64*1024*1024)break;
                suffix.Append(row);existing.Add(pair.Key,pair.Value);
            }
            if(suffix.Length==0)return;
            string pending=path+"."+Guid.NewGuid().ToString("N")+".pending";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using(var output=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    output.Write(original);if(original.Length>0&&original[^1]!=10)output.WriteByte(10);
                    output.Write(Encoding.UTF8.GetBytes(suffix.ToString()));output.Flush(true);
                }
                if(File.Exists(path))
                {
                    if(!File.ReadAllBytes(path).AsSpan().SequenceEqual(original))throw new IOException("译文正在变化，未合并。");
                    File.Replace(pending,path,path+".previous");
                }
                else File.Move(pending,path);
            }
            finally{if(File.Exists(pending))File.Delete(pending);}
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        {AppLog.Write("translation-memory","Legacy exchange unavailable; original cache retained.",ex);}
    }
}
