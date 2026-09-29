using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ScreenshotTranslationUiTester;

/// <summary>Process-wide data placement, selected before any GUI/service initialization.</summary>
internal static class AppDataPaths
{
    private static string? _explicitRoot;
    private static bool _initialized;
    internal static bool IsInitialized => _initialized;
    internal static bool HasExplicitRoot => _explicitRoot is not null;
    internal static bool DisableGlobalInput { get; private set; }
    internal static bool IsDataRootVerification { get; private set; }
    internal static string DefaultRoot => Path.Combine(AppContext.BaseDirectory,"data");
    internal static string Root => _explicitRoot ?? DefaultRoot;
    internal static string SettingsPath => Path.Combine(Root, "settings.json");
    internal static string LogsRoot => Path.Combine(Root, "logs");
    // Preserve existing default diagnostic locations. An explicit data root owns every log.
    internal static string ProductLogsRoot => HasExplicitRoot ? LogsRoot : Path.Combine(AppContext.BaseDirectory, "logs");
    internal static string HistoryRoot => Path.Combine(Root, "history");
    internal static string CacheRoot => Path.Combine(Root, "cache");
    internal static string OutputRoot => Path.Combine(Root, "output");
    internal static string TempRoot => Path.Combine(Root, "temp");
    internal static string InstanceId => HasExplicitRoot ? BuildIdentity.StableApplicationId + ".Data." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())))[..24] : BuildIdentity.StableApplicationId;

    internal static void Initialize(string[] args)
    {
        if (_initialized) throw new InvalidOperationException("Application data paths are already initialized.");
        var roots = args.Where(x => x.StartsWith("--data-root=", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (roots.Length > 1) throw new ArgumentException("Specify --data-root only once.");
        if (args.Any(x => x.Equals("--data-root", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Use --data-root=<absolute local directory>.");
        var verify = args.Any(x => x.Equals("--verify-data-root", StringComparison.OrdinalIgnoreCase) ||
            x.Equals("--verify-data-root-readback", StringComparison.OrdinalIgnoreCase));
        var disabled = args.Any(x => x.Equals("--disable-global-input", StringComparison.OrdinalIgnoreCase));
        if (verify && (roots.Length != 1 || !disabled))
            throw new ArgumentException("Data-root verification requires --data-root and --disable-global-input.");
        var root = roots.Length == 1 ? ValidateExplicitRoot(roots[0]["--data-root=".Length..]) : null;
        if (root is not null)
        {
            // Validation precedes writes. Never fall back to the user's profile on a bad path.
            foreach (var name in new[] { "logs", "history", "cache", "output", "temp", "settings.json" })
                RejectReparsePoints(Path.Combine(root,name));
            foreach (var name in new[] { "logs", "history", "cache", "output", "temp" })
                Directory.CreateDirectory(Path.Combine(root, name));
            _explicitRoot = root;
            foreach (var pair in IsolatedEnvironment())
                Environment.SetEnvironmentVariable(pair.Key, pair.Value, EnvironmentVariableTarget.Process);
        }
        DisableGlobalInput = disabled;
        IsDataRootVerification = verify;
        _initialized = true;
    }

    internal static string ValidateExplicitRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) ||
            value.Length < 4 || value[1] != ':' || value[2] != '\\' || !char.IsLetter(value[0]))
            throw new ArgumentException("The data root must be an absolute local drive directory.");
        if (value[3..].Contains(':') || value.Split('\\').Skip(1).Any(x =>
            x is "." or ".." || x.EndsWith(' ') || x.EndsWith('.')))
            throw new ArgumentException("The data root contains an ambiguous path component.");
        var root = Path.GetFullPath(value).TrimEnd('\\');
        if (root.Length <= 3 || File.Exists(root)) throw new ArgumentException("Choose a dedicated data directory, not a drive or file.");
        RejectReparsePoints(root);
        return root;
    }

    private static void RejectReparsePoints(string path)
    {
        for(var current=path;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new ArgumentException("A data path cannot pass through a reparse point.");
    }

    internal static string ConstrainSettingsPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (HasExplicitRoot && !full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An explicit settings path must remain inside --data-root.");
        if(HasExplicitRoot)RejectReparsePoints(full);
        return full;
    }

    internal static Dictionary<string, string> IsolatedEnvironment() => new(StringComparer.Ordinal)
    {
        ["TEMP"] = Path.Combine(Root, "temp"), ["TMP"] = Path.Combine(Root, "temp"),
        ["PYTHONPYCACHEPREFIX"] = Path.Combine(CacheRoot, "python"),
        ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONNOUSERSITE"] = "1",
        ["PIP_CACHE_DIR"] = Path.Combine(CacheRoot, "pip"),
        ["XDG_CACHE_HOME"] = CacheRoot, ["MPLCONFIGDIR"] = Path.Combine(CacheRoot, "matplotlib"),
        ["HF_HOME"] = Path.Combine(CacheRoot, "huggingface"),
        ["PADDLEX_HOME"] = Path.Combine(CacheRoot, "paddlex"),
        ["PADDLE_PDX_CACHE_HOME"] = Path.Combine(CacheRoot, "paddlex")
    };

    internal static void ConfigureChildEnvironment(ProcessStartInfo info)
    {
        if (!HasExplicitRoot) return;
        foreach (var pair in IsolatedEnvironment()) info.Environment[pair.Key] = pair.Value;
    }

    internal static string RuntimeLogs(string runtimeRoot, string component) =>
        HasExplicitRoot ? Path.Combine(LogsRoot, component) : Path.Combine(runtimeRoot, "logs");
}
