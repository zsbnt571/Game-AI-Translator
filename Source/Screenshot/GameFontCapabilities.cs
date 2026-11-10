namespace ScreenshotTranslationUiTester;

// Engine protocol facts, not evidence that a particular game has rendered a font.
internal sealed record GameFontCapability(bool Custom, bool Live, bool Restore, bool Independent,
    bool SystemFamilyOnly = false, bool RestartRequired = false, string Limitation = "");

internal static class GameFontCapabilities
{
    internal const string NextStart = "字体选择已保存；此引擎在下次启动游戏时应用或恢复默认，当前画面保持不变。";
    internal const string StartupFallback = "所选字体未能加载，游戏继续使用原字体和现有缺字补齐。字体选择已保留，可更换或恢复默认后重新启动。";
    internal static GameFontCapability ForAdapter(string? adapter) => adapter switch
    {
        "wolf" => new(true, true, true, true,
            Limitation: "已识别的 WOLF 字形缓存可实时更换和恢复字体；其他版本在下次启动时应用。仅支持独立 TTF 或 OTF 文件。"),
        "rgss" => new(true, true, true, true, SystemFamilyOnly: true,
            Limitation: "此引擎支持已安装字体，暂不直接加载外部字体文件。"),
        "gamemaker" => new(true, true, true, true,
            Limitation: "仅支持已接入的 GameMaker 2.3+ VM 游戏和独立 TTF 或 OTF 文件。"),
        "unity-mono" or "unity-il2cpp" => new(true, true, true, true,
            Limitation: "仅支持已接入的标准文字组件；部分旧 TMP 无法创建自定义字体，缺字或加载失败时保留当前字体。"),
        "yukar-bakin" => new(true, true, true, true,
            Limitation: "仅支持标准文字组件和独立 TTF 或 OTF 文件；连续更换达到上限后需重启游戏。"),
        "unreal" => new(true, true, true, true,
            Limitation: "仅在识别到 Unreal 4.27 原生字体接口时应用；未识别时保留原有字体。"),
        "web-vnmaker" => new(true, true, true, true,
            Limitation: "仅支持标准矢量文字组件；静态文字实时刷新，对话在下一次绘制时应用。图片和位图字体不在此范围内。"),
        "defold" => new(true, true, true, true,
            Limitation: "兼容的单通道字库可实时更换；不支持热更新的版本在下次启动时应用。仅支持独立 TTF 或 OTF 文件。"),
        "godot3" or "godot4" or "renpy" or "rpg-maker-mv" or "rpg-maker-mz" or "web-rpg"
            or "web-construct" or "web-tyrano" or "web-choicescript" => new(true, true, true, true),
        _ => new(false, false, false, false, Limitation: "此游戏暂不支持自定义内嵌字体。")
    };

    internal static GameFontCapability ForGame(GameInfo? game)
    {
        var result = ForAdapter(game?.AdapterId);
        if (game?.AdapterId != "unity-il2cpp") return result;
        try
        {
            var engine = UnityEmbeddedAdapter.Detect(game.ExePath);
            if (engine is null)
                return new(false, false, false, false,
                    Limitation: "此 Unity 运行时路径暂不支持自定义字体，将继续使用现有字体适配。" );
            if(UnityNativeRuntime.Required(engine))
                return result with {Limitation="原生 Unity 支持已识别的 UGUI/TMP 组件；缺少字体创建接口时保留默认。连续更换达到上限后需重启游戏。"};
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable runtime is not evidence that it can accept fonts.
            return new(false, false, false, false,
                Limitation: "暂时无法确认此游戏的字体加载能力，现有字体适配保持不变。" );
        }
        return result;
    }
}
