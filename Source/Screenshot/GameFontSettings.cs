using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// An absent preference preserves the existing per-engine font behavior exactly.
// Font choices belong to a game, never to an API profile or the desktop UI font.
internal sealed record GameFontPreference(bool Custom = false, string Family = "", string Path = "", string Hash = "");
internal sealed record GameFontPreferenceStamp(GameFontPreference Value, long Revision);
internal sealed record GameFontPreferenceChange(GameFontPreference Previous, GameFontPreferenceStamp Current);
// Only an explicit runtime response proving that the previous preference was
// retained can use this type. Transport failures are deliberately different.
internal sealed class GameFontPreferenceRejectedException(string message) : Exception(message);

internal static class GameFontSettings
{
    internal const string PathVariable = "FUSION_GAME_FONT_PATH", FamilyVariable = "FUSION_GAME_FONT_FAMILY";
    internal const long MaximumFileBytes = 32 * 1024 * 1024;
    internal const string DefaultLabel = "保持当前字体适配（默认）";
    internal const string RestartHint = "连接后可保存并实时应用，也可随时恢复默认。翻译关闭时只保存选择；字号和布局继续由现有适配处理。";
    internal const string IndependentHint = "连接后可实时更换字体，也可恢复默认；无需开启翻译。字体大小和布局继续沿用现有适配。";
    private static readonly ConcurrentDictionary<string, GameFontPreference> cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object preferenceGate = new();
    private static readonly Dictionary<string, long> revisions = new(StringComparer.OrdinalIgnoreCase);
    internal static bool Supports(GameInfo? game) => GameFontCapabilities.ForGame(game).Custom;
    internal static bool SystemFamilyOnly(GameInfo game) => GameFontCapabilities.ForAdapter(game.AdapterId).SystemFamilyOnly;
    internal static string Storage(string exe) => System.IO.Path.Combine(AppDataPaths.Root, "game-fonts", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RecentGameStore.Normalize(exe).ToUpperInvariant()))));
    private static string Record(string exe) => System.IO.Path.Combine(Storage(exe), "preference.json");
    internal static GameFontPreference Read(string exe)
    {
        lock (preferenceGate) return ReadCore(exe);
    }
    private static GameFontPreference ReadCore(string exe) => cache.GetOrAdd(Record(exe), path =>
    {
        AppDataPaths.RejectReparsePoints(path);
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 16384) throw new IOException("游戏字体设置过大，请重新选择字体。");
        var preference=JsonSerializer.Deserialize<GameFontPreference>(File.ReadAllText(path)) ?? new();
        preference = preference with { Family=preference.Family??"", Path=preference.Path??"", Hash=preference.Hash??"" };
        return ResolveOwnedPreference(preference, System.IO.Path.GetDirectoryName(path)!);
    });
    // A copied data directory owns its saved font bytes. Resolve only a verified
    // content-addressed file in this game's current storage, even while the old
    // absolute path still exists. The persisted preference remains untouched.
    internal static GameFontPreference ResolveOwnedPreference(GameFontPreference preference, string storage)
    {
        if (!preference.Custom || preference.Hash.Length != 64 || !preference.Hash.All(Uri.IsHexDigit) || preference.Path.Length == 0)
            return preference;
        try
        {
            string extension = System.IO.Path.GetExtension(preference.Path);
            if (extension.ToLowerInvariant() is not (".ttf" or ".otf" or ".ttc") ||
                !System.IO.Path.GetFileName(preference.Path).Equals(preference.Hash + extension, StringComparison.OrdinalIgnoreCase))
                return preference;
            string owned = System.IO.Path.Combine(storage, "files", preference.Hash + extension);
            AppDataPaths.RejectReparsePoints(owned);
            if (!File.Exists(owned)) return preference;
            string digest;
            using (var input = new FileStream(owned, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length is < 1 or > MaximumFileBytes) return preference;
                digest = Convert.ToHexString(SHA256.HashData(input));
            }
            AppDataPaths.RejectReparsePoints(owned);
            return digest.Equals(preference.Hash, StringComparison.OrdinalIgnoreCase)
                ? preference with { Path = owned, Hash = digest } : preference;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Failed relocation keeps the existing preference/error behavior;
            // it never rewrites user data or changes the ordinary translation.
            return preference;
        }
    }
    internal static GameFontPreferenceStamp Capture(string exe)
    {
        lock (preferenceGate) return new(ReadCore(exe), revisions.GetValueOrDefault(Record(exe)));
    }
    internal static bool IsCurrent(string exe, GameFontPreferenceStamp stamp)
    {
        lock (preferenceGate) return revisions.GetValueOrDefault(Record(exe)) == stamp.Revision && ReadCore(exe) == stamp.Value;
    }
    internal static bool TryRestore(string exe, GameFontPreferenceStamp stamp, GameFontPreference previous)
    {
        lock (preferenceGate)
        {
            if (!IsCurrent(exe, stamp)) return false;
            WritePreference(exe, previous);
            return true;
        }
    }
    // Caller holds preferenceGate; the revision changes even for equal values,
    // so a later explicit save is never replaced by an earlier failed request.
    private static GameFontPreferenceStamp WritePreference(string exe, GameFontPreference preference)
    {
        string record = Record(exe);
        AppDataPaths.RejectReparsePoints(record);
        PortableDataStorage.WriteJson(record, preference);
        cache[record] = preference;
        long revision = revisions.GetValueOrDefault(record) + 1;
        revisions[record] = revision;
        return new(preference, revision);
    }
    internal static string Describe(string exe)
    {
        try { var p = Read(exe); return p.Custom ? UiLanguage.Text("自定义字体") + " · " + p.Family : UiLanguage.Text(DefaultLabel); }
        catch { return UiLanguage.Text("字体设置无法读取，请重新选择。"); }
    }
    internal static string[] Families()
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    internal static string ResolveFamilyFile(string family)
    {
        var typeface = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily(family), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var glyph) || !glyph.FontUri.IsFile) throw new IOException("无法找到此字体的文件，请手动选择 TTF 或 OTF 文件。");
        // WPF encodes a non-default collection face as e.g. MSYH.TTC#1.
        // All current file-font bridges load face zero. Dropping the fragment
        // would silently apply a different face from the selected family.
        if (glyph.FontUri.Fragment is { Length: > 0 } face && face != "#0")
            throw new IOException("此字体位于字体集合的非默认位置，暂不能单独选择。请改选独立字体文件或其他已安装字体。");
        return glyph.FontUri.LocalPath;
    }
    internal static GameFontCoverage.Result CheckInstalledFamily(string family, string text)
    {
        // Family-only engines can address nonzero TTC faces. Check that exact
        // installed face rather than inspecting the first face of its file.
        var typeface = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily(family), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var glyph)) throw new IOException("无法找到此字体的文件，请手动选择 TTF 或 OTF 文件。");
        var characters = text.EnumerateRunes().Where(r => !Rune.IsControl(r) && !Rune.IsWhiteSpace(r)).Select(r => r.Value).Distinct().Take(4097).ToArray();
        bool truncated = characters.Length > 4096;
        if (truncated) characters = characters[..4096];
        var missing = characters.Where(c => !glyph.CharacterToGlyphMap.TryGetValue(c, out ushort index) || index == 0).Order().ToArray();
        return new("", characters.Length, characters.Length - missing.Length, missing, truncated);
    }
    internal static (string Family, string Coverage) Inspect(string path, string previewText = "")
    {
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".ttf" or ".otf" or ".ttc")) throw new IOException("请选择 TTF、OTF 或 TTC 字体文件。");
        byte[] bytes;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length > MaximumFileBytes) throw new IOException("字体文件不能超过 32 MB。");
            bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
        }
        var result = GameFontCoverage.Check(bytes, previewText);
        // Read identity from the same bytes as coverage. GDI file loading has
        // a path-length limit, and memory-font registration can reuse cached
        // family names when mixed with WPF previews in the same process.
        return (GameFontNames.Family(bytes), GameFontPreview.Coverage(result));
    }
    internal static void Save(GameInfo game, bool custom, string family, string file)
        => SaveChange(game, custom, family, file);
    internal static GameFontPreferenceChange SaveChange(GameInfo game, bool custom, string family, string file)
    {
        AppDataPaths.RejectReparsePoints(Record(game.ExePath));
        GameFontPreference next = new();
        if (custom)
        {
            if (!Supports(game)) throw new InvalidOperationException("此游戏暂不支持自定义内嵌字体。");
            if (SystemFamilyOnly(game))
            {
                if (!Families().Contains(family, StringComparer.OrdinalIgnoreCase)) throw new IOException("此引擎请选择已安装的字体。");
                next = new(true, family);
            }
            else
            {
                string source = System.IO.Path.GetFullPath(file);
                if((game.AdapterId is "gamemaker" or "wolf" or "yukar-bakin" or "defold" or "unreal")&&System.IO.Path.GetExtension(source).Equals(".ttc",StringComparison.OrdinalIgnoreCase))
                    throw new IOException("此游戏需要独立的 TTF 或 OTF 字体文件，已保留之前的字体选择。");
                var inspected = Inspect(source);
                byte[] bytes = File.ReadAllBytes(source);
                if (bytes.LongLength > MaximumFileBytes) throw new IOException("字体文件不能超过 32 MB。");
                // Revalidate the exact bytes we keep if a source file was replaced during inspection.
                GameFontCoverage.Check(bytes, "");
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                string directory = System.IO.Path.Combine(Storage(game.ExePath), "files");
                AppDataPaths.RejectReparsePoints(directory);
                Directory.CreateDirectory(directory);
                string saved = System.IO.Path.Combine(directory, hash + System.IO.Path.GetExtension(source).ToLowerInvariant());
                AppDataPaths.RejectReparsePoints(saved);
                if (!File.Exists(saved) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(saved))) != hash)
                {
                    string pending = saved + "." + Guid.NewGuid().ToString("N") + ".pending";
                    try
                    {
                        using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
                        if (File.Exists(saved)) File.Replace(pending, saved, saved + ".damaged-" + Guid.NewGuid().ToString("N"));
                        else File.Move(pending, saved);
                    }
                    finally { if (File.Exists(pending)) File.Delete(pending); }
                }
                if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(saved))) != hash) throw new IOException("保存的字体校验失败，请重新选择字体。");
                next = new(true, Inspect(saved).Family, saved, hash);
            }
        }
        lock (preferenceGate)
        {
            GameFontPreference previous;
            try { previous = ReadCore(game.ExePath); }
            catch (Exception ex) when (ex is IOException or JsonException) { previous = new(); }
            return new(previous, WritePreference(game.ExePath, next));
        }
    }
    internal static void ApplyLaunch(ProcessStartInfo start, string exe, bool translationAtStartup, bool independent = false)
    {
        start.Environment.Remove("FUSION_GAME_FONT_INDEPENDENT");
        try
        {
            ApplyLaunchCore(start,exe,translationAtStartup || independent,true);
            if (independent) start.Environment["FUSION_GAME_FONT_INDEPENDENT"] = "1";
        }
        catch (Exception ex) when (independent && ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // A stale preference must not prevent an ordinary game launch.
            // Reconciliation reports it after the authenticated connection is ready.
            start.Environment.Remove(PathVariable); start.Environment.Remove(FamilyVariable);
        }
    }
    // GameMaker receives bytes through its own session sandbox, so a separate
    // shortened desktop runtime copy would be unused and lose source ownership.
    internal static void ApplyGameMakerLaunch(ProcessStartInfo start,string exe,bool translationAtStartup,bool independent=false)
    {
        start.Environment.Remove("FUSION_GAME_FONT_INDEPENDENT");
        try
        {
            ApplyLaunchCore(start,exe,translationAtStartup||independent,false);
            if(independent)start.Environment["FUSION_GAME_FONT_INDEPENDENT"]="1";
        }
        catch(Exception ex) when(independent && ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {start.Environment.Remove(PathVariable);start.Environment.Remove(FamilyVariable);}
    }
    // WOLF owns a process-lifetime glyph cache. Select before its first glyph;
    // never switch that face underneath cached bitmaps or tie it to translation.
    internal static void ApplyWolfLaunch(ProcessStartInfo start,string exe)
    {
        start.Environment.Remove("FUSION_WOLF_FONT_FAILED");
        start.Environment.Remove("FUSION_WOLF_FONT_HASH");
        try
        {
            // This native loader reads Unicode extended paths itself, so keep
            // the owned file instead of publishing a second short-path copy.
            ApplyLaunchCore(start,exe,true,false);
            if(start.Environment.ContainsKey(PathVariable))start.Environment["FUSION_WOLF_FONT_HASH"]=Read(exe).Hash;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            start.Environment.Remove(PathVariable);start.Environment.Remove(FamilyVariable);
            start.Environment["FUSION_WOLF_FONT_FAILED"]="1";
            // A missing custom font must not stop the game or its translation.
            // The preference is retained so the UI can explain and repair it.
        }
    }
    private static void ApplyLaunchCore(ProcessStartInfo start,string exe,bool translationAtStartup,bool prepareRuntimeFile)
    {
        start.Environment.Remove(PathVariable); start.Environment.Remove(FamilyVariable);
        if (!translationAtStartup) return;
        var preference = Read(exe);
        if (!preference.Custom) return;
        if (string.IsNullOrWhiteSpace(preference.Family)) throw new IOException("自定义字体名称无效，请重新选择或恢复默认。");
        string runtimeFamily = preference.Family;
        if (preference.Path.Length > 0)
        {
            string path = System.IO.Path.GetFullPath(preference.Path);
            AppDataPaths.RejectReparsePoints(path);
            string owned = System.IO.Path.GetFullPath(System.IO.Path.Combine(Storage(exe), "files")) + System.IO.Path.DirectorySeparatorChar;
            if (!path.StartsWith(owned, StringComparison.OrdinalIgnoreCase))
                throw new IOException("自定义字体文件缺失或已改变，请重新选择或恢复默认。");
            byte[] bytes;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length is < 1 or > MaximumFileBytes) throw new IOException("自定义字体文件缺失或已改变，请重新选择或恢复默认。");
                bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            }
            if (Convert.ToHexString(SHA256.HashData(bytes)) != preference.Hash)
                throw new IOException("自定义字体文件缺失或已改变，请重新选择或恢复默认。");
            AppDataPaths.RejectReparsePoints(path);
            start.Environment[PathVariable] = prepareRuntimeFile?GameFontRuntimeFiles.Prepare(path, bytes, preference.Hash):path;
            // Use the font's own English face identity for runtime lookup. Some
            // engines enumerate only this alias; an arbitrary localized name
            // silently falls back to a different face. The displayed/saved
            // family remains unchanged, and file-only engines still use bytes.
            // Keep Bold/Italic; a confirmed regular face uses its legacy family.
            runtimeFamily = GameFontNames.RuntimeFaceForCulture(bytes, System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        }
        else if (!Families().Contains(preference.Family, StringComparer.OrdinalIgnoreCase)) throw new IOException("自定义字体已不可用，请重新选择或恢复默认。");
        start.Environment[FamilyVariable] = runtimeFamily;
    }
}
