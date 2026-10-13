using System.Globalization;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Soft line-break preferences from the system Unicode word dictionary.
/// Text, font size and safety limits remain owned by the existing layout planner.</summary>
internal sealed record PreferredWordBreaks(HashSet<int> Offsets, string Engine)
{
    internal static PreferredWordBreaks Create(string text, IReadOnlyList<string>? protectedTerms = null)
    {
        var offsets=new HashSet<int>(StringInfo.ParseCombiningCharacters(text)){text.Length};
        var engine="GRAPHEME_FALLBACK";
        if(text.Any(c=>c is >= '\u3400' and <= '\u9fff'))
        {
            IntPtr buffer=IntPtr.Zero,iterator=IntPtr.Zero;
            try
            {
                // ICU retains the UTF-16 buffer until the iterator is closed.
                buffer=Marshal.StringToHGlobalUni(text);var error=0;
                iterator=ubrk_open(1,"zh",buffer,text.Length,ref error);
                if(iterator!=IntPtr.Zero && error<=0)
                {
                    var words=new HashSet<int>();var count=0;
                    for(var at=ubrk_first(iterator);at!=-1 && count++<=text.Length+1;at=ubrk_next(iterator))
                        if(at>=0 && at<=text.Length)words.Add(at);
                    if(words.Contains(0)&&words.Contains(text.Length)){offsets=words;engine="WINDOWS_ICU_WORD_DICTIONARY";}
                }
            }
            catch(Exception e) when(e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { engine="SYSTEM_ICU_UNAVAILABLE_GRAPHEME_FALLBACK"; }
            finally { if(iterator!=IntPtr.Zero)ubrk_close(iterator);if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer); }
        }
        foreach(var term in protectedTerms??Array.Empty<string>())
        {
            if(term.Length<2||term.Length>16)continue;
            for(var at=text.IndexOf(term,StringComparison.Ordinal);at>=0;at=text.IndexOf(term,at+1,StringComparison.Ordinal))
                offsets.RemoveWhere(x=>x>at && x<at+term.Length);
        }
        return new(offsets,engine);
    }

    internal static int[] ElementOffsets(string[] elements)
    {var offsets=new int[elements.Length+1];for(var i=0;i<elements.Length;i++)offsets[i+1]=offsets[i]+elements[i].Length;return offsets;}

    [DllImport("icu.dll",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr ubrk_open(int type,[MarshalAs(UnmanagedType.LPUTF8Str)]string locale,IntPtr text,int length,ref int status);
    [DllImport("icu.dll",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ubrk_first(IntPtr iterator);
    [DllImport("icu.dll",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ubrk_next(IntPtr iterator);
    [DllImport("icu.dll",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void ubrk_close(IntPtr iterator);
}
