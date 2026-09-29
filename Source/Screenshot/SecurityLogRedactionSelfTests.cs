using System.Reflection;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class SecurityLogRedactionSelfTests
{
    private const string First = "SYNTHETIC_ONLY_CREDENTIAL_ALPHA_92Z";
    private const string Second = "SYNTHETIC_ONLY_CREDENTIAL_BETA_51Q";
    private const string BearerOne = "SYNTHETIC_ONLY_BEARER_UNREGISTERED_X1";
    private const string BearerTwo = "SYNTHETIC_ONLY_BEARER_UNREGISTERED_X2";
    private static readonly string[] Markers = [First, Second, BearerOne, BearerTwo];

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var auditRoot = Environment.GetEnvironmentVariable("ST_AUDIT_ROOT");
        if (string.IsNullOrWhiteSpace(auditRoot) ||
            !Path.GetFullPath(AppLog.Root).StartsWith(Path.GetFullPath(auditRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AppLog must be verified inside the isolated test root before log tests.");
        var rows = new List<object>();
        var fail = 0;
        var serial = 0;
        void Test(string name, Action action)
        {
            try { action(); rows.Add(new { Name = name, Pass = true }); }
            catch (Exception ex)
            {
                fail++;
                rows.Add(new { Name = name, Pass = false, ErrorType = ex.GetType().Name,
                    Detail = SafeDiagnosticOutput.ExceptionSummary(ex) });
            }
        }
        static void Require(bool value, string reason)
        { if (!value) throw new InvalidOperationException(reason); }
        static void Clean(string text)
        { foreach (var marker in Markers) Require(!text.Contains(marker, StringComparison.Ordinal), "synthetic marker survived output"); }
        string Log(string message, Exception? error = null)
        {
            var category = "safe-output-test-" + (++serial).ToString("00");
            var path = Path.Combine(AppLog.Root, category + ".log");
            AppLog.Write(category, message, error);
            var text = File.ReadAllText(path); Clean(text); return text;
        }
        const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        static object? Call(string name, params object[] parameters) =>
            typeof(TranslationService).GetMethod(name, PrivateStatic)!.Invoke(null, parameters);

        Test("Configured credential registered by normalization without persistence", () =>
        {
            var settings = new ApiSettings { ApiKey = First };
            ConfigurationManager.NormalizeAll(settings);
            Require(settings.ApiKey == First, "credential value was mutated");
            Require(Log("configured=" + First).Contains("[REDACTED]"), "exact credential not protected");
        });
        Test("Direct service settings register credential even for zero-request batch", () =>
        {
            var settings = new ApiSettings { ApiKey = Second };
            var result = new TranslationService().TranslateBatchAsync([], settings).GetAwaiter().GetResult();
            Require(result.RequestCount == 0, "empty batch used network");
            Log("second=" + Second);
        });
        Test("Every Bearer token across case whitespace and quoted delimiters", () =>
        {
            Log($"Authorization: bEaReR {BearerOne}\r\nother=BEARER\t{BearerTwo}; quoted=Bearer '{BearerOne}' / Bearer \"{BearerTwo}\"");
            var result = SafeDiagnosticOutput.Redact($"Bearer Bearer {BearerOne} then Bearer\r\n{BearerTwo}");
            Clean(result);
            const string punctuated = "SYNTHETIC_ONLY_PUNCTUATED,SUFFIX";
            SafeDiagnosticOutput.RegisterCredential(punctuated);
            Require(!SafeDiagnosticOutput.Redact("Bearer " + punctuated).Contains("SUFFIX"), "credential fragment survived token scanning");
        });
        Test("Message exception inner exception and aggregate all protected", () =>
        {
            var error = new AggregateException("outer " + First,
                new InvalidOperationException("inner " + Second,
                    new IOException("nested Bearer " + BearerOne)),
                new ArgumentException("other Bearer " + BearerTwo));
            var text = Log("message " + Second, error);
            Clean(AppLog.LastError);
            Require(text.Contains("AggregateException") && text.Contains("IOException"), "error types lost");
        });
        Test("Redaction precedes truncation at credential boundary", () =>
        {
            var raw = new string('-', 395) + First + " tail";
            var summary = (string)Call("SafeSnippet", raw)!;
            Clean(summary);
            Require(!summary.Contains("SYNTH", StringComparison.Ordinal), "truncated credential prefix survived");
            Require(summary.Length <= 401, "summary bound changed");
            Require(raw.EndsWith(First + " tail", StringComparison.Ordinal), "input copy mutated");
        });
        Test("Provider error message safe before exception creation", () =>
        {
            var body = JsonSerializer.Serialize(new { error = new { message = First + " / Bearer " + BearerTwo } });
            var summary = (string)Call("ExtractApiError", body)!;
            Clean(summary); Log("provider failure", new InvalidOperationException(summary));
            Require(body.Contains(First, StringComparison.Ordinal), "raw provider body changed");
        });
        Test("Malformed provider body fallback safe", () =>
        {
            var summary = (string)Call("ExtractApiError", "malformed " + First + " Bearer " + BearerOne)!;
            Clean(summary);
        });
        Test("Retry diagnostic and final service sink use same output policy", () =>
        {
            TranslationService.DiagnosticLog($"retry attempt=2 raw={First} Bearer {BearerOne} Bearer {BearerTwo}");
            Call("WriteLog", "raw service exception=" + Second);
            var path = Path.Combine(AppDataPaths.ProductLogsRoot, "translation.log");
            Clean(File.ReadAllText(path));
        });
        Test("Parse failure output preserves diagnostic type without response credential", () =>
        {
            try { Call("ParseApiResponse", "not-json " + First + " Bearer " + BearerOne); }
            catch (TargetInvocationException ex)
            {
                var text = Log("parse failure", ex.InnerException);
                Require(text.Contains("BatchJsonException"), "parse error type missing");
                return;
            }
            throw new InvalidOperationException("malformed parse unexpectedly succeeded");
        });
        Test("Credential-bearing parse metadata omits raw length and hash", () =>
        {
            Call("WriteParseDiagnostics", "safe-output-metadata", First, "SYNTHETIC_RAW_HASH_MUST_NOT_BE_LOGGED", 1, 0,
                "retry", new JsonException("parse " + First));
            var log = File.ReadAllText(Path.Combine(AppDataPaths.ProductLogsRoot, "translation.log"));
            Require(!log.Contains("SYNTHETIC_RAW_HASH_MUST_NOT_BE_LOGGED"), "sensitive response fingerprint logged");
            Require(log.Contains("response_length=REDACTED response_sha256=REDACTED"), "sensitive metadata not marked");
            Clean(log);
        });
        Test("Raw request and parsed translation remain byte-for-byte semantic inputs", () =>
        {
            var settings = new ApiSettings { ApiKey = First };
            var items = new[] { new TranslationItem("TEST-CORE", "line 1\nC:\\new\\notes " + First,
                StructuredTextRole.Unknown, ["SOURCE-1"], TranslationIdentityContract.CoreV2Block) };
            using var request = (HttpRequestMessage)Call("BuildBatchRequest", items, settings)!;
            Require(request.Headers.Authorization?.Parameter == First, "HTTP credential changed");
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var requestJson = JsonDocument.Parse(body);
            var user = requestJson.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var input = JsonDocument.Parse(user[(user.IndexOf('['))..]);
            Require(input.RootElement[0].GetProperty("text").GetString() == items[0].Text, "source text changed");
            var content = JsonSerializer.Serialize(new { translations = new[] { new { id = "TEST-CORE", translation = items[0].Text } } });
            var response = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            var parsed = TranslationService.ParseCoreV2PartialResponseForRecovery(response, ["TEST-CORE"], "safe-output-contract");
            Require(parsed.Translations["TEST-CORE"] == items[0].Text, "parser data was redacted");
            Clean(File.ReadAllText(Path.Combine(AppDataPaths.ProductLogsRoot, "translation.log")));
        });
        Test("Normal diagnostics preserve useful content", () =>
        {
            var normal = "HTTP 429 bounded retry attempt=2; malformed JSON";
            Require(SafeDiagnosticOutput.Redact(normal) == normal, "normal diagnostic changed");
            var text = Log(normal, new InvalidOperationException("ordinary diagnostic"));
            Require(text.Contains(normal) && text.Contains("InvalidOperationException: ordinary diagnostic"), "normal exception missing");
        });
        Test("Exception formatting failure is fail-closed", () =>
        {
            var text = Log("message " + First, new BrokenDiagnosticException());
            Require(text.Contains("DIAGNOSTIC_REDACTION_FAILED:InvalidOperationException"), "safe fallback not present");
            Clean(text);
        });
        File.WriteAllText(Path.Combine(output, "SEC-LOG-SELFTESTS.json"), JsonSerializer.Serialize(new
        {
            TestOnly = true, RealApiCalls = 0, RealCredentialRead = false,
            Pass = rows.Count - fail, Fail = fail, AppLogRoot = AppLog.Root,
            TranslationLog = Path.Combine(AppDataPaths.ProductLogsRoot, "translation.log"), Rows = rows
        }, new JsonSerializerOptions { WriteIndented = true }));
        return fail == 0 ? 0 : 1;
    }

    private sealed class BrokenDiagnosticException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("formatting failure " + First);
    }
}
