using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public static class ConfigurationManager
{
    public const int CurrentSettingsSchemaVersion = 10;
    public const int DefaultFirstByteTimeoutSeconds = 30;
    public const int DefaultCompleteRequestTimeoutSeconds = 90;
    public const string TestConfigEnvironmentVariable = "SCREENSHOT_TRANSLATION_TESTER_CONFIG";
    private static readonly object SaveLock = new();
    internal static Action<string>? SaveStageHook { get; set; }

    public static string UserSettingsPath => AppDataPaths.SettingsPath;

    public static string ResolveSettingsPath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return AppDataPaths.ConstrainSettingsPath(explicitPath);
        if (AppDataPaths.HasExplicitRoot) return UserSettingsPath;
        var environmentPath = Environment.GetEnvironmentVariable(TestConfigEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(environmentPath)
            ? Path.GetFullPath(environmentPath)
            : UserSettingsPath;
    }

    public static ApiSettings Load(string path, bool allowLegacyMigration)
    {
        if (AppDataPaths.HasExplicitRoot){AppDataPaths.ConstrainSettingsPath(path);allowLegacyMigration = false;} // Never import user settings or credentials.
        if (allowLegacyMigration) TryRecoverKnownBlankSettings(path);
        if (File.Exists(path))
        {
            var settings = Read(path, out var requiresSave);
            if (requiresSave) Save(path, settings);
            return settings;
        }
        if (allowLegacyMigration)
        {
            var legacy = FindLegacy022Settings();
            if (legacy is not null)
            {
                var migrated = Read(legacy, out _);
                Save(path, migrated);
                WriteMigrationRecord(path, legacy, migrated.ApiKey);
                return migrated;
            }
        }
        var fresh=new ApiSettings();
        if(AppDataPaths.HasExplicitRoot)Save(path,fresh);
        return fresh;
    }

    public static void Save(string path, ApiSettings settings)
    {
        if(AppDataPaths.HasExplicitRoot)AppDataPaths.ConstrainSettingsPath(path);
        SafeDiagnosticOutput.RegisterCredential(settings.ApiKey);
        lock (SaveLock)
        {
            NormalizeTimeouts(settings);
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("配置路径没有有效目录。");
            Directory.CreateDirectory(directory);
            var temporaryPath = path + ".tmp";
            var backupPath = path + ".bak";
            var json = FusionSecrets.Serialize(settings);

            File.WriteAllText(temporaryPath, json);
            using (var validation = JsonDocument.Parse(File.ReadAllText(temporaryPath)))
            {
                _ = validation.RootElement.ValueKind;
            }
            _ = JsonSerializer.Deserialize<ApiSettings>(File.ReadAllText(temporaryPath))
                ?? throw new InvalidDataException("临时配置无法反序列化。");
            SaveStageHook?.Invoke("AfterTempValidated");

            if (File.Exists(path))
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Replace(temporaryPath, path, backupPath, true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
            SaveStageHook?.Invoke("AfterReplace");
        }
    }

    public static bool TryLoad(string path, bool allowLegacyMigration,
        out ApiSettings settings, out string error)
    {
        try
        {
            settings = Load(path, allowLegacyMigration);
            if (!File.Exists(path))
            {
                error = "配置文件不存在。";
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            settings = new ApiSettings();
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public static string KeyStatus(string? key)
    {
        var length = key?.Length ?? 0;
        return length == 0 ? "未配置（长度 0）" : $"已配置（********，长度 {length}）";
    }

    public static ApiSettings NormalizeTimeouts(ApiSettings settings)
    {
        // 0.2.5 初版曾把错误的 90 秒首字节值与完整超时机械组合成 90/91。
        // 该组合不满足最小间隔，按已知错误迁移结果恢复为正式默认值。
        if (settings.FirstByteTimeoutSeconds == 90 && settings.RequestTimeoutSeconds == 91)
        {
            settings.FirstByteTimeoutSeconds = DefaultFirstByteTimeoutSeconds;
            settings.RequestTimeoutSeconds = DefaultCompleteRequestTimeoutSeconds;
            return settings;
        }

        settings.FirstByteTimeoutSeconds = settings.FirstByteTimeoutSeconds <= 0
            ? DefaultFirstByteTimeoutSeconds
            : Math.Clamp(settings.FirstByteTimeoutSeconds, 10, 90);
        settings.RequestTimeoutSeconds = settings.RequestTimeoutSeconds <= 0
            ? DefaultCompleteRequestTimeoutSeconds
            : Math.Clamp(settings.RequestTimeoutSeconds, 30, 180);
        if (settings.RequestTimeoutSeconds - settings.FirstByteTimeoutSeconds < 30)
        {
            settings.RequestTimeoutSeconds = Math.Min(180,
                Math.Max(DefaultCompleteRequestTimeoutSeconds, settings.FirstByteTimeoutSeconds + 30));
        }
        return settings;
    }

    public static ApiSettings NormalizeAll(ApiSettings settings)
    {
        SafeDiagnosticOutput.RegisterCredential(settings.ApiKey);
        NormalizeTimeouts(settings);
        settings.SettingsSchemaVersion = CurrentSettingsSchemaVersion;
        settings.ApiUrl ??= "";
        settings.ApiKey ??= "";
        settings.Model ??= "";
        settings.TargetLanguage = string.IsNullOrWhiteSpace(settings.TargetLanguage)
            ? "Simplified Chinese" : settings.TargetLanguage;
        settings.OcrLanguage = string.IsNullOrWhiteSpace(settings.OcrLanguage) ? "自动" : settings.OcrLanguage;
        settings.HotKeyModifiers = string.IsNullOrWhiteSpace(settings.HotKeyModifiers) ? "Ctrl + Alt" : settings.HotKeyModifiers;
        settings.HotKeyKey = string.IsNullOrWhiteSpace(settings.HotKeyKey) ? "T" : settings.HotKeyKey;
        settings.StartCaptureBinding??=LegacyBinding(settings.HotKeyModifiers,settings.HotKeyKey,InputBindingDefaults.StartCapture);
        settings.CancelCaptureBinding??=InputBindingDefaults.CancelCapture;settings.ToggleResultsBinding??=InputBindingDefaults.ToggleResults;
        settings.PreviewZoomInBinding??=InputBindingDefaults.ZoomIn;settings.PreviewZoomOutBinding??=InputBindingDefaults.ZoomOut;settings.PreviewResetFitBinding??=InputBindingDefaults.ResetFit;
        settings.HistoryLimit = Math.Clamp(settings.HistoryLimit <= 0 ? 20 : settings.HistoryLimit, 10, 200);
        settings.PreviewWindowWidth = Math.Clamp(settings.PreviewWindowWidth <= 0 ? 1220 : settings.PreviewWindowWidth, 820, 10000);
        settings.PreviewWindowHeight = Math.Clamp(settings.PreviewWindowHeight <= 0 ? 800 : settings.PreviewWindowHeight, 560, 10000);
        if (!Enum.IsDefined(settings.PreviewWindowSizingMode)) settings.PreviewWindowSizingMode=PreviewWindowSizingMode.CurrentAuto;
        settings.FixedPreviewWidth=Math.Clamp(settings.FixedPreviewWidth<=0?1220:settings.FixedPreviewWidth,820,10000); settings.FixedPreviewHeight=Math.Clamp(settings.FixedPreviewHeight<=0?800:settings.FixedPreviewHeight,560,10000);
        settings.LastPreviewWidth=Math.Clamp(settings.LastPreviewWidth<=0?settings.PreviewWindowWidth:settings.LastPreviewWidth,820,10000); settings.LastPreviewHeight=Math.Clamp(settings.LastPreviewHeight<=0?settings.PreviewWindowHeight:settings.LastPreviewHeight,560,10000);
        settings.LastUserPreviewWidth=Math.Clamp(settings.LastUserPreviewWidth<=0?settings.LastPreviewWidth:settings.LastUserPreviewWidth,820,10000);
        settings.LastUserPreviewHeight=Math.Clamp(settings.LastUserPreviewHeight<=0?settings.LastPreviewHeight:settings.LastUserPreviewHeight,560,10000);
        if(!Enum.IsDefined(settings.PreviewChromeMode))settings.PreviewChromeMode=PreviewChromeMode.GameCompact;
        if(!Enum.IsDefined(settings.ThemeMode))settings.ThemeMode=ApplicationThemeMode.Night;
        if(!Enum.IsDefined(settings.CloseMainWindowBehavior))settings.CloseMainWindowBehavior=CloseMainWindowBehavior.Exit;
        if(!Enum.IsDefined(settings.HistoryThumbnailSize))settings.HistoryThumbnailSize=HistoryThumbnailSize.Medium;
        settings.HistoryRetentionDays=Math.Clamp(settings.HistoryRetentionDays<=0?30:settings.HistoryRetentionDays,1,3650);
        settings.HistoryMaximumMegabytes=Math.Clamp(settings.HistoryMaximumMegabytes<=0?512:settings.HistoryMaximumMegabytes,32,102400);
        settings.HistoryTextSize=Math.Clamp(settings.HistoryTextSize<=0?10:settings.HistoryTextSize,8,24);
        settings.CustomThemeMainBackground??="#121722";settings.CustomThemeSecondaryBackground??="#1D222C";settings.CustomThemeText??="#F4F7FB";settings.CustomThemeSecondaryText??="#BECDDF";settings.CustomThemeAccent??="#4EA1FF";settings.CustomThemeBorder??="#46546A";
        if(!Enum.IsDefined(settings.UiFontMode))settings.UiFontMode=UiFontMode.CurrentDefault;if(!Enum.IsDefined(settings.PreviewTextFontMode))settings.PreviewTextFontMode=PreviewTextFontMode.CurrentDefault;if(!Enum.IsDefined(settings.OverlayFontMode))settings.OverlayFontMode=OverlayFontMode.CurrentDefault;if(!Enum.IsDefined(settings.OverlayBackgroundStyle))settings.OverlayBackgroundStyle=TranslationOverlayBackgroundStyle.Automatic;
        settings.UiFontFamily??="";settings.PreviewTextFontFamily??="";settings.OverlayFontFamily??="";settings.UiFontSize=Math.Clamp(settings.UiFontSize<=0?10:settings.UiFontSize,7,24);settings.PreviewTextFontSize=Math.Clamp(settings.PreviewTextFontSize<=0?10:settings.PreviewTextFontSize,7,48);settings.OverlayFontSize=Math.Clamp(settings.OverlayFontSize,0,48);settings.OverlayBackgroundOpacity=Math.Clamp(settings.OverlayBackgroundOpacity,0,255);
        // V0 CurrentDefault meant each control's historical font. In V1 the same
        // serialized enum value means the centralized Product Default profile.
        // Custom family names are intentionally preserved even when not installed.
        if(settings.TypographyConfigVersion<FontManager.CurrentConfigVersion)settings.TypographyConfigVersion=FontManager.CurrentConfigVersion;
        if (!Enum.IsDefined(settings.PreviewDefaultImage)) settings.PreviewDefaultImage = PreviewDefaultImage.Original;
        if (!Enum.IsDefined(settings.PreviewDefaultText)) settings.PreviewDefaultText = PreviewDefaultText.Organized;
        if(settings.PreviewDefaultText==PreviewDefaultText.Hidden){settings.PreviewDefaultText=PreviewDefaultText.Organized;settings.PreviewTextPanelVisible=false;}
        if (!Enum.IsDefined(settings.TranslationTextSource)) settings.TranslationTextSource = TranslationTextSource.Auto;
        if (!Enum.IsDefined(settings.TranslationProviderKind)) settings.TranslationProviderKind = TranslationProviderKind.OpenAiCompatible;
        if (!Enum.IsDefined(settings.TranslationStyle)) settings.TranslationStyle = TranslationStyle.GameLocalization;
        if (!Enum.IsDefined(settings.VisualModel)) settings.VisualModel = VisualModelKind.Off;
        settings.ProviderDisplayName = string.IsNullOrWhiteSpace(settings.ProviderDisplayName)
            ? "DeepSeek (OpenAI Compatible)" : settings.ProviderDisplayName.Trim();
        settings.CustomTranslationPrompt ??= "";
        settings.TranslationPromptVersion = TranslationPromptBuilder.Version;
        return settings;
    }
    private static InputBinding LegacyBinding(string modifiers,string key,InputBinding fallback)
    {if(!Enum.TryParse<Keys>(key,true,out var k))return fallback;return new(InputBindingKind.Keyboard,k,Ctrl:modifiers.Contains("Ctrl",StringComparison.OrdinalIgnoreCase),Alt:modifiers.Contains("Alt",StringComparison.OrdinalIgnoreCase),Shift:modifiers.Contains("Shift",StringComparison.OrdinalIgnoreCase));}

    private static ApiSettings Read(string path, out bool requiresSave)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var settings = JsonSerializer.Deserialize<ApiSettings>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ApiSettings();

        settings.ApiKey = FusionSecrets.Unprotect(settings.ApiKey);
        SafeDiagnosticOutput.RegisterCredential(settings.ApiKey);
        var firstByteValue = DefaultFirstByteTimeoutSeconds;
        var hasFirstByte = root.TryGetProperty(nameof(ApiSettings.FirstByteTimeoutSeconds), out var firstByte)
            && firstByte.TryGetInt32(out firstByteValue);
        settings.FirstByteTimeoutSeconds = hasFirstByte
            ? firstByteValue
            : DefaultFirstByteTimeoutSeconds;

        var completeValue = DefaultCompleteRequestTimeoutSeconds;
        var legacyCompleteValue = DefaultCompleteRequestTimeoutSeconds;
        var hasComplete = root.TryGetProperty("CompleteRequestTimeoutSeconds", out var complete)
            && complete.TryGetInt32(out completeValue);
        var hasLegacyComplete = root.TryGetProperty(nameof(ApiSettings.RequestTimeoutSeconds), out var legacyComplete)
            && legacyComplete.TryGetInt32(out legacyCompleteValue);
        settings.RequestTimeoutSeconds = hasComplete
            ? completeValue
            : hasLegacyComplete ? legacyCompleteValue : DefaultCompleteRequestTimeoutSeconds;

        var originalFirstByte = settings.FirstByteTimeoutSeconds;
        var originalComplete = settings.RequestTimeoutSeconds;
        var oldSchema = settings.SettingsSchemaVersion;
        NormalizeAll(settings);
        requiresSave = oldSchema != CurrentSettingsSchemaVersion || !hasFirstByte || !hasComplete
            || originalFirstByte != settings.FirstByteTimeoutSeconds
            || originalComplete != settings.RequestTimeoutSeconds;
        return settings;
    }

    private static string? FindLegacy022Settings()
    {
        const string versionDirectory = "截图翻译界面测试器-0.2.2-test";
        const string fileName = "screenshot-tester.settings.json";
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, fileName) };
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, versionDirectory, fileName));
            candidates.Add(Path.Combine(current.FullName, "outputs", versionDirectory, fileName));
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
    }

    private static void TryRecoverKnownBlankSettings(string path)
    {
        if (!File.Exists(path) ||
            !string.Equals(Path.GetFullPath(path), Path.GetFullPath(UserSettingsPath),
                StringComparison.OrdinalIgnoreCase)) return;

        ApiSettings current;
        try
        {
            current = JsonSerializer.Deserialize<ApiSettings>(File.ReadAllText(path)) ?? new ApiSettings();
        }
        catch
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(current.ApiUrl) ||
            !string.IsNullOrWhiteSpace(current.ApiKey) ||
            !string.IsNullOrWhiteSpace(current.Model) ||
            !string.IsNullOrWhiteSpace(current.TargetLanguage)) return;

        var legacyPath = FindLegacy022Settings();
        if (legacyPath is null) return;
        var legacy = Read(legacyPath, out _);
        if (string.IsNullOrWhiteSpace(legacy.ApiUrl) || string.IsNullOrWhiteSpace(legacy.ApiKey) ||
            string.IsNullOrWhiteSpace(legacy.Model) || string.IsNullOrWhiteSpace(legacy.TargetLanguage) ||
            string.IsNullOrWhiteSpace(legacy.OcrLanguage)) return;

        legacy.FirstByteTimeoutSeconds = DefaultFirstByteTimeoutSeconds;
        legacy.RequestTimeoutSeconds = DefaultCompleteRequestTimeoutSeconds;
        var blankBackup = path + $".blank-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(path, blankBackup, false);
        Save(path, legacy);
        WriteRecoveryRecord(path, legacyPath, legacy.ApiKey);
    }

    private static void WriteRecoveryRecord(string settingsPath, string sourcePath, string? key)
    {
        var recordPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, "recovery.log");
        File.AppendAllText(recordPath,
            $"{DateTimeOffset.Now:O} restoredFrom={sourcePath} keyConfigured={!string.IsNullOrWhiteSpace(key)} keyLength={key?.Length ?? 0}{Environment.NewLine}");
    }

    private static void WriteMigrationRecord(string settingsPath, string sourcePath, string? key)
    {
        var recordPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, "migration.log");
        File.AppendAllText(recordPath,
            $"{DateTimeOffset.Now:O} migratedFrom={sourcePath} keyConfigured={!string.IsNullOrWhiteSpace(key)} keyLength={key?.Length ?? 0}{Environment.NewLine}");
    }
}
