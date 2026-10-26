using System.Globalization;
using System.Text;

namespace ScreenshotTranslationUiTester;

// Examples do not restrict selectable fonts or preview text to these languages.
// UI language and the game's translation target are independent settings.
internal static class GameFontPreview
{
    internal static string Sample(string? targetLanguage)
    {
        var code = (targetLanguage ?? "").Trim().Replace('_', '-').ToLowerInvariant();
        return code switch
        {
            "zh-tw" or "zh-hk" or "zh-hant" or "traditional chinese" => "你好！開始遊戲 · 儲存 · 123",
            "中文" or "simplified chinese" => "你好！开始游戏 · 保存 · 123",
            "русский" or "russian" => "Привет! Новая игра · Сохранить · 123",
            "日本語" or "japanese" => "こんにちは！ニューゲーム · セーブ · 123",
            "한국어" or "korean" => "안녕하세요! 새 게임 · 저장 · 123",
            _ => code.Split('-')[0] switch
            {
                "zh" => "你好！开始游戏 · 保存 · 123",
                "ja" => "こんにちは！ニューゲーム · セーブ · 123",
                "ko" => "안녕하세요! 새 게임 · 저장 · 123",
                "ru" => "Привет! Новая игра · Сохранить · 123",
                "uk" => "Вітаємо! Нова гра · Зберегти · 123",
                "el" => "Γεια σας! Νέο παιχνίδι · Αποθήκευση · 123",
                "ar" => "مرحبًا! لعبة جديدة · حفظ · 123",
                "he" => "שלום! משחק חדש · שמירה · 123",
                "hi" => "नमस्ते! नया खेल · सहेजें · 123",
                "th" => "สวัสดี! เริ่มเกมใหม่ · บันทึก · 123",
                "vi" => "Xin chào! Trò chơi mới · Lưu · 123",
                "fr" => "Bonjour ! Nouvelle partie · Sauvegarder · 123",
                "de" => "Hallo! Neues Spiel · Speichern · 123",
                "es" => "¡Hola! Nueva partida · Guardar · 123",
                "pt" => "Olá! Novo jogo · Salvar · 123",
                _ => "Hello! New game · Save · 123"
            }
        };
    }

    internal static string Coverage(GameFontCoverage.Result result)
    {
        if (result.Checked == 0) return UiLanguage.Text("输入预览文字可检查所选字体是否缺字。");
        if (result.Missing.Length == 0) return UiLanguage.Text("所选字体包含全部预览字符。字形连接和实际效果请在游戏中确认。");
        string codes = string.Join(", ", result.Missing.Take(10).Select(value => "U+" + value.ToString("X4", CultureInfo.InvariantCulture)));
        return UiLanguage.Text("所选字体缺少以下预览字符：") + codes
            + (result.Missing.Length > 10 ? " …" : "") + "\n"
            + UiLanguage.Text("预览可能由系统补字；游戏未必支持相同的补字方式。");
    }

    internal static bool RightToLeft(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            return rune.Value is >= 0x0590 and <= 0x08ff or >= 0xfb1d and <= 0xfdff or >= 0xfe70 and <= 0xfeff or >= 0x1e900 and <= 0x1e95f;
        }
        return false;
    }
}
