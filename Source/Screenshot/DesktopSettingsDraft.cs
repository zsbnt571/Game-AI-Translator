using System.ComponentModel;
using System.Reflection;

namespace ScreenshotTranslationUiTester;

// Only editable desktop preferences live here. Session/window placement and
// translation content belong to their existing stores, not to this draft.
internal sealed class DesktopSettingsDraft
{
    internal static readonly string[] Names =
    [
        nameof(ApiSettings.InterfaceLanguage), nameof(ApiSettings.ThemeMode), nameof(ApiSettings.CloseMainWindowBehavior),
        nameof(ApiSettings.UiFontMode), nameof(ApiSettings.UiFontFamily), nameof(ApiSettings.UiFontSize),
        nameof(ApiSettings.OcrLanguage), nameof(ApiSettings.CleanupStrength), nameof(ApiSettings.OcrLoad),
        nameof(ApiSettings.OcrCacheEnabled), nameof(ApiSettings.TranslationCacheEnabled), nameof(ApiSettings.ImageTranslationEnabled),
        nameof(ApiSettings.BackgroundComputeDevice), nameof(ApiSettings.BackgroundTreatment),
        nameof(ApiSettings.HideMainWindowDuringCapture), nameof(ApiSettings.HidePreviewWindowsDuringCapture),
        nameof(ApiSettings.CaptureToolbarPlacement),
        nameof(ApiSettings.PreviewDefaultImage), nameof(ApiSettings.PreviewDefaultText), nameof(ApiSettings.PreviewTextPanelVisible),
        nameof(ApiSettings.TranslationTextSource), nameof(ApiSettings.RememberLastPreviewSelection),
        nameof(ApiSettings.HistoryLimit), nameof(ApiSettings.HistoryCleanupByCount), nameof(ApiSettings.HistoryCleanupByAge),
        nameof(ApiSettings.HistoryRetentionDays), nameof(ApiSettings.HistoryCleanupBySpace), nameof(ApiSettings.HistoryMaximumMegabytes),
        nameof(ApiSettings.HistoryThumbnailSize), nameof(ApiSettings.HistoryTextSize),
        nameof(ApiSettings.PreviewWindowSizingMode), nameof(ApiSettings.FixedPreviewWidth), nameof(ApiSettings.FixedPreviewHeight),
        nameof(ApiSettings.PreviewTextFontMode), nameof(ApiSettings.PreviewTextFontFamily), nameof(ApiSettings.PreviewTextFontSize),
        nameof(ApiSettings.OverlayFontMode), nameof(ApiSettings.OverlayFontFamily), nameof(ApiSettings.OverlayFontSize),
        nameof(ApiSettings.OverlayBackgroundStyle), nameof(ApiSettings.OverlayBackgroundOpacity),
        nameof(ApiSettings.CustomThemeMainBackground), nameof(ApiSettings.CustomThemeSecondaryBackground),
        nameof(ApiSettings.CustomThemeText), nameof(ApiSettings.CustomThemeSecondaryText), nameof(ApiSettings.CustomThemeAccent), nameof(ApiSettings.CustomThemeBorder)
    ];
    private readonly Dictionary<string, SettingValue> fields = [];
    internal event Action<string>? Changed;
    internal DesktopSettingsDraft()
    {
        var defaults = new ApiSettings();
        foreach (var name in Names)
        {
            var property = typeof(ApiSettings).GetProperty(name)!;
            fields.Add(name, new(property, property.GetValue(defaults), () => Changed?.Invoke(name)));
        }
    }
    internal SettingValue Field(string name) => fields[name];
    internal T Get<T>(string name) => (T)fields[name].Value!;
    internal void Set(string name, object? value) => fields[name].Value = value;
    internal void Load(ApiSettings settings)
    { foreach (var field in fields.Values) field.Load(field.Property.GetValue(settings)); }
    internal void ApplyTo(ApiSettings settings)
    { foreach (var field in fields.Values) field.Property.SetValue(settings, field.Value); }
}

internal sealed class SettingValue(PropertyInfo property, object? initialValue, Action changed) : INotifyPropertyChanged
{
    private object? current = initialValue;
    internal PropertyInfo Property { get; } = property;
    public event PropertyChangedEventHandler? PropertyChanged;
    public object? Value
    {
        get => current;
        set
        {
            var next = value is null ? null : Property.PropertyType.IsInstanceOfType(value) ? value :
                Property.PropertyType.IsEnum ? Enum.ToObject(Property.PropertyType, value) : Convert.ChangeType(value, Property.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
            if (Equals(current, next)) return;
            current = next; PropertyChanged?.Invoke(this, new(nameof(Value))); changed();
        }
    }
    internal void Load(object? next)
    { if (!Equals(current, next)) { current = next; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
}
