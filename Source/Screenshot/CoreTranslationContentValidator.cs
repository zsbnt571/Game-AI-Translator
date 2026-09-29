using System.Text.RegularExpressions;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

internal sealed record CoreContentValidation(bool Accepted,string Text,bool Recovered,string Reason);

internal static class CoreTranslationContentValidator
{
    internal const string ContractVersion="core-content-v18-arabic-literal";

    internal static CoreContentValidation Validate(TranslationItem item,string translated,string requestId,string targetLanguage="")
    {
        if(item.IdentityContract!=TranslationIdentityContract.CoreV2Block)
            return new(true,translated,false,"NON_CORE_CONTRACT");
        var result=ValidateSeparators(item,translated,requestId);
        if(!result.Accepted)return result;
        var reason=string.IsNullOrWhiteSpace(result.Text)?"EMPTY_BLOCK_TRANSLATION":
            !CorePipelineEngine.NumericTokensMatch(item.Text,result.Text)?"NUMERIC_FIDELITY_MISMATCH":
            !Variables(item.Text).SequenceEqual(Variables(result.Text),StringComparer.Ordinal)?"PROTECTED_VARIABLE_MISMATCH":
            UnchangedSemanticSource(item,result.Text,targetLanguage)?"UNTRANSLATED_SOURCE_SEMANTIC_TEXT":"";
        if(reason.Length==0)return result;
        TranslationBoundaryDiagnostics.Record("ContentRejected",requestId,item.Id,reason);
        RecordRejection(item,translated,requestId,reason);
        return new(false,translated,false,reason);
    }

    private static void RecordRejection(TranslationItem item,string translated,string requestId,string reason)
    {
        // Record only rejected item text, not the response body or other items.
        // Redact before JSON escaping/truncation. AppLog rotates at 2 MiB plus
        // one backup; diagnostic I/O failure cannot change the acceptance result.
        AppLog.Write("translation-validation",$"request={requestId} block={item.Id} reason={reason} "+
            $"sourceQuantities={NumericFidelityV2.Describe(item.Text,translated,true)} "+
            $"targetQuantities={NumericFidelityV2.Describe(translated,item.Text,false)}");
        try
        {
            var safe=SafeDiagnosticOutput.Redact(translated);
            const int maxCharacters=8192;
            AppLog.Write("translation-rejections",System.Text.Json.JsonSerializer.Serialize(new
            {
                Contract=ContractVersion,RequestId=requestId,BlockId=item.Id,Reason=reason,
                SourceSha256=TranslationPromptBuilder.Sha256(item.Text),
                SourceNumericLiterals=NumericFidelityV2.Tokens(item.Text,translated,true),
                RejectedTranslation=safe.Length>maxCharacters?safe[..maxCharacters]:safe,
                Truncated=safe.Length>maxCharacters
            }));
        }
        catch { /* Evidence cannot block or authorize a translation. */ }
    }

    private static IEnumerable<string> Variables(string value)=>Regex.Matches(value,@"\{\{[^{}]+\}\}")
        .Select(x=>x.Value).OrderBy(x=>x,StringComparer.Ordinal);

    // Called at the current-product admission boundary, including session caches.
    // This does not manufacture transport evidence for an old saved translation.
    internal static TranslationBatchResult Revalidate(IReadOnlyList<TranslationItem> items,
        TranslationBatchResult batch,string requestId,string targetLanguage="")
    {
        var expected=items.ToDictionary(x=>x.Id,StringComparer.Ordinal);
        if(batch.Translations.Keys.Any(id=>!expected.ContainsKey(id)))
            throw new InvalidOperationException("Unexpected translation identity at current-content admission");
        var accepted=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var item in items)
        {
            if(!batch.Translations.TryGetValue(item.Id,out var text))continue;
            var result=Validate(item,text,requestId,targetLanguage);
            TranslationBoundaryDiagnostics.Record("CurrentAdmissionOriginal",requestId,item.Id,text);
            TranslationBoundaryDiagnostics.Record("CurrentAdmissionReason",requestId,item.Id,result.Reason);
            if(result.Accepted)accepted[item.Id]=result.Text;
        }
        var missing=items.Where(x=>!accepted.ContainsKey(x.Id)).Select(x=>x.Id).ToArray();
        return batch with {Translations=accepted,MissingIds=missing,
            FullTranslation=string.Join("\n\n",items.Where(x=>accepted.ContainsKey(x.Id)).Select(x=>accepted[x.Id])),
            SegmentMappingReliable=missing.Length==0};
    }

    private static CoreContentValidation ValidateSeparators(TranslationItem item,string translated,string requestId)
    {
        if(item.IdentityContract!=TranslationIdentityContract.CoreV2Block||!translated.Contains(@"\n",StringComparison.Ordinal))
            return new(true,translated,false,"UNCHANGED");
        var source=item.Text.Replace("\r\n","\n").Replace('\r','\n');
        var lines=source.Split('\n');
        // No unescaping of paths, code, or source-authored literal escapes.
        if(source.Contains('\\')||LooksLikeCodeOrPath(source))
            return new(true,translated,false,"SOURCE_LITERAL_OR_CODE_PRESERVED");
        var structured=lines.Length is >=2 and <=8&&lines.All(line=>!string.IsNullOrWhiteSpace(line)&&line.Length<=300)&&
            item.StableSourceIds.Distinct(StringComparer.Ordinal).Count()>=lines.Length;
        if(!structured)return Reject("UNPROVEN_LITERAL_UI_SEPARATOR");
        if(LooksLikeCodeOrPath(translated))return Reject("AMBIGUOUS_CODE_OR_PATH_IN_UI_RESPONSE");
        if(translated.Contains('\n')||translated.Contains('\r'))return Reject("MIXED_REAL_AND_LITERAL_UI_LINEBREAK");
        var pieces=translated.Split(new[]{@"\n"},StringSplitOptions.None);
        if(pieces.Length!=lines.Length||pieces.Any(piece=>string.IsNullOrWhiteSpace(piece)||piece.Contains('\\')))
            return Reject("UI_LINEBREAK_COUNT_OR_ESCAPE_MISMATCH");
        var recovered=string.Join("\n",pieces);
        // Repair only the evidenced separators. Preserve every other codepoint;
        // Core numeric and protected-variable contracts are rechecked before accept.
        if(!CorePipelineEngine.NumericTokensMatch(source,recovered))
            return Reject("RECOVERED_UI_NUMERIC_FIDELITY_MISMATCH");
        var variables=Regex.Matches(source,@"\{\{[^{}]+\}\}").Select(x=>x.Value).OrderBy(x=>x,StringComparer.Ordinal);
        var translatedVariables=Regex.Matches(recovered,@"\{\{[^{}]+\}\}").Select(x=>x.Value).OrderBy(x=>x,StringComparer.Ordinal);
        if(!variables.SequenceEqual(translatedVariables,StringComparer.Ordinal))
            return Reject("RECOVERED_UI_VARIABLE_MISMATCH");
        TranslationBoundaryDiagnostics.Record("ContentOriginal",requestId,item.Id,translated);
        TranslationBoundaryDiagnostics.Record("ContentRecoveryReason",requestId,item.Id,"VERIFIED_SOURCE_LINES_LITERAL_SEPARATOR; numeric=true; variables=true; exact_nonseparator_codepoints=true");
        TranslationBoundaryDiagnostics.Record("ContentRecoveredAndRevalidated",requestId,item.Id,recovered);
        return new(true,recovered,true,"VERIFIED_SOURCE_LINES_LITERAL_SEPARATOR");

        CoreContentValidation Reject(string reason)
        {
            TranslationBoundaryDiagnostics.Record("ContentOriginal",requestId,item.Id,translated);
            TranslationBoundaryDiagnostics.Record("ContentRejected",requestId,item.Id,reason);
            RecordRejection(item,translated,requestId,reason);
            return new(false,translated,false,reason);
        }
    }

    private static bool LooksLikeCodeOrPath(string text)=>
        text.Contains((char)96)||Regex.IsMatch(text,@"(?:[A-Za-z]:\\|https?://|\\\\|=>|\b(?:print|printf|Console\.WriteLine|Regex|Path)\s*\(|\b(?:var|const|let)\s+\w+\s*=)",
            RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);

    // This is a copied-content check, not an English lexicon or general semantic grader.
    // Only source-established functional roles enter it. Uncertain names and opaque
    // identifiers are not made translatable by letter case or visual appearance.
    private static bool UnchangedSemanticSource(TranslationItem item,string target,string language)
    {
        if(!TranslationPromptBuilder.NormalizeTarget(language).Contains("Chinese",StringComparison.OrdinalIgnoreCase)||
            item.SemanticPurpose.Length==0||item.RoleType is StructuredTextRole.CharacterName or StructuredTextRole.Species||
            LooksLikeCodeOrPath(item.Text)||Regex.IsMatch(item.Text,@"[@#]|\{\{|[\u3400-\u9fff]"))return false;
        var words=Regex.Matches(item.Text,@"[A-Za-z]+(?:['’-][A-Za-z]+)*").Select(m=>m.Value).ToArray();
        if(words.Length==0||Regex.IsMatch(item.Text,@"\b\w*[\d_]\w*\b"))return false;
        if(Regex.IsMatch(item.Text.Trim(),@"^[A-Z]{1,4}(?:[:/.\-][A-Z]{1,4})+$"))return false;
        if(words.Length==1 && (words[0].Length<3||
            words[0].Length<=5&&words[0].All(char.IsUpper)||
            words[0].Skip(1).Any(char.IsUpper)&&!words[0].All(char.IsUpper)))return false;
        static string Lexical(string value)=>string.Concat(value.Where(char.IsLetter)).ToLowerInvariant();
        return Lexical(item.Text)==Lexical(target);
    }
}

