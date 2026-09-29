using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Complete recognizer readings remain evidence; they never replace the primary OCR text.
public sealed record OcrAlternativeEvidence
{
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("confidence")] public float Confidence { get; init; }
    [JsonPropertyName("origin")] public string Origin { get; init; } = "";
    [JsonPropertyName("transform")] public string Transform { get; init; } = "";
    [JsonPropertyName("sourceBlockId")] public string SourceBlockId { get; init; } = "";
    [JsonPropertyName("sourceImageSha256")] public string SourceImageSha256 { get; init; } = "";
    [JsonPropertyName("cropSha256")] public string CropSha256 { get; init; } = "";
    [JsonPropertyName("cropWidth")] public int CropWidth { get; init; }
    [JsonPropertyName("cropHeight")] public int CropHeight { get; init; }
    [JsonPropertyName("sourceRect")] public float[] SourceRect { get; init; } = [];
    public string Contract { get; init; } = "";
    public bool Verified { get; init; }
    public string ValidationReason { get; init; } = "NOT_VALIDATED";
    public string EvidenceId { get; init; } = "";
    public OcrAlternativeEvidence Copy() => this with { SourceRect = SourceRect?.ToArray() ?? [] };
}

internal static class OcrAlternativeEvidenceValidator
{
    public const string Contract = "ocr-alternatives-v1";
    private static bool Hash(string? value) => value is not null && Regex.IsMatch(value, "^[A-F0-9]{64}$");
    public static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static IReadOnlyList<OcrAlternativeEvidence> Validate(string contract,
        IEnumerable<OcrAlternativeEvidence>? candidates, string requestHash, string blockId,
        RectangleF bounds, Size canvas)
    {
        return (candidates ?? []).Select((candidate, index) =>
        {
            var copy = candidate.Copy();
            var reason = Check(contract, copy, requestHash, blockId, bounds, canvas, index);
            var identity = string.Join("|", Contract, copy.SourceBlockId, copy.SourceImageSha256,
                copy.CropSha256, copy.Transform, copy.Text);
            return copy with { Contract = contract, Verified = reason == "VERIFIED_SAME_REQUEST",
                ValidationReason = reason, EvidenceId = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(identity))) };
        }).ToArray();
    }

    private static string Check(string contract, OcrAlternativeEvidence c, string requestHash,
        string blockId, RectangleF bounds, Size canvas, int index)
    {
        if (contract != Contract) return "UNSUPPORTED_CONTRACT";
        if (index >= 8) return "ALTERNATIVE_LIMIT";
        if (!Hash(requestHash) || c.SourceImageSha256 != requestHash) return "SOURCE_HASH_MISMATCH";
        if (c.SourceBlockId != blockId) return "SOURCE_BLOCK_MISMATCH";
        if (!Hash(c.CropSha256)) return "INVALID_CROP_HASH";
        if (string.IsNullOrWhiteSpace(c.Text) || c.Text.Length > 512 || c.Text.Any(char.IsControl))
            return "INVALID_CANDIDATE_TEXT";
        if (!float.IsFinite(c.Confidence) || c.Confidence < 0 || c.Confidence > 1)
            return "INVALID_CONFIDENCE";
        if (string.IsNullOrWhiteSpace(c.Origin) || c.Origin.Length > 80 ||
            string.IsNullOrWhiteSpace(c.Transform) || c.Transform.Length > 160)
            return "INVALID_PROVENANCE";
        if (c.CropWidth <= 0 || c.CropHeight <= 0 || c.CropWidth > 4096 || c.CropHeight > 1024 ||
            (long)c.CropWidth * c.CropHeight > 1048576) return "INVALID_CROP_DIMENSIONS";
        var r = c.SourceRect;
        if (r.Length != 4 || r.Any(x => !float.IsFinite(x)) ||
            r[0] < 0 || r[1] < 0 || r[2] <= 0 || r[3] <= 0 ||
            r[0] + r[2] > canvas.Width || r[1] + r[3] > canvas.Height)
            return "INVALID_SOURCE_DOMAIN";
        // The sampler is allowed rounding/padding at this line, not a different panel or neighbour.
        var domain = new RectangleF(r[0], r[1], r[2], r[3]);
        if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y) || !float.IsFinite(bounds.Width) ||
            !float.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0 ||
            domain.Width < bounds.Width * 0.7f || domain.Height < bounds.Height * 0.4f ||
            domain.Left < bounds.Left - 4 || domain.Top < bounds.Top - 4 ||
            domain.Right > bounds.Right + 4 || domain.Bottom > bounds.Bottom + 4)
            return "CROP_BLOCK_DOMAIN_MISMATCH";
        return "VERIFIED_SAME_REQUEST";
    }
}

public sealed record OcrTokenReading(string Text, IReadOnlyList<string> EvidenceIds);
public sealed record OcrTokenHint(string SourceId, int SourceStart, int SourceLength,
    string SourceToken, IReadOnlyList<OcrTokenReading> Readings);
public sealed record OcrTranslationContext(string Schema, string SourceTextSha256,
    IReadOnlyList<OcrTokenHint> TokenHints)
{
    public string IdentityHash => TranslationPromptBuilder.Sha256(
        System.Text.Json.JsonSerializer.Serialize(new { Schema, SourceTextSha256, TokenHints }));
}

internal static class OcrTranslationContextContract
{
    public const string Schema = "ocr-token-hints-v1";
    public static bool IsApplicable(TranslationItem item) =>
        item.IdentityContract == TranslationIdentityContract.CoreV2Block &&
        item.OcrContext is { TokenHints.Count: > 0 } c && c.Schema == Schema &&
        c.SourceTextSha256 == TranslationPromptBuilder.Sha256(item.Text) &&
        c.TokenHints.All(h => item.StableSourceIds.Contains(h.SourceId, StringComparer.Ordinal) &&
            h.SourceStart >= 0 && h.SourceLength > 0 && h.SourceStart + h.SourceLength <= item.Text.Length &&
            item.Text.Substring(h.SourceStart, h.SourceLength) == h.SourceToken &&
            h.Readings.Count > 0 && h.Readings.All(r => r.EvidenceIds.Distinct().Count() >= 2));
    public static string CacheSuffix(TranslationItem item) =>
        IsApplicable(item) ? "|ocr-context:" + TranslationPromptBuilder.OcrContextVersion + ":" + item.OcrContext!.IdentityHash : "";
}
