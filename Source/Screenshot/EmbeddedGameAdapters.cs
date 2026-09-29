namespace ScreenshotTranslationUiTester;

// Only these registered adapters share the desktop translation queue. Existing
// game-specific plugins retain their own lifecycle and payloads.
internal static class EmbeddedGameAdapters
{
    internal static bool Handles(GameInfo? game)=>game?.AdapterId is "godot3" or "godot4" or "unity-mono" or "unity-il2cpp" or "unreal";
    private static bool Godot(GameInfo game)=>game.AdapterId is "godot3" or "godot4";
    internal static bool HasRecord(GameInfo game)=>game.AdapterId=="unreal"?UnrealGameAdapter.HasRecord(game.ExePath):Godot(game)?GodotGameAdapter.HasRecord(game.ExePath):UnityEmbeddedAdapter.HasRecord(game.ExePath);
    internal static bool IsInstalled(GameInfo game)=>game.AdapterId=="unreal"?UnrealGameAdapter.IsInstalled(game.ExePath):Godot(game)?GodotGameAdapter.IsInstalled(game.ExePath):UnityEmbeddedAdapter.IsInstalled(game.ExePath);
    internal static string InspectionStamp(GameInfo game)
    {
        if(game.AdapterId=="unreal")return UnrealGameAdapter.InspectionStamp(game.ExePath);
        if(!Godot(game))return UnityEmbeddedAdapter.InspectionStamp(game.ExePath);
        string root=Path.GetDirectoryName(game.ExePath)!;
        return game.ExePath+"|"+string.Join("|",new[]{Path.Combine(GodotGameAdapter.Storage(game.ExePath),"install.json"),Path.Combine(root,GodotGameAdapter.Script),Path.Combine(root,"override.cfg")}
            .Select(path=>{var f=new FileInfo(path);return f.Exists?f.Length+":"+f.LastWriteTimeUtc.Ticks:"missing";}));
    }
    internal static void Install(GameInfo game){if(game.AdapterId=="unreal")UnrealGameAdapter.Install(game.ExePath);else if(Godot(game))GodotGameAdapter.Install(game.ExePath);else UnityEmbeddedAdapter.Install(game.ExePath);}
    internal static void Restore(GameInfo game){if(game.AdapterId=="unreal")UnrealGameAdapter.Restore(game.ExePath);else if(Godot(game))GodotGameAdapter.Restore(game.ExePath);else UnityEmbeddedAdapter.Restore(game.ExePath);}
    internal static IEmbeddedTranslationConnection Connect(GameInfo game)=>game.AdapterId=="unreal"?new UnrealTranslationConnection(game.ExePath):Godot(game)?new GodotTranslationConnection(game.ExePath):new RpgGameDataConnection(game.ExePath,"UNITY");
    internal static EmbeddedTranslationOptions Options(GameInfo game)=>new(EmbeddedTextSyntax.Generic,true,
        CatalogBoundOnly:game.AdapterId=="unreal",
        ConfiguredDesktopCatalog:Godot(game)||game.AdapterId=="unreal"?null:
            (settings,token)=>Task.Run(()=>UnityEmbeddedCatalog.Read(game.ExePath,token,settings.SourceLanguage),token));
}
