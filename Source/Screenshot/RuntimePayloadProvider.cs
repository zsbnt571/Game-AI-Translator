using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Changes only the source of installation bytes. The catalogue pins existing packages;
// it is an integrity contract, not permission to redistribute those packages.
internal static class RuntimePayloadProvider
{
    internal const string PackageVersion = "0.6.0.85";
    internal const string RootEnvironmentVariable = "GAME_AI_TRANSLATOR_PAYLOAD_ROOT";
    internal sealed record Package(string Id, string Engine, string Backend, string Architecture,
        string Version, string FileName, long Bytes, string Sha256);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Package[] Packages = ReadCatalog();

    private static Package[] ReadCatalog()
    {
        using var stream = typeof(RuntimePayloadProvider).Assembly.GetManifestResourceStream("Fusion.RuntimePayloadCatalog.json")
            ?? throw new InvalidDataException("缺少运行依赖版本清单。");
        return JsonSerializer.Deserialize<Package[]>(stream, JsonOptions)
            ?? throw new InvalidDataException("运行依赖版本清单无效。");
    }

    internal static Stream OpenUnity(string backend, string architecture)
    {
        string id = (backend, architecture) switch
        {
            ("Mono", "x86") => "unity-mono-x86",
            ("Mono", "x64") => "unity-mono-x64",
            ("IL2CPP", "x64") => "unity-il2cpp-x64",
            _ => throw new IOException("没有匹配此 Unity 后端与位数的翻译组件。")
        };
        return Open(id, "Unity", backend, architecture, PackageVersion);
    }

    internal static Stream Open(string id, string engine, string backend, string architecture, string version)
    {
        var expected = Packages.SingleOrDefault(p => p.Id == id)
            ?? throw new IOException("未登记的运行依赖包：" + id);
        if (expected.Engine != engine || expected.Backend != backend || expected.Architecture != architecture || expected.Version != version)
            throw new IOException("运行依赖的引擎、后端、位数或版本不匹配：" + id);

        string? configured = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && !Path.IsPathFullyQualified(configured))
            throw new IOException(RootEnvironmentVariable + " 必须是明确的绝对目录。");
        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "runtime-payloads") : configured);
        string path = Path.Combine(root, expected.Id, expected.Version, expected.FileName);
        RejectLinks(path);
        string metadata = path + ".json";
        RejectLinks(metadata);
        if (!File.Exists(path) || !File.Exists(metadata))
            throw new IOException($"缺少本地运行依赖 {id}（{version}，{backend}/{architecture}）。请合法提供对应运行包及清单到 {Path.GetDirectoryName(path)}。程序不会自动下载。");

        using (var descriptor = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (descriptor.Length > 16384) throw new InvalidDataException("运行依赖清单超过限制：" + id);
            Package? actual;
            try { actual = JsonSerializer.Deserialize<Package>(descriptor, JsonOptions); }
            catch (JsonException ex) { throw new InvalidDataException("运行依赖清单格式无效：" + id, ex); }
            if (actual is null || actual.Id != expected.Id || actual.Engine != expected.Engine || actual.Backend != expected.Backend ||
                actual.Architecture != expected.Architecture || actual.Version != expected.Version || actual.FileName != expected.FileName ||
                actual.Bytes != expected.Bytes || !string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("运行依赖清单与固定版本不符（包括后端、位数、版本及 SHA-256）：" + id);
        }

        // Keep the verified handle open; deny replacement/writes while installers read it.
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (input.Length != expected.Bytes || !string.Equals(Convert.ToHexString(SHA256.HashData(input)), expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("运行依赖 SHA-256 或大小不匹配，未开始安装：" + id);
            input.Position = 0;
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("运行依赖路径不能包含文件或目录链接。");
    }
}
