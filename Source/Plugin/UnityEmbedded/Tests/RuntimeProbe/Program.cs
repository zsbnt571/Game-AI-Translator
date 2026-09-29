using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

string exe=args[0],identity=args[1],evidence=args[2];bool startOff=args.Contains("--start-off");
exe=IsolatedGamePath.Require(exe);
if(!identity.StartsWith("FusionTest75_",StringComparison.Ordinal))throw new Exception("Isolated copy required.");
var asm=typeof(ScreenshotTranslationUiTester.GameInfo).Assembly;
var connectionType=asm.GetType("ScreenshotTranslationUiTester.RpgGameDataConnection")!;
var connection=Activator.CreateInstance(connectionType,BindingFlags.Instance|BindingFlags.NonPublic,null,new object?[]{exe,"UNITY"},null)!;
object? Invoke(string method,params object?[] values)=>connectionType.GetMethod(method,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(connection,values);
async Task<JsonElement> Request(string op,Action<JsonObject>? configure=null)
{
    var message=new JsonObject{["op"]=op};configure?.Invoke(message);var task=(Task<JsonElement>)Invoke("RequestAsync",message,CancellationToken.None)!;return await task;
}
var records=new List<object>();Process? game=null;
try
{
    var cache=new Dictionary<string,string>{{"START","开始"},{"BACK","返回"},{"CREDITS","制作名单"},{"CHEATS","辅助选项"}};
    if(args.Contains("--inheat")){cache.Clear();cache["Settings"]="设置";cache["Quit Game"]="退出游戏";cache["Play Game"]="开始游戏";cache["quit game"]="退出游戏";cache["Adult Disclaimer"]="内容提示";cache["Yes"]="是";cache["No"]="否";cache["Choice"]="选项";}
    if(args.Contains("--ascii"))foreach(var key in cache.Keys.ToArray())cache[key]="TEST-"+key;
    Invoke("PrimeStartup",cache);var start=(ProcessStartInfo)Invoke("StartInfo",!startOff)!;start.ArgumentList.Add("-screen-fullscreen");start.ArgumentList.Add("0");start.ArgumentList.Add("-screen-width");start.ArgumentList.Add("960");start.ArgumentList.Add("-screen-height");start.ArgumentList.Add("540");
    game=Process.Start(start)!;var waitType=asm.GetType("ScreenshotTranslationUiTester.EmbeddedLaunchWait")!;await (Task)waitType.GetMethod("AcceptAsync",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{connection,game,CancellationToken.None})!;
    JsonElement diagnostic=await Request("translationDiagnostics");
    if(diagnostic.GetProperty("companyName").GetString()!=identity||diagnostic.GetProperty("productName").GetString()!=identity||!diagnostic.GetProperty("persistentDataPath").GetString()!.Contains(identity,StringComparison.Ordinal))throw new Exception("Unity runtime save identity is not isolated.");
    records.Add(new{Phase="connected",Diagnostic=diagnostic});
    await Task.Delay(args.Contains("--inheat")?18000:4000);diagnostic=await Request("translationDiagnostics");records.Add(new{Phase="startup-cache",Diagnostic=diagnostic});
    records.Add(new{Phase="runtime-source-poll",Result=await Request("translationPoll")});
    if(startOff&&(diagnostic.GetProperty("enabled").GetBoolean()||diagnostic.GetProperty("displayed").GetArrayLength()!=0))throw new Exception("Translation-at-start disabled flag was not respected.");
    if(!startOff&&!args.Contains("--loader-only")&&diagnostic.GetProperty("setterCacheHits").GetInt32()+diagnostic.GetProperty("enableCacheHits").GetInt32()+diagnostic.GetProperty("renderCacheHits").GetInt32()==0)throw new Exception("No pre-render cache hook observed.");
    int epoch=(await Request("translationPrepare")).GetProperty("epoch").GetInt32();
    await Request("translationApply",o=>{o["epoch"]=epoch;o["prime"]=true;o["entries"]=new JsonArray(cache.Select(p=>(JsonNode)new JsonObject{["source"]=p.Key,["text"]=p.Value}).ToArray());});
    await Request("translationEnable");
    diagnostic=await Request("translationDiagnostics");
    if(!args.Contains("--loader-only")&&!diagnostic.GetProperty("displayed").EnumerateArray().Any(v=>cache.TryGetValue(v.GetProperty("source").GetString()!,out var translated)&&v.GetProperty("text").GetString()==translated))throw new Exception("No actual component getter returned a cached translation after enable.");
    await Request("translationApply",o=>{o["epoch"]=epoch;o["entries"]=new JsonArray(new JsonObject{["source"]="SCENES",["text"]="场景"},new JsonObject{["source"]="This text has not appeared in the game",["text"]="后台预译验证"});});
    records.Add(new{Phase="background-cache",Diagnostic=await Request("translationDiagnostics")});
    await Request("translationDisable");diagnostic=await Request("translationDiagnostics");records.Add(new{Phase="disabled",Diagnostic=diagnostic});if(diagnostic.GetProperty("enabled").GetBoolean()||diagnostic.GetProperty("displayed").GetArrayLength()!=0)throw new Exception("Owned translations were not restored.");
    if(diagnostic.TryGetProperty("observed",out var observed)&&observed.EnumerateArray().Any(v=>cache.ContainsKey(v.GetProperty("source").GetString()??"")&&v.GetProperty("current").GetString()!=v.GetProperty("source").GetString()))throw new Exception("A component getter did not restore its original value.");
    bool rejected=false;try{await Request("translationApply",o=>{o["epoch"]=epoch;o["entries"]=new JsonArray(new JsonObject{["source"]="START",["text"]="过期"});});}catch{rejected=true;}if(!rejected)throw new Exception("Stale epoch accepted.");records.Add(new{Phase="stale-rejected",Passed=true});
    await Request("translationEnable");records.Add(new{Phase="reenabled",Diagnostic=await Request("translationDiagnostics")});
    Console.WriteLine(args.Contains("--loader-only") ? "PASS isolated Unity loader/pipe/cache/epoch control only; neutral fixture has no text scene, so no text-render acceptance." : "PASS isolated Unity runtime pipe, startup cache hook, apply, restore, stale-epoch rejection, reenable.");
}
catch(Exception ex){records.Add(new{Phase="failure",Error=ex.ToString()});throw;}
finally
{
    Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);File.WriteAllText(evidence,JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}));
    ((IDisposable)connection).Dispose();
    if(game is not null&&!game.HasExited){game.CloseMainWindow();if(!game.WaitForExit(5000))game.Kill(true);game.Dispose();}
}







