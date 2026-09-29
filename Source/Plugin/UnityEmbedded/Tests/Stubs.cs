using System.Reflection;
namespace HarmonyLib { public class Harmony { public Harmony(string id){} public void Patch(MethodBase m,HarmonyMethod prefix){} public void UnpatchSelf(){} }public class HarmonyMethod {public HarmonyMethod(MethodInfo m){} } }
namespace UnityEngine
{
    public class Object { static int sequence;readonly int id=++sequence;public bool destroyed;public int GetInstanceID()=>id;public static implicit operator bool(Object o)=>o!=null&&!o.destroyed; }
    public class Font:Object {public bool HasCharacter(char c)=>c<256;public static Font CreateDynamicFontFromOSFont(string[] fonts,int size)=>new();}
    public class Component:Object { public T GetComponentInParent<T>() where T:class=>null; }
    public class Resources { public static T[] FindObjectsOfTypeAll<T>()=>Array.Empty<T>(); }
}
namespace UnityEngine.UI {public class Text:UnityEngine.Component {public string text {get;set;}public UnityEngine.Font font{get;set;}=new();}public class InputField:UnityEngine.Component{} }
namespace ScreenshotTranslationUiTester
{
    // This test-only declaration avoids pulling Windows Forms models into the
    // offline fixture; Program verifies its members against the production source.
    public enum SourceLanguageMode { Auto, English, SimplifiedChinese, Japanese, Korean, Mixed }
    internal sealed record RpgTextCatalogResult(string[] Texts,int Files,string[] Unreadable)
    {
        internal string SelectedLanguage { get; init; } = "";
        internal int SkippedByLanguage { get; init; }
        internal IReadOnlySet<string> TrustedSourceTexts { get; init; }
    }
    internal enum SupportLevel { Candidate } internal enum FusionGameProcessState { Stopped }
    internal sealed record GameInfo(string ExePath,string Name,string Engine,string Architecture,string Details,SupportLevel Support,string DataDirectory);
    internal static class FusionAdapterInstaller {internal static FusionGameProcessState ProcessState(GameInfo game)=>FusionGameProcessState.Stopped;}
    internal static class EngineStructureDetection
    {
        internal static string UnityDataDirectory(string root,string stem){string exact=Path.Combine(root,stem+"_Data");if(Directory.Exists(exact))return exact;var found=Directory.GetDirectories(root,"*_Data");return found.Length==1?found[0]:null;}
    }
}
namespace ScreenshotTranslationUiTester { internal sealed class EmbeddedCompatibilityException : System.IO.IOException { internal EmbeddedCompatibilityException(string message):base(message){} } }
