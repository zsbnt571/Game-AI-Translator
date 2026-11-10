using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Session-owned font maps only. Compilation never rewrites the game's archive,
// adapter archive, original glyphs or translation memory. The bridge commits a
// complete bounded set synchronously, or restores the previous set.
internal sealed class DefoldFontSession(string exe, string session)
{
    internal const int MaximumTransferBytes = 32 * 1024 * 1024;
    private sealed record Map(string Resource, byte[] Bytes, string File, string Kind="font",string? Bank=null);
    private Map[]? defaults;
    private readonly Dictionary<string, Map[]> prepared = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);
    internal bool RestartRequired { get; private set; }
    internal bool StartupApplied { get; private set; }
    internal bool StartupFailed { get; private set; }
    internal string StartupHash { get; private set; } = "";

    // Modern 1.9.8 resource.set(fontc) passes a null filename into the native
    // font loader. Build before process start, while no glyph pointers exist.
    // The verified adapter archive remains the permanent default baseline.
    internal string PrepareLaunch()
    {
        StartupApplied=false;StartupFailed=false;StartupHash="";
        string baseline=DefoldGameAdapter.InGame(exe,DefoldGameAdapter.Folder+"/game.projectc");
        using var archive=new DefoldArchive(Path.Combine(Path.GetDirectoryName(baseline)!,"font-session.exe"));
        RestartRequired=archive.Names.Where(n=>n.EndsWith(".fontc",StringComparison.Ordinal)).Any(n=>DefoldGlyphBank.Resource(archive.Read(n)) is not null);
        if(!RestartRequired)return baseline;
        try
        {
            var preference=GameFontSettings.Capture(exe);
            var validated=new System.Diagnostics.ProcessStartInfo();
            GameFontSettings.ApplyGameMakerLaunch(validated,exe,true);
            if(!GameFontSettings.IsCurrent(exe,preference))throw Unavailable();
            if(!validated.Environment.ContainsKey(GameFontSettings.FamilyVariable))return baseline;
            if(!validated.Environment.TryGetValue(GameFontSettings.PathVariable,out string? path)||string.IsNullOrEmpty(path))throw Unavailable();
            string project=PrepareLaunchCustom(archive,baseline,path,preference.Value.Hash);
            if(!GameFontSettings.IsCurrent(exe,preference)){StartupApplied=false;StartupHash="";throw Unavailable();}
            return project;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException or GameDataOperationException or System.Runtime.InteropServices.ExternalException)
        { StartupFailed=true;return baseline; }
    }
    internal string PrepareLaunchCustom(DefoldArchive archive,string baseline,string path,string expectedHash)
    {
        string inputHash;using(var input=File.OpenRead(path))inputHash=Convert.ToHexString(SHA256.HashData(input));
        if(inputHash!=expectedHash)throw Unavailable();
        var command=Prepare(new JsonObject{["mode"]="custom",["path"]=path},default);
        string target=Path.Combine(session,"startup-font-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(target);
        var changes=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        foreach(var entry in command["entries"]!.AsArray())
        {
            string name=entry!["resource"]!.GetValue<string>(),file=entry["file"]!.GetValue<string>();
            if(entry["file"]!.GetValue<string>()!=entry["baseline"]!.GetValue<string>())changes.Add(name,File.ReadAllBytes(Path.Combine(session,file)));
        }
        archive.Write(target,changes);
        foreach(string name in new[]{"game.projectc","game.dmanifest","game.public.der"})
        {string source=Path.Combine(Path.GetDirectoryName(baseline)!,name);if(File.Exists(source))File.Copy(source,Path.Combine(target,name));}
        _=DefoldArchive.VerifyWritten(Path.Combine(target,"font-session.exe"),changes);
        // All files are durable before exposing this project to the launcher.
        using(var input=File.OpenRead(path))if(Convert.ToHexString(SHA256.HashData(input))!=inputHash)throw Unavailable();
        StartupHash=inputHash;
        StartupApplied=true;return Path.Combine(target,"game.projectc");
    }

    internal async Task<JsonObject> PrepareAsync(JsonObject request, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try { return await Task.Run(() => Prepare(request, cancellation), cancellation); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or InvalidOperationException or OverflowException
            or System.Runtime.InteropServices.ExternalException)
        { throw Unavailable(); }
        finally { gate.Release(); }
    }

    private JsonObject Prepare(JsonObject request, CancellationToken cancellation)
    {
        string mode = request["mode"]?.GetValue<string>() ?? "";
        if (mode is not ("custom" or "default")) throw new GameDataOperationException("Invalid font preference mode.");
        // An untouched session has no custom resource to restore. Avoid loading
        // or rebuilding the default fonts on every ordinary launch.
        if (mode == "default" && defaults is null) return Command(mode, [], []);
        if (defaults is null)
        {
            if (!DefoldGameAdapter.IsInstalled(exe)) throw Unavailable();
            using var archive = new DefoldArchive(DefoldGameAdapter.InGame(exe, DefoldGameAdapter.Folder + "/font-session.exe"));
            var names = archive.Names.Where(n => n.EndsWith(".fontc", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Take(17).ToArray();
            if (names.Length is < 1 or > 16) throw Unavailable();
            var maps = new List<Map>();var banks = new Dictionary<string,Map>(StringComparer.Ordinal); long total = 0, textures = 0;
            foreach (string name in names)
            {
                cancellation.ThrowIfCancellationRequested();
                byte[] bytes = archive.Read(name);
                total += bytes.Length;
                string? bank=DefoldGlyphBank.Resource(bytes);
                if(bank is null)textures += TextureBytes(bytes);
                else if(!banks.ContainsKey(bank))
                {
                    byte[] data=archive.Read(bank);total+=data.Length;textures+=TextureBytes(DefoldGlyphBank.Legacy(data));
                    banks.Add(bank,new(bank,data,Save(data),"bank"));
                }
                if (total > MaximumTransferBytes || textures > 64L * 1024 * 1024) throw Unavailable();
                maps.Add(new(name, bytes, Save(bytes),"font",bank));
            }
            // Every shared bank is generated once. All style maps remain part
            // of the session snapshot; modern resources load before startup.
            defaults = banks.Values.OrderBy(b=>b.Resource,StringComparer.Ordinal).Concat(maps).ToArray();
        }
        if (mode == "default") return Command(mode, defaults, defaults);
        string font = request["path"]?.GetValue<string>() ?? "";
        if (!Path.IsPathFullyQualified(font) || Path.GetExtension(font).ToLowerInvariant() is not (".ttf" or ".otf"))
            throw new GameDataOperationException("This engine requires a standalone TTF or OTF font. The previous preference was retained.");
        AppDataPaths.RejectReparsePoints(font);
        if (!File.Exists(font) || new FileInfo(font).Length > GameFontSettings.MaximumFileBytes) throw Unavailable();
        string hash;
        using (var input = File.OpenRead(font)) hash = Convert.ToHexString(SHA256.HashData(input));
        if (!prepared.TryGetValue(hash, out var replacement))
        {
            if (prepared.Count >= 4) throw new GameDataOperationException("The font preview limit for this game session was reached. Restart the game to select more fonts.");
            var maps = new List<Map>(); long total = 0, textures = 0;
            try
            {
                foreach (var original in defaults)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(original.Bank is not null)
                    {total+=original.Bytes.Length;if(total>MaximumTransferBytes)throw Unavailable();maps.Add(original);continue;}
                    var result = original.Kind=="bank"?DefoldGlyphBank.Replace(original.Bytes,font):DefoldFontBuilder.ReplaceWithCoverage(original.Bytes, font);
                    total += result.Bytes.Length;
                    textures += TextureBytes(original.Kind=="bank"?DefoldGlyphBank.Legacy(result.Bytes):result.Bytes);
                    if (total > MaximumTransferBytes || textures > 64L * 1024 * 1024 || result.Coverage.Replaced == 0) throw Unavailable();
                    maps.Add(new(original.Resource, result.Bytes, Save(result.Bytes),original.Kind,original.Bank));
                }
                using var input = File.OpenRead(font);
                if (Convert.ToHexString(SHA256.HashData(input)) != hash) throw Unavailable();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
            { throw Unavailable(); }
            replacement = maps.ToArray(); prepared.Add(hash, replacement);
        }
        cancellation.ThrowIfCancellationRequested();
        return Command(mode, replacement, defaults);
    }

    private string Save(byte[] bytes)
    {
        string name = "font-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".fontc";
        string path = Path.Combine(session, name); AppDataPaths.RejectReparsePoints(path);
        if (!File.Exists(path))
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(bytes); output.Flush(true);
        }
        using var check = File.OpenRead(path);
        if (check.Length != bytes.Length || !SHA256.HashData(check).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw Unavailable();
        return name;
    }
    private static JsonObject Command(string mode, Map[] maps, Map[] baseline)
    {
        var entries = new JsonArray();
        for (int i = 0; i < maps.Length; i++)
        {
          var entry=new JsonObject {
            ["resource"] = maps[i].Resource, ["file"] = maps[i].File,
            ["baseline"] = baseline[i].File, ["bytes"] = maps[i].Bytes.Length,
            ["baselineBytes"] = baseline[i].Bytes.Length,["kind"]=maps[i].Kind
          };if(maps[i].Bank is not null)entry["bank"]=maps[i].Bank;entries.Add(entry);
        }
        return new JsonObject { ["op"] = "fontPreferences", ["mode"] = mode, ["independent"] = true, ["entries"] = entries };
    }
    private static long TextureBytes(byte[] bytes)
    {
        var fields = DefoldWire.Read(bytes);
        int Number(int field) => checked((int)(fields.SingleOrDefault(x => x.Number == field && x.Kind == 0)?.Value ?? 0));
        int width = Number(15), height = Number(16), channels = Number(20);
        // Multi-channel old fonts encode outline/shadow masks separately. A
        // monochrome glyph cannot preserve those styles, so leave them alone.
        if (width is < 1 or > 2048 || height is < 1 or > 2048 || channels != 1) throw Unavailable();
        return checked((long)width * height * channels);
    }
    private static GameDataOperationException Unavailable() => new("The running engine could not load the selected font. The previous preference was retained.");
}
