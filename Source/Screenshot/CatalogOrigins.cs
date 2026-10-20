using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Provenance is data, never permission to rewrite a resource or a runtime
// binding. Keep occurrences before source deduplication, and keep unknown
// positions explicitly unknown rather than inventing an offset.
internal sealed record CatalogTextOrigin(string Text,string File,string Location,string ContentHash,
    string Kind,string Encoding="",string Container="",string Language="",string SchemaFile="",string SchemaHash="",string Context="");
internal sealed class CatalogOrigins
{
    private readonly List<CatalogTextOrigin> items=[];
    private readonly HashSet<string> keys=new(StringComparer.Ordinal);
    internal bool Truncated {get;private set;}
    internal CatalogTextOrigin[] Items=>items.ToArray();
    internal static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string TextHash(string text)=>Hash(Encoding.UTF8.GetBytes(text));
    internal static string Identity(CatalogTextOrigin value)=>TextHash(string.Join("\0",value.File,value.Container,value.Location,value.Kind,value.Language,TextHash(value.Text))
        +(value.SchemaFile.Length>0?"\0schema="+value.SchemaFile:""));
    internal void Add(CatalogTextOrigin value)
    {
        if(string.IsNullOrWhiteSpace(value.Text)||value.Text.Length>6000||value.Text.Contains('\0'))return;
        string key=Identity(value);if(keys.Contains(key))return;
        if(items.Count>=100000){Truncated=true;return;}
        items.Add(value);keys.Add(key);
    }
    internal void Json(JsonElement root,string file,string hash,string container="")
    {
        int nodes=0;
        string Escape(string name)=>name.Replace("~","~0").Replace("/","~1");
        void AddText(JsonElement value,string pointer,string kind,int depth=0)
        {
            if(depth>48||++nodes>1000000){Truncated=true;return;}
            if(value.ValueKind==JsonValueKind.String)Add(new(value.GetString()!,file,pointer,hash,kind,"json-unicode",container));
            else if(value.ValueKind==JsonValueKind.Array)
            {int i=0;foreach(var item in value.EnumerateArray())AddText(item,pointer+"/"+i++,kind,depth+1);}
        }
        void Walk(JsonElement node,string pointer,int depth=0)
        {
            if(Truncated)return;
            if(depth>48||++nodes>1000000){Truncated=true;return;}
            if(node.ValueKind==JsonValueKind.Array)
            {
                int i=0;foreach(var child in node.EnumerateArray())
                {if(child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)Walk(child,pointer+"/"+i,depth+1);i++;}return;
            }
            if(node.ValueKind!=JsonValueKind.Object)return;
            if(node.TryGetProperty("code",out var code)&&code.ValueKind==JsonValueKind.Number&&code.TryGetInt32(out int command)&&node.TryGetProperty("parameters",out var args)&&args.ValueKind==JsonValueKind.Array)
            {
                if(command is 401 or 405&&args.GetArrayLength()>0)AddText(args[0],pointer+"/parameters/0","dialogue");
                else if(command==102&&args.GetArrayLength()>0)AddText(args[0],pointer+"/parameters/0","choice");
                else if(command is 320 or 324 or 325&&args.GetArrayLength()>1)AddText(args[1],pointer+"/parameters/1","display-name");
                else if(command==101&&args.GetArrayLength()>4)AddText(args[4],pointer+"/parameters/4","speaker");
                // Event script, resource and control parameters stay opaque.
                return;
            }
            foreach(var property in node.EnumerateObject())
            {
                string at=pointer+"/"+Escape(property.Name);
                bool display=property.Name is "name" or "nickname" or "profile" or "description" or "displayName" or "message1" or "message2" or "message3" or "message4" or "gameTitle" or "currencyUnit";
                if(display)AddText(property.Value,at,"display-field");
                else if(property.Name is "terms" or "messages" or "basic" or "params" or "commands" or "elements" or "skillTypes" or "weaponTypes" or "armorTypes" or "equipTypes")Terms(property.Value,at,depth+1);
                else if(property.Name is not ("parameters" or "note"))Walk(property.Value,at,depth+1);
            }
        }
        void Terms(JsonElement value,string at,int depth)
        {
            if(depth>48||++nodes>1000000){Truncated=true;return;}
            if(value.ValueKind==JsonValueKind.Object)foreach(var p in value.EnumerateObject())Terms(p.Value,at+"/"+Escape(p.Name),depth+1);
            else AddText(value,at,"system-term",depth);
        }
        Walk(root,"");
    }
}
