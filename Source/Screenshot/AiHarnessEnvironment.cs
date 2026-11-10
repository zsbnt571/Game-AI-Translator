using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Only these pinned, independently exercised native protocols are supported. Never launch a
// shell wrapper, inherit a desktop login, or silently substitute a direct paid API transport.
internal static class AiHarnessEnvironment
{
    internal const string CodexVersion = "0.166.0";
    internal const string OpenCodeVersion = "1.19.5";
    private const string CodexSha256 = "9D563EE4E0441FF9D59D612013E0460DDF8E5709735C2D9F6B98B7EF8CA4EB42";
    private const string OpenCodeSha256 = "D7B473388CB62555970E400CE54CDFB6D6573608BEEA6F8FDF382CD1A698A3FA";

    internal static (Uri Url, bool Responses) ApiEndpoint(ApiSettings settings, string backend)
    {
        var endpoint = AiConversationClient.Endpoint(settings);
        if (endpoint.Url.Query.Length != 0) throw new AiToolsException("harness_endpoint");
        // DeepSeek's official Codex instructions explicitly use wire_api=responses. Select
        // that documented protocol for this backend without modifying the saved API profile.
        if (backend == "codex" && endpoint.Url.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) && !endpoint.Responses)
        {
            var builder = new UriBuilder(endpoint.Url);
            builder.Path = builder.Path[..^"/chat/completions".Length] + "/responses";
            return (builder.Uri, true);
        }
        return endpoint;
    }

    internal static string ResolveExecutable(AiAgentOptions options)
    {
        if (options.Backend is not ("codex" or "opencode")) throw new AiToolsException("harness_backend");
        string name = options.Backend + ".exe";
        string candidate = options.HarnessPath.Trim();
        if (candidate.Length == 0) candidate = options.Backend == "codex"
            ? Path.Combine(AppContext.BaseDirectory, "adapters", "ai-harness", "codex", "bin", name)
            : Path.Combine(AppContext.BaseDirectory, "adapters", "ai-harness", options.Backend, name);
        if (!Path.IsPathFullyQualified(candidate) || !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
            throw new AiToolsException("harness_missing");
        AppDataPaths.RejectReparsePoints(candidate);
        return Path.GetFullPath(candidate);
    }

    internal static string AccountHome(string dataDirectory, AiAgentOptions options)
    {
        string provider = options.Backend == "codex" ? "openai" : options.ProviderId;
        if (provider.Length == 0) throw new AiToolsException("harness_provider");
        string root = AiNativeHome147.AccountHome(dataDirectory, options.Backend, provider);
        AppDataPaths.RejectReparsePoints(root);
        return root;
    }

    internal static ProcessStartInfo CreateLoginStartInfo(AiAgentOptions options, string dataDirectory)
    {
        options = options.Validated();
        if (options.AuthMode != "account") throw new AiToolsException("harness_auth_mode");
        if (options.Backend == "codex" && options.ProviderId is not ("" or "openai")) throw new AiToolsException("harness_provider");
        string home = AccountHome(dataDirectory, options);
        Directory.CreateDirectory(home);
        var info = StartInfo(ResolveExecutable(options), home, home);
        info.CreateNoWindow = false;
        info.RedirectStandardInput = false; info.RedirectStandardOutput = false; info.RedirectStandardError = false;
        if (options.Backend == "codex")
        {
            info.ArgumentList.Add("-c"); info.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            info.ArgumentList.Add("-c"); info.ArgumentList.Add("forced_login_method=\"chatgpt\"");
            info.ArgumentList.Add("login"); info.ArgumentList.Add("--device-auth");
        }
        else
        {
            if (options.ProviderId != "openai") throw new AiToolsException("harness_account_provider");
            info.ArgumentList.Add("--pure"); info.ArgumentList.Add("providers"); info.ArgumentList.Add("login");
            info.ArgumentList.Add("--provider"); info.ArgumentList.Add("openai");
            info.ArgumentList.Add("--method"); info.ArgumentList.Add("ChatGPT Pro/Plus (headless)");
        }
        return info;
    }

    internal static ProcessStartInfo StartInfo(string executable, string home, string workingDirectory)
    {
        foreach (string path in new[] { home, workingDirectory, Path.Combine(home, "tmp") })
        { AppDataPaths.RejectReparsePoints(path); Directory.CreateDirectory(path); }
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // Do not inherit provider keys, Codex desktop secrets, proxy auth, plugin paths or hooks.
        var keep = new[] { "SystemRoot", "WINDIR", "COMSPEC", "PATH", "PATHEXT", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS" };
        info.Environment.Clear();
        foreach (string key in keep) if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
        info.Environment["HOME"] = home; info.Environment["USERPROFILE"] = home;
        info.Environment["APPDATA"] = Path.Combine(home, "appdata");
        info.Environment["LOCALAPPDATA"] = Path.Combine(home, "localappdata");
        info.Environment["TEMP"] = info.Environment["TMP"] = Path.Combine(home, "tmp");
        info.Environment["CODEX_HOME"] = home;
        info.Environment["XDG_CONFIG_HOME"] = Path.Combine(home, "config");
        info.Environment["XDG_DATA_HOME"] = Path.Combine(home, "data");
        info.Environment["XDG_CACHE_HOME"] = Path.Combine(home, "cache");
        info.Environment["XDG_STATE_HOME"] = Path.Combine(home, "state");
        info.Environment["OPENCODE_TEST_HOME"] = home;
        info.Environment["OPENCODE_DISABLE_PROJECT_CONFIG"] = "true";
        info.Environment["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
        info.Environment["OPENCODE_DISABLE_MODELS_FETCH"] = "true";
        info.Environment["OPENCODE_EXPERIMENTAL_DISABLE_FILEWATCHER"] = "true";
        return info;
    }

    internal static async Task VerifyVersionAsync(string executable, string backend, string home, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        // Match the independently downloaded official package, not merely executable output
        // claiming a compatible version. No different binary is allowed to inherit API keys.
        await using (var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true))
        {
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, timeout.Token).ConfigureAwait(false));
            if (hash != (backend == "codex" ? CodexSha256 : OpenCodeSha256)) throw new AiToolsException("harness_version");
        }
        var info = StartInfo(executable, home, home); info.ArgumentList.Add("--version");
        using var process = Process.Start(info) ?? throw new AiToolsException("harness_start");
        using var stop = timeout.Token.Register(() => Kill(process));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        string actual = (await output.ConfigureAwait(false)).Trim(); _ = await error.ConfigureAwait(false);
        if (process.ExitCode != 0 || actual != (backend == "codex" ? "codex-cli " + CodexVersion : OpenCodeVersion))
            throw new AiToolsException("harness_version");
    }

    internal static void Kill(Process? process)
    { if (process is null) return; try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
    internal static string Toml(string value) => JsonSerializer.Serialize(value);
}
