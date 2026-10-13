namespace ScreenshotTranslationUiTester;

public static class TargetLanguageDisplay
{
    private static readonly (string Display, string Code)[] Languages =
    [
        ("中文", "zh-CN"),
        ("繁體中文", "zh-TW"),
        ("English", "en"),
        ("日本語", "ja"),
        ("한국어", "ko"),
        ("Español", "es"),
        ("Français", "fr"),
        ("Deutsch", "de")
    ];

    public static IReadOnlyList<string> DisplayNames => Languages.Select(x => x.Display).ToArray();

    public static string DisplayFromCode(string? value)
    {
        var normalized = (value ?? "").Trim();
        var match = Languages.FirstOrDefault(x =>
            string.Equals(x.Code, normalized, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(match.Display)) return match.Display;
        return normalized.ToLowerInvariant() switch
        {
            "simplified chinese" => "中文",
            "traditional chinese" => "繁體中文",
            "english" => "English",
            "japanese" => "日本語",
            "korean" => "한국어",
            "spanish" => "Español",
            "french" => "Français",
            "german" => "Deutsch",
            _ => "中文"
        };
    }

    public static string CodeFromDisplay(string? display)
    {
        var normalized = (display ?? "").Trim();
        var match = Languages.FirstOrDefault(x =>
            string.Equals(x.Display, normalized, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(match.Code)) return match.Code;
        var byCode = Languages.FirstOrDefault(x =>
            string.Equals(x.Code, normalized, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(byCode.Code) ? "zh-CN" : byCode.Code;
    }
}

public static class UiTargetLanguageDisplay
{
    private static readonly (string Display,string Code)[] Languages=[("中文","zh-CN"),("繁体中文","zh-TW"),("英语","en"),("日语","ja"),("韩语","ko"),("西班牙语","es"),("法语","fr"),("德语","de")];
    public static IReadOnlyList<string> DisplayNames=>Languages.Select(x=>x.Display).ToArray();
    public static string DisplayFromCode(string? value){var normalized=(value??"").Trim();var byCode=Languages.FirstOrDefault(x=>x.Code.Equals(normalized,StringComparison.OrdinalIgnoreCase));if(!string.IsNullOrEmpty(byCode.Display))return byCode.Display;var legacy=TargetLanguageDisplay.DisplayFromCode(value);return legacy switch{"繁體中文"=>"繁体中文","English"=>"英语","日本語"=>"日语","한국어"=>"韩语","Español"=>"西班牙语","Français"=>"法语","Deutsch"=>"德语",_=>legacy};}
    public static string CodeFromDisplay(string? display){var normalized=(display??"").Trim();var match=Languages.FirstOrDefault(x=>x.Display==normalized);return !string.IsNullOrEmpty(match.Code)?match.Code:TargetLanguageDisplay.CodeFromDisplay(display);}
}
