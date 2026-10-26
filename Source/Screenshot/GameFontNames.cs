using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ScreenshotTranslationUiTester;

// OpenType name table, first TTC face (the same face inspected by cmap).
// https://learn.microsoft.com/en-us/typography/opentype/spec/name
// Does not register fonts or depend on process-wide renderer font caches.
internal static class GameFontNames
{
    internal static string Family(byte[] bytes)
        => FamilyForCulture(bytes, CultureInfo.CurrentCulture);

    internal static string FamilyForCulture(byte[] bytes, CultureInfo culture)
        => NameForCulture(bytes, culture, 0);

    internal static string FullNameForCulture(byte[] bytes, CultureInfo culture)
        => NameForCulture(bytes, culture, 4);

    internal static string RuntimeFaceForCulture(byte[] bytes, CultureInfo culture)
    {
        string full = FullNameForCulture(bytes, culture);
        try
        {
            // Legacy runtimes enumerate the regular face by its legacy family.
            // Require all three name-table records from the same face; never
            // collapse a distinct Bold/Black family to typographic nameID16.
            string family = NameForCulture(bytes, culture, 1);
            string style = NameForCulture(bytes, culture, 2);
            if (style == "Regular" && full == family + " Regular") return family;
        }
        catch (IOException) { /* Missing legacy metadata: retain the full name. */ }
        return full;
    }

    private static string NameForCulture(byte[] bytes, CultureInfo culture, int requestedName)
    {
        InvalidDataException Invalid() => new("字体表范围无效。");
        ushort U16(int at) => at >= 0 && at <= bytes.Length - 2
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at, 2)) : throw Invalid();
        uint U32(int at) => at >= 0 && at <= bytes.Length - 4
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4)) : throw Invalid();
        int Position(uint value) => value <= int.MaxValue ? (int)value : throw Invalid();
        int face = 0;
        if (U32(0) == 0x74746366)
        {
            uint faces = U32(8);
            if (faces is < 1 or > 256 || 12L + 4L * faces > bytes.Length) throw Invalid();
            face = Position(U32(12));
        }
        if (face > bytes.Length - 12 || U32(face) is not (0x00010000 or 0x4f54544f or 0x74727565)) throw Invalid();
        int count = U16(face + 4), start = -1, length = 0;
        if (count is < 1 or > 512 || face + 12L + 16L * count > bytes.Length) throw Invalid();
        for (int i = 0; i < count; i++)
        {
            int record = face + 12 + 16 * i;
            int offset = Position(U32(record + 8)), size = Position(U32(record + 12));
            if (offset > bytes.Length - size) throw Invalid();
            if (U32(record) != 0x6e616d65) continue;
            if (start >= 0) throw Invalid();
            start = offset; length = size;
        }
        if (start < 0 || length < 6) throw new IOException("无法读取字体名称。");
        int version = U16(start), records = U16(start + 2), storage = U16(start + 4);
        int recordsEnd = 6 + 12 * records;
        if (version > 1 || recordsEnd > length || storage > length || storage < recordsEnd) throw Invalid();
        var utf16 = new UnicodeEncoding(true, false, true);
        string[] languageTags = [];
        if (version == 1)
        {
            if (recordsEnd > length - 2) throw Invalid();
            int tags = U16(start + recordsEnd), tagsEnd = recordsEnd + 2 + 4 * tags;
            if (tagsEnd > length || storage < tagsEnd) throw Invalid();
            languageTags = new string[tags];
            for (int i = 0; i < tags; i++)
            {
                int at = start + recordsEnd + 2 + 4 * i, size = U16(at), offset = U16(at + 2);
                if ((size & 1) != 0 || offset > length - storage - size) throw Invalid();
                try { languageTags[i] = utf16.GetString(bytes, start + storage + offset, size); }
                catch (DecoderFallbackException) { throw Invalid(); }
            }
        }
        string? best = null; int bestScore = -1;
        for (int i = 0; i < records; i++)
        {
            int at = start + 6 + 12 * i;
            int platform = U16(at), encoding = U16(at + 2), language = U16(at + 4), name = U16(at + 6);
            int size = U16(at + 8), offset = U16(at + 10);
            if (offset > length - storage - size) throw Invalid();
            if ((requestedName != 0 ? name != requestedName : name is not (1 or 16)) || size == 0 || size > 2048) continue;
            string? tag = null;
            if (language >= 0x8000)
            {
                int index = language - 0x8000;
                if (index >= languageTags.Length) continue;
                tag = languageTags[index];
            }
            string value;
            try
            {
                if (platform == 0 || platform == 3 && encoding is 0 or 1 or 10)
                {
                    if ((size & 1) != 0) continue;
                    value = utf16.GetString(bytes, start + storage + offset, size);
                }
                else if (platform == 1 && encoding == 0)
                {
                    var roman = CodePagesEncodingProvider.Instance.GetEncoding(10000);
                    if (roman is null) continue;
                    value = roman.GetString(bytes, start + storage + offset, size);
                }
                else continue;
            }
            catch (DecoderFallbackException) { continue; }
            value = value.Trim();
            if (value.Length == 0 || value.Any(char.IsControl)) continue;
            // Legacy family preserves distinct installed faces (e.g. Arial
            // Black). Typographic family is a fallback, never a style collapse.
            int score = (name is 1 or 4 ? 2000 : 1000) + (platform == 3 ? 20 : platform == 0 ? 10 : 0);
            if (tag is not null)
                score += tag.Equals(culture.Name, StringComparison.OrdinalIgnoreCase) ? 200 : tag.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? 100 : 0;
            else if (platform == 3)
                score += language == culture.LCID ? 200 : language == 0x0409 ? 100 : 0;
            else if (platform == 1 && language == 0) score += 100;
            if (score > bestScore) { best = value; bestScore = score; }
        }
        return best ?? throw new IOException("无法读取字体名称。");
    }
}
