using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Copied native databases may contain absolute paths to the preserved source.
// Start a root-scoped native home and carry only the pinned file-store login.
internal static class AiNativeHome147
{
    private static readonly object credentialsGate = new();
    internal static string ApiHome(string dataDirectory, AiAgentOptions options, ApiSettings settings, string taskId)
    {
        string identity = string.Join("\n", options.Backend, options.ProviderId, settings.ApiUrl, settings.ApiKey, taskId,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant());
        string stable = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(dataDirectory, "ai-harness", "sessions", stable, "home");
    }
    internal static string AccountHome(string dataDirectory, string backend, string provider)
    {
        string target = AccountPath(dataDirectory, backend, provider, dataDirectory);
        AppDataPaths.RejectReparsePoints(target);
        string relative = backend switch { "codex" => "auth.json", "opencode" => Path.Combine("data", "opencode", "auth.json"), _ => throw new AiToolsException("harness_backend") };
        // Fusion currently supports only the explicit OpenAI account login in
        // either assistant. Do not infer another provider's credential location.
        if (provider != "openai") return target;
        lock (credentialsGate)
        {
            string destination = Path.Combine(target, relative); AppDataPaths.RejectReparsePoints(destination);
            if (File.Exists(destination)) return target; // Never overwrite a newer login/token refresh.
            // An already initialized home with no credential may be signed out.
            // Do not resurrect an archived login after native logout removed it.
            if (Directory.Exists(target)) return target;
            string storageRoot = Path.GetDirectoryName(Path.GetFullPath(dataDirectory))!;
            var candidates = AiStorageMigration147.PreviousRoots(storageRoot)
                .Select(oldRoot => AccountPath(dataDirectory, backend, provider, Path.Combine(oldRoot, "harness")))
                .Append(LegacyAccountPath(dataDirectory, backend, provider));
            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (AiStoragePaths147.Same(candidate, target)) continue;
                AppDataPaths.RejectReparsePoints(candidate);
                if (!Directory.Exists(candidate)) continue;
                string source = Path.Combine(candidate, relative); AppDataPaths.RejectReparsePoints(source);
                // A missing/malformed newest login must not resurrect an older
                // archived login after the user signed out. Native login checks
                // remain the authority; copying a file never reports logged-in.
                if (!File.Exists(source) || new FileInfo(source).Length > 1024 * 1024) return target;
                try { using var json = JsonDocument.Parse(File.ReadAllBytes(source)); if (json.RootElement.ValueKind != JsonValueKind.Object) return target; }
                catch (JsonException) { return target; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".pending";
                try
                {
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { input.CopyTo(output); output.Flush(true); }
                    using (var original = File.OpenRead(source))
                    using (var copied = File.OpenRead(temporary))
                        if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(copied))) throw new AiToolsException("ai_storage_changed");
                    if (!File.Exists(destination)) File.Move(temporary, destination);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                break;
            }
        }
        return target;
    }
    internal static string AccountPath(string destinationDataDirectory, string backend, string provider, string identityDataDirectory)
    {
        string identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(identityDataDirectory)).ToUpperInvariant();
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider + "\n" + identity))).ToLowerInvariant()[..16];
        return Path.GetFullPath(Path.Combine(destinationDataDirectory, "ai-harness", backend + "-" + suffix));
    }
    internal static string LegacyAccountPath(string dataDirectory, string backend, string provider)
    {
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider))).ToLowerInvariant()[..16];
        return Path.GetFullPath(Path.Combine(dataDirectory, "ai-harness", backend + "-" + suffix));
    }
}
