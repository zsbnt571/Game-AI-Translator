using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using HarmonyLib;

namespace FusionYukar;
internal static class Runtime
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=32*1024*1024,RecursionLimit=32};
    static readonly Dictionary<string,string> Cache=new Dictionary<string,string>(StringComparer.Ordinal);
    static readonly HashSet<string> Queued=new HashSet<string>(StringComparer.Ordinal),Outputs=new HashSet<string>(StringComparer.Ordinal);
    static readonly HashSet<string> AppliedSources=new HashSet<string>(StringComparer.Ordinal);
    static readonly Queue<string> Queue=new Queue<string>();
    static readonly object Gate=new object();
    static string folder,secret;static bool enabled,adoptable,stopped;static int epoch=1,lastId;static long lastTick,drawReplacements,measureReplacements,formattedReplacements;
    static readonly Dictionary<string,long> Retry=new Dictionary<string,long>();
    static object gameMain;static readonly GameData Data=new GameData();
    static readonly List<string> Hooks=new List<string>();
    static bool testMuted;
    internal static object Field(object value,string name)=>value?.GetType().GetField(name,All)?.GetValue(value);
    internal static void Start(string player)
    {
        folder=Environment.GetEnvironmentVariable("FUSION_YUKAR_SESSION");secret=Environment.GetEnvironmentVariable("FUSION_YUKAR_SECRET");
        if(!Path.IsPathRooted(folder??"")||!Regex.IsMatch(secret??"","^[A-Fa-f0-9]{64}$"))throw new InvalidOperationException("Invalid session.");
        enabled=Environment.GetEnvironmentVariable("FUSION_YUKAR_START")=="1";
        testMuted=TestEnvironment.Active;
        var seed=Read("startup.json",32*1024*1024);
        if(seed!=null&&Get(seed,"secret")==secret&&seed.TryGetValue("entries",out object entries)&&entries is Dictionary<string,object> values)
            foreach(var item in values.Take(100000))if(item.Value is string value&&Compatible(item.Key,value)){Cache[item.Key]=value;Outputs.Add(value);}
        adoptable=enabled;
        var engine=Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(player),"bakinengine.dll"));
        var type=engine.GetType("Yukar.Engine.GameMain",true);
        var update=type.GetMethod("update",All,null,new[]{typeof(float)},null);
        if(update==null||type.GetField("data",All)==null)throw new InvalidOperationException("Player update/data contract is unsupported.");
        var harmony=new Harmony("local.fusion.yukar.143");Patch(harmony,update,nameof(Update));
        foreach(var t in engine.GetTypes().Where(t=>t.FullName=="Yukar.Engine.Graphics"||t.FullName=="Yukar.Engine.GraphicsCore"))
            foreach(var m in t.GetMethods(All).Where(m=>(m.Name.StartsWith("DrawString",StringComparison.Ordinal)||m.Name=="MeasureString")&&m.GetParameters().Count(p=>p.ParameterType==typeof(string))==1))
                Patch(harmony,m,nameof(TextArguments));
        // Translate a complete message before the engine splits it into glyphs.
        foreach(var m in type.GetMethods(All).Where(m=>m.Name=="replaceForFormat"&&m.ReturnType==typeof(string)))
            Patch(harmony,m,null,nameof(Formatted));
        if(testMuted)
        {
            var plays=engine.GetTypes().Where(t=>t.FullName=="Yukar.Engine.Audio"||t.FullName=="Yukar.Engine.AudioCore").SelectMany(t=>t.GetMethods(All)).Where(m=>m.Name.StartsWith("Play",StringComparison.Ordinal)&&m.ReturnType==typeof(void)).ToArray();
            if(plays.Length==0)throw new InvalidOperationException("Silent test audio hook unavailable.");
            foreach(var m in plays)Patch(harmony,m,nameof(SilentPlay));
        }
        Write("hello.json",new{secret,protocol=143,engine="yukar",pid=Process.GetCurrentProcess().Id,supervisor=Environment.GetEnvironmentVariable("FUSION_YUKAR_SUPERVISOR"),player,hooks=Hooks.ToArray(),testMuted});
    }
    static void Patch(Harmony h,MethodInfo method,string prefix,string postfix=null)
    {
        try{h.Patch(method,prefix==null?null:new HarmonyMethod(typeof(Runtime).GetMethod(prefix,All)),postfix==null?null:new HarmonyMethod(typeof(Runtime).GetMethod(postfix,All)));}
        catch(Exception e){throw new InvalidOperationException("Hook failed: "+method.DeclaringType.FullName+"."+method.Name,e);}
        Hooks.Add(method.DeclaringType.FullName+"."+method.Name);
    }
    static bool SilentPlay()=>false;
    static void TextArguments(MethodBase __originalMethod,object[] __args)
    {lock(Gate){var ps=__originalMethod.GetParameters();for(int i=0;i<ps.Length;i++)if(ps[i].ParameterType==typeof(string)&&__args[i] is string text)__args[i]=Translate(text,__originalMethod.Name=="MeasureString"?"measure":"draw");}}
    static void Formatted(ref string __result){lock(Gate)__result=Translate(__result,"format");}
    static string Translate(string source,string route)
    {
        if(!enabled||!Valid(source)||Outputs.Contains(source))return source;
        if(Cache.TryGetValue(source,out string value)){AppliedSources.Add(source);if(route=="draw")drawReplacements++;else if(route=="measure")measureReplacements++;else formattedReplacements++;return value;}
        if(Queue.Count<4096&&Queued.Count<100000&&(!Retry.TryGetValue(source,out long until)||Stopwatch.GetTimestamp()>=until)&&Queued.Add(source))Queue.Enqueue(source);
        return source;
    }
    static readonly Regex Controls=new Regex(TextCodes.Pattern+@"|\{[^{}\r\n]+\}|<[^<>\r\n]+>",RegexOptions.Compiled);
    static bool Valid(string s)=>!string.IsNullOrWhiteSpace(s)&&s.Length>1&&s.Length<=6000&&s.IndexOf('\0')<0&&Controls.Replace(s,"").Any(char.IsLetter);
    static string[] Tokens(string s)=>Controls.Matches(s).Cast<Match>().Select(m=>m.Value).ToArray();
    static bool Compatible(string s,string t)=>Valid(s)&&!string.IsNullOrWhiteSpace(t)&&t.Length<=12000&&t.IndexOf('\0')<0&&!t.Contains("⟦")&&Tokens(s).SequenceEqual(Tokens(t));
    static void Update(object __instance,ref float __0)
    {
        if(testMuted)System.Threading.Thread.Sleep(25);
        lock(Gate)
        {
            if(stopped)return;
            gameMain=__instance;
            long now=Stopwatch.GetTimestamp();if((now-lastTick)*1000/Stopwatch.Frequency<50)return;lastTick=now;
            try
            {
                if(File.Exists(Path.Combine(folder,"stop"))&&File.ReadAllText(Path.Combine(folder,"stop"))==secret){enabled=false;Data.Stop();gameMain=null;stopped=true;return;}
                var q=Read("request.json",262144);
                if(q!=null&&Get(q,"secret")==secret)
                {
                    int id=Convert.ToInt32(q["id"]);
                    if(id>lastId)
                    {
                        lastId=id;object result;
                        try{result=Command(q);Write("response-"+id+".json",new{id,secret,ok=true,result});}
                        catch(Exception e){Write("response-"+id+".json",new{id,secret,ok=false,error=(e.InnerException??e).Message});}
                    }
                }
                // Process disable before touching game data or enforcing locks.
                // Sync also gates the provider, so translation-only frames do
                // not traverse the modification data object.
                Data.Sync(()=>Field(gameMain,"data"));Data.Tick();
            }catch(Exception e){try{File.WriteAllText(Path.Combine(folder,"runtime-error.txt"),e.ToString());}catch{}}
        }
    }
    static object Status()=>new{enabled,epoch,applied=AppliedSources.Count,drawReplacements,measureReplacements,formattedReplacements,live=enabled&&gameMain!=null?1:0,hooked=true};
    static object Command(Dictionary<string,object> q)
    {
        string op=Get(q,"op");if(op.StartsWith("data",StringComparison.Ordinal))return Data.Command(q,()=>Field(gameMain,"data"));
        if(op=="translationStatus")return Status();
        if(op=="translationPrepare")
        {
            if(q.TryGetValue("preserveStartup",out var preserve)&&Equals(preserve,true)&&adoptable&&enabled){adoptable=false;return Status();}
            enabled=false;adoptable=false;epoch++;Cache.Clear();Outputs.Clear();Queue.Clear();Queued.Clear();Retry.Clear();AppliedSources.Clear();drawReplacements=measureReplacements=formattedReplacements=0;return Status();
        }
        if(op=="translationEnable"){enabled=true;return Status();}
        if(op=="translationDisable"){enabled=false;epoch++;Queue.Clear();Queued.Clear();return Status();}
        if(op=="translationPoll"){var texts=new List<string>();while(enabled&&Queue.Count>0&&texts.Count<8)texts.Add(Queue.Dequeue());return new{enabled,epoch,applied=AppliedSources.Count,drawReplacements,measureReplacements,formattedReplacements,texts};}
        if(op=="translationRetry"){
            if(Convert.ToInt32(q["epoch"])==epoch){string s=Get(q,"source");Queued.Remove(s);if(Retry.Count<4096)Retry[s]=Stopwatch.GetTimestamp()+30*Stopwatch.Frequency;}return new{accepted=0};}
        if(op=="translationApply")
        {
            int accepted=0;if(Convert.ToInt32(q["epoch"])==epoch&&(enabled||q.TryGetValue("prime",out var prime)&&Equals(prime,true))&&q.TryGetValue("entries",out var items)&&items is object[] list)
                foreach(var item in list.Take(256).OfType<Dictionary<string,object>>())
                {string s=Get(item,"source"),t=Get(item,"text");if(Compatible(s,t)&&(Cache.ContainsKey(s)||Cache.Count<100000)){Cache[s]=t;Outputs.Add(t);Queued.Remove(s);Retry.Remove(s);accepted++;}}
            return new{accepted};
        }
        throw new InvalidOperationException("Unsupported request.");
    }
    internal static string Get(Dictionary<string,object> q,string key)=>q.TryGetValue(key,out var value)&&value is string s?s:"";
    static Dictionary<string,object> Read(string name,int max)
    {string p=Path.Combine(folder,name);if(!File.Exists(p))return null;var f=new FileInfo(p);if(f.Length>max||(f.Attributes&FileAttributes.ReparsePoint)!=0)return null;return Json.DeserializeObject(File.ReadAllText(p,Encoding.UTF8)) as Dictionary<string,object>;}
    static void Write(string name,object value)
    {string p=Path.Combine(folder,name),s=Json.Serialize(value);if(Encoding.UTF8.GetByteCount(s)>1048576)throw new IOException("Reply too large.");string t=p+".tmp";File.WriteAllText(t,s,new UTF8Encoding(false));if(File.Exists(p))File.Delete(p);File.Move(t,p);}
}
