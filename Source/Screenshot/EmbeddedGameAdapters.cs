namespace ScreenshotTranslationUiTester;

// Only these registered adapters share the desktop translation queue. Existing
// game-specific plugins retain their own lifecycle and payloads.
internal static class EmbeddedGameAdapters
{
    internal static bool Handles(GameInfo? game)=>game?.AdapterId is "godot3" or "godot4" or "unity-mono" or "unity-il2cpp" or "unreal" or "gamemaker" or "web-rpg" or "web-construct" or "web-tyrano" or "web-vnmaker" or "web-choicescript" or "defold" or "wolf" or "yukar-bakin" or "rgss";
    private static bool Web(GameInfo game)=>game.AdapterId is "web-rpg" or "web-construct" or "web-tyrano" or "web-vnmaker" or "web-choicescript";
    private static bool Godot(GameInfo game)=>game.AdapterId is "godot3" or "godot4";
    internal static bool HasRecord(GameInfo game)=>game.AdapterId=="rgss"?RgssGameAdapter.HasRecord(game.ExePath):game.AdapterId=="yukar-bakin"?YukarGameAdapter.HasRecord(game.ExePath):game.AdapterId=="wolf"?WolfGameAdapter.HasRecord(game.ExePath):game.AdapterId=="defold"?DefoldGameAdapter.HasRecord(game.ExePath):Web(game)?WebGameAdapter.HasRecord(game.ExePath):game.AdapterId=="gamemaker"?GameMakerGameAdapter.HasRecord(game.ExePath):game.AdapterId=="unreal"?UnrealGameAdapter.HasRecord(game.ExePath):Godot(game)?GodotGameAdapter.HasRecord(game.ExePath):UnityEmbeddedAdapter.HasRecord(game.ExePath);
    internal static bool IsInstalled(GameInfo game)=>game.AdapterId=="rgss"?RgssGameAdapter.IsInstalled(game.ExePath):game.AdapterId=="yukar-bakin"?YukarGameAdapter.IsInstalled(game.ExePath):game.AdapterId=="wolf"?WolfGameAdapter.IsInstalled(game.ExePath):game.AdapterId=="defold"?DefoldGameAdapter.IsInstalled(game.ExePath):Web(game)?WebGameAdapter.IsInstalled(game.ExePath):game.AdapterId=="gamemaker"?GameMakerGameAdapter.IsInstalled(game.ExePath):game.AdapterId=="unreal"?UnrealGameAdapter.IsInstalled(game.ExePath):Godot(game)?GodotGameAdapter.IsInstalled(game.ExePath):UnityEmbeddedAdapter.IsInstalled(game.ExePath);
    internal static string InspectionStamp(GameInfo game)
    {
        if(game.AdapterId=="rgss")return RgssGameAdapter.InspectionStamp(game.ExePath);
        if(game.AdapterId=="yukar-bakin")return YukarGameAdapter.InspectionStamp(game.ExePath);
        if(game.AdapterId=="wolf")return WolfGameAdapter.InspectionStamp(game.ExePath);
        if(game.AdapterId=="defold")return DefoldGameAdapter.InspectionStamp(game.ExePath);
        if(Web(game))return WebGameAdapter.InspectionStamp(game.ExePath);
        if(game.AdapterId=="gamemaker")return GameMakerGameAdapter.InspectionStamp(game.ExePath);
        if(game.AdapterId=="unreal")return UnrealGameAdapter.InspectionStamp(game.ExePath);
        if(!Godot(game))return UnityEmbeddedAdapter.InspectionStamp(game.ExePath);
        string root=Path.GetDirectoryName(game.ExePath)!;
        return game.ExePath+"|"+string.Join("|",new[]{Path.Combine(GodotGameAdapter.Storage(game.ExePath),"install.json"),Path.Combine(root,GodotGameAdapter.Script),Path.Combine(root,"override.cfg")}
            .Select(path=>{var f=new FileInfo(path);return f.Exists?f.Length+":"+f.LastWriteTimeUtc.Ticks:"missing";}));
    }
    internal static void Install(GameInfo game){if(game.AdapterId=="rgss")RgssGameAdapter.Install(game.ExePath);else if(game.AdapterId=="yukar-bakin")YukarGameAdapter.Install(game.ExePath);else if(game.AdapterId=="wolf")WolfGameAdapter.Install(game.ExePath);else if(game.AdapterId=="defold")DefoldGameAdapter.Install(game.ExePath);else if(Web(game))WebGameAdapter.Install(game.ExePath);else if(game.AdapterId=="gamemaker")GameMakerGameAdapter.Install(game.ExePath);else if(game.AdapterId=="unreal")UnrealGameAdapter.Install(game.ExePath);else if(Godot(game))GodotGameAdapter.Install(game.ExePath);else UnityEmbeddedAdapter.Install(game.ExePath);}
    internal static void Restore(GameInfo game){if(game.AdapterId=="rgss")RgssGameAdapter.Restore(game.ExePath);else if(game.AdapterId=="yukar-bakin")YukarGameAdapter.Restore(game.ExePath);else if(game.AdapterId=="wolf")WolfGameAdapter.Restore(game.ExePath);else if(game.AdapterId=="defold")DefoldGameAdapter.Restore(game.ExePath);else if(Web(game))WebGameAdapter.Restore(game.ExePath);else if(game.AdapterId=="gamemaker")GameMakerGameAdapter.Restore(game.ExePath);else if(game.AdapterId=="unreal")UnrealGameAdapter.Restore(game.ExePath);else if(Godot(game))GodotGameAdapter.Restore(game.ExePath);else UnityEmbeddedAdapter.Restore(game.ExePath);}
    internal static IEmbeddedTranslationConnection Connect(GameInfo game)=>game.AdapterId=="rgss"?new RgssTranslationConnection(game.ExePath):game.AdapterId=="yukar-bakin"?new YukarTranslationConnection(game.ExePath):game.AdapterId=="wolf"?new WolfTranslationConnection(game.ExePath):game.AdapterId=="defold"?new DefoldTranslationConnection(game.ExePath):Web(game)?new WebTranslationConnection(game.ExePath):game.AdapterId=="gamemaker"?new GameMakerTranslationConnection(game.ExePath):game.AdapterId=="unreal"?new UnrealTranslationConnection(game.ExePath):Godot(game)?new GodotTranslationConnection(game.ExePath):new RpgGameDataConnection(game.ExePath,"UNITY");
    internal static EmbeddedTranslationOptions Options(GameInfo game)=>game.AdapterId=="rgss"?new(EmbeddedTextSyntax.Rpg,false,DesktopCatalog:token=>Task.Run(()=>RgssTextCatalog.Read(game.ExePath,token),token),StartupCache:true):game.AdapterId=="yukar-bakin"?new(EmbeddedTextSyntax.Yukar,false,StartupCache:true):game.AdapterId=="wolf"?new(EmbeddedTextSyntax.Generic,false,DesktopCatalog:token=>Task.Run(()=>WolfGameAdapter.ReadCatalog(game.ExePath,token),token),StartupCache:true){DisplayBoundaryWhitespace=true}:game.AdapterId=="defold"?new(EmbeddedTextSyntax.Generic,false,DesktopCatalog:token=>Task.Run(()=>DefoldGameAdapter.ReadCatalog(game.ExePath,token),token),StartupCache:true):Web(game)?new(game.AdapterId=="web-rpg"?EmbeddedTextSyntax.Rpg:EmbeddedTextSyntax.Generic,false,ConfiguredDesktopCatalog:(settings,token)=>Task.Run(()=>WebTextCatalog.Read(game.ExePath,token,settings.SourceLanguage,preferredLanguage:settings.CatalogLanguageHint),token),StartupCache:true):game.AdapterId=="gamemaker"?new(EmbeddedTextSyntax.Generic,false,DesktopCatalog:token=>Task.Run(()=>GameMakerGameAdapter.ReadCatalog(game.ExePath,token),token),StartupCache:true):new(EmbeddedTextSyntax.Generic,true,
        CatalogBoundOnly:game.AdapterId=="unreal",
        ConfiguredDesktopCatalog:Godot(game)||game.AdapterId=="unreal"?null:
            (settings,token)=>Task.Run(()=>UnityEmbeddedCatalog.Read(game.ExePath,token,settings.SourceLanguage,preferredLanguage:settings.CatalogLanguageHint),token),
        StartupCache:true);
}

