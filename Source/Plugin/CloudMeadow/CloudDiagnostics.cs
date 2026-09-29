using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace Fusion.CloudMeadow
{
    // Uses the existing BepInEx log source/listener switches. Bounds apply to this
    // plugin's contribution; it never truncates the shared log of other plugins.
    internal sealed class CloudDiagnostics
    {
        readonly ManualLogSource log;
        readonly string session=Guid.NewGuid().ToString("N").Substring(0,8);
        int occurrence,request,lines,bytes,windowLines,suppressed,repeated;
        readonly int[] laneBytes=new int[4],laneLines=new int[4],laneWindow=new int[4];
        float window,nextSummary;
        const int MaxLines=4608,MaxBytes=640*1024,PerSecond=60;
        // Normal activity cannot consume the failure and dialogue reserves.
        static readonly int[] ByteLimits={256*1024,128*1024,128*1024,128*1024},LineLimits={2048,1024,1024,512},RateLimits={32,8,8,12};
        internal static string Number(float v){return float.IsNaN(v)||float.IsInfinity(v)?"unavailable":v.ToString("F2",CultureInfo.InvariantCulture);}
        internal CloudDiagnostics(ManualLogSource source){log=source;}
        internal CloudTrace Begin(string source,string role)
        {return new CloudTrace{Id=++occurrence,Hash=CloudTranslation.Hash(source??"").Substring(0,16),Role=role,Started=Time.realtimeSinceStartup};}
        internal int NextRequest(){return ++request;}
        internal void Event(CloudTrace trace,string stage,int job=0,float milliseconds=-1,string detail=null)
        {
            if(trace==null)return;
            string stamp=stage+":"+job+":"+(detail??"");
            if(trace.Stages.Contains(stamp)){repeated++;return;}
            bool failure=Failure(stage);int lane=trace.Role=="Bubble"||stage.StartsWith("date-",StringComparison.Ordinal)?3:failure?1:trace.Role=="Dialogue"?2:0;
            // Critical first failures/layout changes retain room after ordinary events.
            if(trace.Stages.Count>=(lane==0?64:96))return;
            trace.Stages.Add(stamp);
            Write("o="+trace.Id+" r="+job+" text="+trace.Hash+" role="+trace.Role+" stage="+stage+
                (milliseconds<0?"":" ms="+((int)milliseconds).ToString(CultureInfo.InvariantCulture))+(detail==null?"":" "+detail),lane);
        }
        static bool Failure(string stage){return stage.IndexOf("exception",StringComparison.Ordinal)>=0||stage.IndexOf("unresolved",StringComparison.Ordinal)>=0||stage.IndexOf("capacity",StringComparison.Ordinal)>=0||stage.StartsWith("rejected-",StringComparison.Ordinal)||stage=="font-unavailable";}
        internal void Global(string stage,string detail=null){Write("o=0 r=0 stage="+stage+(detail==null?"":" "+detail),Failure(stage)?1:0);}
        internal void FlushSummary()
        {
            if(Time.realtimeSinceStartup<nextSummary)return;nextSummary=Time.realtimeSinceStartup+15;
            if(repeated==0)return;int count=repeated;repeated=0;Global("stable-events-coalesced","events="+count+" api-requests=not-inferred");
        }
        bool Write(string value,int lane)
        {
            if(lines>=MaxLines||bytes>=MaxBytes||laneBytes[lane]>=ByteLimits[lane]||laneLines[lane]>=LineLimits[lane])return false;
            float now=Time.realtimeSinceStartup;
            if(now-window>=1){window=now;windowLines=0;Array.Clear(laneWindow,0,laneWindow.Length);}
            if(windowLines>=PerSecond||laneWindow[lane]>=RateLimits[lane]){suppressed++;return false;}
            string line="Cloud trace session="+session+" t="+now.ToString("F3",CultureInfo.InvariantCulture)+" "+value;
            if(suppressed>0){line+=" suppressed="+suppressed;suppressed=0;}
            int size=Encoding.UTF8.GetByteCount(line)+96;
            if(bytes+size>MaxBytes||laneBytes[lane]+size>ByteLimits[lane])return false;
            bytes+=size;lines++;windowLines++;laneBytes[lane]+=size;laneLines[lane]++;laneWindow[lane]++;log.LogInfo(line);return true;
        }
    }
    internal sealed class CloudTrace
    {
        internal int Id,Job;
        internal string Hash,Role;
        internal float Started;
        internal bool AwaitedRequest;
        internal readonly HashSet<string> Stages=new HashSet<string>();
    }
}
