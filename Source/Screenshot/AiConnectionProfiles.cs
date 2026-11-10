using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Only AI connection fields are persisted. Translation profiles are never linked or rewritten.
internal sealed class AiConnectionProfiles
{
    private sealed class Document
    {
        public int Schema { get; set; } = 1;
        public List<Entry> Profiles { get; set; } = [];
    }
    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "";
        public bool AllowEmptyApiKey { get; set; }
        public int RequestTimeoutSeconds { get; set; } = 60;
        public AiAgentOptions? Options { get; set; }
    }
    private readonly string path;
    private readonly object gate = new();
    private Document document;
    internal AiConnectionProfiles(string path)
    {
        this.path = Path.GetFullPath(path);
        AppDataPaths.RejectReparsePoints(this.path);
        document = Load();
    }
    internal IReadOnlyList<WorkspaceProfileChoice> Choices()
    {
        lock (gate) return document.Profiles.Select(p => new WorkspaceProfileChoice(p.Id, p.Name)).ToArray();
    }
    internal string Name(string id)
    { lock(gate) return Find(id).Name; }
    internal ApiSettings Settings(string id)
    { lock(gate) { var entry=Find(id);var value=ToSettings(entry);value.ProviderDisplayName=entry.Name;return value; } }
    internal AiAgentOptions Options(string id)
    { lock(gate) return (Find(id).Options ?? new AiAgentOptions()).Clone(); }
    internal void SaveOptions(string id, AiAgentOptions options)
    {
        var clean = options.Validated();
        lock(gate)
        {
            var existing = Find(id);
            Validate(id, existing.Name, ToSettings(existing), clean);
            var next = Clone(document);
            next.Profiles.Single(p => p.Id == id).Options = clean;
            Persist(next); document = next;
        }
    }
    internal static ApiSettings ConnectionOnly(ApiSettings value) => new()
    {
        ApiUrl = value.ApiUrl?.Trim() ?? "", ApiKey = value.ApiKey ?? "", Model = value.Model?.Trim() ?? "",
        AllowEmptyApiKey = value.AllowEmptyApiKey, RequestTimeoutSeconds = Math.Clamp(value.RequestTimeoutSeconds, 15, 90),
        ProviderDisplayName = "AI", TranslationProviderKind = TranslationProviderKind.OpenAiCompatible,
        TranslationProvider = "openai-compatible"
    };
    internal static void Validate(string id, string name, ApiSettings settings)
        => Validate(id, name, settings, new AiAgentOptions());
    internal static void Validate(string id, string name, ApiSettings settings, AiAgentOptions options)
        => ValidateCore(id, name, settings, options, false);
    private static void ValidateCore(string id, string name, ApiSettings settings, AiAgentOptions options, bool unavailableKeyDuringLoad)
    {
        options = options.Validated();
        bool account = options.AuthMode == "account";
        if (!Guid.TryParseExact(id, "N", out _) || string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120 || name.Any(char.IsControl))
            throw new AiToolsException("ai_connection_name");
        if (settings is null)
            throw new AiToolsException("ai_connection_fields");
        if (settings.Model is null || (options.Backend == "direct" && string.IsNullOrWhiteSpace(settings.Model)) || settings.Model.Trim().Length > 256 || settings.Model.Any(char.IsControl))
            throw new AiToolsException("ai_connection_model");
        string url = settings.ApiUrl ?? "";
        if ((!account || !string.IsNullOrWhiteSpace(url)) &&
            (string.IsNullOrWhiteSpace(url) || url.Length > 2048 ||
             !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") ||
             endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0))
            throw new AiToolsException("ai_connection_fields");
        string key = settings.ApiKey ?? "";
        if ((!unavailableKeyDuringLoad && !account && !settings.AllowEmptyApiKey && string.IsNullOrWhiteSpace(key)) || key.Length > 8192 || key.Any(char.IsControl))
            throw new AiToolsException("ai_connection_key");
    }
    internal void Save(string id, string name, ApiSettings settings)
    {
        lock(gate)
            SaveCore(id, name, settings, document.Profiles.FirstOrDefault(p => p.Id == id)?.Options);
    }
    // One validated snapshot and one durable replace: connection + backend/auth metadata never split.
    internal void Save(string id, string name, ApiSettings settings, AiAgentOptions options)
    {
        var cleanOptions = options.Validated();
        lock(gate) SaveCore(id, name, settings, cleanOptions);
    }
    private void SaveCore(string id, string name, ApiSettings settings, AiAgentOptions? options)
    {
        Validate(id, name, settings, options ?? new AiAgentOptions());
        var clean = ConnectionOnly(settings);
        var next = Clone(document);
        var entry = next.Profiles.FirstOrDefault(p => p.Id == id);
        if (entry is null)
        {
            if (next.Profiles.Count >= 30) throw new AiToolsException("ai_connection_limit");
            entry = new() { Id = id }; next.Profiles.Add(entry);
        }
        entry.Name = name.Trim(); entry.Endpoint = clean.ApiUrl; entry.ApiKey = clean.ApiKey; entry.Model = clean.Model;
        entry.AllowEmptyApiKey = clean.AllowEmptyApiKey; entry.RequestTimeoutSeconds = clean.RequestTimeoutSeconds;
        entry.Options = options?.Clone();
        Persist(next); document = next;
    }
    internal void Delete(string id)
    {
        lock(gate)
        {
            var next = Clone(document);
            if (next.Profiles.RemoveAll(p => p.Id == id) == 0) throw new AiToolsException("select_profile");
            Persist(next); document = next;
        }
    }
    private Entry Find(string id) => document.Profiles.FirstOrDefault(p => p.Id == id) ?? throw new AiToolsException("select_profile");
    private static ApiSettings ToSettings(Entry e) => ConnectionOnly(new ApiSettings
    { ApiUrl=e.Endpoint, ApiKey=e.ApiKey, Model=e.Model, AllowEmptyApiKey=e.AllowEmptyApiKey, RequestTimeoutSeconds=e.RequestTimeoutSeconds });
    private static Document Clone(Document input) => new() { Profiles = input.Profiles.Select(p => new Entry
    { Id=p.Id, Name=p.Name, Endpoint=p.Endpoint, ApiKey=p.ApiKey, Model=p.Model, AllowEmptyApiKey=p.AllowEmptyApiKey, RequestTimeoutSeconds=p.RequestTimeoutSeconds, Options=p.Options?.Clone() }).ToList() };
    private Document Load()
    {
        if (!File.Exists(path)) { UnavailableCredentials.Forget(path); return new(); }
        try
        {
            if (new FileInfo(path).Length > 1024*1024) throw new InvalidDataException();
            var data = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), new JsonSerializerOptions { MaxDepth=12 });
            if (data is null || data.Schema != 1 || data.Profiles is null || data.Profiles.Count > 30 ||
                data.Profiles.Any(p=>p is null) || data.Profiles.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=data.Profiles.Count)
                throw new InvalidDataException();
            var pending = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach(var entry in data.Profiles)
            {
                if (entry.ApiKey is null || entry.RequestTimeoutSeconds is <15 or >90) throw new InvalidDataException();
                if (entry.ApiKey.Length > 0)
                {
                    const string prefix="dpapi:v1:";
                    if (!entry.ApiKey.StartsWith(prefix,StringComparison.Ordinal)) throw new InvalidDataException();
                    entry.ApiKey=UnavailableCredentials.Read(entry.ApiKey, KeyField(entry.Id), pending);
                }
                if (entry.Options is not null)
                {
                    entry.Options.XaiManagementApiKey = UnprotectSecret(entry.Options.XaiManagementApiKey, ManagementField(entry.Id), pending);
                    entry.Options = entry.Options.Validated();
                }
                ValidateCore(entry.Id,entry.Name,ToSettings(entry),entry.Options ?? new AiAgentOptions(), pending.ContainsKey(KeyField(entry.Id)));
            }
            UnavailableCredentials.Loaded(path, pending);
            return data;
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or CryptographicException or FormatException or AiToolsException)
        { throw new AiToolsException("ai_connection_storage"); }
    }
    private void Persist(Document next)
    {
        string? temporary = null;
        try
        {
            AppDataPaths.RejectReparsePoints(path);
            string directory=Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
            var saved=Clone(next);
            foreach(var entry in saved.Profiles)
                entry.ApiKey=UnavailableCredentials.ForSave(path, KeyField(entry.Id), entry.ApiKey);
            foreach(var entry in saved.Profiles) if(entry.Options is not null)
                entry.Options.XaiManagementApiKey=UnavailableCredentials.ForSave(path, ManagementField(entry.Id), entry.Options.XaiManagementApiKey);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(saved,new JsonSerializerOptions{WriteIndented=true});
            if(bytes.Length>1024*1024)throw new InvalidDataException();
            temporary=Path.Combine(directory, ".connections-"+Guid.NewGuid().ToString("N")+".tmp");
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush(true);}
            AppDataPaths.RejectReparsePoints(path); File.Move(temporary,path,true); temporary=null;
            foreach (var entry in next.Profiles)
            {
                UnavailableCredentials.Saved(path, KeyField(entry.Id), entry.ApiKey);
                if (entry.Options is not null) UnavailableCredentials.Saved(path, ManagementField(entry.Id), entry.Options.XaiManagementApiKey);
            }
            UnavailableCredentials.RetainFields(path, next.Profiles.SelectMany(e => new[] { KeyField(e.Id), ManagementField(e.Id) }));
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or CryptographicException)
        { throw new AiToolsException("ai_connection_storage"); }
        finally { if(temporary is not null)try{File.Delete(temporary);}catch{} }
    }
    private static string KeyField(string id) => "/Profiles/" + id + "/ApiKey";
    private static string ManagementField(string id) => "/Profiles/" + id + "/Options/XaiManagementApiKey";
    private static string UnprotectSecret(string? value, string field, Dictionary<string, string> pending)
    {
        if (value is null) throw new InvalidDataException();
        if (value.Length == 0) return "";
        const string prefix = "dpapi:v1:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException();
        return UnavailableCredentials.Read(value, field, pending);
    }
}
