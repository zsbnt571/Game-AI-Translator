namespace ScreenshotTranslationUiTester;

// Exact software-owned protocol diagnostics only. Never translate substrings,
// font names, paths, provider replies, or other unrecognized external text.
internal static class GameFontRuntimeMessages
{
    // Only replies which explicitly guarantee that the previous live preference
    // survived may trigger the host's separate, acknowledged default fallback.
    internal static bool PreferenceWasRetained(string? message) => message is
        "This game's TextMesh Pro version cannot load a custom font. The previous font and translations were retained." or
        "A current text component cannot use the selected font. The previous font and translations were retained." or
        "The selected font cannot draw all current text. The previous font and translations were retained." or
        "The selected font could not prepare the current text. The previous font and translations were retained." or
        "The selected font name is not available in this Unity runtime. Choose another installed font." or
        "This Unity runtime cannot use the selected font file directly. Select an installed font whose file matches the selection." or
        "The running Unity engine could not load the selected font. The previous preference was retained.";

    internal static string? Canonical(string? message) => message switch
    {
        "This game's TextMesh Pro version cannot load a custom font. The previous font and translations were retained." => "此游戏的文字组件版本不支持自定义字体，已保留原有字体和译文。",
        "A current text component cannot use the selected font. The previous font and translations were retained." => "当前部分文字无法使用所选字体，已保留原有字体和译文。",
        "The selected font cannot draw all current text. The previous font and translations were retained." => "所选字体无法显示当前全部文字，已保留原有字体和译文。",
        "The selected font could not prepare the current text. The previous font and translations were retained." => "所选字体未能完成当前文字的显示准备，已保留原有字体和译文。",
        "The selected font name is not available in this Unity runtime. Choose another installed font." => "此游戏无法使用所选字体名称，请改选其他已安装字体。已保留之前的字体选择。",
        "This Unity runtime cannot use the selected font file directly. Select an installed font whose file matches the selection." => "此游戏不能直接加载所选字体文件，请选择文件内容一致的已安装字体。已保留之前的字体选择。",
        "Invalid custom font file." => "所选字体文件无效。",
        "Invalid font preference mode." => "字体方式无效，未应用字体。",
        "Invalid custom font preference." => "自定义字体设置无效，未应用字体。",
        "Select an absolute TTF, OTF or TTC font path." => "请选择完整路径的 TTF、OTF 或 TTC 字体文件。",
        "The selected font file is unavailable or exceeds 32 MiB." => "所选字体文件不可用或超过 32 MiB，未应用字体。",
        "The running Unity engine could not load the selected font. The previous preference was retained." => "当前游戏无法加载所选字体，已保留之前的选择。",
        "Unity bridge operation failed." => "游戏组件未能应用字体，请稍后重试。",
        "Custom font unavailable for this component; default translation font retained." => "此处文字暂不支持所选字体，已保留当前翻译字体。",
        _ => null
    };
}
