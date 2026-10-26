using System;using System.Collections;using System.Collections.Generic;using System.Diagnostics;using System.IO;using System.Reflection;using System.Threading;using Fusion.UnityEmbedded;using UnityEngine.UI;
static class Program
{
 static UnityBridgeRuntime bridge;static List<object> results=new List<object>();static int failed;
 static void Check(string name,bool passed){results.Add(new Dictionary<string,object>{{"name",name},{"passed",passed}});if(!passed)failed++;}
 static object Call(string name,params object[] args)=>typeof(UnityBridgeRuntime).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(bridge,args);
 static Text Label(string source,bool active=true,bool fit=false){var t=new Text();t.SetBackingDirect(source);t.gameObject.activeInHierarchy=active;t.font.Chinese=true;t.resizeTextForBestFit=fit;UnityEngine.Object.Scene.Add(t);return t;}
 static void Pump(int milliseconds){var clock=Stopwatch.StartNew();do{bridge.Tick();bridge.LateTick();UnityEngine.Canvas.Draw();Thread.Sleep(4);}while(clock.ElapsedMilliseconds<milliseconds);}
 static void DisableAndWait(){for(int i=0;i<1200;i++){var receipt=(Dictionary<string,object>)Call("Handle",BridgeJson.Parse("{\"op\":\"translationDisable\"}"));if((bool)receipt["restored"]&&(int)receipt["restorePending"]==0)return;Pump(4);}throw new Exception("restoration did not complete");}
 sealed class Parser:TMPro.ITextPreprocessor {public int Calls;public string PreprocessText(string text){Calls++;return text.Replace("{name}","Alice");}}
 static object TestBinding(object target,string source)
 {var type=typeof(UnityBridgeRuntime).GetNestedType("Binding",BindingFlags.NonPublic);var value=Activator.CreateInstance(type,true);foreach(var item in new[]{new KeyValuePair<string,object>("Target",target),new KeyValuePair<string,object>("Source",source)})type.GetField(item.Key,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(value,item.Value);return value;}
 static int Main(string[] args)
 {
  string scenario=args[0];var entry=new Dictionary<string,object>{{"source","設定"},{"text","设置"}};
  var cache=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])),scenario+"-cache.json");
  var entries=new[]{entry,new Dictionary<string,object>{{"source","Quest"},{"text","任务"}},new Dictionary<string,object>{{"source","Quest for"},{"text","寻找任务"}},new Dictionary<string,object>{{"source","Russian text."},{"text","Привет, мир."}},new Dictionary<string,object>{{"source","Arabic text."},{"text","مرحبا بالعالم."}},new Dictionary<string,object>{{"source","Turkish text."},{"text","Çıkış işlemi."}},new Dictionary<string,object>{{"source","Polish text."},{"text","Łódź działa."}},new Dictionary<string,object>{{"source","Ж привет."},{"text","Ж привет!"}}};
  File.WriteAllText(cache,BridgeJson.Write(new Dictionary<string,object>{{"exactOnly",true},{"entries",entries}}));
  Environment.SetEnvironmentVariable("FUSION_UNITY_STARTUP_FILE",cache);Environment.SetEnvironmentVariable("FUSION_UNITY_TRANSLATION_START","1");UnityEngine.Font.ManagedArrayAllowed=true;
  bridge=new UnityBridgeRuntime(_=>{},scenario!="first-layout-query");Call("LoadStartup");Call("InstallHooks");
  try
  {
   if(scenario=="tmp-material-restore")
   {
    var text=new TMPro.TextMeshProUGUI();var original=text.font;var preset=new UnityEngine.Material{Label="authored outline"};text.fontSharedMaterial=preset;
    var binding=TestBinding(text,"Russian text.");
    Check("dynamic TMP fallback is prepared",(bool)Call("PrepareFont",binding,text,"Привет, мир."));
    Check("dynamic replacement uses its own atlas material",text.font!=original&&text.fontSharedMaterial==text.font.material&&text.fontSharedMaterial!=preset);
    Call("RestoreFont",binding,text);Check("restore returns exact original font and custom material",text.font==original&&text.fontSharedMaterial==preset);
    Call("PrepareFont",binding,text,"Привет, мир.");var external=new UnityEngine.Material{Label="new game preset"};text.fontSharedMaterial=external;
    Call("RestoreFont",binding,text);Check("newer external material survives font restoration",text.font==original&&text.fontSharedMaterial==external);
    text.fontSharedMaterial=preset;Call("PrepareFont",binding,text,"Привет, мир.");text.IgnoreMaterialWrites=true;Call("RestoreFont",binding,text);
    Check("rejected material restore retains a retry lease",binding.GetType().GetField("OwnedFont",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(binding)!=null&&text.font==original&&text.fontSharedMaterial!=preset);
    text.IgnoreMaterialWrites=false;Call("RestoreFont",binding,text);Check("later restore retries the exact original preset",text.fontSharedMaterial==preset&&binding.GetType().GetField("OwnedFont",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(binding)==null);
    Call("PrepareFont",binding,text,"Привет, мир.");var externalFont=new TMPro.TMP_FontAsset{Chinese=true};text.font=externalFont;text.fontSharedMaterial=external;
    Call("RestoreFont",binding,text);Check("game replacement font and material remain authoritative",text.font==externalFont&&text.fontSharedMaterial==external);
    if(typeof(UnityBridgeRuntime).GetMethod("AssignFont",BindingFlags.NonPublic|BindingFlags.Instance)!=null)
    {
     text.font=original;text.fontSharedMaterial=preset;binding=TestBinding(text,"A label.");var clone=new TMPro.TMP_FontAsset{Chinese=true};
     Check("same-atlas clone keeps authored preset",(bool)Call("AssignFont",binding,text,text.GetType().GetProperty("font"),original,clone,true)&&text.fontSharedMaterial==preset);
     Call("RestoreFont",binding,text);Check("same-atlas clone restoration keeps exact preset",text.font==original&&text.fontSharedMaterial==preset);
    }
   }
   else if(scenario=="tmp-dynamic-layout")
   {
    string source=new string('A',150),translated=new string('中',80);var text=new TMPro.TextMeshProUGUI{MeasureByCharacters=true};text.rectTransform.rect.width=400;text.rectTransform.rect.height=200;text.overflowMode=TMPro.TextOverflowModes.Truncate;text.maxVisibleCharacters=7;
    var layout=new CjkTextLayout();layout.Prepare(text,source,translated);float fitted=text.fontSize;
    Check("long dialogue wraps and fits within fixed allocation",text.enableWordWrapping&&text.GetPreferredValues(translated,400,1000000).y*1.05f<=200.01f&&fitted>=8&&fitted<60);
    Check("long dialogue retains truncation reveal and hit state",text.overflowMode==TMPro.TextOverflowModes.Truncate&&text.maxVisibleCharacters==7&&!text.raycastTarget&&text.interactable&&text.rectTransform.rect.width==400&&text.rectTransform.rect.height==200);
    layout.Prepare(text,source,translated);Check("identical dynamic display does not recursively shrink",text.fontSize==fitted);
    layout.Prepare(text,"Next.","下一句。");Check("pooled short dialogue releases prior paragraph fit",text.fontSize==60&&!text.enableWordWrapping);
    layout.Prepare(text,source,translated);text.overflowMode=TMPro.TextOverflowModes.Page;layout.Prepare(text,source,translated);
    Check("switching to page flow restores owned font size and wrapping",text.fontSize==60&&!text.enableWordWrapping&&text.overflowMode==TMPro.TextOverflowModes.Page&&text.maxVisibleCharacters==7);
    text.overflowMode=TMPro.TextOverflowModes.Linked;layout.Prepare(text,source,translated);Check("linked dialogue flow is not compressed into one rectangle",text.fontSize==60&&!text.enableWordWrapping);
    text.overflowMode=TMPro.TextOverflowModes.Truncate;layout.Prepare(text,source,translated);text.fontSize=31;text.enableWordWrapping=false;layout.Restore(text);
    Check("game layout updates survive disable",text.fontSize==31&&!text.enableWordWrapping);
   }
   else if(scenario=="tmp-preprocessor-chain")
   {
    var parser=new Parser();var text=new TMPro.TextMeshProUGUI{text="Hello {name}.",textPreprocessor=parser};int translations=0;
    var hook=NativeTmpPreprocessor.Attach(text,s=>{translations++;return s=="Hello Alice."?"你好，Alice。":s;});
    Check("game parser runs before translation exactly once",text.textPreprocessor.PreprocessText(text.text)=="你好，Alice。"&&parser.Calls==1&&translations==1);
    Check("preprocessing preserves game source string",text.text=="Hello {name}.");hook.Dispose();Check("disable restores original parser",ReferenceEquals(text.textPreprocessor,parser));
    hook=NativeTmpPreprocessor.Attach(text,s=>{throw new InvalidOperationException();});Check("translation failure retains parsed readable source",text.textPreprocessor.PreprocessText(text.text)=="Hello Alice.");
    var replacement=new Parser();text.textPreprocessor=replacement;hook.Dispose();Check("external preprocessor replacement survives release",ReferenceEquals(text.textPreprocessor,replacement));
   }
   else if(scenario=="speed-merge-regression")
   {
    Check("translation startup does not inspect or change engine time",UnityEngine.Time.Reads==0&&UnityEngine.Time.Writes==0);
    bool denied=false;try{Call("Handle",BridgeJson.Parse("{\"op\":\"gameSpeed\",\"rate\":2}"));}catch(TargetInvocationException){denied=true;}
    Check("speed operation still requires explicit enable",denied&&UnityEngine.Time.Reads==0&&UnityEngine.Time.Writes==0);
    Call("Handle",BridgeJson.Parse("{\"op\":\"speedEnable\"}"));Call("Handle",BridgeJson.Parse("{\"op\":\"gameSpeed\",\"rate\":2}"));Check("merged runtime applies authorized speed",UnityEngine.Time.timeScale==2);
    Call("Handle",BridgeJson.Parse("{\"op\":\"speedDisable\"}"));Check("speed disable restores baseline",UnityEngine.Time.timeScale==1);
    Call("Handle",BridgeJson.Parse("{\"op\":\"speedEnable\"}"));Call("Handle",BridgeJson.Parse("{\"op\":\"gameSpeed\",\"rate\":2}"));UnityEngine.Time.timeScale=0;Call("Handle",BridgeJson.Parse("{\"op\":\"speedDisable\"}"));Check("game-owned pause survives disable",UnityEngine.Time.timeScale==0);
    UnityEngine.Time.timeScale=1;Call("Handle",BridgeJson.Parse("{\"op\":\"speedEnable\"}"));Call("Handle",BridgeJson.Parse("{\"op\":\"gameSpeed\",\"rate\":3}"));bridge.RestoreAndDispose();Check("plugin disposal restores owned speed",UnityEngine.Time.timeScale==1);
   }
   else if(scenario=="multilingual-font-unavailable")
   {
    UnityEngine.Font.FailFactory=true;var text=Label("Russian text.");text.font.Chinese=false;var original=text.font;Pump(300);
    Check("unavailable non-CJK fallback retains readable source",text.text=="Russian text.");
    Check("failed candidate never overwrites original font",text.font==original&&text.FontWrites==0);
    UnityEngine.Font.FailFactory=false;Pump(1500);
    Check("missing font recovery reuses saved translation",text.text=="Привет, мир."&&text.font!=original);
    DisableAndWait();Check("recovered fallback restores exact source and font",text.text=="Russian text."&&text.font==original);
   }
   else if(scenario=="multilingual-font")
   {
    var originals=new[]{"Russian text.","Arabic text.","Turkish text.","Polish text.","Ж привет."};
    var translated=new[]{"Привет, мир.","مرحبا بالعالم.","Çıkış işlemi.","Łódź działa.","Ж привет!"};
    var labels=new List<Text>();var fonts=new List<UnityEngine.Font>();
    foreach(string original in originals){var label=Label(original);label.font.Chinese=false;labels.Add(label);fonts.Add(label.font);}
    Pump(1300);
    for(int i=0;i<labels.Count;i++){Check("non-CJK translation appears "+i,labels[i].text==translated[i]);Check("fallback is verified rather than accepting Latin-only font "+i,labels[i].font!=fonts[i]&&labels[i].font.Chinese);}
    var external=new UnityEngine.Font{Chinese=true};labels[0].font=external;DisableAndWait();
    Check("external font replacement survives disable",labels[0].font==external);
    for(int i=0;i<labels.Count;i++)Check("multilingual disable restores exact source "+i,labels[i].text==originals[i]);
    for(int i=1;i<labels.Count;i++)Check("owned fallback restored "+i,labels[i].font==fonts[i]);
   }
   else if(scenario=="inactive-tail")
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
