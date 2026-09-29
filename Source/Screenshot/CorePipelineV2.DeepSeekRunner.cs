using System.Drawing;
using System.Text.Json;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static class CorePipelineV2DeepSeekRunner
{
    internal const string TransportMode = "PRODUCTION_PROVIDER_BATCH";
    internal const bool UsesParallelPerBlockTransport = false;
    internal const bool UsesMockTransport = false;
    internal const bool UsesIsolatedServicePerRun = true;
    internal static ITranslationProvider CreateProductionProvider(TranslationService service, ApiSettings settings) =>
        TranslationProviderRegistry.Get(settings.TranslationProviderKind).Factory(service);

    public static async Task<int> RunAsync(string corpusRoot, string rawRoot, string outputRoot, string settingsPath,
        string mode = "all", string? fixtureFilter = null)
    {
        Directory.CreateDirectory(outputRoot);
        var settings = ConfigurationManager.Load(settingsPath, false);
        var apiUrlOverride = Environment.GetEnvironmentVariable("CORE_V2_API_URL_OVERRIDE");
        if (!string.IsNullOrWhiteSpace(apiUrlOverride)) settings.ApiUrl = apiUrlOverride;
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) throw new InvalidOperationException("DeepSeek API is not configured.");
        if (string.Equals(mode, "minimal", StringComparison.OrdinalIgnoreCase))
            return await RunMinimalAsync(settings, outputRoot);

        var rows = new List<FixtureResult>();
        var fixtures = (from set in new[] { "KNOWN", "HELDOUT" }
                        from fixture in Directory.GetDirectories(Path.Combine(corpusRoot, set)).Order(StringComparer.Ordinal)
                        let id = Path.GetFileName(fixture)
                        where string.IsNullOrWhiteSpace(fixtureFilter) || string.Equals(id, fixtureFilter, StringComparison.Ordinal)
                        select (set, fixture, id)).ToArray();
        if (string.Equals(mode, "single", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(fixtureFilter))
            fixtures = fixtures.Where(x => x.id == "KNOWN-GARDEN-DIALOG-001").ToArray();

        foreach (var (set, fixture, id) in fixtures)
        {
            var output = Path.Combine(outputRoot, set, id);
            Directory.CreateDirectory(output);
            using var source = new Bitmap(Path.Combine(fixture, "source.png"));
            var rawPath = Path.Combine(rawRoot, id, "PPDocLayoutS-Rapid-regions.json");
            var raw = CorePipelineCorpusRunner.LoadRaw(rawPath);
            var document = CorePipelineEngine.Analyze(source.Size, raw, CorePipelineCorpusRunner.LoadVisualEvidence(rawPath));
            var service = new TranslationService();
            var provider = CreateProductionProvider(service, settings);
            var failures = await TranslateDocumentAsync(provider, settings, document, output);
            CorePipelineCorpusRunner.SaveStructureImages(source, document, output);
            var audits = CorePipelineCorpusRunner.Render(source, document, output);
            rows.Add(new FixtureResult(id, set, document.VisualBlocks.Count,
                document.Translations.Values.Count(x => x.State == BlockTranslationState.Accepted),
                document.Translations.Values.Count(x => x.State == BlockTranslationState.Failed),
                audits.Count(x => x.AtomicCommit), failures));

            if (string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase) &&
                id is "KNOWN-ARALI-CARD-001" or "KNOWN-MHA-LONGTEXT-001" or
                    "HELDOUT-SINSPIRE-EXTRAS-001" or "KNOWN-INVENTORY-TRADER-001" or
                    "HELDOUT-OLIVIA-PHONE-001" or "HELDOUT-BATTLE-CARD-HUD-001")
                await RepeatFreshAsync(settings, document, output, 2);
        }

        File.WriteAllText(Path.Combine(outputRoot, "CORE-PIPELINE-V2-DEEPSEEK-E2E.json"), JsonSerializer.Serialize(new
        {
            TransportMode, Provider = settings.ProviderDisplayName, settings.Model, Target = settings.TargetLanguage,
            SettingsPathHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(settingsPath)))),
            ApiKeyRecorded = false, AuthorizationRecorded = false, CacheHits = 0, Rows = rows, ReserveOpened = 0
        }, JsonOptions));
        return rows.Count > 0 && rows.All(x => x.Failed == 0) ? 0 : 2;
    }

    private static async Task<int> RunMinimalAsync(ApiSettings settings, string outputRoot)
    {
        var service = new TranslationService();
        var provider = CreateProductionProvider(service, settings);
        try
        {
            var result = await provider.TranslateAsync([new TranslationItem("MINIMAL-001", "Hello")], settings, null, CancellationToken.None);
            var translation = result.Translations.GetValueOrDefault("MINIMAL-001", "");
            File.WriteAllText(Path.Combine(outputRoot, "PRODUCTION-PATH-MINIMAL-REAL-API.json"), JsonSerializer.Serialize(new
            {
                TransportMode, ProviderType = provider.GetType().FullName, Source = "Hello", Translation = translation,
                Pass = !string.IsNullOrWhiteSpace(translation), result.RequestCount, result.WaitMs, CacheHits = 0,
                ApiKeyRecorded = false, AuthorizationRecorded = false
            }, JsonOptions));
            return string.IsNullOrWhiteSpace(translation) ? 2 : 0;
        }
        catch (Exception ex)
        {
            SaveException(Path.Combine(outputRoot, "PRODUCTION-PATH-MINIMAL-REAL-API-FAIL.json"), ex);
            return 2;
        }
    }

    private static async Task<string[]> TranslateDocumentAsync(ITranslationProvider provider, ApiSettings settings,
        CorePipelineDocument document, string output)
    {
        foreach (var block in document.VisualBlocks.Where(x => x.TextSelection == TextSelectionAction.Preserve))
        {
            var preserved = document.Translations[block.BlockId];
            preserved.TranslatedText = block.SourceText;
            preserved.State = BlockTranslationState.Preserved;
            preserved.FailureReason = block.TextSelectionReason;
            SaveSanitized(output, block, preserved, 0, 0, 0);
        }

        var blocks = document.VisualBlocks.Where(x => x.TextSelection == TextSelectionAction.Translate).ToArray();
        var items = blocks.Select(x => CoreTranslationItemFactory.Create(document, x, MapRole(x.RoleHint))).ToArray();
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var response = await provider.TranslateAsync(items, settings, null, CancellationToken.None);
                var failures = new List<string>();
                foreach (var block in blocks)
                {
                    var target = document.Translations[block.BlockId];
                    target.TranslatedText = response.Translations.GetValueOrDefault(block.BlockId, "").Trim();
                    if (!string.IsNullOrWhiteSpace(target.TranslatedText) && CorePipelineEngine.NumericTokensMatch(block.SourceText, target.TranslatedText))
                    {
                        target.State = BlockTranslationState.Accepted;
                        target.FailureReason = "";
                    }
                    else
                    {
                        target.State = BlockTranslationState.Failed;
                        target.FailureReason = string.IsNullOrWhiteSpace(target.TranslatedText) ? "MISSING_OR_EMPTY_RESPONSE" : "NUMERIC_FIDELITY_MISMATCH";
                        failures.Add(block.BlockId);
                    }
                    SaveSanitized(output, block, target, attempt, response.RequestCount, response.WaitMs);
                }
                return failures.ToArray();
            }
            catch (Exception ex)
            {
                last = ex;
                SaveException(Path.Combine(output, $"DEEPSEEK-BATCH-FAIL-{attempt:000}.json"), ex);
            }
        }
        foreach (var block in blocks)
        {
            var target = document.Translations[block.BlockId];
            target.State = BlockTranslationState.Failed;
            target.FailureReason = last?.GetType().Name ?? "UNKNOWN";
        }
        return blocks.Select(x => x.BlockId).ToArray();
    }

    private static async Task RepeatFreshAsync(ApiSettings settings, CorePipelineDocument document, string output, int count)
    {
        var translatable = document.VisualBlocks.Where(x => x.TextSelection == TextSelectionAction.Translate).ToArray();
        var focus = translatable.OrderByDescending(x => x.SourceText.Length).Take(3)
            .Concat(translatable.OrderBy(x => x.SourceText.Length).Take(5)).DistinctBy(x => x.BlockId).ToArray();
        for (var run = 1; run <= count; run++)
        {
            var service = new TranslationService();
            var provider = CreateProductionProvider(service, settings);
            try
            {
                var response = await provider.TranslateAsync(focus.Select(x => CoreTranslationItemFactory.Create(document, x, MapRole(x.RoleHint))).ToArray(), settings, null, CancellationToken.None);
                File.WriteAllText(Path.Combine(output, $"DEEPSEEK-FRESH-{run:00}.json"), JsonSerializer.Serialize(new
                {
                    TransportMode, CacheHits = 0, response.RequestCount, response.WaitMs,
                    Items = focus.Select(x => new { x.BlockId, SourceText = x.SourceText,
                        Translation = response.Translations.GetValueOrDefault(x.BlockId, ""),
                        NumericFidelity = CorePipelineEngine.NumericTokensMatch(x.SourceText, response.Translations.GetValueOrDefault(x.BlockId, "")) }),
                    ApiKeyRecorded = false, AuthorizationRecorded = false
                }, JsonOptions));
            }
            catch (Exception ex) { SaveException(Path.Combine(output, $"DEEPSEEK-FRESH-FAIL-{run:00}.json"), ex); }
        }
    }

    private static StructuredTextRole MapRole(string role) => Enum.TryParse<StructuredTextRole>(role, true, out var parsed)
        ? parsed : StructuredTextRole.Unknown;

    private static void SaveSanitized(string output, VisualBlock block, BlockTranslation result, int attempt, int requests, long waitMs)
    {
        File.WriteAllText(Path.Combine(output, $"DEEPSEEK-{block.BlockId}.json"), JsonSerializer.Serialize(new
        {
            block.BlockId, SourceText = block.SourceText, TextSelection = block.TextSelection.ToString(), block.TextSelectionReason,
            TranslationState = result.State.ToString(), result.TranslatedText, Attempt = attempt, RequestCount = requests,
            TotalWaitMs = waitMs, TransportMode, CacheHits = 0, ApiKeyRecorded = false, AuthorizationRecorded = false
        }, JsonOptions));
    }

    private static void SaveException(string path, Exception ex)
    {
        var chain = new List<object>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
            chain.Add(new { ErrorType = current.GetType().FullName, current.Message, HResult = $"0x{current.HResult:X8}" });
        File.WriteAllText(path, JsonSerializer.Serialize(new { Errors = chain, ApiKeyRecorded = false, AuthorizationRecorded = false }, JsonOptions));
    }

    private sealed record FixtureResult(string FixtureId, string Set, int Blocks, int Accepted, int Failed, int AtomicCommits, string[] FailureBlocks);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
