using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using ScreenshotTranslationUiTester;
using W=System.Windows;
using C=System.Windows.Controls;
using M=System.Windows.Media;
using I=System.Windows.Media.Imaging;

static class TrialAcceptance
{
 static IEnumerable<W.DependencyObject> Walk(W.DependencyObject r){yield return r;foreach(var c in W.LogicalTreeHelper.GetChildren(r).OfType<W.DependencyObject>())foreach(var x in Walk(c))yield return x;}
 static T Id<T>(W.DependencyObject r,string id)where T:W.DependencyObject=>Walk(r).OfType<T>().Single(x=>W.Automation.AutomationProperties.GetAutomationId(x)==id);
 static void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
 static void Click(C.Button b){b.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));Pump();}
 static void Until(Func<bool> done){var watch=System.Diagnostics.Stopwatch.StartNew();while(!done()){if(watch.ElapsedMilliseconds>20000)throw new TimeoutException();Pump();Thread.Sleep(10);}Pump();}
 static T Wait<T>(Task<T> t){Until(()=>t.IsCompleted);return t.GetAwaiter().GetResult();}
 static void Shot(W.FrameworkElement v,string file){var bmp=new I.RenderTargetBitmap((int)v.ActualWidth,(int)v.ActualHeight,96,96,M.PixelFormats.Pbgra32);bmp.Render(v);var e=new I.PngBitmapEncoder();e.Frames.Add(I.BitmapFrame.Create(bmp));using var f=File.Create(file);e.Save(f);}
 [STAThread]static int Main(string[] args)
 {
  var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var checks=new List<object>();int failures=0;string? error=null;
  void Check(bool ok,string name){checks.Add(new{name,passed=ok});if(!ok)Console.WriteLine("FAIL "+name);if(!ok)failures++;}
  void Throws(Action action,string name){try{action();Check(false,name);}catch{Check(true,name);}}
  AppDataPaths.Initialize(["--data-root="+Path.Combine(output,"data"),"--verify-data-root","--disable-global-input"]);
  WpfWorkspace.Enabled=true;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());var app=new W.Application{ShutdownMode=W.ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(WpfThemeResources.Create());UiLanguage.Set(ApplicationLanguage.SimplifiedChinese);
  var requests=new ConcurrentQueue<string>();using var listener=new HttpListener();
  var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();int port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
  var server=Task.Run(async()=>{while(listener.IsListening){HttpListenerContext context;try{context=await listener.GetContextAsync();}catch{break;} _=Task.Run(async()=>{try{using var reader=new StreamReader(context.Request.InputStream);string body=await reader.ReadToEndAsync();requests.Enqueue(body);using var parsed=JsonDocument.Parse(body);string model=parsed.RootElement.GetProperty("model").GetString()!;if(model=="slow")await Task.Delay(1500);context.Response.StatusCode=model=="bad"?400:200;var bytes=Encoding.UTF8.GetBytes(model=="bad"?"{\"error\":{\"message\":\"Synthetic invalid model\"}}":"{\"choices\":[{\"message\":{\"content\":\"今晚在月桥见。\"},\"finish_reason\":\"stop\"}]}");context.Response.ContentType="application/json";await context.Response.OutputStream.WriteAsync(bytes);context.Response.Close();}catch{}});}});
  try
  {
   if(args.Length>1){var destination=Path.Combine(AppContext.BaseDirectory,"adapters","gamemaker","bridge-font.otf");Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(args[1],destination);Check(SHA256.HashData(File.ReadAllBytes(destination)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(args[1]))),"Font fixture uses the actual bundled font bytes");}
   var profile=new ApiSettings{ApiUrl=$"http://127.0.0.1:{port}/v1/chat/completions",ApiKey="synthetic-private-key",Model="good",AdvancedApiParameters="{\"reasoning_effort\":\"none\"}"};
   var state=new FusionState{Profiles=new(){{"A",profile},{"B",ApiSettingsSnapshot.Copy(profile)}},TranslationContent=new(){PromptPresetId="ao3-fusion",PromptPresetText=TranslationLibrary.Presets().Single(p=>p.Id=="ao3-fusion").Instructions,GlossaryPackIds=["social-en-zh"]}};state.Profiles["B"].Model="bad";
   state.ProfileInfo["A"]=new(){Name="测试连接 A"};state.EmbeddedProfile="A";
   var config=TranslationTrial.Snapshot(state);string original=JsonSerializer.Serialize(state);
   Check(config.Names!["A"]=="测试连接 A"&&config.PreferredProfile=="A","Trial shows profile display names and defaults to current embedded connection");
   var social=TranslationLibrary.Presets().Single(p=>p.Id=="social-dialogue");
   Check(TranslationLibrary.Presets().Any(p=>p.Id=="game-community"),"Player community preset is available");
   Check(TranslationLibrary.Packs().Length==6&&TranslationLibrary.Packs().Sum(p=>p.Terms.Count)==369,"Six builtin packs contain 369 reviewed terms");
   Check(social.Instructions.Contains("@handles")&&social.Instructions.Contains("hashtag"),"Social preset protects accounts and searchable tags");
   var request=TranslationTrial.Prepare(config,"A","retweet",social,["social-en-zh"],new Dictionary<string,string>());
   Check(request.Hits.Single().Target=="转推"&&request.Hits.Single().Pack=="英语社交平台用词","Trial shows matching term and its pack source");
   Check(request.Settings.AdvancedApiParameters==profile.AdvancedApiParameters&&request.Settings.ApiKey==profile.ApiKey,"Trial preserves connection and thinking-off parameters");
   Check(request.Settings.PromptPresetText==social.Instructions&&config.Content.PromptPresetId=="ao3-fusion","Preset trial overrides only its request snapshot");
   var temp=TranslationTrial.ParseTerms("retweet = 我的转推\nルカ\t路卡");
   var overrideRequest=TranslationTrial.Prepare(config,"A","retweet",social,["social-en-zh"],temp);
   Check(overrideRequest.Hits.Single().Target=="我的转推"&&overrideRequest.Hits.Single().Pack=="本次临时术语","Temporary terms override matching packs and show provenance");
   var off=TranslationTrial.Prepare(config,"A","retweet",null,[],new Dictionary<string,string>());Check(off.Hits.Length==0,"Unchecked glossary packs produce no mappings");
   var homonym=TranslationTrial.Prepare(config,"A","I like the forest.",null,["social-en-zh"],new Dictionary<string,string>());Check(homonym.Hits.Length==0,"Platform label does not change a narrative homonym");
   var different=TranslationTrial.Snapshot(state);different.Content.TargetLanguage="en";Check(TranslationTrial.Prepare(different,"A","retweet",null,["social-en-zh"],new Dictionary<string,string>()).Hits.Length==0,"Incompatible target language omits pack");
   Throws(()=>TranslationTrial.ParseTerms("x = a\nX = b"),"Duplicate temporary terms rejected");Throws(()=>TranslationTrial.ParseTerms("missing delimiter"),"Malformed temporary terms rejected");
   Throws(()=>TranslationTrial.Prepare(config,"A",new string('x',4001),null,[],new Dictionary<string,string>()),"Bounded trial input");
   Throws(()=>TranslationTrial.Prepare(config,"missing","x",null,[],new Dictionary<string,string>()),"Missing profile cannot trigger request");
   Check(JsonSerializer.Serialize(state)==original,"Trial preparation leaves all original settings intact");
   var japanese=TranslationTrial.Prepare(config,"A","今夜、月の橋で会おう。",null,[],new Dictionary<string,string>());
   var live=Wait(TranslationTrial.RunAsync(japanese,CancellationToken.None));Check(live.Success&&live.Text=="今晚在月桥见。"&&live.Requests==1,"Real HTTP pipeline returns a validated loopback response");
   using(var sent=JsonDocument.Parse(requests.Last())){Check(sent.RootElement.GetProperty("reasoning_effort").GetString()=="none","Transport sends thinking-off parameter");Check(sent.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains(config.Content.PromptPresetText),"Transport sends selected saved preset");Check(!sent.RootElement.GetProperty("stream").GetBoolean(),"Nonstreaming transport is accurately represented in UI");}
   var failure=Wait(TranslationTrial.RunAsync(japanese,CancellationToken.None,(_,_,_)=>Task.FromException<PlainTranslationResult>(new IOException("Synthetic provider failure"))));Check(!failure.Success&&failure.Error.Contains("Synthetic"),"Provider failure has an explicit failed result");
   using(var cancel=new CancellationTokenSource()){cancel.Cancel();var stopped=Wait(TranslationTrial.RunAsync(japanese,cancel.Token,(_,_,t)=>Task.FromCanceled<PlainTranslationResult>(t)));Check(stopped.Cancelled&&!stopped.Success,"Cancellation does not produce fake success");}
   foreach(var theme in new[]{ApplicationThemeMode.Day,ApplicationThemeMode.Night})foreach(var width in new[]{800d,1024d,1440d})
   {
    using var themeOwner=new System.Windows.Forms.Form();UiTheme.Apply(themeOwner,new ApiSettings{ThemeMode=theme});WpfThemeResources.Apply(app.Resources);
    using var area=new WpfTestWorkspace(()=>TranslationTrial.Snapshot(state));var window=new W.Window{Content=area,Width=width,Height=850,ShowInTaskbar=false};window.Show();area.SetActive(true);Pump();int before=requests.Count;
    Check(Id<C.Button>(area,"TrialStart").IsVisible&&!Walk(area).OfType<WpfEngineTestPanel>().Single().IsVisible,$"{theme}/{width}: trial is default and diagnostics hidden");
    var start=Id<C.Button>(area,"TrialStart");var bounds=start.TransformToAncestor(window).TransformBounds(new W.Rect(0,0,start.ActualWidth,start.ActualHeight));Check(bounds.Right<=window.ActualWidth&&bounds.Top<850,$"{theme}/{width}: primary trial action fits viewport");
    Click(Id<C.Button>(area,"TestFontTab"));Check(Id<C.ComboBox>(area,"TestLayoutStyle").IsVisible,$"{theme}/{width}: font layout control is available");
    Id<C.ComboBox>(area,"TestLayoutStyle").SelectedIndex=2;Pump();Shot(window,Path.Combine(output,$"font-{theme}-{width}.png"));
    Click(Id<C.Button>(area,"TestAdvancedTab"));Check(Walk(area).OfType<WpfCharacterTestPanel>().Single().IsVisible,$"{theme}/{width}: advanced defaults to role/cache check");
    Click(Id<C.Button>(area,"Diagnostic_media"));Check(Id<C.Button>(area,"MediaSelfTestScreenshot").IsVisible,$"{theme}/{width}: media selfcheck reachable");
    Click(Id<C.Button>(area,"Diagnostic_engine"));Check(Walk(area).OfType<WpfEngineTestPanel>().Single().IsVisible,$"{theme}/{width}: existing engine checks preserved");
    Click(Id<C.Button>(area,"TestTrialTab"));Id<C.ComboBox>(area,"TrialCompare").SelectedIndex=1;Pump();Check(Id<C.ComboBox>(area,"TrialPresetA").IsEnabled&&!Id<C.ComboBox>(area,"TrialProfileB").IsEnabled,$"{theme}/{width}: preset comparison holds model fixed");
    Id<C.ComboBox>(area,"TrialCompare").SelectedIndex=2;Pump();Check(!Id<C.ComboBox>(area,"TrialPresetA").IsEnabled,$"{theme}/{width}: glossary comparison holds preset fixed");
    Shot(window,Path.Combine(output,$"trial-{theme}-{width}.png"));Check(requests.Count==before,$"{theme}/{width}: opening/changing pages sends no requests");
    window.Close();
   }
   using(var area=new WpfTestWorkspace(()=>TranslationTrial.Snapshot(state)))
   {
    var w=new W.Window{Content=area,Width=1120,Height=950,ShowInTaskbar=false};w.Show();area.SetActive(true);Pump();
    Id<C.TextBox>(area,"TrialInput").Text="今夜、月の橋で会おう。";Id<C.ComboBox>(area,"TrialProfileB").SelectedIndex=2;
    Click(Id<C.Button>(area,"TrialStart"));Until(()=>Id<C.Button>(area,"TrialStart").IsEnabled);
    Check(Id<C.TextBox>(area,"TrialOutputA").Text=="今晚在月桥见。"&&Id<C.TextBox>(area,"TrialOutputB").Text.Contains("400"),"Two sides execute independently; failed side does not erase success");
    int good=requests.Count(s=>s.Contains("\"model\":\"good\""));var retry=Walk(area).OfType<C.Button>().Where(x=>W.Automation.AutomationProperties.GetName(x)=="重试这一侧"||x.Content?.ToString()=="重试这一侧").Last();Click(retry);Until(()=>Id<C.Button>(area,"TrialStart").IsEnabled);
    Check(requests.Count(s=>s.Contains("\"model\":\"good\""))==good,"Retry failed side does not repeat successful API request");
    Id<C.TextBox>(area,"TrialInput").Text="明日会おう。";Check(Id<C.TextBox>(area,"TrialOutputA").Text=="尚未测试","Editing input clears stale result and timing");
    Shot(w,Path.Combine(output,"trial-after-edit.png"));w.Close();
   }
   Check(JsonSerializer.Serialize(state)==original,"Full UI and network trials keep saved state unchanged");
   Check(!File.Exists(Path.Combine(AppDataPaths.Root,"fusion-settings.json"))&&!Directory.Exists(Path.Combine(AppDataPaths.Root,"recent-games-v1")),"Standalone trials create no game/profile data");
  }
  catch(Exception ex){failures++;error=ex.ToString();Console.WriteLine(error);}
  listener.Stop();var report=new{checks,failures,error,loopbackRequests=requests.Count,realProviderCalls=0,assemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainForm).Assembly.Location)))};
  File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));return failures==0?0:1;
 }
}
