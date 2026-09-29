using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ScreenshotTranslationUiTester;

/// <summary>Retains one declared StringTable language without letting earlier language tables consume its quota.</summary>
internal sealed class UnityLocalizedTableSelection(SourceLanguageMode mode)
{
    private readonly HashSet<string> texts=new(StringComparer.Ordinal);
    private readonly HashSet<UInt128> all=new(),chosen=new();
    private long characters;
    private bool countLimit,selectedLimit;
    internal string SelectedLanguage {get;private set;}="";
    internal bool LimitReached=>selectedLimit;
    internal bool Seen {get;private set;}
    internal IReadOnlySet<string> Texts=>texts;
    private static int Rank(string locale)=>locale=="en"?0:locale=="ja"?1:2;
    private static bool Better(string candidate,string current)=>current.Length==0||Rank(candidate)<Rank(current)||Rank(candidate)==Rank(current)&&StringComparer.Ordinal.Compare(candidate,current)<0;
    internal void ObserveLocale(string? code)
    {Seen=true;}
    private void SelectLocale(string? code)
    {
        Seen=true;
        string locale=UnityTextResourceLanguageSelection.DeclaredLocale(code);
        string? requested=UnityTextResourceLanguageSelection.RequestedLanguage(mode);
        if(mode==SourceLanguageMode.Mixed||locale=="und-declared")return;
        if(requested is not null){if(locale==requested)SelectedLanguage=requested;return;}
        if(!Better(locale,SelectedLanguage))return;
        SelectedLanguage=locale;texts.Clear();chosen.Clear();characters=0;selectedLimit=false;
    }
    internal void Add(string? code,string value)
    {
        if(!UnityEmbeddedCatalog.IsDisplayText(value))return;
        SelectLocale(code);
        UInt128 identity=Identity(value);
        if(all.Count<2_000_000)all.Add(identity);else if(!all.Contains(identity))countLimit=true;
        string locale=UnityTextResourceLanguageSelection.DeclaredLocale(code);
        if(mode!=SourceLanguageMode.Mixed&&(SelectedLanguage.Length==0||locale!=SelectedLanguage))return;
        if(chosen.Count<2_000_000)chosen.Add(identity);else if(!chosen.Contains(identity))countLimit=true;
        if(texts.Contains(value))return;
        if(texts.Count>=150000||characters+value.Length>64L*1024*1024){selectedLimit=true;return;}
        texts.Add(value);characters+=value.Length;
    }
    internal void Complete(Action<string> add,string? selectedLanguage=null)
    {
        if(countLimit)throw new IOException("语言候选超过安全统计上限，未能完成目录；运行时继续补译。");
        if(mode!=SourceLanguageMode.Mixed&&!string.IsNullOrEmpty(selectedLanguage)&&selectedLanguage!=SelectedLanguage)return;
        foreach(string text in texts)add(text);
    }
    internal int Excluded(IEnumerable<string> otherAccepted,IEnumerable<UInt128> otherExcluded,string? selectedLanguage=null)
    {
        // The count concerns different strings, not rows. Shared names in an
        // accepted resource must not also be counted as excluded-language text.
        var retained=mode==SourceLanguageMode.Mixed||string.IsNullOrEmpty(selectedLanguage)||selectedLanguage==SelectedLanguage?new HashSet<UInt128>(chosen):new();
        foreach(string text in otherAccepted)retained.Add(Identity(text));
        var candidates=new HashSet<UInt128>(all);
        candidates.UnionWith(otherExcluded);
        return candidates.Count(id=>!retained.Contains(id));
    }
    internal static UInt128 Identity(string text)
    {
        Span<byte> digest=stackalloc byte[32];SHA256.HashData(Encoding.UTF8.GetBytes(text),digest);
        return ((UInt128)BinaryPrimitives.ReadUInt64LittleEndian(digest)<<64)|BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]);
    }
}
