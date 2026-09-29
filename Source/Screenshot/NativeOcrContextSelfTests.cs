using System.Drawing;
using System.Reflection;
using System.Text.Json;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

internal static class NativeOcrContextSelfTests
{
    internal static int Run(string output, string taskRoot)
    {
        Directory.CreateDirectory(output);
        var rows = new List<object>(); var failures = 0;
        void Check(string name, bool pass, object? detail = null)
        { if (!pass) failures++; rows.Add(new { Name = name, Pass = pass, Detail = detail }); }
        var bounds = new RectangleF(700, 950, 220, 14);
        var canvas = new Size(1200, 1000);
        var hash = new string('A', 64);
        OcrAlternativeEvidence Candidate(string text, char crop, string transform = "scale-2x") => new()
        {
            Text = text, Confidence = .91f, Origin = "test-real-reading-shape",
            Transform = transform, SourceBlockId = "NOTE", SourceImageSha256 = hash,
            CropSha256 = new string(crop, 64), CropWidth = 440, CropHeight = 28,
            SourceRect = [700, 950, 220, 14]
        };
        IReadOnlyList<OcrAlternativeEvidence> Verify(params OcrAlternativeEvidence[] values) =>
            OcrAlternativeEvidenceValidator.Validate(OcrAlternativeEvidenceValidator.Contract,
                values, hash, "NOTE", bounds, canvas);
        var a = Candidate("Bio totally not written by opnella", 'B');
        var b = Candidate("Bio totally not witten by opnella", 'C', "scale-2x-pad");
        var verified = Verify(a, b);
        Check("genuine_same_request_readings_verified", verified.All(x => x.Verified), verified);
        foreach (var test in new (string Name, OcrAlternativeEvidence Value)[]
        {
            ("wrong_source_hash", a with { SourceImageSha256 = new string('F',64) }),
            ("wrong_block", a with { SourceBlockId = "OTHER" }),
            ("out_of_image", a with { SourceRect = [1190,950,220,14] }),
            ("different_panel", a with { SourceRect = [200,950,220,14] }),
            ("nonfinite_domain", a with { SourceRect = [float.NaN,950,220,14] }),
            ("source_domain_expanded", a with { SourceRect = [600,940,400,40] }),
            ("missing_crop_hash", a with { CropSha256 = "" }),
            ("invalid_crop_dimensions", a with { CropHeight = 0 }),
            ("huge_crop_dimensions", a with { CropWidth = 100000 }),
            ("confidence_nan", a with { Confidence = float.NaN }),
            ("missing_origin", a with { Origin = "" })
        }) Check(test.Name, !Verify(test.Value)[0].Verified, Verify(test.Value)[0].ValidationReason);
        Check("unknown_contract_rejected", !OcrAlternativeEvidenceValidator.Validate("future",
            [a],hash,"NOTE",bounds,canvas)[0].Verified);
        Check("overflow_candidates_retained_but_not_admitted",
            Verify(Enumerable.Repeat(a,9).ToArray()).Count == 9 &&
            !Verify(Enumerable.Repeat(a,9).ToArray())[8].Verified);
        Check("evidence_clone_does_not_share_rect", !ReferenceEquals(a.SourceRect,a.Copy().SourceRect));
        CorePipelineDocument Document(string raw, IReadOnlyList<OcrAlternativeEvidence> evidence,
            bool anchors = true)
        {
            RawOcrLine Line(string id,string text,RectangleF rectangle,int order) =>
                new(id,text,text, [new(rectangle.Left,rectangle.Top),new(rectangle.Right,rectangle.Top),
                    new(rectangle.Right,rectangle.Bottom),new(rectangle.Left,rectangle.Bottom)], rectangle,.94f,order);
            var note = Line("NOTE",raw,bounds,5) with { OcrRequestImageSha256=hash,OcrAlternatives=evidence };
            var lines = anchors ? new[] { Line("TITLE","Ophelia",new(30,20,210,65),0),
                Line("BODY","The author Ophelia has a story.",new(30,200,600,25),1),note } : [note];
            return CorePipelineEngine.Analyze(canvas,lines);
        }
        TranslationItem Item(CorePipelineDocument doc) => CoreTranslationItemFactory.Create(doc,
            doc.VisualBlocks.Single(x => x.Lines.Any(l => l.SourceId == "NOTE")));
        var source = "Blo totally not wtten by ophella";
        var doc = Document(source,verified);
        var item = Item(doc);
        Check("raw_source_and_request_text_unchanged", doc.RawLines.Last().RawText == source && item.Text == source);
        Check("real_failed_footnote_reading_available", item.OcrContext?.TokenHints.Any(h =>
            h.SourceToken == "Blo" && h.Readings.Any(r => r.Text == "Bio")) == true, item.OcrContext);
        Check("degraded_name_not_offered", item.OcrContext?.TokenHints.All(h =>
            h.SourceToken != "ophella" && h.Readings.All(r => r.Text != "opnella")) == true);
        var wire = TranslationService.BuildStructuredInputForTest([item]);
        Check("full_alternative_prose_not_sent", !wire.Contains("opnella",StringComparison.Ordinal) &&
            !wire.Contains("totally not written",StringComparison.Ordinal));
        Check("full_candidate_retained_in_history_evidence",
            HistoryCoreSnapshot.Capture(doc).Lines.Last().OcrAlternatives.Any(x=>x.Text.Contains("opnella")));
        Check("supported_primary_not_given_competing_hint", Item(Document(source,
            Verify(a,b,Candidate(source,'D'),Candidate(source,'E')))).OcrContext is null);
        Check("real_correct_source_not_reopened", Item(Document("Bio totally not written by ophella",
            Verify(Candidate("Bio totally not written by ophella",'B'),
                Candidate("Bio totally not written by ophella",'C'),
                Candidate("Blo totally not written by ophella",'D'),
                Candidate("Blo totally not written by ophella",'E')))).OcrContext is null);
        Check("same_page_name_ocr_variant_protected_without_attribution", Item(Document("The key belongs to Opheiia",
            Verify(Candidate("The key belongs to Ophella",'B'),Candidate("The key belongs to Ophella",'C')))).OcrContext is null);
        Check("single_observation_not_enough", Item(Document(source,Verify(a))).OcrContext is null);
        Check("same_crop_duplicate_not_independent", Item(Document(source,Verify(a,a with {Transform="other"}))).OcrContext is null);
        Check("wrong_hash_cannot_resurface_in_factory",
            Item(Document(source,verified.Select(x=>x with {SourceImageSha256=new string('F',64)}).ToArray())).OcrContext is null);
        Check("unverified_diagnostic_candidate_not_admitted", Item(Document(source,[a,b])).OcrContext is null);
        var changedPrimary = doc with { RawLines=doc.RawLines.Select(x=>x.SourceId=="NOTE" ?
            x with {RawText="Different text entirely"} : x).ToArray() };
        Check("edited_raw_invalidates_context", Item(changedPrimary).OcrContext is null);
        var ordinary = Item(Document(source,[]));
        var expected = JsonSerializer.Serialize(new[] {new {id=ordinary.Id,type=ordinary.RoleType.ToString(),
            text=ordinary.Text,sourceIds=ordinary.StableSourceIds}});
        Check("no_candidate_input_bytes_equal", TranslationService.BuildStructuredInputForTest([ordinary]) == expected);
        var settings = new ApiSettings {TargetLanguage="zh-CN",ApiUrl="https://api.deepseek.com",Model="deepseek-v4-flash"};
        Check("no_candidate_system_prompt_bytes_equal", TranslationPromptBuilder.BuildBatchSystemPrompt(settings,[ordinary]) ==
            TranslationPromptBuilder.BuildSystemPrompt(settings));
        Check("conditional_evidence_prompt_applied", TranslationPromptBuilder.BuildBatchSystemPrompt(settings,[item])
            .Contains("Optional ocrContext is evidence",StringComparison.Ordinal));
        Check("context_cache_separated", TranslationCacheKeyBuilder.Build([ordinary],settings) != TranslationCacheKeyBuilder.Build([item],settings));
        Check("stale_context_source_not_sent",
            TranslationService.BuildStructuredInputForTest([item with {Text="An edited sentence."}])
                .Contains("ocrContext",StringComparison.Ordinal)==false);
        Check("legacy_identity_context_ignored",
            TranslationService.BuildStructuredInputForTest([item with {IdentityContract=TranslationIdentityContract.LegacyAllocationV1}])
                .Contains("ocrContext",StringComparison.Ordinal)==false);
        var history=HistoryCoreSnapshot.Capture(doc);
        var roundTrip=JsonSerializer.Deserialize<HistoryCoreSnapshot>(JsonSerializer.Serialize(history))!;
        var restored=roundTrip.Rebuild(canvas); var restoredItem=Item(restored);
        Check("history_restores_context_identity", restoredItem.OcrContext?.IdentityHash==item.OcrContext?.IdentityHash);
        Check("history_restores_stable_block_id", restoredItem.Id==item.Id);
        Check("history_canvas_change_discards_evidence",
            roundTrip.Rebuild(new Size(1100,1000)).RawLines.All(x=>x.OcrAlternatives.Count==0));
        var withoutEvidence=Item(Document(source,[]));
        Check("group_identity_unchanged_by_evidence", withoutEvidence.Id==item.Id &&
            withoutEvidence.StableSourceIds.SequenceEqual(item.StableSourceIds));
        var cloneMethod=typeof(SessionServices).GetMethod("Clone",BindingFlags.Static|BindingFlags.NonPublic)!;
        var originalOcr=new OcrEngineResult {Blocks=[new OcrEngineBlock {Id="NOTE",RawText=source,
            OcrRequestImageSha256=hash,OcrAlternatives=verified}]};
        var cloned=(OcrEngineResult)cloneMethod.Invoke(null,[originalOcr])!;
        Check("ocr_cache_clone_retains_provenance", cloned.Blocks[0].OcrAlternatives[0].EvidenceId==verified[0].EvidenceId &&
            cloned.Blocks[0].OcrRequestImageSha256==hash);
        Check("ocr_cache_clone_deep_rect", !ReferenceEquals(cloned.Blocks[0].OcrAlternatives[0].SourceRect,verified[0].SourceRect));
        var editorMethod=typeof(PreviewForm).GetMethod("BuildCorePipelineV2",BindingFlags.Static|BindingFlags.NonPublic,
            null,[typeof(IEnumerable<OcrRegion>),typeof(Size)],null)!;
        var editor=(CorePipelineDocument)editorMethod.Invoke(null,[new[]{new OcrRegion {Id="NOTE",RawText=source,
            Text="User corrected this line",Bounds=bounds,Confidence=.94f,ReadingOrder=5}},canvas])!;
        Check("editor_rebuild_has_no_stale_candidates", editor.RawLines.All(x=>x.OcrAlternatives.Count==0));
        // Capitalization alone is not a name rule. This ordinary word occurs at sentence start.
        var normalSource="Llnes show the remaining notes";
        var normalCandidates=Verify(Candidate("Lines show the remaining notes",'B'),
            Candidate("Lines show the remaining notes",'C'));
        Check("capitalized_ordinary_word_not_blanket_blocked", Item(Document(normalSource,normalCandidates,false))
            .OcrContext?.TokenHints.Any(h=>h.Readings.Any(r=>r.Text=="Lines"))==true);
        foreach(var protectedSource in new[]{"{{Llnes}} show the remaining notes","@Llnes show the remaining notes",
            "ID_Llnes show the remaining notes","Llnes9 show the remaining notes"})
        {
            var alternatives=Verify(Candidate(protectedSource.Replace("Llnes","Lines"),'B'),
                Candidate(protectedSource.Replace("Llnes","Lines"),'C'));
            Check("protected_token_"+rows.Count,Item(Document(protectedSource,alternatives,false)).OcrContext is null);
        }
        // Unknown author protection does not depend on the fixture's title or known name.
        var unknown="This note was signed by Mlra";
        Check("unknown_attributed_name_not_changed",Item(Document(unknown,
            Verify(Candidate("This note was signed by Mira",'B'),Candidate("This note was signed by Mira",'C')),false)).OcrContext is null);
        foreach(var pathSource in new[]{@"Open C:\Llnes\notes now", "Open /opt/Llnes/notes now",
            "var Llnes = current value;", "`Llnes` is a code identifier"})
            Check("code_path_abstain_"+rows.Count,Item(Document(pathSource,
                Verify(Candidate(pathSource.Replace("Llnes","Lines"),'B'),
                    Candidate(pathSource.Replace("Llnes","Lines"),'C')),false)).OcrContext is null);
        Check("no_arbitrary_spelling_correction",Item(Document("The text shows a cxt",
            Verify(Candidate("The text shows a cat",'B'),Candidate("The text shows a cat",'C')),false)).OcrContext is null);
        File.WriteAllText(Path.Combine(output,"OCR-CONTEXT-CONTRACTS.json"),JsonSerializer.Serialize(
            new {Tests=rows.Count,Failures=failures,Rows=rows,RealApiRequests=0,
                Note="Fixed evidence/contract tests do not establish model semantic or visual success."},
            new JsonSerializerOptions {WriteIndented=true}));
        return failures==0?0:1;
    }
}
