using System.Drawing.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenshotTranslationUiTester;

public sealed record UiTypographyProfile(string PrimaryFamily, IReadOnlyList<string> FallbackFamilies, float Size, FontStyle BodyStyle, FontStyle HeadingStyle);
public sealed record PreviewTypographyProfile(string RequestedFamily, string PrimaryFamily, IReadOnlyList<string> FallbackFamilies, float Size);
public sealed record TranslationImageTypographyProfile(string RequestedFamily, string PrimaryFamily, IReadOnlyList<string> FallbackFamilies, float Size);
public sealed record TypographyFontRun(string Text, string FontFamily, FontStyle Style);
public enum TypographyScript { Latin, SimplifiedChinese, TraditionalChinese, Japanese, Korean, Emoji, Symbol }

public static class FontManager
{
    public const int CurrentConfigVersion = 1;
    private static readonly string[] ProductChain =
    [
        "Segoe UI Variable Text", "Segoe UI",
        "Microsoft YaHei UI", "Microsoft YaHei",
        "Microsoft JhengHei UI", "Microsoft JhengHei",
        "Yu Gothic UI", "Yu Gothic", "Meiryo UI", "Meiryo",
        "Malgun Gothic", "Segoe UI Emoji", "Segoe UI Symbol"
    ];
    private sealed record Baseline(float Size, FontStyle Style, GraphicsUnit Unit);
    private static readonly ConditionalWeakTable<Control, Baseline> Baselines = new();
    private static readonly ConditionalWeakTable<Control, Font> ManagedFonts = new();
    private static readonly ConditionalWeakTable<Control, List<Font>> RetiredFonts = new();
    private static readonly ConditionalWeakTable<Control, object> DisposalSubscriptions = new();
    private static readonly ConditionalWeakTable<Control, object> PreviewTextRoots = new();
    private sealed class UiFontPool
    {
        internal readonly Dictionary<(string,float,FontStyle,GraphicsUnit),Font> Fonts=new();
        internal Font Get(IReadOnlyList<string> chain,float size,FontStyle style,GraphicsUnit unit)
        {
            var key=(chain[0],size,style,unit);
            if(!Fonts.TryGetValue(key,out var font))Fonts[key]=font=CreateValidated(chain,size,style,unit);
            return font;
        }
    }
    private static readonly ConditionalWeakTable<Control,UiFontPool> UiFonts=new();
    private static UiFontPool Pool(Control root)=>UiFonts.GetValue(root,c=>{
        var pool=new UiFontPool();c.Disposed+=(_,_)=>{foreach(var font in pool.Fonts.Values)font.Dispose();pool.Fonts.Clear();};return pool;
    });
    private static IEnumerable<Control> TypographyControls(Control root)
    {
        if(root.IsDisposed||root.Disposing||PreviewTextRoots.TryGetValue(root,out _))yield break;
        yield return root;
        foreach(Control child in root.Controls)foreach(var nested in TypographyControls(child))yield return nested;
    }
    private static bool FontMatches(Font actual,Font desired)=>actual.Name==desired.Name&&Math.Abs(actual.Size-desired.Size)<.01f&&actual.Style==desired.Style&&actual.Unit==desired.Unit;

    private static readonly Lazy<Dictionary<string, string>> Installed = new(() =>
    {
        using var collection = new InstalledFontCollection();
        return collection.Families.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x, x => x, StringComparer.OrdinalIgnoreCase);
    });

    public static bool IsInstalled(string? family) => !string.IsNullOrWhiteSpace(family) && Installed.Value.ContainsKey(family.Trim());
    public static string[] InstalledFamilies() => Installed.Value.Values.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToArray();
    public static string ProductDefaultFamily => FirstInstalled(ProductChain) ?? SystemFonts.MessageBoxFont?.Name ?? FontFamily.GenericSansSerif.Name;

    public static UiTypographyProfile ResolveUiProfile(ApiSettings settings)
    {
        var requested = settings.UiFontMode == UiFontMode.Custom ? settings.UiFontFamily : "Microsoft YaHei UI";
        var chain = BuildChain(requested);
        return new(chain[0], chain, Math.Clamp(settings.UiFontSize, 7F, 24F), FontStyle.Regular,
            ResolveSupportedStyle(chain[0], FontStyle.Bold));
    }

    public static PreviewTypographyProfile ResolvePreviewProfile(ApiSettings settings)
    {
        var requested = settings.PreviewTextFontMode switch
        {
            PreviewTextFontMode.Custom => settings.PreviewTextFontFamily,
            PreviewTextFontMode.FollowUiFont when settings.UiFontMode == UiFontMode.Custom => settings.UiFontFamily,
            _ => ""
        };
        var size = settings.PreviewTextFontMode == PreviewTextFontMode.FollowUiFont
            ? settings.UiFontSize : settings.PreviewTextFontSize;
        var chain = BuildChain(requested);
        return new(requested ?? "", chain[0], chain, Math.Clamp(size, 7F, 48F));
    }

    public static TranslationImageTypographyProfile ResolveTranslationImageProfile(ApiSettings settings)
    {
        var requested = settings.OverlayFontMode == OverlayFontMode.Custom ? settings.OverlayFontFamily : "";
        var chain = BuildTranslationImageChain(requested);
        return new(requested ?? "", chain[0], chain, Math.Clamp(settings.OverlayFontSize <= 0 ? 10F : settings.OverlayFontSize, 7F, 48F));
    }

    public static IReadOnlyList<string> BuildTranslationImageChain(string? requested)
    {
        var result = new List<string>();
        void Add(string? family)
        {
            if (IsInstalled(family) && !result.Contains(family!.Trim(), StringComparer.OrdinalIgnoreCase))
                result.Add(Installed.Value[family.Trim()]);
        }
        Add(requested);
        Add("Microsoft YaHei UI");
        Add("Microsoft YaHei");
        foreach (var family in ProductChain) Add(family);
        Add(SystemFonts.MessageBoxFont?.Name);
        Add(SystemFonts.DefaultFont?.Name);
        if (result.Count == 0) result.Add(FontFamily.GenericSansSerif.Name);
        return result;
    }

    public static IReadOnlyList<string> BuildChain(string? requested)
    {
        var result = new List<string>();
        void Add(string? family)
        {
            if (IsInstalled(family) && !result.Contains(family!.Trim(), StringComparer.OrdinalIgnoreCase))
                result.Add(Installed.Value[family.Trim()]);
        }
        Add(requested);
        foreach (var family in ProductChain) Add(family);
        Add(SystemFonts.MessageBoxFont?.Name);
        Add(SystemFonts.DefaultFont?.Name);
        if (result.Count == 0) result.Add(FontFamily.GenericSansSerif.Name);
        return result;
    }

    public static IReadOnlyList<string> ResolveFallbackChain(TypographyScript script, string? requested = null)
    {
        var preferred = script switch
        {
            TypographyScript.SimplifiedChinese => new[] { "Microsoft YaHei UI", "Microsoft YaHei" },
            TypographyScript.TraditionalChinese => new[] { "Microsoft JhengHei UI", "Microsoft JhengHei" },
            TypographyScript.Japanese => new[] { "Yu Gothic UI", "Yu Gothic", "Meiryo UI", "Meiryo" },
            TypographyScript.Korean => new[] { "Malgun Gothic" },
            TypographyScript.Emoji => new[] { "Segoe UI Emoji" },
            TypographyScript.Symbol => new[] { "Segoe UI Symbol" },
            _ => new[] { "Segoe UI Variable Text", "Segoe UI" }
        };
        var result = new List<string>();
        if (IsInstalled(requested)) result.Add(Installed.Value[requested!.Trim()]);
        foreach (var item in preferred.Concat(ProductChain))
            if (IsInstalled(item) && !result.Contains(item, StringComparer.OrdinalIgnoreCase)) result.Add(Installed.Value[item]);
        if (result.Count == 0) result.Add(ProductDefaultFamily);
        return result;
    }

    public static Font Create(string? requested, float size, FontStyle style = FontStyle.Regular)
    {
        return CreateValidated(BuildChain(requested), size, style, GraphicsUnit.Point);
    }

    public static Font CreatePixel(string? requested,float size,FontStyle style=FontStyle.Regular,bool sourcePixelScale=false)
    {
        return CreateValidated(BuildChain(requested),size,style,GraphicsUnit.Pixel,sourcePixelScale?512f:48f);
    }

    public static void MarkPreviewTextRoot(Control control)
    {
        if (!PreviewTextRoots.TryGetValue(control, out _)) PreviewTextRoots.Add(control, new object());
    }

    public static void ApplyUi(Control root, ApiSettings settings)
    {
        using var uiTiming=UiPerformanceTrace.Measure("ui-font-apply");
        var stages=System.Diagnostics.Stopwatch.StartNew();
        var profile=ResolveUiProfile(settings);
        CaptureBaselines(root);
        var pool=Pool(root);
        var desired=TypographyControls(root).Select(c=>{
            var baseline=Baselines.GetValue(c,x=>new(x.Font.Size,x.Font.Style,x.Font.Unit));
            var style=baseline.Style.HasFlag(FontStyle.Bold)||baseline.Size>=13F?profile.HeadingStyle:profile.BodyStyle;
            return (control:c,font:pool.Get(profile.FallbackFamilies,Math.Clamp(baseline.Size*profile.Size/10F,7F,48F),style,baseline.Unit));
        }).ToArray();
        var prepareMs=stages.Elapsed.TotalMilliseconds;
        if(desired.All(x=>FontMatches(x.control.Font,x.font)))return;
        double assignMs;
        using(var batch=new UiLayoutBatch(root))
        {
            stages.Restart();
            // Children get explicit fonts before parents change inheritance.
            foreach(var (control,font) in desired.Reverse())
                if(!FontMatches(control.Font,font))
                {
                    var watch=System.Diagnostics.Stopwatch.StartNew();control.Font=font;
                    if(UiPerformanceTrace.Enabled&&watch.ElapsedMilliseconds>10)UiPerformanceTrace.Write("font-control-slow",new{type=control.GetType().Name,field=control.AccessibleName,ms=watch.Elapsed.TotalMilliseconds});
                }
            assignMs=stages.Elapsed.TotalMilliseconds;stages.Restart();
        }
        UiPerformanceTrace.Write("ui-font-stages",new{prepareMs,assignMs,layoutMs=stages.Elapsed.TotalMilliseconds});
        root.Invalidate(true);
    }

    private static void CaptureBaselines(Control control)
    {
        if(control.IsDisposed || control.Disposing || PreviewTextRoots.TryGetValue(control,out _))return;
        Baselines.GetValue(control,c=>new(c.Font.Size,c.Font.Style,c.Font.Unit));
        foreach(Control child in control.Controls)CaptureBaselines(child);
    }

    

    public static void ApplyPreviewText(RichTextBox text, ApiSettings settings)
    {
        using var timing=UiPerformanceTrace.Measure("preview-font-apply");
        var profile=ResolvePreviewProfile(settings);MarkPreviewTextRoot(text);
        var font=Pool(text).Get(profile.FallbackFamilies,profile.Size,FontStyle.Regular,GraphicsUnit.Point);
        if(FontMatches(text.Font,font))return;
        using var batch=new UiLayoutBatch(text);text.Font=font;
    }

    private static void SetManagedFont(Control control, Font font)
    {
        if (control.IsDisposed || control.Disposing) { font.Dispose(); return; }
        if (ManagedFonts.TryGetValue(control, out var old))
        {
            // A Control may still need its current Font while FontChanged/layout and
            // native handle creation run. Transfer the control to the replacement
            // first; only then release the previous Font owned by FontManager.
            control.Font = font;
            ManagedFonts.Remove(control);
            ManagedFonts.Add(control, font);
            // Control.Font changes synchronously trigger PreferredHeight/Layout and child
            // inheritance. WinForms can still ask the old Font for metrics after the setter
            // returns, so disposing it here causes Font.GetHeight(Parameter is not valid).
            // Keep manager-owned retired fonts alive until the owning Control is disposed.
            RetiredFonts.GetOrCreateValue(control).Add(old);
            return;
        }
        control.Font = font;
        ManagedFonts.Add(control, font);
        EnsureDisposalSubscription(control);
    }

    private static Font CreateValidated(IEnumerable<string> chain, float size, FontStyle style, GraphicsUnit unit,float maximumSize=48f)
    {
        foreach (var family in chain.Concat([SystemFonts.MessageBoxFont?.Name, SystemFonts.DefaultFont?.Name, FontFamily.GenericSansSerif.Name])
                     .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Font? candidate = null;
            try
            {
                candidate = new Font(family!, Math.Clamp(size, 7F, maximumSize), ResolveSupportedStyle(family!, style), unit);
                _ = candidate.GetHeight(); // reject invalid/damaged/native font handles before assigning to a Control
                return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalException)
            {
                candidate?.Dispose();
                AppLog.Write("typography", $"Font rejected; trying fallback family={family}", ex);
            }
        }
        return new Font(FontFamily.GenericSansSerif, Math.Clamp(size, 7F, maximumSize), FontStyle.Regular, unit);
    }

    private static void ApplySafeFallback(Control root)
    {
        if (root.IsDisposed || root.Disposing) return;
        try
        {
            if (!PreviewTextRoots.TryGetValue(root, out _)) SetManagedFont(root, CreateValidated(BuildChain(null), 10F, FontStyle.Regular, GraphicsUnit.Point));
            foreach (Control child in root.Controls) ApplySafeFallback(child);
        }
        catch (Exception ex) { AppLog.Write("typography", $"Safe fallback skipped control={root.Name}", ex); }
    }

    private static void EnsureDisposalSubscription(Control control)
    {
        if(DisposalSubscriptions.TryGetValue(control,out _))return;
        DisposalSubscriptions.Add(control,new object());
        control.Disposed+=ManagedControlDisposed;
    }

    private static void ManagedControlDisposed(object? sender,EventArgs e)
    {
        if(sender is not Control control)return;
        control.Disposed-=ManagedControlDisposed;
        if(ManagedFonts.TryGetValue(control,out var current))
        {
            ManagedFonts.Remove(control);
            current.Dispose();
        }
        if(RetiredFonts.TryGetValue(control,out var retired))
        {
            RetiredFonts.Remove(control);
            foreach(var font in retired)font.Dispose();
            retired.Clear();
        }
        DisposalSubscriptions.Remove(control);
    }

    public static IReadOnlyList<TypographyFontRun> ResolveTranslationFontRuns(string? text, TranslationImageTypographyProfile profile, FontStyle style = FontStyle.Regular)
        => ResolveFontRuns(text, profile.FallbackFamilies, style);

    public static IReadOnlyList<TypographyFontRun> ResolveFontRuns(string? text, IReadOnlyList<string> fallbackFamilies, FontStyle style = FontStyle.Regular)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<TypographyFontRun>();
        var runs = new List<TypographyFontRun>();
        var family = fallbackFamilies.FirstOrDefault() ?? ProductDefaultFamily;
        var builder = new StringBuilder();
        foreach (var element in StringInfo.GetTextElementEnumerator(text).AsEnumerable())
        {
            // GDI+/RichEdit perform the final glyph fallback. Keep grapheme clusters atomic here.
            var selected = PreferredFamilyFor(element, fallbackFamilies) ?? family;
            if (!selected.Equals(family, StringComparison.OrdinalIgnoreCase) && builder.Length > 0)
            {
                runs.Add(new(builder.ToString(), family, ResolveSupportedStyle(family, style)));
                builder.Clear();
            }
            family = selected;
            builder.Append(element);
        }
        if (builder.Length > 0) runs.Add(new(builder.ToString(), family, ResolveSupportedStyle(family, style)));
        return runs;
    }

    private static string? PreferredFamilyFor(string element, IReadOnlyList<string> chain)
    {
        var rune = element.EnumerateRunes().FirstOrDefault();
        if (rune.Value is >= 0x1F000 and <= 0x1FAFF) return chain.FirstOrDefault(x => x.Equals("Segoe UI Emoji", StringComparison.OrdinalIgnoreCase));
        if (rune.Value is >= 0x3040 and <= 0x30FF) return chain.FirstOrDefault(x => x.StartsWith("Yu Gothic", StringComparison.OrdinalIgnoreCase) || x.StartsWith("Meiryo", StringComparison.OrdinalIgnoreCase));
        if (rune.Value is >= 0xAC00 and <= 0xD7AF) return chain.FirstOrDefault(x => x.Equals("Malgun Gothic", StringComparison.OrdinalIgnoreCase));
        if (rune.Value is >= 0x3400 and <= 0x9FFF) return chain.FirstOrDefault(x => x.Contains("YaHei", StringComparison.OrdinalIgnoreCase) || x.Contains("JhengHei", StringComparison.OrdinalIgnoreCase));
        return chain.FirstOrDefault();
    }

    private static string? FirstInstalled(IEnumerable<string> families) => families.FirstOrDefault(IsInstalled);
    private static FontStyle ResolveSupportedStyle(string family, FontStyle requested)
    {
        try
        {
            using var ff = new FontFamily(family);
            if (ff.IsStyleAvailable(requested)) return requested;
            if (requested.HasFlag(FontStyle.Bold) && ff.IsStyleAvailable(FontStyle.Bold)) return FontStyle.Bold;
            if (ff.IsStyleAvailable(FontStyle.Regular)) return FontStyle.Regular;
            foreach (var candidate in new[] { FontStyle.Bold, FontStyle.Italic, FontStyle.Underline, FontStyle.Strikeout })
                if (ff.IsStyleAvailable(candidate)) return candidate;
        }
        catch { }
        return FontStyle.Regular;
    }

    private static IEnumerable<string> AsEnumerable(this TextElementEnumerator enumerator)
    {
        while (enumerator.MoveNext()) yield return enumerator.GetTextElement();
    }
}

public sealed record TextIntegrityIssue(string Code, int Index, string Message);

public static class TextIntegrityGuard
{
    public static IReadOnlyList<TextIntegrityIssue> Inspect(string? text)
    {
        var issues = new List<TextIntegrityIssue>();
        if (string.IsNullOrEmpty(text)) return issues;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\uFFFD') issues.Add(new("ReplacementCharacter", i, "Text contains U+FFFD."));
            if (char.IsHighSurrogate(c) && (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))) issues.Add(new("InvalidSurrogate", i, "Unpaired high surrogate."));
            else if (char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(text[i - 1]))) issues.Add(new("InvalidSurrogate", i, "Unpaired low surrogate."));
        }
        foreach (var marker in new[] { "锟斤拷", "ï¿½", "Ãƒ", "â€“", "â€™" })
        {
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0) issues.Add(new("ProbableDecodeFailure", index, $"Text contains suspicious decode marker '{marker}'."));
        }
        return issues;
    }
}
