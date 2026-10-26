using System.Reflection;
using System.Security.Cryptography;

namespace ScreenshotTranslationUiTester;

public static class BuildIdentity
{
    public const string ProductName="Game Translator";
    public static readonly string BuildVersion=typeof(BuildIdentity).Assembly.GetName().Version?.ToString()??"Unavailable";
    internal static string DiagnosticVersion => (typeof(BuildIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? BuildVersion)
        + " (" + (typeof(BuildIdentity).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? BuildVersion) + ")";
    internal static readonly string LocalWpfCandidate=System.Text.RegularExpressions.Regex.Match(typeof(BuildIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"",@"wpf\.(\d+)").Groups[1].Value;
    internal static string VersionLabel
    {
        get
        {
            var info=typeof(BuildIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"";
            var preview=System.Text.RegularExpressions.Regex.Match(info,@"^(\d+\.\d+\.\d+)-preview\.(\d+)(?:\+.*)?$");
            if(preview.Success)return preview.Groups[1].Value+" Preview "+preview.Groups[2].Value;
            if(System.Text.RegularExpressions.Regex.IsMatch(info,@"^\d+\.\d+\.\d+$"))return info;
            return BuildVersion+(LocalWpfCandidate.Length>0?" wpf"+LocalWpfCandidate:"");
        }
    }
    public static string DisplayVersion=>"Fusion R1 · "+VersionLabel;
    public const string StableApplicationId="GameTranslator.Fusion.20260914";
    public static string ProductVersion=>DisplayVersion;
    public static string BuildId=>DisplayVersion;
    public static string ExecutablePath=>Environment.ProcessPath??string.Empty;
    public static string MainAssemblyPath=>Assembly.GetExecutingAssembly().Location;
    public static string BuildTimestamp=>File.Exists(MainAssemblyPath)?File.GetLastWriteTimeUtc(MainAssemblyPath).ToString("O"):"Unavailable";
    public static string ExeHashShort=>ShortHash(ExecutablePath);
    public static string MainDllHashShort=>ShortHash(MainAssemblyPath);
    private static string ShortHash(string path)
    {try{if(!File.Exists(path))return "Unavailable";using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream))[..12];}catch{return "Unavailable";}}
}

public static class FontDropDownPolicy
{
    public static int RequiredWidth(ComboBox box,IEnumerable<string> items)
    {
        var flags=TextFormatFlags.NoPadding|TextFormatFlags.SingleLine;
        var text=items.DefaultIfEmpty(string.Empty).Max(x=>TextRenderer.MeasureText(x,box.Font,Size.Empty,flags).Width);
        var dpiScale=box.DeviceDpi/96f;
        return Math.Max(box.Width,(int)Math.Ceiling(text+SystemInformation.VerticalScrollBarWidth+32*dpiScale));
    }
    public static void Apply(ComboBox box,IEnumerable<string> items,ToolTip? toolTip=null)
    {var values=items.ToArray();box.DropDownWidth=RequiredWidth(box,values);toolTip?.SetToolTip(box,box.Text);if(toolTip is not null)box.TextChanged+=(_,_)=>toolTip.SetToolTip(box,box.Text);}
}
















