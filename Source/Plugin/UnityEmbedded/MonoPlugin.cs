using BepInEx;
using UnityEngine;
[assembly:System.Reflection.AssemblyVersion("0.6.0.84")]
[assembly:System.Reflection.AssemblyFileVersion("0.6.0.84")]

namespace Fusion.UnityEmbedded
{
    [BepInPlugin("local.fusion.unity.embedded", "Fusion Embedded Translation", "0.6.0.84")]
    public sealed class MonoPlugin : BaseUnityPlugin
    {
        void Awake()
        {
            if(string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("FUSION_UNITY_PIPE")))return;
            GameObject owner=null;
            try
            {
                // Some games destroy the loader's manager before its first Update.
                // The bridge owns a separate persistent root, never a game object.
                owner=new GameObject("Fusion Translation Host");
                // Match BepInEx's documented manager protection, but only on our
                // own root. DDOL alone does not prevent startup object cleanup.
                owner.hideFlags=HideFlags.HideAndDontSave;
                Object.DontDestroyOnLoad(owner);
                var host=owner.AddComponent<MonoFrameHost>();
                if(!host.Initialize(message=>Logger.LogInfo(message)))Object.Destroy(owner);
            }
            catch(System.Exception ex){Logger.LogError("Unity bridge initialization failed: "+ex.GetType().FullName);if(owner!=null)Object.Destroy(owner);}
        }
    }
    public sealed class MonoFrameHost : MonoBehaviour
    {
        UnityBridgeRuntime bridge;
        internal bool Initialize(System.Action<string> log)
        {
            try{bridge=new UnityBridgeRuntime(log);if(bridge.Start())return true;bridge.Dispose();bridge=null;return false;}
            catch{if(bridge!=null)bridge.Dispose();bridge=null;throw;}
        }
        void Update(){if(bridge==null)return;bridge.Tick();if(bridge.IsStopped){bridge=null;Object.Destroy(gameObject);}}
        void LateUpdate(){if(bridge!=null)bridge.LateTick();}
        void Close(){var closing=bridge;bridge=null;if(closing!=null)closing.RestoreAndDispose();}
        void OnDestroy(){Close();}
        void OnApplicationQuit(){Close();Object.Destroy(gameObject);}
    }
}
