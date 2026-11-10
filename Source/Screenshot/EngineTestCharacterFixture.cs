using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Exercises shared production metadata/cache APIs using invented actors and
// literal translations. No provider, game process or existing store is opened.
internal static class EngineTestCharacterFixture
{
    internal static EngineTestCheck[] Run(string isolatedDirectory)
    {
        string directory=Path.GetFullPath(isolatedDirectory);
        if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())throw new IOException("测试输出位置无效。");
        Directory.CreateDirectory(directory);
        if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("测试输出位置无效。");
        var profiles=GameCharacterProfiles.Open(Path.Combine(directory,"profiles.json"));
        string source="I am ready.";
        GameCharacterBinding Binding(string id)=>new(id,"Alex",GameCharacterBinding.Hash(GameCharacterRuntimeLane.Contract+"\0"+id));
        var a=Binding("mz:1");var b=Binding("mz:2");
        profiles.ObserveRuntime(new Dictionary<string,GameCharacterBinding>{{"a",a},{"b",b}});
        var settings=new ApiSettings{TargetLanguage="es",SourceLanguage=SourceLanguageMode.English,CharacterGenderEnhancementEnabled=true};
        var terms=new Dictionary<string,string>(StringComparer.Ordinal);
        var cache=GameCharacterTranslationCache.Load(Path.Combine(directory,"variants.json"));
        GameDialogueContext Context(string request,GameCharacterBinding binding)
        {
            if(!profiles.TryResolveRuntime(request,binding,out var r))throw new InvalidOperationException("角色信息读取失败。");
            return GameDialogueContext.Create([],character:new(r.CharacterId,r.Scope,r.EffectiveGender,r.Selection,r.Identity))!;
        }
        bool chosen=profiles.SetSelection(a.CharacterId,CharacterGenderSelection.Male)&&profiles.SetSelection(b.CharacterId,CharacterGenderSelection.Female);
        var ca=Context("a",a);var cb=Context("b",b);
        cache.Accept(source,"Estoy preparado.",ca,terms);cache.Accept(source,"Estoy preparada.",cb,terms);
        bool separate=cache.TryGet(source,ca,settings,terms,out var male,EmbeddedTextSyntax.Rpg,false)&&male=="Estoy preparado."
            &&cache.TryGet(source,cb,settings,terms,out var female,EmbeddedTextSyntax.Rpg,false)&&female=="Estoy preparada."
            &&cache.Snapshot().Count==2;
        bool restored=profiles.SetSelection(a.CharacterId,CharacterGenderSelection.Auto)
            &&profiles.TryResolveRuntime("a",a,out var auto)&&auto.Selection==CharacterGenderSelection.Auto&&auto.EffectiveGender=="unknown"
            &&cache.TryRetained(source,a,settings,terms,out var retained,EmbeddedTextSyntax.Rpg)&&retained=="Estoy preparado.";
        var ordinary=new Dictionary<string,string>{{source,"我准备好了。"}};byte[] before=JsonSerializer.SerializeToUtf8Bytes(ordinary);
        var zh=new ApiSettings{TargetLanguage="zh-CN",SourceLanguage=SourceLanguageMode.English,CharacterGenderEnhancementEnabled=true};
        bool chinese=!GameDialogueRoles.Applies(zh)&&!cache.TryGet(source,cb,zh,terms,out _,EmbeddedTextSyntax.Rpg,false)
            &&before.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(ordinary));
        TranslationStorage.WriteJson(Path.Combine(directory,"variants.json"),cache.Snapshot());
        return [new("两名角色说同一句话",chosen&&separate,"",EngineTestEvidenceKind.ControlledAdapter),
            new("手动修改与恢复自动",restored,"",EngineTestEvidenceKind.ControlledAdapter),
            new("中文与原有缓存保持不变",chinese,"",EngineTestEvidenceKind.ControlledAdapter)];
    }
}
