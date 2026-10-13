using BepInEx;
using BepInEx.Unity.Mono;
using UnityEngine;
[assembly:System.Reflection.AssemblyVersion("0.6.0.134")]
[assembly:System.Reflection.AssemblyFileVersion("0.6.0.134")]

namespace Fusion.UnityEmbedded
{
    // BepInEx 6 Mono moved BaseUnityPlugin into a separate assembly/namespace.
    // This wrapper is shipped alone for an existing compatible Mono6 loader.
    [BepInPlugin("local.fusion.unity.embedded", "Fusion Embedded Translation", "0.6.0.134")]
    public sealed class Mono6Plugin : BaseUnityPlugin
    {
        void Awake()
        {
            if(string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("FUSION_UNITY_PIPE")))return;
            GameObject owner=null;
            try
            {
                owner=new GameObject("Fusion Translation Host");
                owner.hideFlags=HideFlags.HideAndDontSave;
                Object.DontDestroyOnLoad(owner);
                var host=owner.AddComponent<Mono6FrameHost>();
                if(!host.Initialize(message=>Logger.LogInfo(message)))Object.Destroy(owner);
            }
            catch(System.Exception ex){Logger.LogError("Unity bridge initialization failed: "+ex.GetType().FullName);if(owner!=null)Object.Destroy(owner);}
        }
    }
    public sealed class Mono6FrameHost : MonoBehaviour
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
        void OnApplicationQuit(){var closing=bridge;bridge=null;if(closing!=null)closing.Shutdown();}
    }
}
