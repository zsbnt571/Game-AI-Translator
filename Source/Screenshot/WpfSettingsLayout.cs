using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

internal static class WpfSettingsLayout
{
    internal sealed record Group(string Name, int[] Pages);
    // Category IDs remain unchanged: saved navigation and existing deep links
    // continue to address exactly the same settings pages.
    internal static readonly Group[] Groups =
    [
        new("常用", [0, 1, 2]), new("翻译", [3, 4, 5]),
        new("库与数据", [7, 6, 8]), new("工具", [10, 9])
    ];
    internal static string Text(string text) => text switch
    {
        "常用" => UiLanguage.Choose(text, "General", "Основное"),
        "翻译" => UiLanguage.Choose(text, "Translation", "Перевод"),
        "库与数据" => UiLanguage.Choose(text, "Library & data", "Библиотека и данные"),
        "工具" => UiLanguage.Choose(text, "Tools", "Инструменты"),
        "立即保存" => UiLanguage.Choose(text, "Saved immediately", "Сохраняется сразу"),
        "修改后保存" => UiLanguage.Choose(text, "Save after editing", "Сохраните изменения"),
        "预览后保存" => UiLanguage.Choose(text, "Preview, then save", "Предпросмотр, затем сохранение"),
        "保存后，下次启动游戏生效" => UiLanguage.Choose(text, "Save; applies next game launch", "После сохранения — при следующем запуске игры"),
        "导入或迁移数据" => UiLanguage.Choose(text, "Import or move data", "Импорт или перенос данных"),
        "主题与图标" => UiLanguage.Choose(text, "Theme & icons", "Тема и значки"),
        "界面文字" => UiLanguage.Choose(text, "Interface text", "Текст интерфейса"),
        "自定义主题" => UiLanguage.Text(text),
        "选择后立即保存；单个游戏固定使用的方案保持不变。" => UiLanguage.Choose(text, "Saved immediately. Games with a fixed profile keep their selection.", "Сохраняется сразу. Фиксированные профили игр не меняются."),
        "修改后点击“保存 API 方案”。测试连接不会自动保存。" => UiLanguage.Choose(text, "Click Save API profile after editing. Testing does not save changes.", "После изменений сохраните профиль API. Проверка не сохраняет изменения."),
        "语言、风格和提示单独保存，切换 API 方案不会改变这些内容。" => UiLanguage.Choose(text, "Language, style and prompt are saved separately and kept when switching API profiles.", "Язык, стиль и запрос сохраняются отдельно и не меняются при выборе другого API."),
        "保存待保存的通用、外观和截图选项；API 方案与翻译内容分别保存。" => UiLanguage.Choose(text, "Saves pending desktop options. API profiles and translation content have separate save buttons.", "Сохраняет общие настройки. Профили API и параметры перевода сохраняются отдельно."),
        "API 方案保存接口、密钥、模型、超时和高级参数。语言与提示请切换到“翻译内容”。" => UiLanguage.Choose(text, "API profiles store endpoint, key, model, timeouts and advanced parameters. Language and prompt are under Translation content.", "Профиль API хранит адрес, ключ, модель, тайм-ауты и параметры. Язык и запрос находятся в разделе параметров перевода."),
        _ => UiLanguage.Text(text)
    };
}

// The same responsive form layout is used by desktop options, API connections
// and translation content. Narrow windows stack labels above fields.
internal sealed class WpfSettingsFieldRow : WC.Grid
{
    protected override W.Size MeasureOverride(W.Size availableSize)
    {
        if (double.IsFinite(availableSize.Width) && Children.Count == 2)
        {
            double width = Math.Max(1, availableSize.Width);
            bool stacked = width < 580;
            var label = (W.FrameworkElement)Children[0];
            var field = (W.FrameworkElement)Children[1];
            bool blank = label is WC.TextBlock text && string.IsNullOrEmpty(text.Text);
            if (RowDefinitions.Count == 0)
            { RowDefinitions.Add(new() { Height = W.GridLength.Auto }); RowDefinitions.Add(new() { Height = W.GridLength.Auto }); }
            double caption = stacked || blank ? 0 : Math.Clamp(width / 4, 150, 206);
            ColumnDefinitions[0].Width = new(caption);
            label.Visibility = blank ? W.Visibility.Collapsed : W.Visibility.Visible;
            label.Margin = stacked ? new(0, 0, 0, 6) : new(0, 6, 16, 0);
            SetColumn(label, 0); SetColumnSpan(label, stacked ? 2 : 1);
            SetRow(field, stacked && !blank ? 1 : 0);
            SetColumn(field, stacked || blank ? 0 : 1);
            SetColumnSpan(field, stacked || blank ? 2 : 1);
            field.MaxWidth = Math.Max(field.MinWidth, width - caption);
        }
        return base.MeasureOverride(availableSize);
    }
}
