using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

// Each trial owns connection/content snapshots. No profile, game cache, history,
// glossary or character preference is written by this service.
internal sealed record TranslationTrialConfiguration(Dictionary<string,ApiSettings> Profiles, TranslationContentSettings Content,
    Dictionary<string,string>? Names=null,string PreferredProfile="");
internal sealed record TranslationTrialHit(string Source,string Target,string Pack,string PackId="");
internal sealed record TranslationTrialRequest(string Text, ApiSettings Settings, TranslationTrialHit[] Hits);
internal sealed record TranslationTrialResult(string Text,double Seconds,int Requests,bool Success,string Error,bool Cancelled=false);

internal static class TranslationTrial
{
    internal static TranslationTrialConfiguration Snapshot(FusionState state) => new(
        state.Profiles.ToDictionary(p=>p.Key,p=>ApiSettingsSnapshot.Copy(p.Value)),
        System.Text.Json.JsonSerializer.Deserialize<TranslationContentSettings>(System.Text.Json.JsonSerializer.Serialize(state.TranslationContent??new()))!,
        state.Profiles.ToDictionary(p=>p.Key,p=>state.ProfileInfo.TryGetValue(p.Key,out var info)&&!string.IsNullOrWhiteSpace(info.Name)?info.Name:p.Value.Model),
        state.EmbeddedProfile??state.CommonProfile);

    internal static Dictionary<string,string> ParseTerms(string text)
    {
        var terms=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var line in text.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
        {
            int split=line.IndexOf('\t');if(split<0)split=line.IndexOf('=');
            if(split<1||split==line.Length-1)throw new ArgumentException("临时术语每行填写：原文 = 译文。");
            var source=line[..split].Trim();var target=line[(split+1)..].Trim();
            if(source.Length is 0 or >200||target.Length is 0 or >500||!terms.TryAdd(source,target)||terms.Count>100)
                throw new ArgumentException("临时术语不能重复或为空，最多 100 条。");
        }
        return terms;
    }

    internal static TranslationTrialRequest Prepare(TranslationTrialConfiguration configuration,string profile,string text,
        TranslationPreset? preset,string[] packIds,IReadOnlyDictionary<string,string> temporaryTerms)
    {
        if(string.IsNullOrWhiteSpace(text)||text.Length>4000)throw new ArgumentException("请填写 1–4000 字的试用文本。");
        if(!configuration.Profiles.TryGetValue(profile,out var saved))throw new ArgumentException("请选择可用的 API 方案。");
        var settings=ApiSettingsSnapshot.Copy(saved);configuration.Content.ApplyTo(settings);
        if(preset is not null)
        {
            settings.PromptPresetId=preset.LegacyStyle is null?preset.Id:"";
            settings.PromptPresetText=preset.LegacyStyle is null?preset.Instructions:"";
            if(preset.LegacyStyle is {} style)settings.TranslationStyle=style;
        }
        settings.GlossaryPackIds=[..packIds];settings.LibraryPacks=TranslationLibrary.Resolve(settings);
        settings.RequestKind=TranslationRequestKind.PlainOrScreenshot;
        var doc=new TranslationWorkspaceStore.Document{
            Terms=temporaryTerms.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal),
            TermRules=temporaryTerms.ToDictionary(p=>p.Key,_=>new TranslationGlossary.TermRule("entity","manual"),StringComparer.Ordinal)};
        settings=TranslationWorkspaceStore.WithTerms(doc,[text],settings,settings).Single;
        var packs=settings.LibraryPacks??[];
        var hits=(settings.RequestTerms??new Dictionary<string,string>()).Select(t=>new TranslationTrialHit(t.Key,t.Value,
            temporaryTerms.ContainsKey(t.Key)?"本次临时术语":packs.FirstOrDefault(p=>TranslationLibrary.Compatible(p,settings)&&p.Terms.TryGetValue(t.Key,out var value)&&value==t.Value)?.Name??"词库",
            temporaryTerms.ContainsKey(t.Key)?"__temporary":packs.FirstOrDefault(p=>TranslationLibrary.Compatible(p,settings)&&p.Terms.TryGetValue(t.Key,out var value)&&value==t.Value)?.Id??"__unknown")).ToArray();
        return new(text,settings,hits);
    }

    internal static async Task<TranslationTrialResult> RunAsync(TranslationTrialRequest request,CancellationToken token,
        Func<string,ApiSettings,CancellationToken,Task<PlainTranslationResult>>? translate=null)
    {
        var timer=Stopwatch.StartNew();
        try
        {
            translate??=new TranslationService().TranslatePlainAsync;
            var result=await translate(request.Text,request.Settings,token);
            token.ThrowIfCancellationRequested();
            return new(result.Text,timer.Elapsed.TotalSeconds,result.RequestCount,true,"");
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){return new("",timer.Elapsed.TotalSeconds,0,false,"已停止",true);}
        catch(Exception ex){AppLog.Write("translation-trial",SafeDiagnosticOutput.ExceptionSummary(ex));return new("",timer.Elapsed.TotalSeconds,0,false,SafeDiagnosticOutput.ExceptionSummary(ex));}
    }
}

public partial class MainForm
{
    internal TranslationTrialConfiguration NativeTrialConfiguration()=>TranslationTrial.Snapshot(_fusion!.State);
}
