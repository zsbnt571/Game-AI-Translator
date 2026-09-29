using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;
internal static class RpgTranslationBatch
{
    internal static async Task<Dictionary<string,string>> Translate(string[] sources,ApiSettings singleSettings,ApiSettings batchSettings,Func<string,ApiSettings,CancellationToken,Task<string>> translate,CancellationToken token,bool renpy=false,EmbeddedTextSyntax? syntax=null)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        var payload=new Dictionary<string,string>();var protectedItems=new Dictionary<string,(string Source,string[] Codes)>();
        foreach(var source in sources)
        {
            var item=(syntax??(renpy?EmbeddedTextSyntax.Renpy:EmbeddedTextSyntax.Rpg)) switch
            {
                EmbeddedTextSyntax.Renpy=>RenpyTranslationText.Protect(source),
                EmbeddedTextSyntax.Generic=>EmbeddedTranslationText.Protect(source),
                _=>RpgTranslationText.Protect(source)
            };
            if(!Regex.IsMatch(Regex.Replace(item.Text,@"⟦RPG\d+⟧",""),@"\p{L}")){result[source]=source;continue;}
            string id="t"+protectedItems.Count;protectedItems[id]=(source,item.Codes);payload[id]=item.Text;
        }
        if(payload.Count==0)return result;
        try
        {
            if(payload.Count==1)
            {
                var pair=protectedItems.First();var text=await translate(payload[pair.Key],singleSettings,token);
                result[pair.Value.Source]=RpgTranslationText.Restore(text,pair.Value.Codes);
            }
            else
            {
                var raw=await translate(JsonSerializer.Serialize(payload),batchSettings,token);
                raw=raw.Trim();if(raw.StartsWith("```")){int line=raw.IndexOf('\n');int end=raw.LastIndexOf("```",StringComparison.Ordinal);if(line>=0&&end>line)raw=raw[(line+1)..end].Trim();}
                using var document=JsonDocument.Parse(raw);if(document.RootElement.ValueKind!=JsonValueKind.Object)return result;
                var returned=document.RootElement.EnumerateObject().ToArray();
                if(returned.Select(x=>x.Name).Distinct().Count()!=returned.Length||returned.Any(x=>!protectedItems.ContainsKey(x.Name)))return result;
                foreach(var pair in protectedItems)
                {
                    if(!document.RootElement.TryGetProperty(pair.Key,out var text)||text.ValueKind!=JsonValueKind.String)continue;
                    try{result[pair.Value.Source]=RpgTranslationText.Restore(text.GetString()!,pair.Value.Codes);}catch(InvalidDataException){}
                }
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
        catch(Exception){/* Scheduler retains each unsuccessful source for bounded retry. */}
        return result;
    }
}
