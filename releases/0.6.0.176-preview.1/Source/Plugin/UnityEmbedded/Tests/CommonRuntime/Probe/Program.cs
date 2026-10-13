using System;using System.Collections;using System.Collections.Generic;using System.Diagnostics;using System.IO;using System.Reflection;using System.Threading;using Fusion.UnityEmbedded;using UnityEngine.UI;
static class Program
{
 static UnityBridgeRuntime bridge;static List<object> results=new List<object>();static int failed;
 static void Check(string name,bool passed){results.Add(new Dictionary<string,object>{{"name",name},{"passed",passed}});if(!passed)failed++;}
 static object Call(string name,params object[] args)=>typeof(UnityBridgeRuntime).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(bridge,args);
 static Text Label(string source,bool active=true,bool fit=false){var t=new Text();t.SetBackingDirect(source);t.gameObject.activeInHierarchy=active;t.font.Chinese=true;t.resizeTextForBestFit=fit;UnityEngine.Object.Scene.Add(t);return t;}
 static void Pump(int milliseconds){var clock=Stopwatch.StartNew();do{bridge.Tick();bridge.LateTick();UnityEngine.Canvas.Draw();Thread.Sleep(4);}while(clock.ElapsedMilliseconds<milliseconds);}
 static void DisableAndWait(){for(int i=0;i<1200;i++){var receipt=(Dictionary<string,object>)Call("Handle",BridgeJson.Parse("{\"op\":\"translationDisable\"}"));if((bool)receipt["restored"]&&(int)receipt["restorePending"]==0)return;Pump(4);}throw new Exception("restoration did not complete");}
 static int Main(string[] args)
 {
  string scenario=args[0];var entry=new Dictionary<string,object>{{"source","設定"},{"text","设置"}};
  var cache=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])),scenario+"-cache.json");
  var entries=new[]{entry,new Dictionary<string,object>{{"source","Quest"},{"text","任务"}},new Dictionary<string,object>{{"source","Quest for"},{"text","寻找任务"}}};
  File.WriteAllText(cache,BridgeJson.Write(new Dictionary<string,object>{{"exactOnly",true},{"entries",entries}}));
  Environment.SetEnvironmentVariable("FUSION_UNITY_STARTUP_FILE",cache);Environment.SetEnvironmentVariable("FUSION_UNITY_TRANSLATION_START","1");UnityEngine.Font.ManagedArrayAllowed=true;
  bridge=new UnityBridgeRuntime(_=>{},scenario!="first-layout-query");Call("LoadStartup");Call("InstallHooks");
  try
  {
   if(scenario=="inactive-tail")
   {
    for(int i=0;i<650;i++)Label("設定",false);
    var tail=Label("設定",false);var unloaded=Label("設定",false);unloaded.gameObject.scene=new UnityEngine.SceneManagement.Scene(false);
    Pump(2800);Check("inactive text has not been rewritten",tail.text=="設定");
    var bindings=(IDictionary)typeof(UnityBridgeRuntime).GetField("bindings",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(bridge);
    Check("later inactive menu is discovered despite per-frame limits",bindings.Contains(tail.GetInstanceID()));
    Check("unloaded scene is excluded",!bindings.Contains(unloaded.GetInstanceID()));
    tail.gameObject.activeInHierarchy=true;UnityEngine.Canvas.Draw();Check("first display uses already prepared Chinese",tail.text=="设置");
    DisableAndWait();Check("disable confirmation restores exact source",tail.text=="設定");
   }
   else if(scenario=="unproven-prefix")
   {
    var writer=Label("Quest");UnityEngine.Canvas.Draw();Check("cached word alone does not authorize a new growing writer",writer.text=="Quest");
    writer.SetBackingDirect("Quest for");UnityEngine.Canvas.Draw();Check("extended cached prefix is retained",writer.text=="Quest for");
   }
   else if(scenario=="auto-font-ownership")
   {
    var a=Label("設定",true,true);var b=Label("設定",true,true);var originalA=a.font;var originalB=b.font;Pump(220);
    Check("two fitted controls display cached Chinese",a.text=="设置"&&b.text=="设置");
    Check("independent auto-fit controls cannot evict each other's atlas",a.font!=b.font&&a.font!=originalA&&b.font!=originalB);
    var leased=a.font;a.gameObject.activeInHierarchy=false;var c=Label("設定",true,true);Pump(220);
    Check("inactive lease is recycled rather than unbounded allocation",c.font==leased&&a.font==originalA);
    var external=new UnityEngine.Font{Chinese=true};b.font=external;
    DisableAndWait();
    Check("restoration preserves an external font change",b.font==external);
    Check("disable restores all original source strings",a.text=="設定"&&b.text=="設定"&&c.text=="設定");
   }
   else if(scenario=="font-pool-bound")
   {
    var labels=new List<Text>();for(int i=0;i<33;i++)labels.Add(Label("設定",true,true));Pump(500);
    int translated=0;foreach(var label in labels)if(label.text=="设置")translated++;
    Check("bounded pool has exactly 32 independent active leases",translated==32);
    Check("capacity overflow retains readable original instead of blank text",labels[32].text=="設定");
    labels[0].gameObject.activeInHierarchy=false;Pump(1300);
    Check("a released slot serves the previously blocked visible label",labels[32].text=="设置");
   }
   else if(scenario=="first-layout-query")
   {
    var text=Label("設定");float width=text.preferredWidth,height=text.preferredHeight;
    var bindings=(IDictionary)typeof(UnityBridgeRuntime).GetField("bindings",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(bridge);
    Check("first preferred-size query captures an unseen control",bindings.Contains(text.GetInstanceID()));
    Check("first layout query does not write fonts or dirty the mesh",text.FontWrites==0&&text.DirtyCalls==0&&text.fontSize==24);
    text.fontSize=40;
    Check("later display size cannot change the captured source allocation",width==48&&height==24&&text.preferredWidth==width&&text.preferredHeight==height);
    Check("the game's text and rectangle remain unchanged",text.text=="設定"&&text.rectTransform.rect.width==800&&text.rectTransform.rect.height==200);
   }
   else if(scenario=="fixed-tmp-glyph-fit")
   {
    var text=new TMPro.TextMeshProUGUI();text.rectTransform.rect.height=100;
    var layout=new CjkTextLayout();layout.Prepare(text,"A line of text.","一行测试文字。");
    Check("translated line fits its unchanged authored height",text.fontSize*2*1.05f<=100.01f&&text.rectTransform.rect.height==100&&text.rectTransform.rect.width==800);
    Check("fitting retains input state",!text.raycastTarget&&text.interactable);
    float fitted=text.fontSize;layout.Prepare(text,"A line of text.","一行测试文字。");Check("no repeated recursive shrinking",text.fontSize==fitted);
    layout.Restore(text);Check("disable restores owned font size",text.fontSize==60);
    layout.Prepare(text,"A line of text.","一行测试文字。");text.fontSize=37;layout.Restore(text);Check("external font size remains authoritative",text.fontSize==37);
    var automatic=new TMPro.TextMeshProUGUI{autoSizeTextContainer=true};automatic.rectTransform.rect.height=40;
    new CjkTextLayout().Prepare(automatic,"Short text.","短句。");Check("content-owned rectangles are not chased",automatic.fontSize==60);
   }
   else if(scenario=="quit-no-restore")
   {
    var text=Label("設定");Pump(220);Check("control prepared before quitting",text.text=="设置");
    int writes=text.FontWrites,dirty=text.DirtyCalls;bridge.Shutdown();
    Check("process quit does not rebuild or restore dying controls",text.text=="设置"&&text.FontWrites==writes&&text.DirtyCalls==dirty);
    bridge.Shutdown();Check("shutdown is idempotent",bridge.IsStopped);
   }
   else throw new ArgumentException("Unknown scenario");
  }
  catch(Exception ex){results.Add(ex.ToString());failed++;}
  finally{bridge.RestoreAndDispose();}
  Check("all canvas callbacks are removed",UnityEngine.Canvas.CallbackCount==0);Check("all atlas callbacks are removed",UnityEngine.Font.ActiveAtlasCallbacks==0);
  File.WriteAllText(args[1],BridgeJson.Write(new Dictionary<string,object>{{"scenario",scenario},{"failed",failed},{"passed",failed==0},{"checks",results}}));Console.WriteLine(scenario+": "+(failed==0?"PASS":"FAIL "+failed));return failed==0?0:1;
 }
}
