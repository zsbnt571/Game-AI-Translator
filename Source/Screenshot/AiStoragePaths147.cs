using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed record AiStorageInfo147
{
    internal string Root { get; init; } = "";
    internal string Tasks { get; init; } = "";
    internal string Conversations { get; init; } = "";
    internal string Projects { get; init; } = "";
    internal string Harness { get; init; } = "";
    internal string PendingRoot { get; init; } = "";
    internal bool IsIsolated { get; init; }
    internal string ErrorCode { get; init; } = "";
}

// AI storage is independent of translation storage. Capture actual launch input
// before FusionRuntime appends its normal --data-root argument.
internal static class AiStoragePaths147
{
    internal sealed record Location(string Path, string? CopyFrom = null, string? MigrationId = null, bool RequireExisting = false);
    private static readonly object gate = new();
    private static bool? explicitLaunchRoot;
    private static string? applicationRoot, activeRoot;
    private static FileStream? activeLease;
    internal static bool IsIsolated => explicitLaunchRoot ?? true;
    internal static void CaptureLaunchArguments(string[] args, string? applicationDirectory = null)
    {
        lock (gate)
        {
            if (explicitLaunchRoot.HasValue) return;
            explicitLaunchRoot = args.Any(x => x.StartsWith("--data-root=", StringComparison.OrdinalIgnoreCase) || x.Equals("--data-root", StringComparison.OrdinalIgnoreCase));
            applicationRoot = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        }
    }
    internal static string Root
    {
        get
        {
            lock (gate)
            {
                if (activeRoot is not null) return activeRoot;
                string app = applicationRoot ?? AppContext.BaseDirectory;
                string resolved = ResolveForLaunch(app, AppDataPaths.Root, IsIsolated);
                if (!IsIsolated)
                {
                    string lease = Path.Combine(resolved, AiStorageMigration147.LeaseName); AppDataPaths.RejectReparsePoints(lease);
                    try { activeLease = new FileStream(lease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { throw new AiToolsException("ai_storage_busy"); }
                }
                return activeRoot = resolved;
            }
        }
    }
    internal static AiStorageInfo147 GetCurrent()
    {
        string app = applicationRoot ?? AppContext.BaseDirectory, pending = "", error = "", root;
        try
        {
            root = Root;
            if (!IsIsolated && !Directory.Exists(root)) throw new AiToolsException("ai_storage_unavailable");
            if (!IsIsolated && ReadLocation(app) is { CopyFrom: not null } location) pending = ResolvePath(location.Path, app);
        }
        catch (Exception ex) when (ex is AiToolsException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex is AiToolsException known ? known.Code : "ai_storage_unavailable";
            root = activeRoot ?? (IsIsolated ? Path.Combine(AppDataPaths.Root, "ai-tools") : Path.Combine(app, "AI chat"));
            try { if (!IsIsolated && ReadLocation(app) is { } location) root = ResolvePath(location.CopyFrom ?? location.Path, app); }
            catch (Exception invalid) when (invalid is AiToolsException or IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return new() { Root = root, Tasks = Path.Combine(root, "tasks"), Conversations = Path.Combine(root, "conversations"),
            Harness = Path.Combine(root, "harness"), Projects = Path.Combine(root, "harness", "projects"), PendingRoot = pending, IsIsolated = IsIsolated, ErrorCode = error };
    }
    internal static string RequestLocation(string target)
    {
        if (IsIsolated) throw new AiToolsException("ai_storage_isolated");
        string app = applicationRoot ?? AppContext.BaseDirectory, current;
        try { current = Root; }
        catch (AiToolsException ex) when (ex.Code == "ai_storage_unavailable" && activeRoot is null)
        {
            using var repair = LocationLock(app);
            var unavailable = ReadLocation(app);
            target = ValidateRoot(target);
            if (unavailable is not { CopyFrom: null, RequireExisting: true } || AiStorageMigration147.IsEstablished(ResolvePath(unavailable.Path, app)) ||
                !AiStorageMigration147.IsEstablished(target)) throw new AiToolsException("ai_storage_unavailable");
            // Explicit repair may point at an existing AI library. It never
            // substitutes an empty library or rewrites the missing old location.
            WriteLocation(app, new(PortablePath(target, app), RequireExisting: true));
            return target;
        }
        using var operation = LocationLock(app);
        if (!Directory.Exists(current)) throw new AiToolsException("ai_storage_unavailable");
        if (ReadLocation(app) is { CopyFrom: not null }) throw new AiToolsException("ai_storage_pending");
        target = ValidateRoot(target);
        if (Same(current, target)) return current;
        AiStorageMigration147.ValidateDestination(current, target);
        WriteLocation(app, new(PortablePath(target, app), PortablePath(current, app), Guid.NewGuid().ToString("N"), true));
        return target;
    }
    // Explicit arguments and direct Initialize test callers stay inside their
    // supplied data root and never consult the application's AI location file.
    internal static string ResolveForLaunch(string app, string dataRoot, bool isolated)
    {
        app = Path.GetFullPath(app);
        if (isolated) return ValidateRoot(Path.Combine(dataRoot, "ai-tools"));
        using var operation = LocationLock(app);
        var location = ReadLocation(app);
        string target = ResolvePath(location?.Path ?? "AI chat", app);
        if (location?.CopyFrom is { } sourceValue)
        {
            if (!Guid.TryParseExact(location.MigrationId, "N", out _)) throw new AiToolsException("ai_storage_invalid");
            AiStorageMigration147.Copy(ResolvePath(sourceValue, app), target, location.MigrationId!);
            WriteLocation(app, new(PortablePath(target, app), RequireExisting: true));
            return target;
        }
        if (location?.RequireExisting == true && !AiStorageMigration147.IsEstablished(target)) throw new AiToolsException("ai_storage_unavailable");
        if (AiStorageMigration147.IsEstablished(target))
        {
            // Pin an established default too: later disappearance must never
            // revive an older legacy copy as an apparently current chat library.
            if (location?.RequireExisting != true) WriteLocation(app, new(PortablePath(target, app), RequireExisting: true));
            return target;
        }
        string legacy = ValidateRoot(Path.Combine(dataRoot, "ai-tools"));
        if (location is null && Directory.Exists(legacy) && Directory.EnumerateFileSystemEntries(legacy).Any())
        {
            AiStorageMigration147.ValidateDestination(legacy, target);
            var pending = new Location(PortablePath(target, app), PortablePath(legacy, app), Guid.NewGuid().ToString("N"), true);
            WriteLocation(app, pending);
            AiStorageMigration147.Copy(legacy, target, pending.MigrationId!);
        }
        else AiStorageMigration147.InitializeEmpty(target);
        WriteLocation(app, new(PortablePath(target, app), RequireExisting: true));
        return target;
    }
    internal static string ValidateRoot(string path)
    {
        try { return AppDataPaths.ValidateExplicitRoot(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new AiToolsException("ai_storage_invalid"); }
    }
    internal static bool Same(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static string ResolvePath(string path, string app) => ValidateRoot(Path.GetFullPath(path, app));
    private static string PortablePath(string path, string app) => path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(app)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        ? Path.GetRelativePath(app, path) : path;
    private static string LocationPath(string app) => Path.Combine(app, "ai-location.json");
    private static Location? ReadLocation(string app)
    {
        string path = LocationPath(app); AppDataPaths.RejectReparsePoints(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new AiToolsException("ai_storage_invalid");
        try { return JsonSerializer.Deserialize<Location>(File.ReadAllText(path)) ?? throw new AiToolsException("ai_storage_invalid"); }
        catch (JsonException) { throw new AiToolsException("ai_storage_invalid"); }
    }
    private static void WriteLocation(string app, Location location) => PortableDataStorage.WriteJson(LocationPath(app), location);
    private static FileStream LocationLock(string app)
    {
        AppDataPaths.RejectReparsePoints(app); Directory.CreateDirectory(app);
        string path = Path.Combine(app, ".fusion-ai-location.lock"); AppDataPaths.RejectReparsePoints(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new AiToolsException("ai_storage_busy"); }
    }
}
