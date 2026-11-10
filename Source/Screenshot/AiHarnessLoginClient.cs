using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal sealed record AiHarnessLoginNotice(string Status, string OfficialUrl = "", string UserCode = "");

// Native CLIs own login and token storage. This helper only displays a fixed official device
// URL and a short device code, and reads native login-status metadata; never credential files.
internal static class AiHarnessLoginClient
{
    internal static async Task<AiHarnessLoginNotice> CheckAsync(AiAgentOptions options, string dataDirectory, CancellationToken token)
    {
        options = Validate(options);
        string executable = AiHarnessEnvironment.ResolveExecutable(options), home = AiHarnessEnvironment.AccountHome(dataDirectory, options);
        await AiHarnessEnvironment.VerifyVersionAsync(executable, options.Backend, home, token).ConfigureAwait(false);
        var info = AiHarnessEnvironment.StartInfo(executable, home, home);
        if (options.Backend == "codex")
        {
            info.ArgumentList.Add("-c"); info.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            info.ArgumentList.Add("login"); info.ArgumentList.Add("status");
        }
        else { info.ArgumentList.Add("--pure"); info.ArgumentList.Add("providers"); info.ArgumentList.Add("list"); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = Process.Start(info) ?? throw new AiToolsException("harness_start");
        using var stop = deadline.Token.Register(() => AiHarnessEnvironment.Kill(process));
        Task<string> stdout = ReadBoundedAsync(process.StandardOutput, null, deadline.Token), stderr = ReadBoundedAsync(process.StandardError, null, deadline.Token);
        await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        string text = StripAnsi(await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false));
        bool loggedIn = options.Backend == "codex"
            ? process.ExitCode == 0 && text.Contains("Logged in using ChatGPT", StringComparison.OrdinalIgnoreCase)
            : process.ExitCode == 0 && Regex.IsMatch(text, @"(?im)\bOpenAI\s+oauth\s*$");
        return new(loggedIn ? "logged_in" : "login_required");
    }

    internal static async Task<AiHarnessLoginNotice> LoginAsync(AiAgentOptions options, string dataDirectory,
        Action<AiHarnessLoginNotice> progress, CancellationToken token)
    {
        options = Validate(options);
        string executable = AiHarnessEnvironment.ResolveExecutable(options), home = AiHarnessEnvironment.AccountHome(dataDirectory, options);
        await AiHarnessEnvironment.VerifyVersionAsync(executable, options.Backend, home, token).ConfigureAwait(false);
        var info = AiHarnessEnvironment.CreateLoginStartInfo(options, dataDirectory);
        info.CreateNoWindow = true; info.RedirectStandardInput = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        info.Environment["NO_COLOR"] = "1";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(15));
        using var process = Process.Start(info) ?? throw new AiToolsException("harness_start");
        using var stop = deadline.Token.Register(() => AiHarnessEnvironment.Kill(process));
        var gate = new object(); var current = new AiHarnessLoginNotice("awaiting_login"); bool completed = false; progress(current);
        AiHarnessLoginNotice Finish(AiHarnessLoginNotice result)
        { lock (gate) { completed = true; current = result; progress(result); } return result; }
        void Inspect(string text)
        {
            var parsed = ParseDeviceNotice(text);
            lock (gate)
            {
                if (completed || deadline.IsCancellationRequested) return;
                var next = new AiHarnessLoginNotice("awaiting_login", parsed.OfficialUrl.Length > 0 ? parsed.OfficialUrl : current.OfficialUrl,
                    parsed.UserCode.Length > 0 ? parsed.UserCode : current.UserCode);
                if (next != current) { current = next; progress(next); }
            }
        }
        try
        {
            Task<string> stdout = ReadBoundedAsync(process.StandardOutput, Inspect, deadline.Token), stderr = ReadBoundedAsync(process.StandardError, Inspect, deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            _ = await stdout.ConfigureAwait(false); _ = await stderr.ConfigureAwait(false);
            var result = process.ExitCode == 0 ? await CheckAsync(options, dataDirectory, token).ConfigureAwait(false) : new AiHarnessLoginNotice("error");
            return Finish(result);
        }
        catch (OperationCanceledException)
        {
            return Finish(new AiHarnessLoginNotice(token.IsCancellationRequested ? "cancelled" : "error"));
        }
        catch (AiToolsException) { return Finish(new AiHarnessLoginNotice("error")); }
        finally { AiHarnessEnvironment.Kill(process); }
    }

    internal static AiHarnessLoginNotice ParseDeviceNotice(string raw)
    {
        string text = StripAnsi(raw);
        // No arbitrary child-supplied URLs may enter a browser button. Do not accept query
        // strings, tokens in a URL, lookalike domains or executable paths.
        string url = Regex.IsMatch(text, @"https://auth\.openai\.com/codex/device(?=$|[\s\u001b])") ? "https://auth.openai.com/codex/device" : "";
        var code = Regex.Match(text, @"(?<![A-Za-z0-9-])[A-Z0-9]{4,8}-[A-Z0-9]{4,8}(?![A-Za-z0-9-])");
        return new("awaiting_login", url, code.Success ? code.Value : "");
    }
    private static AiAgentOptions Validate(AiAgentOptions options)
    {
        var value = options.Validated();
        if (value.AuthMode != "account" || value.Backend is not ("codex" or "opencode")) throw new AiToolsException("harness_auth_mode");
        if (value.ProviderId is not ("" or "openai") || value.Backend == "opencode" && value.ProviderId.Length == 0) throw new AiToolsException("harness_account_provider");
        return value;
    }
    private static string StripAnsi(string text) => Regex.Replace(text, "\u001b\\[[0-9;?]*[A-Za-z]", "");
    private static async Task<string> ReadBoundedAsync(StreamReader reader, Action<string>? progress, CancellationToken token)
    {
        var text = new System.Text.StringBuilder(); var buffer = new char[1024];
        while (true)
        {
            int count = await reader.ReadAsync(buffer, token).ConfigureAwait(false); if (count == 0) break;
            if (text.Length + count > 65536) throw new AiToolsException("harness_protocol");
            text.Append(buffer, 0, count); progress?.Invoke(text.ToString());
        }
        return text.ToString();
    }
}
