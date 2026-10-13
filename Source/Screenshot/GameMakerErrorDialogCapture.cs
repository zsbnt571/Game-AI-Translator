using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal sealed record GameRuntimeError(DateTime AtUtc,int Pid,string Title,string Text);

// Read only standard error dialogs owned by an already verified game process.
// No input, focus change, screenshot, game hook or exception-handler override.
internal static class GameMakerErrorDialogCapture
{
    internal static GameRuntimeError? Parse(int pid,string title,IEnumerable<string> controls)
    {
        string body=string.Join("\n",controls.Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct());
        if(body.Length>24000)body=body[..24000];
        bool engineError=Regex.IsMatch(body,@"ERROR\s+in\s+action\s+number|FATAL\s+ERROR",RegexOptions.IgnoreCase);
        bool stack=Regex.IsMatch(body,@"\bgml_(?:Script|Object|GlobalScript)_");
        if(!engineError&&!stack)return null;
        return new(DateTime.UtcNow,pid,Sanitize(title,160),Sanitize(body,24000));
    }
    private static string Sanitize(string value,int limit)
    {
        string text=Regex.Replace(value,@"[\x00-\x08\x0B\x0C\x0E-\x1F]","");
        text=Regex.Replace(text,@"(?i)(?:bearer\s+\S+|sk-[a-z0-9_-]{8,}|(?:api[-_ ]?key|authorization|secret|token|profilekey)\s*[:=]\s*\S+)","[已省略凭据]");
        text=Regex.Replace(text,@"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n]*","[已省略路径]");
        return text.Length<=limit?text:text[..limit];
    }
    internal static GameRuntimeError[] Capture(IReadOnlySet<int> gamePids)
    {
        if(!OperatingSystem.IsWindows()||gamePids.Count==0)return [];
        var found=new List<GameRuntimeError>();var watch=Stopwatch.StartNew();
        EnumWindows((window,parameter)=>
        {
            if(watch.ElapsedMilliseconds>150||found.Count>=3)return false;
            GetWindowThreadProcessId(window,out uint pid);
            if(!gamePids.Contains((int)pid)||!IsWindowVisible(window))return true;
            var className=new StringBuilder(128);GetClassName(window,className,className.Capacity);
            if(className.ToString()!="#32770")return true;
            var title=new StringBuilder(160);GetWindowText(window,title,title.Capacity);
            var pieces=new List<string>();int count=0,characters=0;
            EnumChildWindows(window,(child,childParameter)=>
            {
                if(++count>32||characters>=24000||watch.ElapsedMilliseconds>150)return false;
                GetWindowThreadProcessId(child,out uint childPid);if(childPid!=pid)return true;
                var name=new StringBuilder(128);GetClassName(child,name,name.Capacity);
                string cls=name.ToString();
                if(!cls.Equals("Static",StringComparison.OrdinalIgnoreCase)&&!cls.Equals("Edit",StringComparison.OrdinalIgnoreCase)&&!cls.StartsWith("RichEdit",StringComparison.OrdinalIgnoreCase))return true;
                var text=new StringBuilder(Math.Min(16384,24000-characters));
                if(SendMessageTimeout(child,0x000D,(nuint)text.Capacity,text,0x0002,20,out _)!=IntPtr.Zero)
                {characters+=text.Length;pieces.Add(text.ToString());}
                return true;
            },IntPtr.Zero);
            if(Parse((int)pid,title.ToString(),pieces) is {} error)found.Add(error);
            return true;
        },IntPtr.Zero);
        return found.ToArray();
    }
    private delegate bool EnumWindow(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumWindow callback,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr parent,EnumWindow callback,IntPtr parameter);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr SendMessageTimeout(IntPtr window,uint message,nuint wParam,StringBuilder text,uint flags,uint timeout,out nuint result);
}
