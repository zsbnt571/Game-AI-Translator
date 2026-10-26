using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed class FusionProfileInfo
{
    public string Name { get; set; } = "";
    public string Origin { get; set; } = "用户创建";
    public bool Draft { get; set; }
}

internal sealed partial class FusionConfiguration
{
    internal static FusionState Clone(FusionState value) => JsonSerializer.Deserialize<FusionState>(JsonSerializer.Serialize(value))!;
    internal void Change(Action<FusionState> update)
    {
        var next=Clone(State); update(next); Persist(next);
    }
    private void Persist(FusionState next)
    {
        foreach(var id in new[]{next.CommonProfile,next.ScreenshotProfile,next.EmbeddedProfile})
            if(id is not null&&!next.Profiles.ContainsKey(id))throw new InvalidOperationException("方案引用无效，未保存任何更改。");
        foreach(var id in next.GameProfiles.Values) if(!next.Profiles.ContainsKey(id)) throw new InvalidOperationException("游戏方案引用无效，未保存更改。");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp=_path+".tmp";var json=FusionSecrets.Serialize(next);
        try
        {
            using(var stream=new FileStream(tmp,FileMode.Create,FileAccess.Write,FileShare.None))
            {var bytes=Encoding.UTF8.GetBytes(json);stream.Write(bytes);stream.Flush(true);}
            var reload=FusionSecrets.Deserialize<FusionState>(File.ReadAllText(tmp));
            if(JsonSerializer.Serialize(reload)!=JsonSerializer.Serialize(next))throw new IOException("保存内容复核失败。");
            File.Move(tmp,_path,true);
            State=FusionSecrets.Deserialize<FusionState>(File.ReadAllText(_path));
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    private void MigrateProfiles()
    {
        if(State.SchemaVersion>=2)return;
        var next=Clone(State); var names=next.Profiles.Keys.ToArray(); var ids=names.ToDictionary(x=>x,x=>Guid.NewGuid().ToString("N"));
        var evidencePath=Path.Combine(AppContext.BaseDirectory,"legacy-template-evidence.json");
        string? emptyName=null; ApiSettings? emptySettings=null;
        if(File.Exists(evidencePath))
        {
            using var evidence=JsonDocument.Parse(File.ReadAllText(evidencePath));
            emptyName=evidence.RootElement.GetProperty("LegacyName").GetString();
            emptySettings=evidence.RootElement.GetProperty("Settings").Deserialize<ApiSettings>();
        }
        next.Profiles.Clear();next.ProfileInfo.Clear();
        foreach(var name in names)
        {
            var p=State.Profiles[name];
            var referenced=name==State.CommonProfile||name==State.ScreenshotProfile||name==State.EmbeddedProfile;
            var provenEmpty=name==emptyName&&emptySettings is not null&&string.IsNullOrEmpty(p.ApiKey)&&JsonSerializer.Serialize(p)==JsonSerializer.Serialize(emptySettings);
            if(provenEmpty&&!referenced)
            {
                next.ArchivedTemplates[name]=ApiSettingsSnapshot.Copy(p);
                next.MigrationNotes.Add(name+"：与首次导入的空预设逐字段一致且无引用，移入模板；原记录归档保留。");continue;
            }
            var id=ids[name];next.Profiles[id]=ApiSettingsSnapshot.Copy(p);
            next.ProfileInfo[id]=new(){Name=name,Origin=name.StartsWith("内嵌 · ")?"旧内嵌导入":name=="公共 · R4"?"R4 导入":"现有 Fusion 记录",Draft=Missing(p).Count>0};
        }
        string Map(string name)
        {
            if(ids.TryGetValue(name,out var id)&&next.Profiles.ContainsKey(id))return id;
            id=Guid.NewGuid().ToString("N");next.Profiles[id]=new(){ApiUrl="",ApiKey="",Model=""};
            next.ProfileInfo[id]=new(){Name=name,Origin="原引用缺失，保留待修复",Draft=true};return id;
        }
        next.CommonProfile=Map(State.CommonProfile);
        next.ScreenshotProfile=State.ScreenshotProfile is null?null:Map(State.ScreenshotProfile);
        next.EmbeddedProfile=State.EmbeddedProfile is null?null:Map(State.EmbeddedProfile);
        next.SchemaVersion=2;Persist(next);
    }
    internal string Name(string id)=>State.ProfileInfo.TryGetValue(id,out var info)?info.Name:id;
    internal string SelectedId(bool embedded)=>(embedded?State.EmbeddedProfile:State.ScreenshotProfile)??State.CommonProfile;
    internal static List<string> Missing(ApiSettings p)
    {
        var missing=MissingConnection(p);
        if(p.SourceLanguage==SourceLanguageMode.Mixed && TranslationLanguagePolicy.Selected(p).Length==0)missing.Add("原文语言（至少选择一种）");
        return missing;
    }
    internal static List<string> MissingConnection(ApiSettings p)
    {
        return ApiConnectionPolicy.Missing(p);
    }
    internal static string EditIdentity(ApiSettings p)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TranslationIdentity(p)+"|"+p.AllowEmptyApiKey+"|"+p.FirstByteTimeoutSeconds+"|"+p.RequestTimeoutSeconds)));
    internal static string ConnectionIdentity(ApiSettings p)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {p.ApiUrl,p.ApiKey,p.Model,p.AdvancedApiParameters,p.AllowEmptyApiKey,p.ProviderDisplayName,p.TranslationProviderKind,p.FirstByteTimeoutSeconds,p.RequestTimeoutSeconds}))));
    internal string[] References(string id)
    {
        var refs=new List<string>();
        if(State.CommonProfile==id)refs.Add("公共默认");
        foreach(var embedded in new[]{false,true})
        {
            var label=embedded?"内嵌":"截图";var choice=embedded?State.EmbeddedProfile:State.ScreenshotProfile;
            if(choice==id)refs.Add(label+"固定使用此方案");
            else if(choice is null&&State.CommonProfile==id)refs.Add(label+"跟随公共方案");
        }
        foreach(var pair in State.GameProfiles) if(pair.Value==id) refs.Add("游戏："+Path.GetFileNameWithoutExtension(pair.Key));
        return refs.ToArray();
    }
    internal void SaveProfile(string id,string name,ApiSettings p,string origin="用户创建")
    {
        ChatRequestParameters.Parse(p.AdvancedApiParameters);
        name=name.Trim();if(name.Length==0)throw new InvalidOperationException("请填写方案名称。");
        if(State.ProfileInfo.Any(x=>x.Key!=id&&x.Value.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("已有同名方案，请使用不同名称。");
        SafeDiagnosticOutput.RegisterCredential(p.ApiKey);
        Change(next=>
        {
            var connection=ApiSettingsSnapshot.Copy(next.Profiles.GetValueOrDefault(id)??new ApiSettings());
            ApplyConnection(connection,p);
            next.Profiles[id]=connection;
            next.ProfileInfo[id]=new(){Name=name,Origin=next.ProfileInfo.GetValueOrDefault(id)?.Origin??origin,Draft=MissingConnection(connection).Count>0};
        });
    }
    internal void SaveTranslationContent(TranslationContentSettings content)
    {
        if(content.HasEmptyCombination)throw new InvalidOperationException("请至少选择一种需要翻译的原文语言。");
        var snapshot=new ApiSettings();content.ApplyTo(snapshot);
        Change(next=>next.TranslationContent=TranslationContentSettings.From(snapshot));
    }
    internal void DeleteProfile(string id,string? replacementId=null)
    {
        if(!State.Profiles.ContainsKey(id))throw new InvalidOperationException("此方案已不存在，请重新选择。");
        var refs=References(id);
        if(refs.Length>0&&replacementId is null)throw new InvalidOperationException("此方案仍用于："+string.Join("、",refs)+"。请先明确选择替代方案。");
        if(replacementId is not null&&(replacementId==id||!State.Profiles.ContainsKey(replacementId)))
            throw new InvalidOperationException("请选择另一个现有方案，未删除任何内容。");
        Change(next=>
        {
            if(replacementId is not null)
            {
                if(next.CommonProfile==id)next.CommonProfile=replacementId;
                if(next.ScreenshotProfile==id)next.ScreenshotProfile=replacementId;
                if(next.EmbeddedProfile==id)next.EmbeddedProfile=replacementId;
                foreach(var path in next.GameProfiles.Where(pair=>pair.Value==id).Select(pair=>pair.Key).ToArray())next.GameProfiles[path]=replacementId;
            }
            next.Profiles.Remove(id);next.ProfileInfo.Remove(id);
        });
    }
    internal void RequireReady(bool embedded)
    {
        var id=SelectedId(embedded);var p=Effective(new ApiSettings(),embedded);var missing=Missing(p);
        if(missing.Count>0)throw new InvalidOperationException(Name(id)+" 尚未配置完成："+string.Join("、",missing)+"。请点击“编辑此方案”。");
    }
}
