using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// A receipt binds the glossary used by a request to that exact automatic
// result. Separate receipts for native/display caches preserve their authority.
internal sealed class TranslationGlossaryCache
{
    internal sealed record Receipt(string Stamp,string TextHash);
    private readonly Dictionary<string,Receipt> receipts=new(StringComparer.Ordinal);
    private bool unreadable;
    internal bool Readable=>!unreadable;
    internal static TranslationGlossaryCache Load(string path,IReadOnlyDictionary<string,Receipt>? inherited=null)
    {
        var result=new TranslationGlossaryCache();
        if(inherited is not null)foreach(var pair in inherited)result.receipts[pair.Key]=pair.Value;
        try
        {
            if(!File.Exists(path+".glossary.json"))return result;
            if(new FileInfo(path+".glossary.json").Length>64*1024*1024)throw new IOException("Glossary receipts too large");
            var data=JsonSerializer.Deserialize<Dictionary<string,Receipt>>(File.ReadAllText(path+".glossary.json"));
            if(data is null||data.Count>150000||data.Any(p=>p.Key.Length>6000||p.Value is null||p.Value.Stamp is not {Length:64}||p.Value.TextHash is not {Length:64}))throw new IOException("Invalid glossary receipts");
            foreach(var p in data)result.receipts.TryAdd(p.Key,p.Value);
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException){result.unreadable=true;}
        return result;
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static bool Matches(Receipt receipt,string source,string text,IReadOnlyDictionary<string,string> terms)
        =>receipt.TextHash==Hash(text)&&receipt.Stamp==TranslationGlossary.Stamp(terms,source);
    internal bool Matches(string source,string text,IReadOnlyDictionary<string,string> terms)
        =>receipts.TryGetValue(source,out var r)?r.TextHash==Hash(text)&&r.Stamp==TranslationGlossary.Stamp(terms,source)
            :!unreadable&&TranslationGlossary.Applicable(terms,[source]).Count==0;
    internal void Accept(string source,string text,IReadOnlyDictionary<string,string> terms)
        =>receipts[source]=new(TranslationGlossary.Stamp(terms,source),Hash(text));
    internal Dictionary<string,Receipt> Snapshot(IReadOnlyDictionary<string,string> entries)
        =>receipts.Where(p=>entries.TryGetValue(p.Key,out var text)&&p.Value.TextHash==Hash(text)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
    // Keep rejection evidence when an interrupted write left a new receipt
    // beside old text. Publishing an unrelated source must not erase it.
    internal Dictionary<string,Receipt> ForSources(IReadOnlyDictionary<string,string> entries)
        =>receipts.Where(p=>entries.ContainsKey(p.Key)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
}
