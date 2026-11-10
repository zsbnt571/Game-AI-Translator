using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using ScreenshotTranslationUiTester;
using W=System.Windows;
using C=System.Windows.Controls;
using M=System.Windows.Media;
using I=System.Windows.Media.Imaging;

static class LibraryAcceptance
{
    static IEnumerable<W.DependencyObject> Walk(W.DependencyObject root){yield return root;foreach(var c in W.LogicalTreeHelper.GetChildren(root).OfType<W.DependencyObject>())foreach(var v in Walk(c))yield return v;}
    static T Id<T>(W.DependencyObject root,string id) where T:W.DependencyObject=>Walk(root).OfType<T>().Single(x=>W.Automation.AutomationProperties.GetAutomationId(x)==id);
    static void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
    static void Click(C.Button b){b.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));Pump();}
    static void Shot(W.FrameworkElement v,string path){var bitmap=new I.RenderTargetBitmap((int)v.ActualWidth,(int)v.ActualHeight,96,96,M.PixelFormats.Pbgra32);bitmap.Render(v);var e=new I.PngBitmapEncoder();e.Frames.Add(I.BitmapFrame.Create(bitmap));using var file=File.Create(path);e.Save(file);}
    [STAThread]static int Main(string[] args)
    {
        var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var checks=new List<object>();int failures=0;string? error=null;
        void Check(bool ok,string name){checks.Add(new{name,passed=ok});if(!ok){failures++;Console.WriteLine("FAIL "+name);}}
        void Throws(Action a,string name){try{a();Check(false,name);}catch{Check(true,name);}}
        AppDataPaths.Initialize(["--data-root="+Path.Combine(output,"data"),"--verify-data-root","--disable-global-input"]);
        WpfWorkspace.Enabled=true;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());var app=new W.Application{ShutdownMode=W.ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(WpfThemeResources.Create());UiLanguage.Set(ApplicationLanguage.SimplifiedChinese);
        try
        {
            var old=JsonSerializer.Deserialize<TranslationContentSettings>("{\"TranslationStyle\":5,\"CustomTranslationPrompt\":\"Keep my wording\",\"TargetLanguage\":\"zh-CN\"}")!;
            var legacy=new ApiSettings{ApiUrl="http://localhost/v1/chat/completions",ApiKey="synthetic",Model="model",AdvancedApiParameters="{\"reasoning_effort\":\"none\"}"};old.ApplyTo(legacy);
            Check(legacy.PromptPresetId==""&&legacy.GlossaryPackIds.Length==0&&legacy.CustomTranslationPrompt=="Keep my wording","Legacy content migrates without selecting new packs or replacing custom text");
            Check(TranslationLibrary.Guidance(legacy)==TranslationStylePolicy.Guidance(TranslationStyle.Literary),"Legacy literary prompt stays byte-equivalent");
            var state=new TranslationContentEditorState();state.Load(old);var ao3=TranslationLibrary.Presets().Single(p=>p.Id=="ao3-fusion");state.Choose(ao3);state.PackIds=["ui-en-zh"];state.Snapshot().ApplyTo(legacy);
            Check(legacy.ApiKey=="synthetic"&&legacy.Model=="model"&&legacy.AdvancedApiParameters.Contains("none"),"Preset selection leaves API, model and disabled reasoning unchanged");
            string prompt=TranslationPromptBuilder.BuildPlainSystemPrompt(legacy);
            Check(prompt.Contains(ao3.Instructions)&&!prompt.Contains("Selected style: Literary"),"AO3 replaces the style instruction instead of stacking literary modes");
            Check(prompt.Contains("Keep my wording")&&prompt.Contains("Return only the translated text")&&prompt.Contains("placeholders"),"Custom supplement and protected output contract survive preset selection");
            var copy=ApiSettingsSnapshot.Copy(legacy);Check(copy.PromptPresetText==legacy.PromptPresetText&&copy.GlossaryPackIds.SequenceEqual(legacy.GlossaryPackIds),"Settings snapshots carry preset and pack selection");
            copy.GlossaryPackIds[0]="different";Check(legacy.GlossaryPackIds[0]=="ui-en-zh","Snapshot pack IDs are independent");
            var round=JsonSerializer.Deserialize<TranslationContentSettings>(JsonSerializer.Serialize(state.Snapshot()))!;Check(round.PromptPresetText==ao3.Instructions&&round.GlossaryPackIds.Single()=="ui-en-zh","Saved content round trip retains preset and packs");
            var all=TranslationLibrary.Packs();Check(all.Single(p=>p.Id=="common-ja-zh").Terms.Count==180&&all.Single(p=>p.Id=="galgame-ja-zh").Terms.Count==85,"MIT term counts match reviewed source data");
            Check(!TranslationLibrary.RequestGuidance(legacy,["A quiet evening."]).Contains("New Game"),"Unused glossary entries are not sent");
            Check(TranslationLibrary.RequestGuidance(legacy,["New Game"]).Contains("New Game"),"Matching menu term enters request");
            Check(TranslationLibrary.RequestGuidance(legacy,["We should continue walking."])=="","Label scope avoids forcing menu terms into narrative homonyms");
            var wrong=ApiSettingsSnapshot.Copy(legacy);wrong.TargetLanguage="ja";Check(TranslationLibrary.RequestGuidance(wrong,["New Game"])=="","Target-language mismatch disables pack");
            wrong.TargetLanguage="zh-CN";wrong.SourceLanguage=SourceLanguageMode.Japanese;Check(TranslationLibrary.RequestGuidance(wrong,["New Game"])=="","Source-language mismatch disables pack");
            var document=new TranslationWorkspaceStore.Document{Terms=new(){{"New Game","开始新旅程"}},TermRules=new(){{"New Game",new("label","manual")}},Manual=new(){{"Hello","我的修订"}}};
            var effective=TranslationLibrary.WithPacks(document,legacy);
            Check(effective.Terms["New Game"]=="开始新旅程"&&document.Terms.Count==1,"Manual game terms override packs without changing stored workspace");
            var automatic=document with{TermRules=new(){{"New Game",new("label","automatic")}}};
            Check(TranslationLibrary.WithPacks(automatic,legacy).Terms["New Game"]=="新游戏","Explicit pack selection overrides previously automatic terminology");
            var prepared=TranslationWorkspaceStore.WithTerms(document,["New Game"],legacy,legacy);
            Check(prepared.Single.RequestTerms!["New Game"]=="开始新旅程"&&!TranslationLibrary.RequestGuidance(prepared.Single,["New Game"]).Contains("New Game"),"Request contains the explicit game mapping only once");
            Check(prepared.Single.CustomTranslationPrompt.Contains(JsonEncodedText.Encode("开始新旅程").ToString())&&!prepared.Single.CustomTranslationPrompt.Contains("Quick Save"),"Game request includes only matching terms");
            var noPack=ApiSettingsSnapshot.Copy(legacy);noPack.GlossaryPackIds=[];noPack.LibraryPacks=[];
            TranslationItem Item(string s)=>new("GRP001",s);
            Check(TranslationCacheKeyBuilder.Build([Item("A quiet evening.")],legacy)==TranslationCacheKeyBuilder.Build([Item("A quiet evening.")],noPack),"Selecting irrelevant pack reuses screenshot cache");
            Check(TranslationCacheKeyBuilder.Build([Item("New Game")],legacy)!=TranslationCacheKeyBuilder.Build([Item("New Game")],noPack),"Matching pack changes screenshot cache identity");
            var another=ApiSettingsSnapshot.Copy(legacy);another.PromptPresetText+="\nDifferent wording.";
            Check(RpgTranslationCache.Id(legacy,"x")!=RpgTranslationCache.Id(another,"x"),"Changed preset separates game cache identity");
            Check(RpgTranslationCache.Id(legacy,"x")==RpgTranslationCache.Id(noPack,"x"),"Pack selection keeps game cache files and uses per-source receipts");
            var receipts=new TranslationGlossaryCache();receipts.Accept("New Game","新游戏",TranslationLibrary.WithPacks(new(),legacy).Terms);
            Check(!receipts.Matches("New Game","新游戏",effective.Terms),"Changed matching game term rejects old receipt");
            receipts.Accept("A quiet evening.","宁静的夜晚。",TranslationLibrary.WithPacks(new(),legacy).Terms);
            Check(receipts.Matches("A quiet evening.","宁静的夜晚。",effective.Terms),"Unrelated glossary edit preserves receipt");
            var request=(System.Net.Http.HttpRequestMessage)typeof(TranslationService).GetMethod("BuildPlainRequest",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,["New Game",legacy])!;
            var body=request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();using var json=JsonDocument.Parse(body);
            Check(json.RootElement.GetProperty("reasoning_effort").GetString()=="none"&&body.Contains("New Game"),"Actual HTTP request builder retains reasoning-off and glossary");
            Check(json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains(ao3.Instructions),"Actual HTTP request carries selected preset");
            request.Dispose();
            var csv=Path.Combine(output,"terms.csv");File.WriteAllText(csv,"source,target\n\"Hello, friend\",\"你好，朋友\"\nNew Game,新游戏\n");
            var imported=TranslationLibrary.ReadTerms(csv);Check(imported.Count==2&&imported["Hello, friend"]=="你好，朋友","CSV import supports quoted commas");
            var custom=new TranslationLibraryDocument{Presets=[new("my-preset","我的预设","测试","Keep names.")],Packs=[new("my-pack","我的词库","测试","en","zh-CN",imported)]};
            TranslationLibrary.Save(custom);var loaded=TranslationLibrary.Load();Check(loaded.Packs.Single().Terms.SequenceEqual(imported)&&loaded.Presets.Single().Instructions=="Keep names.","Custom library persists under data and reloads exactly");
            byte[] before=File.ReadAllBytes(TranslationLibrary.FilePath);custom.Packs[0].Terms["bad"]="";Throws(()=>TranslationLibrary.Save(custom),"Invalid import rejected before replacement");Check(File.ReadAllBytes(TranslationLibrary.FilePath).SequenceEqual(before),"Failed import leaves library bytes intact");
            Throws(()=>TranslationLibrary.ReadTermsFromJson("{\"A\":\"一\",\"a\":\"二\"}"),"Case-insensitive duplicate term rejected");
            var evil=new TranslationLibraryDocument{Presets=[new("evil","Bad","","Test","","file:///C:/Windows/notepad.exe")]};Throws(()=>TranslationLibrary.Save(evil),"Imported source links cannot launch local executable");
            using var main=new MainForm(Path.Combine(AppDataPaths.Root,"settings.json"));
            var host=(IDesktopSettingsHost)main;Check(host.NativeMediaPreferences==GameMediaPreferences.Default,"Main settings loads screenshot and video defaults without opening gallery");
            using var shortcuts=new WpfDesktopSettingsPage(host,2);var view=new W.Window{Width=900,Height=760,Content=shortcuts.Content,ShowInTaskbar=false};view.Show();Pump();
            Check(Walk(view).OfType<C.TextBlock>().Any(t=>t.Text=="游戏截图与录像")&&Id<C.Button>(view,"ConfigureGameMediaKeys").IsVisible,"Keys page exposes game screenshot and recording section");view.Close();
            using(var controller=new GameMediaController(()=>Task.FromResult(Array.Empty<RecentGame>()),_=>{},p=>p.Screenshot.Key==Keys.F12?"conflict":null))
            {
                var changed=new GameMediaPreferences(new(InputBindingKind.Keyboard,Keys.F7,Ctrl:true,Alt:true),new(InputBindingKind.Keyboard,Keys.F6,Ctrl:true,Alt:true));
                Check(controller.SavePreferences(changed)==null&&GameMediaPreferences.Load()==changed,"Global controller saves keys without gallery");
                Check(controller.SavePreferences(changed with{Screenshot=new(InputBindingKind.Keyboard,Keys.F12)})=="conflict"&&GameMediaPreferences.Load()==changed,"Conflicting key keeps previously saved configuration");
                Throws(()=>controller.SavePreferences(changed with{Recording=changed.Screenshot}),"Screenshot and recording cannot share a key");
            }
            using(var reopened=new GameMediaController(()=>Task.FromResult(Array.Empty<RecentGame>()),_=>{}))Check(reopened.Preferences.Recording.Key==Keys.F6,"Global keys survive controller restart");
            foreach(var theme in new[]{ApplicationThemeMode.Day,ApplicationThemeMode.Night})
            {
                using var themeOwner=new System.Windows.Forms.Form();UiTheme.Apply(themeOwner,new ApiSettings{ThemeMode=theme});WpfThemeResources.Apply(app.Resources);
                foreach(var width in new[]{740d,1050d})
                {
                    var draft=new TranslationContentEditorState();draft.Load(old);
                    using var library=new NativeTranslationLibraryDialog(draft);library.Width=width;library.Show();Pump();
                    Check(Id<C.Button>(library,"Preset_ao3-fusion").ActualWidth>20,$"{theme} {width}: preset card is usable");
                    Click(Id<C.Button>(library,"Preset_ao3-fusion"));Click(Id<C.Button>(library,"LibraryPacks"));
                    var check=Id<C.CheckBox>(library,"Pack_ui-en-zh");check.IsChecked=true;check.RaiseEvent(new W.RoutedEventArgs(C.Primitives.ButtonBase.ClickEvent));Pump();
                    Shot(library,Path.Combine(output,$"library-{theme}-{width}.png"));
                    var apply=Id<C.Button>(library,"LibraryApply");var bounds=apply.TransformToAncestor(library).TransformBounds(new W.Rect(0,0,apply.ActualWidth,apply.ActualHeight));
                    Check(bounds.Bottom<=library.ActualHeight&&bounds.Right<=library.ActualWidth,$"{theme} {width}: apply remains in viewport");Click(apply);
                    Check(draft.PresetId=="ao3-fusion"&&draft.PackIds.SequenceEqual(new[]{"ui-en-zh"}),$"{theme} {width}: choosing preset and independent pack updates content draft");
                }
            }
        }
        catch(Exception ex){failures++;error=ex.ToString();Console.WriteLine(error);}
        var result=new{checks,failures,error,assemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainForm).Assembly.Location))),realProviderCalls=0};File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));return failures==0?0:1;
    }
}
