using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ScreenshotTranslationUiTester;
using W=System.Windows;
using C=System.Windows.Controls;
using M=System.Windows.Media;
using I=System.Windows.Media.Imaging;

static class LocalizationAcceptance
{
 static readonly JsonSerializerOptions Json=new(){WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
 static IEnumerable<W.DependencyObject> Walk(W.DependencyObject root)
 {
  var queue=new Queue<W.DependencyObject>();var seen=new HashSet<W.DependencyObject>();queue.Enqueue(root);
  while(queue.TryDequeue(out var item))
  {
   if(!seen.Add(item))continue;yield return item;
   foreach(var c in W.LogicalTreeHelper.GetChildren(item).OfType<W.DependencyObject>())queue.Enqueue(c);
   if(item is M.Visual or System.Windows.Media.Media3D.Visual3D)for(int i=0;i<M.VisualTreeHelper.GetChildrenCount(item);i++)queue.Enqueue(M.VisualTreeHelper.GetChild(item,i));
  }
 }
 static T Id<T>(W.DependencyObject root,string id) where T:W.DependencyObject=>Walk(root).OfType<T>().Single(x=>W.Automation.AutomationProperties.GetAutomationId(x)==id);
 static void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
 static void Click(C.Button b){b.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));Pump();}
 static void Shot(W.FrameworkElement v,string path){var bitmap=new I.RenderTargetBitmap((int)Math.Ceiling(v.ActualWidth),(int)Math.Ceiling(v.ActualHeight),96,96,M.PixelFormats.Pbgra32);bitmap.Render(v);var encoder=new I.PngBitmapEncoder();encoder.Frames.Add(I.BitmapFrame.Create(bitmap));using var f=File.Create(path);encoder.Save(f);}
 static bool ForeignHan(ApplicationLanguage lang)=>lang is not(ApplicationLanguage.SimplifiedChinese or ApplicationLanguage.TraditionalChinese or ApplicationLanguage.Japanese);
 [STAThread] static int Main(string[] args)
 {
  string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var checks=new List<object>();var residuals=new List<object>();var surfaces=new List<object>();int failures=0;string? error=null;
  void Check(bool ok,string name){checks.Add(new{name,passed=ok});if(!ok){failures++;Console.WriteLine("FAIL "+name);}}
  AppDataPaths.Initialize(["--data-root="+Path.Combine(output,"data"),"--verify-data-root","--disable-global-input"]);WpfWorkspace.Enabled=true;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());var app=new W.Application{ShutdownMode=W.ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(WpfThemeResources.Create());
  var assembly=typeof(MainForm).Assembly;
  var keys=JsonSerializer.Deserialize<string[]>(Assembly.GetExecutingAssembly().GetManifestResourceStream("ReviewedKeys")!)!;
  var catalogs=UiLanguage.Supported.Where(l=>l!=ApplicationLanguage.SimplifiedChinese).ToDictionary(l=>l,l=>JsonSerializer.Deserialize<Dictionary<string,string>>(assembly.GetManifestResourceStream("Fusion.Ui"+l+".json")!)!);
  void Surface(W.FrameworkElement root,string page,ApplicationLanguage lang)
  {
   WpfUiLanguage.Dialog(root);root.UpdateLayout();Pump();
   var text=Walk(root).OfType<C.TextBlock>().Where(x=>x.IsVisible&&!string.IsNullOrWhiteSpace(x.Text)).Select(x=>x.Text).Distinct().ToArray();
   surfaces.Add(new{language=lang.ToString(),page,text});
   var leftovers=text.Where(x=>
   {
    if(WpfTestWorkspace.Languages.Any(l=>l.Name==x)||UiLanguage.Options.Any(l=>l.NativeName==x))return false;
    string prose=x.Replace(FusionRuntime.Root,"").Replace(AppDataPaths.Root,"");
    return ForeignHan(lang)&&Regex.IsMatch(prose,"[\\u3400-\\u9fff]") || lang!=ApplicationLanguage.Russian&&Regex.IsMatch(prose,"[\\u0400-\\u04ff]");
   }).ToArray();
   foreach(var value in leftovers)residuals.Add(new{language=lang.ToString(),page,value});
   Check(leftovers.Length==0,$"{lang}/{page}: visible product labels contain no Chinese residue");
  }
  try
  {
   Check(catalogs.Values.All(c=>c.Keys.ToHashSet().SetEquals(catalogs[ApplicationLanguage.English].Keys)),"Eight catalogs have identical key coverage");
   foreach(var lang in UiLanguage.Supported)
   {
    UiLanguage.Set(lang);
    foreach(var key in keys)Check(UiLanguage.Text(key)==(lang==ApplicationLanguage.SimplifiedChinese?key:catalogs[lang][key]),$"{lang}: reviewed text {key}");
    foreach(var p in TranslationLibrary.Presets().Where(p=>p.LegacyStyle is null)){Check(TranslationLibrary.DisplayName(p)==UiLanguage.Text(p.Name)&&TranslationLibrary.DisplayDescription(p)==UiLanguage.Text(p.Description),$"{lang}: preset {p.Id} metadata");}
    foreach(var p in TranslationLibrary.Packs())Check(TranslationLibrary.DisplayName(p)==UiLanguage.Text(p.Name)&&TranslationLibrary.DisplayDescription(p)==UiLanguage.Text(p.Description),$"{lang}: pack {p.Id} metadata");
    foreach(var key in new[]{"ExperimentalNoticeTitle","ExperimentalNoticeBody","ExperimentalNoticeRemember","ExperimentalNoticeContinue","ExperimentalNoticeClose","ExperimentalNoticeSaveFailed"})Check(AiUiText.TryText(key,lang,out var caption)&&!string.IsNullOrEmpty(caption)&&(!ForeignHan(lang)||!Regex.IsMatch(caption,"[\\u3400-\\u9fff]")),$"{lang}: AI warning {key}");
    var custom=new TranslationPreset("user-test","开发状态","用户说明","User instructions","我的预设");Check(TranslationLibrary.DisplayName(custom)==custom.Name&&TranslationLibrary.DisplayDescription(custom)==custom.Description,$"{lang}: custom preset name and description stay verbatim");
    var data=LibraryTagStore.Defaults();string original=JsonSerializer.Serialize(data);
    Check(data.Groups.All(g=>LibraryTagStore.DisplayName(g)==UiLanguage.Text(g.Name)),$"{lang}: built-in group names localized");
    Check(LibraryTagStore.DisplayName(new LibraryTagGroup("user-custom","开发状态",true,[]))=="开发状态",$"{lang}: custom category matching a product label is not translated");
    Check(JsonSerializer.Serialize(data)==original,$"{lang}: display lookup does not mutate serialized tag data");
    Check(NativeLibraryNavigation.DisplayText(new LibraryNavigationNode("标签浏览"){Tag=new LibraryNavigationTarget("tag-browser","")})==UiLanguage.Text("标签浏览"),$"{lang}: custom-drawn tag browser navigation caption localized");
    Check(NativeLibraryNavigation.DisplayText(new LibraryNavigationNode("标签浏览"){Tag=new LibraryNavigationTarget("tag","user-category")})=="标签浏览",$"{lang}: identically named user category stays verbatim");
    Check(UiOperationText.ErrorText("ArgumentException: 请填写 1–4000 字的试用文本。")==UiLanguage.Text("请填写 1–4000 字的试用文本。"),$"{lang}: validation uses selected language without exception class");
    string remote=UiOperationText.ErrorText("HttpRequestException: API 返回 401: foreign provider text");Check(remote.Contains("401")&&!remote.Contains("foreign provider text")&&(!ForeignHan(lang)||!Regex.IsMatch(remote,"[\\u3400-\\u9fff]")),$"{lang}: provider failure preserves HTTP code in a localized explanation");
    Check(!UiOperationText.ErrorText("Exception: foreign provider text").Contains("foreign provider text"),$"{lang}: unknown external error stays out of product caption");
    Check(UiLanguage.Text("HttpRequestException: foreign provider text")==UiLanguage.Text("操作未完成，请检查设置后重试。详情已记录到日志。"),$"{lang}: legacy exception route uses localized explanation");
    Check(UiLanguage.Text("API 返回 403: foreign provider text").Contains("403")&&!UiLanguage.Text("API 返回 403: foreign provider text").Contains("foreign provider text"),$"{lang}: API error route preserves code without leaking foreign body");
    string opaque="我的游戏 TOP";var formatted=UiLanguage.Format($"截图已保存 · {opaque} · {1920} × {1080}");Check(formatted.Contains(opaque)&&formatted.Contains("1920")&&formatted.Contains("1080"),$"{lang}: notification retains user game title and dimensions");
   }
   using var main=new MainForm(Path.Combine(AppDataPaths.Root,"settings.json"));
   main.NativePrepareProfilePage();main.ProfileDraft.Name="Fixture profile";main.ProfileDraft.Model="fixture-model";main.ProfileDraft.ApiUrl="https://example.invalid/v1/chat/completions";main.ProfileDraft.ApiKey="synthetic-local-key";main.NativeSaveProfile();
   var shell=new WpfWorkspaceWindow(main){ShowInTaskbar=false};shell.Show();Pump();
   foreach(var lang in UiLanguage.Supported)
   {
    UiLanguage.Set(lang);shell.RefreshChrome();Pump();Surface(shell,"library-shell",lang);
    var aiButton=Walk(shell).OfType<C.Button>().Single(b=>b.Content is string caption&&caption==AiToolsText.Text("AiTools"));
    Check(aiButton.ToolTip?.ToString()==AiToolsText.Text("AiTools"),$"{lang}: AI tools tooltip follows selected interface language");
    Shot(shell,Path.Combine(output,$"{lang}-library-shell.png"));
   }
   main.Hide();
   using var settings=new WpfSettingsWorkspace(main);
   var window=new W.Window{Content=settings,Width=1100,Height=780,ShowInTaskbar=false,WindowStyle=W.WindowStyle.None};window.Show();Pump();
   foreach(var lang in UiLanguage.Supported)
   {
    UiLanguage.Set(lang);
    foreach(int page in Enumerable.Range(0,11))
    {
     main.NativeSelectSettings(page);settings.Refresh();window.UpdateLayout();Pump();Surface(settings,"settings-"+page,lang);
     if(page==2||page==8)Shot(window,Path.Combine(output,$"{lang}-settings-{page}.png"));
    }
    main.NativeSelectSettings(3);settings.Refresh();Click(Id<C.Button>(settings,"SettingsProfileTab_2"));Surface(settings,"translation-content",lang);Shot(window,Path.Combine(output,$"{lang}-translation-content.png"));
   }
   window.Content=null;window.Close();
   using(var test=new WpfTestWorkspace())
   {
    var surface=new W.Window{Content=test,Width=1150,Height=820,ShowInTaskbar=false};surface.Show();Pump();
    foreach(var lang in UiLanguage.Supported)
    {
     UiLanguage.Set(lang);test.SetActive(true);Pump();Surface(test,"trial",lang);
     Click(Id<C.Button>(test,"TrialStart"));Surface(test,"trial-validation",lang);Check(Id<C.TextBox>(test,"TrialOutputA").Text==UiLanguage.Text("尚未测试"),$"{lang}: result placeholder localized");Shot(surface,Path.Combine(output,$"{lang}-trial.png"));
    }
    Id<C.TextBox>(test,"TrialInput").Text="保持用户原文 / user text";UiLanguage.Set(ApplicationLanguage.English);UiLanguage.Set(ApplicationLanguage.Japanese);Check(Id<C.TextBox>(test,"TrialInput").Text=="保持用户原文 / user text","Switching UI language keeps trial input verbatim");
    surface.Close();
   }
   var beforeLanguage=UiLanguage.Current;string startupPath=ConfigurationManager.ResolveSettingsPath();string? savedStartup=File.Exists(startupPath)?File.ReadAllText(startupPath):null;
   foreach(var lang in UiLanguage.Supported){File.WriteAllText(startupPath,JsonSerializer.Serialize(new{InterfaceLanguage=(int)lang}));UiLanguage.Set(ApplicationLanguage.FollowSystem);UiLanguage.ReadStartupPreference();Check(UiLanguage.Current==lang,$"{lang}: startup preference is restored before diagnostics");}
   if(savedStartup is not null)File.WriteAllText(startupPath,savedStartup);else File.Delete(startupPath);UiLanguage.Set(beforeLanguage);
   foreach(var lang in UiLanguage.Supported)
   {
    UiLanguage.Set(lang);var draft=new TranslationContentEditorState();draft.Load(new());
    using(var library=new NativeTranslationLibraryDialog(draft))
    {library.Show();Pump();Surface(library,"presets",lang);Click(Id<C.Button>(library,"LibraryPacks"));Surface(library,"glossaries",lang);Shot(library,Path.Combine(output,$"{lang}-glossaries.png"));library.Close();}
    var store=new LibraryTagStore(Path.Combine(output,"tags.json"));
    using(var tags=new NativeLibraryTagSettings(store)){tags.Show();Pump();Surface(tags,"tag-settings",lang);tags.Close();}
    using(var tags=new NativeLibraryTagManager(store)){tags.Show();Pump();Surface(tags,"tag-manager",lang);tags.Close();}
    var game=new RecentGame("512b8fe651074b1299f34fce7448a100","Language fixture",Path.Combine(output,"fixture.exe"),DateTimeOffset.UtcNow);
    using(var tags=new NativeGameTagEditor(store,[game])){tags.Show();Pump();Surface(tags,"game-tags",lang);tags.Close();}
    using(var media=new NativeGameMediaDialog(game)){media.Show();Pump();Surface(media,"media",lang);Shot(media,Path.Combine(output,$"{lang}-media.png"));media.Close();}
    using(var keysDialog=new NativeGameMediaSettingsDialog(GameMediaPreferences.Default,_=>null)){keysDialog.Show();Pump();Surface(keysDialog,"media-keys",lang);keysDialog.Close();}
    using(var icons=new NativeIconAppearanceDialog(UiIconColorMode.Monochrome,17,(_,_)=>{})){icons.Show();Pump();Surface(icons,"icon-preview",lang);icons.Close();}
   }
  }
  catch(Exception ex){failures++;error=ex.ToString();Console.WriteLine(error);}
  File.WriteAllText(Path.Combine(output,"surfaces.json"),JsonSerializer.Serialize(surfaces,Json));File.WriteAllText(Path.Combine(output,"residuals.json"),JsonSerializer.Serialize(residuals,Json));
  File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{checks,failures,error,languages=UiLanguage.Supported.Select(x=>x.ToString()),reviewedKeys=keys.Length,surfaces=surfaces.Count,assemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),realApiRequests=0},Json));
  return failures==0?0:1;
 }
}
