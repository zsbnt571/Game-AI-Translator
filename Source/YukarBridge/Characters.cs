using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace FusionYukar;
// Only an explicit native nameplate Cast reference establishes the speaker.
// Event senders, face resources, arbitrary labels and adjacent text do not.
internal static class Characters
{
    internal const string Contract="fusion-character-runtime-1";
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly Regex Nameplate=new Regex(@"^\\NP[LCR]?\[\\h\[(0|[1-9][0-9]{0,4})\]\]([^\\\r\n\0]+)$",RegexOptions.CultureInvariant);
    static readonly Regex Expanded=new Regex(@"^\\NP[LCR]?\[([^\[\]\\\r\n\0]{1,200})\]",RegexOptions.CultureInvariant);
    sealed class Row { internal object Reader,Entry,Part;internal string Id,Character,Source,Body,DisplayBody,Prefix,Name,Text,Rendered;internal bool Fallback;internal long Deadline; }
    internal sealed class Reading {internal Reading Previous;internal object Reader;internal string Raw,Body,Source,DisplayBody,Prefix,Character,Name;internal bool Seen;}
    internal sealed class Drawing {internal Drawing Previous;internal object Entry;}
    static readonly Dictionary<object,Row> Rows=new Dictionary<object,Row>();
    [ThreadStatic] static Reading reading;
    [ThreadStatic] static Drawing drawing;
    static bool ready,negotiated;static long serial;
    internal static bool Negotiated=>negotiated;
    static object F(object v,string n)=>v?.GetType().GetField(n,All)?.GetValue(v);
    static bool Typed(object v,string n)=>v!=null&&v.GetType().FullName==n;
    static bool Plain(string s)=>!string.IsNullOrWhiteSpace(s)&&s.Length<=6000&&!s.Any(c=>char.IsControl(c)||c=='\\'||c=='<'||c=='>'||c=='['||c==']');
    static void Hook(Harmony harmony,MethodInfo method,string prefix,string finalizer)
    {if(method==null)throw new MissingMethodException();harmony.Patch(method,prefix:new HarmonyMethod(typeof(Characters).GetMethod(prefix,All)),finalizer:new HarmonyMethod(typeof(Characters).GetMethod(finalizer,All)));}
    internal static void Initialize(Assembly engine,Harmony harmony)
    {
        try
        {
            var reader=engine.GetType("Yukar.Engine.MessageReader",true);
            var entry=reader.GetNestedType("MessageEntry",All);
            var read=reader.GetMethods(All).Single(m=>m.Name=="ReadMessage"&&m.GetParameters().Length==7&&m.GetParameters()[0].ParameterType==typeof(string)&&m.GetParameters()[5].ParameterType==typeof(Guid));
            if(reader.GetField("messageQueue",All)==null||entry?.GetField("parts",All)==null) return;
            Hook(harmony,read,nameof(BeginRead),nameof(EndRead));
            foreach(var method in entry.GetMethods(All).Where(m=>m.Name=="drawStringWithCommands"||m.Name=="measureString"||m.Name=="measureStringSingleLine"))
                Hook(harmony,method,nameof(BeginDraw),nameof(EndDraw));
            ready=true;
        }
        catch {ready=false;} // Optional role support cannot stop ordinary translation.
    }
    internal static void Prepare(Dictionary<string,object> q)
    {Rows.Clear();negotiated=ready&&Runtime.Get(q,"characterRuntimeContract")==Contract;}
    internal static void Stop(){Rows.Clear();negotiated=false;}
    static void BeginRead(object __instance,string __0,out Reading __state)
    {
        __state=new Reading{Previous=reading,Reader=__instance,Raw=__0};reading=__state;
        try
        {
            if(!ready||!negotiated||!Runtime.Enabled||!Typed(__instance,"Yukar.Engine.MessageReader")||__0==null)return;
            Rows.Remove(__instance);
            var match=Nameplate.Match(__0);if(!match.Success||!Plain(match.Groups[2].Value))return;
            int index=int.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
            var main=F(__instance,"gameMain");var catalog=F(main,"catalog");
            if(!Typed(main,"Yukar.Engine.GameMain")||!Typed(catalog,"Yukar.Common.Catalog"))return;
            var cast=catalog.GetType().Assembly.GetType("Yukar.Common.Rom.Cast",false);if(cast==null)return;
            // This is exactly the native \h[index] lookup, before any name
            // expansion; neither displayed names nor party position are keys.
            var method=catalog.GetType().GetMethod("getFilteredItemList",All,null,new[]{typeof(Type),typeof(bool)},null);
            var list=method?.Invoke(catalog,new object[]{cast,true}) as IList;
            if(list==null||list.Count>5000||index>=list.Count||!Typed(list[index],"Yukar.Common.Rom.Cast")||!(F(list[index],"guId") is Guid id)||id==Guid.Empty)return;
            __state.Character="yukar:cast:"+id.ToString("N");__state.Body=match.Groups[2].Value;
        }
        catch {__state.Character=null;}
    }
    internal static void Formatted(object main,string raw,string formatted,string translated)
    {
        try
        {
            var r=reading;if(r?.Character==null||raw!=r.Raw||!ReferenceEquals(F(r.Reader,"gameMain"),main)||formatted==null||formatted.Length>6000)return;
            var match=Expanded.Match(formatted);
            if(!match.Success||formatted.Substring(match.Length)!=r.Body||!translated.StartsWith(match.Value,StringComparison.Ordinal)||!Plain(translated.Substring(match.Length)))return;
            // Preserve the existing format-cache key and its original lookup.
            // Role variants use that same complete source with a native Cast ID.
            r.Source=formatted;r.Prefix=match.Value;r.DisplayBody=translated.Substring(match.Length);r.Name=match.Groups[1].Value;r.Seen=true;
        }
        catch{}
    }
    static Exception EndRead(Exception __exception,Reading __state)
    {
        try
        {
            if(__exception!=null||__state?.Seen!=true||Rows.Count>=32)return __exception;
            var entries=F(__state.Reader,"messageEntries") as Array;if(entries==null||entries.Length>8)return __exception;
            object entry=null,part=null;
            foreach(var item in entries)
            {
                var parts=F(item,"parts") as IList;if(parts==null||parts.Count>8)return __exception;
                foreach(var p in parts)
                {
                    string text=F(p,"text") as string;
                    if(string.IsNullOrEmpty(text))continue;
                    // A page, style split, command or second text fragment is
                    // ambiguous. Keep it on the original translation route.
                    if(entry!=null||text!=__state.DisplayBody||!string.IsNullOrEmpty(F(p,"command") as string)||!string.IsNullOrEmpty(F(p,"ruby") as string))return __exception;
                    entry=item;part=p;
                }
            }
            if(entry!=null)Rows[__state.Reader]=new Row{Reader=__state.Reader,Entry=entry,Part=part,Source=__state.Source,Body=__state.Body,DisplayBody=__state.DisplayBody,Prefix=__state.Prefix,Name=__state.Name,Character=__state.Character,Id="y"+(++serial),Deadline=Stopwatch.GetTimestamp()+3*Stopwatch.Frequency};
        }
        catch {}
        finally {reading=__state?.Previous;}
        return __exception;
    }
    static bool Current(Row row,bool complete)
    {
        try
        {
            if(!negotiated||!Runtime.Enabled||!ReferenceEquals((F(row.Reader,"messageQueue") as IEnumerable)?.Cast<object>().FirstOrDefault(),row.Entry)
                ||F(row.Part,"text") as string!=row.DisplayBody||!(F(row.Entry,"parts") is IList parts)||!parts.Contains(row.Part))return false;
            if(!complete)return true;
            return Equals(F(row.Reader,"<WaitingInput>k__BackingField"),true)&&F(row.Reader,"messageState")?.ToString()=="Wait"
                &&F(row.Entry,"time") is float time&&F(row.Part,"endTime") is float end&&!float.IsNaN(time)&&!float.IsNaN(end)&&time>=end;
        }
        catch{return false;}
    }
    static void BeginDraw(object __instance,out Drawing __state){__state=new Drawing{Previous=drawing,Entry=__instance};drawing=__state;}
    static Exception EndDraw(Exception __exception,Drawing __state){drawing=__state?.Previous;return __exception;}
    internal static bool Render(string source,string route,out string text)
    {
        text=source;if(drawing==null||!negotiated||!Runtime.Enabled)return false;
        var row=Rows.Values.FirstOrDefault(r=>ReferenceEquals(r.Entry,drawing.Entry));if(row==null||!Current(row,false))return false;
        if(!Current(row,true))return row.DisplayBody.StartsWith(source??"",StringComparison.Ordinal);
        if(source!=row.DisplayBody&&source==row.Rendered)return true;
        if(source!=row.DisplayBody)return false;
        if(row.Text!=null){text=row.Rendered=row.Text;return true;}
        // The role deadline only delays an ordinary miss, never a usable cache.
        string ordinary=Runtime.Ordinary(row.Source,route,!row.Fallback&&Stopwatch.GetTimestamp()<=row.Deadline);
        text=row.Rendered=ordinary.StartsWith(row.Prefix,StringComparison.Ordinal)?ordinary.Substring(row.Prefix.Length):source;return true;
    }
    internal static void Poll(Dictionary<string,object> q,Dictionary<string,object> result)
    {
        if(!negotiated)return;
        foreach(var key in Rows.Keys.ToArray())
        {
            var row=Rows[key];
            try{if(!(F(row.Reader,"messageQueue") is IEnumerable queue)||!queue.Cast<object>().Contains(row.Entry))Rows.Remove(key);}catch{Rows.Remove(key);}
        }
        var active=Rows.Values.Where(r=>Current(r,true)).Take(16).ToArray();var accepted=new List<string>();
        if(Runtime.Get(q,"characterRuntimeContract")==Contract&&q.TryGetValue("characterResults",out var value)&&value is IList replies)
            foreach(var reply in replies.Cast<object>().Take(16).OfType<Dictionary<string,object>>())
            {
                var row=active.FirstOrDefault(r=>r.Id==Runtime.Get(reply,"requestId")&&r.Source==Runtime.Get(reply,"source")&&r.Character==Runtime.Get(reply,"characterId"));if(row==null)continue;
                if(reply.TryGetValue("fallback",out var fallback)&&Equals(fallback,true)){row.Text=null;row.Fallback=true;}
                else if(reply.TryGetValue("pending",out var pending)&&Equals(pending,true)){row.Deadline=Stopwatch.GetTimestamp()+3*Stopwatch.Frequency;}
                else {string text=Runtime.Get(reply,"text");if(!text.StartsWith(row.Prefix,StringComparison.Ordinal)||!Plain(text.Substring(row.Prefix.Length)))continue;row.Text=text.Substring(row.Prefix.Length);row.Fallback=false;}
                accepted.Add(row.Id);
            }
        result["characterRuntimeContract"]=Contract;
        result["characterRequests"]=active.Select(r=>new{requestId=r.Id,source=r.Source,characterId=r.Character,displayName=r.Name,evidence=new[]{r.Body}}).ToArray();
        result["characterAccepted"]=accepted.ToArray();
    }
}
