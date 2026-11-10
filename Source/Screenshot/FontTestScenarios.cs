namespace ScreenshotTranslationUiTester;

// Neutral samples are local text. Loading a sample never changes a translation
// target, game preference, profile, or cache and never sends an API request.
internal static class FontTestScenarios
{
    internal static readonly string[] Names = ["自定义文字", "菜单", "短对话", "长段落", "混合语言", "小字号"];
    internal static string Text(int scene, string code)
    {
        if (scene is 1 or 5) return GameFontPreview.Sample(code);
        if (scene == 4) return "你好！Hello! こんにちは！안녕하세요! Привет!\nÁÉÍÓÚ · äöüß · Français · 0123456789 · [] {} % — …";
        string shortText = code switch
        {
            "zh-CN" => "雨停了。我们沿着小路走，天黑之前就能到家。",
            "zh-TW" => "雨停了。我們沿著小路走，天黑之前就能到家。",
            "ja" => "雨がやみました。この道を進めば、日が暮れる前に家に着きます。",
            "ko" => "비가 그쳤어요. 이 길을 따라가면 어두워지기 전에 집에 도착할 수 있어요.",
            "es" => "Ha dejado de llover. Si seguimos este camino, llegaremos a casa antes de que anochezca.",
            "fr" => "La pluie a cessé. En suivant ce chemin, nous arriverons à la maison avant la nuit.",
            "de" => "Der Regen hat aufgehört. Auf diesem Weg kommen wir noch vor Einbruch der Dunkelheit nach Hause.",
            "ru" => "Дождь закончился. Если пойдём по этой дороге, доберёмся домой до наступления темноты.",
            _ => "The rain has stopped. If we follow this path, we can get home before dark."
        };
        return scene == 3 ? string.Join("\n\n", Enumerable.Repeat(shortText, 3)) : shortText;
    }
}
