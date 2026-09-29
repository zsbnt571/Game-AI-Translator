using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal static class FusionSecrets
{
    private const string Prefix = "dpapi:v1:";
    internal static string Protect(string text) => string.IsNullOrEmpty(text) || text.StartsWith(Prefix) ? text :
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));
    internal static string Unprotect(string text) => text.StartsWith(Prefix) ?
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text[Prefix.Length..]), null, DataProtectionScope.CurrentUser)) : text;
    internal static string Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value)!;
        Visit(node, true);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    internal static T Deserialize<T>(string json)
    {
        var node = JsonNode.Parse(json)!;
        Visit(node, false);
        return node.Deserialize<T>()!;
    }
    private static void Visit(JsonNode node, bool protect)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
                if (pair.Key == "ApiKey" && pair.Value is JsonValue v && v.TryGetValue<string>(out var key))
                    obj[pair.Key] = protect ? Protect(key) : Unprotect(key);
                else if (pair.Value is JsonObject or JsonArray) Visit(pair.Value, protect);
        if (node is JsonArray array) foreach (var value in array) if (value is not null) Visit(value, protect);
    }
}

internal static class FusionRuntime
{
    internal static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "runtime");
    internal static string Root { get; private set; } = DefaultRoot;
    internal const string ReadOnlyMessage = "OCR 环境和模型由用户合法提供，本 Alpha 不打包或下载；依赖检查不会修复、删除或移动文件。";
    internal static string ConfigurationWarning { get; private set; } = "";
    internal static bool IsSharedPath(string path)
    {
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    internal static string[] Prepare(string[] args)
    {
        if (!args.Any(x => x.StartsWith("--data-root=", StringComparison.OrdinalIgnoreCase)))
            args = args.Append("--data-root=" + PortableDataStorage.Resolve(AppContext.BaseDirectory)).ToArray();
        return args;
    }
    internal static void Initialize()
    {
        Root = DefaultRoot;
        ConfigurationWarning = "";
        var file = Path.Combine(AppContext.BaseDirectory, "dependencies.json");
        if (File.Exists(file))
        {
            try
            {
                if (new FileInfo(file).Length > 16384) throw new InvalidDataException("Runtime configuration is too large.");
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var configuredRoot = doc.RootElement.GetProperty("RuntimeRoot").GetString();
                if (!string.IsNullOrWhiteSpace(configuredRoot))
                {
                    if (Path.IsPathRooted(configuredRoot)) throw new InvalidDataException("Only app-local relative runtime paths are allowed.");
                    var resolved = Path.GetFullPath(configuredRoot, AppContext.BaseDirectory);
                    var appRoot = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!resolved.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Runtime path leaves the application directory.");
                    Root = resolved;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                ConfigurationWarning = "运行环境配置无效；仅检查程序目录内的 runtime，不读取其他位置。";
                Root = DefaultRoot;
            }
        }
        Environment.SetEnvironmentVariable("ST_BACKGROUND_RUNTIME_ROOT", Root);
        Environment.SetEnvironmentVariable("ST_BACKGROUND_MODEL_PATH", Path.Combine(Root,"background","lama_fp32.onnx"));
        Environment.SetEnvironmentVariable("ST_BACKGROUND_GPU_SITE", Path.Combine(Root,"background-gpu","site"));
        Environment.SetEnvironmentVariable("ST_FUSION_RUNTIME_ROOT", Root);
    }
}

internal sealed class FusionState
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, ApiSettings> Profiles { get; set; } = new();
    public string CommonProfile { get; set; } = "公共 · R4";
    public string? ScreenshotProfile { get; set; }
    public string? EmbeddedProfile { get; set; }
    public Dictionary<string,string> GameProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DefaultGameLaunchWithTranslation { get; set; }
    public string LastGame { get; set; } = "";
    public string ImportedLastGame { get; set; } = "";
    public string EmbeddedToggleKey { get; set; } = "F8";
    public Dictionary<string, FusionProfileInfo> ProfileInfo { get; set; } = new();
    public Dictionary<string, ApiSettings> ArchivedTemplates { get; set; } = new();
    public List<string> MigrationNotes { get; set; } = new();
}

internal sealed partial class FusionConfiguration
{
    private readonly string _path = Path.Combine(AppDataPaths.Root, "fusion-settings.json");
    internal FusionState State { get; private set; }
    internal FusionConfiguration()
    {
        if (File.Exists(_path)) State = FusionSecrets.Deserialize<FusionState>(File.ReadAllText(_path));
        else
        {
            State = new();
            State.Profiles["我的方案"] = new ApiSettings { ApiUrl="",ApiKey="",Model="" };
            State.CommonProfile="我的方案";
        }
        MigrateProfiles();
        foreach (var p in State.Profiles.Values) SafeDiagnosticOutput.RegisterCredential(p.ApiKey);
    }
    internal void Save()
    {
        Persist(State);
    }
    internal ApiSettings Effective(ApiSettings basis, bool embedded, string? editingName = null, ApiSettings? editing = null)
    {
        var name = (embedded ? State.EmbeddedProfile : State.ScreenshotProfile) ?? State.CommonProfile;
        var source = State.Profiles.TryGetValue(name,out var saved) ? saved : throw new InvalidOperationException("所选方案不存在，请重新选择。");
        var result = ApiSettingsSnapshot.Copy(basis);
        ApplyTranslation(result, source);
        return result;
    }
    internal static void ApplyTranslation(ApiSettings target, ApiSettings p)
    {
        target.ApiUrl=p.ApiUrl;target.ApiKey=p.ApiKey;target.AllowEmptyApiKey=p.AllowEmptyApiKey;target.Model=p.Model;target.TargetLanguage=NormalizeLanguage(p.TargetLanguage);
        target.SourceLanguage=p.SourceLanguage;target.TranslationStyle=p.TranslationStyle;target.CustomTranslationPrompt=p.CustomTranslationPrompt;
        target.PreserveIdentifiers=p.PreserveIdentifiers;target.PreserveNumbers=p.PreserveNumbers;target.PreserveVariables=p.PreserveVariables;
        target.ProviderDisplayName=p.ProviderDisplayName;target.TranslationProviderKind=p.TranslationProviderKind;
        target.FirstByteTimeoutSeconds=p.FirstByteTimeoutSeconds;target.RequestTimeoutSeconds=p.RequestTimeoutSeconds;
    }
    internal static string NormalizeLanguage(string? language) => UiTargetLanguageDisplay.CodeFromDisplay(UiTargetLanguageDisplay.DisplayFromCode(language));
    internal static string TranslationIdentity(ApiSettings s) => string.Join("|",s.ApiUrl.TrimEnd('/'),s.ApiKey,s.Model,NormalizeLanguage(s.TargetLanguage),s.SourceLanguage,s.TranslationStyle,s.CustomTranslationPrompt,s.PreserveNumbers,s.PreserveVariables,s.PreserveIdentifiers);
    internal static string PluginPrompt(ApiSettings s)
    {
        var style = s.TranslationStyle switch {
            TranslationStyle.Custom => "Follow the user's translation style.",
            _ => "Translation style: " + UiStrings.TranslationStyleName(s.TranslationStyle) + "."
        };
        return "Translate game UI and complete dialogue naturally. Preserve Unity rich-text tags, their nesting, and line breaks. " +
            "Source language: " + s.SourceLanguage + ". Target language: " + NormalizeLanguage(s.TargetLanguage) + ". " + style + " " +
            (s.PreserveNumbers ? "Preserve Arabic digits and quantities. Translate written number words naturally. " : "") +
            (s.PreserveVariables ? "Preserve variables and placeholders exactly. " : "") +
            (s.PreserveIdentifiers ? "Preserve identifiers and code. " : "") +
            "User instruction: " + s.CustomTranslationPrompt;
    }
}
