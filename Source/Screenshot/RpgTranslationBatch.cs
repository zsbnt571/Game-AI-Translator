using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;
internal sealed class RpgTranslationBatchResult : Dictionary<string,string>
{
    internal RpgTranslationBatchResult():base(StringComparer.Ordinal){}
    internal Dictionary<string,string> Failures {get;}=new(StringComparer.Ordinal);
}
internal static class RpgTranslationBatch
{
    internal static async Task<Dictionary<string,string>> Translate(string[] sources,ApiSettings singleSettings,ApiSettings batchSettings,Func<string,ApiSettings,CancellationToken,Task<string>> translate,CancellationToken token,bool renpy=false,EmbeddedTextSyntax? syntax=null,IReadOnlyDictionary<string,string>? terms=null,TranslationTemplates? templates=null,bool allowLanguageRecovery=true)
    {
        token.ThrowIfCancellationRequested();
        var result=new RpgTranslationBatchResult();
        var effectiveSyntax=syntax??(renpy?EmbeddedTextSyntax.Renpy:EmbeddedTextSyntax.Rpg);
        var payload=new Dictionary<string,string>();var protectedItems=new Dictionary<string,List<(string Source,string[] Codes)>>();
        var templateKeys=new Dictionary<string,string>();var grouped=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var source in sources.Distinct(StringComparer.Ordinal))
        {
            if(!TranslationLanguagePolicy.ShouldTranslate(source,singleSettings)){result[source]=source;continue;}
            var item=effectiveSyntax switch
            {
                EmbeddedTextSyntax.Renpy=>RenpyTranslationText.Protect(source),
                EmbeddedTextSyntax.Generic=>EmbeddedTranslationText.Protect(source,lineBreaks:true),
                EmbeddedTextSyntax.Yukar=>EmbeddedTranslationText.Protect(source,lineBreaks:true,yukar:true),
                _=>RpgTranslationText.Protect(source)
            };
            if(!Regex.IsMatch(Regex.Replace(item.Text,@"⟦RPG\d+⟧",""),@"\p{L}")){result[source]=source;continue;}
            var request=TranslationTemplates.Prepare(item.Text,item.Codes,singleSettings,effectiveSyntax);
            if(request.Key.Length>0&&templates is not null&&templates.TryGet(request.Key,out var remembered)
                &&TranslationTemplates.ValidResult(remembered)&&Accept(source,remembered,request.Codes))continue;
            if(request.Key.Length>0&&grouped.TryGetValue(request.Key,out var same))
            {protectedItems[same].Add((source,request.Codes));continue;}
            string id="t"+protectedItems.Count;protectedItems[id]=[(source,request.Codes)];payload[id]=request.Text;
            if(request.Key.Length>0){grouped[request.Key]=id;templateKeys[id]=request.Key;}
        }
        if(payload.Count==0)return result;
        void FailEntry(string id,string reason){foreach(var item in protectedItems[id])result.Failures[item.Source]=reason;}
        void FailAll(string reason){foreach(var item in protectedItems.Values.SelectMany(x=>x))if(!result.ContainsKey(item.Source))result.Failures[item.Source]=reason;}
        bool Accept(string source,string text,string[] codes)
        {
            if(string.IsNullOrWhiteSpace(text)){result.Failures[source]="empty-result";return false;}
            try
            {
                string restored=Restore(source,text,codes);
                if(!AutomaticTranslationQuality.Valid(source,restored,singleSettings,terms,effectiveSyntax))
                {result.Failures[source]="quality-rejected";return false;}
                result[source]=restored;result.Failures.Remove(source);return true;
            }
            catch(InvalidDataException){result.Failures[source]="placeholder-mismatch";return false;}
        }
        void AcceptEntry(string id,string text)
        {
            if(templateKeys.ContainsKey(id)&&!TranslationTemplates.ValidResult(text)){FailEntry(id,"placeholder-mismatch");return;}
            bool valid=true;foreach(var item in protectedItems[id])valid=Accept(item.Source,text,item.Codes)&&valid;
            if(valid&&templateKeys.TryGetValue(id,out var key))templates?.Remember(key,text);
        }
        try
        {
            if(payload.Count==1)
            {
                var pair=protectedItems.First();var text=await translate(payload[pair.Key],singleSettings,token);
                AcceptEntry(pair.Key,text);
            }
            else
            {
                var raw=await translate(JsonSerializer.Serialize(payload,TranslationLanguageSpans.MessageJson),batchSettings,token);
                raw=raw.Trim();if(raw.Length==0){FailAll("empty-result");return result;}if(raw.StartsWith("```")){int line=raw.IndexOf('\n');int end=raw.LastIndexOf("```",StringComparison.Ordinal);if(line>=0&&end>line)raw=raw[(line+1)..end].Trim();}
                using var document=JsonDocument.Parse(raw);if(document.RootElement.ValueKind!=JsonValueKind.Object){FailAll("invalid-response");return result;}
                var returned=document.RootElement.EnumerateObject().ToArray();
                if(returned.Select(x=>x.Name).Distinct().Count()!=returned.Length||returned.Any(x=>!protectedItems.ContainsKey(x.Name))){FailAll("invalid-response");return result;}
                foreach(var pair in protectedItems)
                {
                    if(!document.RootElement.TryGetProperty(pair.Key,out var text)){FailEntry(pair.Key,"missing-entry");continue;}
                    if(text.ValueKind!=JsonValueKind.String){FailEntry(pair.Key,"invalid-response");continue;}
                    AcceptEntry(pair.Key,text.GetString()!);
                }
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
        catch(OperationCanceledException){FailAll("provider-timeout");}
        catch(TimeoutException){FailAll("provider-timeout");}
        catch(TranslationHttpException ex){FailAll(ex.StatusCode==429?"provider-rate-limited":ex.StatusCode is 401 or 403?"provider-authentication":"provider-failed");}
        catch(HttpRequestException ex){FailAll(ex.StatusCode==System.Net.HttpStatusCode.TooManyRequests?"provider-rate-limited":"provider-failed");}
        catch(JsonException){FailAll("invalid-response");}
        catch(Exception){FailAll("provider-failed");}
        // Retry only content rejected after a valid provider response. Network,
        // authentication and rate-limit errors keep their existing queue policy.
        // One bounded smaller request prevents a copied foreign clause from
        // silently becoming a completed game translation.
        if(allowLanguageRecovery)
        {
            var missing=result.Failures.Where(p=>p.Value=="quality-rejected").Select(p=>p.Key).ToArray();
            if(missing.Length>0)
            {
                var retrySingle=ApiSettingsSnapshot.Copy(singleSettings);var retryBatch=ApiSettingsSnapshot.Copy(batchSettings);
                const string correction="\nLanguage-boundary recovery: the preceding attempt copied eligible foreign content or changed protected language content. Translate every eligible passage in each entire item. Preserve the per-item verbatimPassages exactly. Do not copy a bilingual item unchanged and do not delete one half as redundant. Source selection and target language remain unchanged.";
                string targetReminder=" Required output language: "+TranslationPromptBuilder.NormalizeTarget(singleSettings.TargetLanguage)+". If the supplied source is already in that language, return it exactly unchanged. Do not switch to another output language because of examples in earlier instructions or uncertainty about an unfamiliar phrase.";
                retrySingle.CustomTranslationPrompt+=correction+targetReminder;retryBatch.CustomTranslationPrompt+=correction+targetReminder;
                var recovered=await Translate(missing,retrySingle,retryBatch,translate,token,renpy,syntax,terms,templates,false);
                foreach(var pair in recovered){result[pair.Key]=pair.Value;result.Failures.Remove(pair.Key);}
            }
        }
        return result;
        string Restore(string source,string text,string[] codes)=>(syntax??(renpy?EmbeddedTextSyntax.Renpy:EmbeddedTextSyntax.Rpg)) is EmbeddedTextSyntax.Generic or EmbeddedTextSyntax.Yukar
            ?EmbeddedTextContinuity.RestoreRequest(source,text,codes,syntax??EmbeddedTextSyntax.Generic):RpgTranslationText.Restore(text,codes);
    }
}
