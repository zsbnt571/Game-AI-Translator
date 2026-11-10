using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// The runner can read only its own sandbox. Copy verified, immutable font bytes
// into this connection's namespace; never ask it to open a desktop user path.
internal static class GameMakerFontPolicy
{
    internal static JsonObject Stage(string exe, string io, JsonObject request)
    {
        var staged = (JsonObject)request.DeepClone();
        string mode = request["mode"]?.GetValue<string>() ?? "";
        if (mode == "default")
        {
            staged.Remove("path"); staged.Remove("family");
            return staged;
        }
        if (mode != "custom") throw new GameDataOperationException("Invalid font preference mode.");
        string path = Path.GetFullPath(request["path"]?.GetValue<string>() ?? "");
        string extension = Path.GetExtension(path).ToLowerInvariant();
        // GameMaker's file-font API accepts standalone TTF/OTF faces, not TTC.
        if (extension is not (".ttf" or ".otf"))
            throw new GameDataOperationException("GameMaker requires a standalone TTF or OTF font. The previous preference was retained.");
        string owned = Path.GetFullPath(Path.Combine(GameFontSettings.Storage(exe), "files")) + Path.DirectorySeparatorChar;
        AppDataPaths.RejectReparsePoints(path);
        if (!path.StartsWith(owned, StringComparison.OrdinalIgnoreCase)) throw new GameDataOperationException("Invalid custom font file.");
        byte[] bytes;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length is <12 or >GameFontSettings.MaximumFileBytes) throw new GameDataOperationException("Invalid custom font file.");
            bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        }
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(Path.GetFileNameWithoutExtension(path), hash, StringComparison.OrdinalIgnoreCase))
            throw new GameDataOperationException("Invalid custom font file.");
        string coverage = Coverage(bytes);
        string directory = Path.Combine(io, "fonts"), destination = Path.Combine(directory, hash + extension);
        AppDataPaths.RejectReparsePoints(destination);
        Directory.CreateDirectory(directory);
        // Content-addressed files are never rewritten while a runner may use
        // them. A damaged session copy is a failure, not permission to replace it.
        if (File.Exists(destination))
        {
            using var existing = File.OpenRead(destination);
            if (Convert.ToHexString(SHA256.HashData(existing)) != hash) throw new GameDataOperationException("Invalid custom font file.");
        }
        else
        {
            string pending = destination + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
                File.Move(pending, destination);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        staged.Remove("path"); staged.Remove("family");
        staged["file"] = Path.GetFileName(destination); staged["coverage"] = coverage;
        return staged;
    }

    // A compact, exact BMP cmap bitmap avoids asking the runner to enumerate
    // glyph textures. Unproven supplementary characters retain the default
    // translation font. This is coverage evidence, not a rendering guarantee.
    internal static string Coverage(byte[] bytes)
    {
        string sample = new(Enumerable.Range(0, 65536).Where(c => c is <0xd800 or >0xdfff).Select(c => (char)c).ToArray());
        var checkedFont = GameFontCoverage.Check(bytes, sample, 65536);
        var bits = Enumerable.Repeat(15, 16384).ToArray();
        foreach (int code in checkedFont.Missing) bits[code / 4] &= ~(1 << (code % 4));
        for (int code = 0xd800; code <= 0xdfff; code++) bits[code / 4] = 0;
        return new string(bits.Select(n => "0123456789ABCDEF"[n]).ToArray());
    }
}
