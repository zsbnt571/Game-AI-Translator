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
        var missing=new List<string>();
        if(!Uri.TryCreate(p.ApiUrl,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https"))missing.Add("有效的 http/https 接口地址");
        if(string.IsNullOrWhiteSpace(p.Model))missing.Add("模型");
        if(!p.AllowEmptyApiKey&&string.IsNullOrWhiteSpace(p.ApiKey))missing.Add("API 密钥");
        return missing;
    }
    internal static string EditIdentity(ApiSettings p)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TranslationIdentity(p)+"|"+p.AllowEmptyApiKey+"|"+p.FirstByteTimeoutSeconds+"|"+p.RequestTimeoutSeconds)));
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
        name=name.Trim();if(name.Length==0)throw new InvalidOperationException("请填写方案名称。");
        if(State.ProfileInfo.Any(x=>x.Key!=id&&x.Value.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("已有同名方案，请使用不同名称。");
        SafeDiagnosticOutput.RegisterCredential(p.ApiKey);
        Change(next=>{next.Profiles[id]=ApiSettingsSnapshot.Copy(p);next.ProfileInfo[id]=new(){Name=name,Origin=next.ProfileInfo.GetValueOrDefault(id)?.Origin??origin,Draft=Missing(p).Count>0};});
    }
    internal void DeleteProfile(string id)
    {
        var refs=References(id);if(refs.Length>0)throw new InvalidOperationException("此方案仍用于："+string.Join("、",refs)+"。请先明确选择替代方案。");
        Change(next=>{next.Profiles.Remove(id);next.ProfileInfo.Remove(id);});
    }
    internal void RequireReady(bool embedded)
    {
        var id=SelectedId(embedded);var p=State.Profiles[id];var missing=Missing(p);
        if(missing.Count>0)throw new InvalidOperationException(Name(id)+" 尚未配置完成："+string.Join("、",missing)+"。请点击“编辑此方案”。");
        if(embedded&&string.IsNullOrEmpty(p.ApiKey))throw new InvalidOperationException("当前 MGI 插件仍需密钥；无鉴权方案可用于截图，请为内嵌选择带密钥的方案。");
    }
}
