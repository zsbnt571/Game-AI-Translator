using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenshotTranslationUiTester.CorePipelineV2;
namespace ScreenshotTranslationUiTester;

internal static class NativeOcrSemanticPromptSelfTests
{
    internal static int Run(string output, string taskRoot)
    {
        Directory.CreateDirectory(output);
        var rows = new List<object>(); var failed = 0;
        void Check(string name, bool pass, object evidence)
        { if (!pass) failed++; rows.Add(new { Name = name, Pass = pass, Evidence = evidence }); }
        var settings = new ApiSettings { TargetLanguage = "zh-CN", ApiUrl = "https://api.deepseek.com",
            Model = "deepseek-v4-flash", PreserveIdentifiers = true, PreserveNumbers = true,
            PreserveVariables = true, TranslationStyle = TranslationStyle.GameLocalization };
        var prompt = TranslationPromptBuilder.BuildSystemPrompt(settings);
        Check("prompt_identity_version", TranslationPromptBuilder.Version ==
            "settings1.2-core-v2-ocr-semantic-completeness1", new { TranslationPromptBuilder.Version });
        Check("grammar_evidence_required", prompt.Contains("strong, consistent evidence", StringComparison.Ordinal),
            new { PromptHash = TranslationPromptBuilder.PromptHash(settings) });
        Check("unknown_names_not_guessed", prompt.Contains("Never guess or rename an unfamiliar person, brand, code, or identifier.", StringComparison.Ordinal), new { });
        Check("complete_subject_object_negation_attribution", new[] { "content-bearing subject and object", "leading noun", "negation", "authorship relationship" }.All(x => prompt.Contains(x, StringComparison.Ordinal)), new { });
        Check("genuine_id_variable_url_protection_retained", new[] { "@usernames", "URLs byte-for-byte", "variables and placeholders", "FPS, GPU, CPU, G-SYNC" }.All(x => prompt.Contains(x, StringComparison.Ordinal)), new { });
        Check("numeric_and_strict_identity_policy_retained", new[] { "exact numeric value", "Every expected group ID", "Every supplied sourceId" }.All(x => prompt.Contains(x, StringComparison.Ordinal)), new { });
        Check("unresolved_ambiguity_not_invented", prompt.Contains("preserve the uncertain token rather than invent unsupported meaning", StringComparison.Ordinal), new { });

        var sources = new[] { "Mr. Qxz owns C9.", "NPC ID: V2-A7", "Open C:\\new\\notes", "{{user}} gained 2 items.",
            "Join @player at https://example.test/a?q=1", "Do not remove the object." };
        foreach (var raw in sources)
        {
            var item = new TranslationItem("NOTE", raw, StructuredTextRole.Unknown, ["LINE-1"], TranslationIdentityContract.CoreV2Block);
            using var wire = JsonDocument.Parse(TranslationService.BuildStructuredInputForTest([item]));
            Check("request_source_preserved_" + rows.Count, item.Text == raw &&
                wire.RootElement[0].GetProperty("text").GetString() == raw &&
                wire.RootElement[0].GetProperty("id").GetString() == item.Id &&
                wire.RootElement[0].GetProperty("sourceIds")[0].GetString() == "LINE-1",
                new { Source = raw, TranslationMutation = false });
        }

        Check("identifier_numeric_value_cannot_change", !CorePipelineEngine.NumericTokensMatch("C9", "C8"), new { });
        Check("identifier_numeric_value_cannot_disappear", !CorePipelineEngine.NumericTokensMatch("33/5000 C9 × 1", "33/5000 × 1"), new { });
        Check("ordinary_proper_name_not_blanket_rejected", CorePipelineEngine.NumericTokensMatch("Mr. Qxz", "Qxz先生"), new { });

        var itemForCache = new TranslationItem("CACHE-SEMANTIC", "A short note.", StructuredTextRole.Unknown, ["L1"], TranslationIdentityContract.CoreV2Block);
        var currentKey = TranslationCacheKeyBuilder.Build([itemForCache], settings);
        var oldVersionKey = OldVersionKey(itemForCache, settings);
        Check("persistent_cache_old_prompt_isolated", currentKey != oldVersionKey, new { CurrentKey = currentKey, OldVersionKey = oldVersionKey });
        var originalFoundation = Path.Combine(taskRoot, "patches", "PROMPT-A", "TranslationSettingsFoundation.cs");
        var originalSource = File.ReadAllText(originalFoundation);
        Check("prior_prompt_file_preserved", originalSource.Contains("settings1.2-core-v2-identifier-localization", StringComparison.Ordinal) &&
            !originalSource.Contains("OCR-source fidelity:", StringComparison.Ordinal),
            new { Path = originalFoundation, SHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalFoundation))) });
        Check("plain_prompt_inherits_semantic_rule", TranslationPromptBuilder.BuildPlainSystemPrompt(settings).Contains("OCR-source fidelity:", StringComparison.Ordinal), new { });

        var baselineEvidence = Path.Combine(taskRoot, "real-api", "NATIVE-A1-CONTENT-FOOTNOTE-SUMMARY.json");
        using var actual = JsonDocument.Parse(File.ReadAllText(baselineEvidence));
        var observations = new List<object>();
        foreach (var foot in actual.RootElement.GetProperty("RightFootnotes").EnumerateArray())
        {
            var raw = foot.GetProperty("OcrSource").GetString()!;
            var bid = foot.GetProperty("BlockId").GetString()!;
            var sourceIds = new[] { "R020" }; // Fixture evidence only, never a product identity rule.
            var item = new TranslationItem(bid, raw, StructuredTextRole.Unknown, sourceIds, TranslationIdentityContract.CoreV2Block);
            using var wire = JsonDocument.Parse(TranslationService.BuildStructuredInputForTest([item]));
            Check("actual_raw_source_unchanged_" + rows.Count, wire.RootElement[0].GetProperty("text").GetString() == raw && item.Text == raw, new { foot = foot.Clone() });
            observations.Add(new { Round = foot.GetProperty("Round").GetInt32(), Fixture = foot.GetProperty("Fixture").GetString(),
                Source = raw, OriginalAccepted = foot.GetProperty("Accepted").GetString(),
                Statement = "Recorded fixed counterexample / observation only. A prompt policy test cannot certify semantic completeness of a model response." });
        }
        File.WriteAllText(Path.Combine(output, "PRIOR-REAL-FOOTNOTE-OBSERVATIONS.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
        Check("all_prior_real_observations_registered", observations.Count == 9, new { Count = observations.Count });
        File.WriteAllText(Path.Combine(output, "PROMPT-CONTRACT-RESULTS.json"), JsonSerializer.Serialize(
            new { Passed = rows.Count - failed, Failed = failed, Requests = 0, Inference = "Policy, identity and source-immutability tests only; real semantic improvement must be tested separately.", Rows = rows },
            new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }

    private static string OldVersionKey(TranslationItem item, ApiSettings settings)
    {
        var text = $"{item.Id}:{item.IdentityContract}:{item.RoleType}:{string.Join(',', item.StableSourceIds)}:{item.Text}";
        var identity = string.Join("|", TranslationPromptBuilder.Sha256(text),
            TranslationPromptBuilder.NormalizeTarget(settings.TargetLanguage), settings.TranslationStyle,
            TranslationPromptBuilder.Sha256(settings.CustomTranslationPrompt?.Trim() ?? ""),
            TranslationProviderRegistry.Get(settings.TranslationProviderKind).Id,
            TranslationCacheKeyBuilder.NormalizeBaseUrl(settings.ApiUrl), settings.Model.Trim(),
            settings.PreserveIdentifiers, settings.PreserveNumbers, settings.PreserveVariables,
            "settings1.2-core-v2-identifier-localization", CoreTranslationContentValidator.ContractVersion);
        return TranslationPromptBuilder.Sha256(identity);
    }
}
