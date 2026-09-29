using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Fusion.CloudMeadow
{
    internal interface ICloudReaderUpdate { void CommitReading(); }

    // Only the two field readers use this queue. Requests are data-only, including
    // requests made by a mesh, preferred-size probe, text setter or disposal hook.
    internal static class CloudReaderUpdates
    {
        static readonly HashSet<ICloudReaderUpdate> pending=new HashSet<ICloudReaderUpdate>();
        static bool committing,shutdownHook;static int reading;
        // Depth also covers direct mesh/preferred calls outside CanvasUpdateRegistry.
        internal static bool Safe {get{return reading==0&&!CanvasUpdateRegistry.IsRebuildingLayout()&&!CanvasUpdateRegistry.IsRebuildingGraphics();}}
        internal static void EnterRead(){reading++;}
        internal static void ExitRead(){reading--;}
        internal static void Request(ICloudReaderUpdate reader){pending.Add(reader);}
        internal static void Flush()
        {
            if(committing||!Safe)return;
            committing=true;
            try
            {
                // Exactly one snapshot. Reentrant requests remain for a later pass;
                // never drain recursively or force the Canvas to catch up here.
                var batch=new List<ICloudReaderUpdate>(pending);pending.Clear();
                foreach(var reader in batch)
                {
                    if(!Safe){pending.Add(reader);continue;}
                    try{reader.CommitReading();}catch(Exception ex){Debug.LogException(ex);}
                }
            }
            finally{committing=false;}
        }
        internal static void Shutdown()
        {
            // OnDestroy may itself come from an unknown native layout callback.
            // Never commit inline there; finish at a safe Canvas event after the
            // callback has unwound, even though plugin LateUpdate has ended.
            if(pending.Count!=0&&!shutdownHook){shutdownHook=true;Canvas.willRenderCanvases+=FinishShutdown;}
        }
        static void FinishShutdown()
        {Flush();if(pending.Count==0){Canvas.willRenderCanvases-=FinishShutdown;shutdownHook=false;}}
    }
}
