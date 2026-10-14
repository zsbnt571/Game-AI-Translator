using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenshotTranslationUiTester;

internal static class Program
{
    static readonly BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static readonly Assembly Product=typeof(ApiSettings).Assembly;
    static readonly Type Language=Product.GetType("ScreenshotTranslationUiTester.UiLanguage")!;
    static int passed;
    static readonly List<string> checks=[];
    static void Assert(bool value,string name)
    {
        if(!value)throw new InvalidOperationException(name);
        passed++;checks.Add(name);
    }
    static object? Invoke(Type type,object? instance,string method,params object?[] args)=>type.GetMethod(method,All)!.Invoke(instance,args);
    static object? Call(object instance,string method,params object?[] args)=>Invoke(instance.GetType(),instance,method,args);
    static T Field<T>(object instance,string name)=>(T)instance.GetType().GetField(name,All)!.GetValue(instance)!;
    static void Set(ApplicationLanguage language)=>Invoke(Language,null,"Set",language);
    static string Text(string source)=>(string)Invoke(Language,null,"Text",source)!;
    static Dictionary<string,string> Resource(string name)
    {
        using var stream=Product.GetManifestResourceStream("Fusion."+name+".json")!;
        return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;
    }
    static string Tokens(string text)=>string.Join("|",Regex.Matches(text,@"\{\d+(?:[^{}]*)?\}").Select(x=>x.Value).Order());

    [STAThread]
    static int Main(string[] args)
    {
        var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Invoke(Product.GetType("ScreenshotTranslationUiTester.AppDataPaths")!,null,"Initialize",
                (object)new[]{"--data-root="+Path.Combine(root,"data"),"--disable-global-input"});
            Assert((int)ApplicationLanguage.SimplifiedChinese==0&&(int)ApplicationLanguage.English==1&&(int)ApplicationLanguage.Russian==2,"Existing saved language IDs remain compatible");
            Assert(new ApiSettings().InterfaceLanguage==ApplicationLanguage.SimplifiedChinese,"Fresh settings still default to Simplified Chinese");
            var english=Resource("UiEnglish");var russian=Resource("UiRussian");
            Assert(english.Keys.Order().SequenceEqual(russian.Keys.Order()),"Russian covers every existing English resource key");
            foreach(var pair in russian)
            {
                Assert(!string.IsNullOrWhiteSpace(pair.Value),"Nonempty resource: "+pair.Key);
                Assert(Tokens(pair.Key)==Tokens(pair.Value),"Preserved placeholders: "+pair.Key);
            }
            foreach(var alias in new[]{"ru","RU","ru-RU","Russian","русский","Русский","俄语"})
            {
                Assert(UiTargetLanguageDisplay.DisplayFromCode(alias)=="俄语","Russian target display: "+alias);
                Assert(TargetLanguageDisplay.CodeFromDisplay(alias)=="ru","Russian target code: "+alias);
            }
            Assert(UiTargetLanguageDisplay.CodeFromDisplay("俄语")=="ru","Localized target value saves as ru");
            Assert(UiTargetLanguageDisplay.DisplayNames.Count==9,"Existing target options are preserved");
            Assert(UiTargetLanguageDisplay.CodeFromDisplay("日语")=="ja"&&TargetLanguageDisplay.DisplayFromCode("en")=="English","Existing target mappings remain compatible");
            Assert(TranslationPromptBuilder.NormalizeTarget("ru")=="Russian (ru)","Translation prompt explicitly names Russian");
            var russianSettings=new ApiSettings{TargetLanguage="ru"};
            Assert(TranslationPromptBuilder.BuildSystemPrompt(russianSettings).Contains("Russian (ru)"),"Actual system prompt keeps the selected Russian target");

            Set(ApplicationLanguage.SimplifiedChinese);
            using(var form=new Form())
            using(var menu=new ContextMenuStrip())
            {
                var label=new Label{Text="设置"};
                var preserved=new Label{Text="Настройки"};
                var input=new TextBox{Text="Моя игра / private-profile",AccessibleName="目标语言",PlaceholderText="搜索名称、标签或便签"};
                var choices=new ComboBox();choices.Items.AddRange(new object[]{"中文","俄语","My profile"});choices.SelectedIndex=1;
                var column=new DataGridViewTextBoxColumn{HeaderText="目标语言"};
                var grid=new DataGridView();grid.Columns.Add(column);
                form.Controls.AddRange([label,preserved,input,choices,grid]);
                Invoke(Language,null,"Preserve",preserved);Invoke(Language,null,"BindTree",form);
                var item=menu.Items.Add("设置");Invoke(Language,null,"BindMenu",menu);
                foreach(var pair in new[]{(ApplicationLanguage.Russian,"Настройки"),(ApplicationLanguage.English,"Settings"),(ApplicationLanguage.SimplifiedChinese,"设置"),(ApplicationLanguage.Russian,"Настройки")})
                {
                    Set(pair.Item1);
                    Assert(label.Text==pair.Item2&&item.Text==pair.Item2,"Bound controls and menu switch to "+pair.Item1);
                    Assert(input.Text=="Моя игра / private-profile"&&preserved.Text=="Настройки","User text preserved in "+pair.Item1);
                    Assert(choices.SelectedIndex==1&&(string)choices.SelectedItem! == "俄语","Combo values and selection preserved in "+pair.Item1);
                }
                var translatedFirst=new Label{Text=Text("设置")};form.Controls.Add(translatedFirst);
                Set(ApplicationLanguage.English);
                Assert(translatedFirst.Text=="Settings","Controls created in Russian can switch back to English");
                Set(ApplicationLanguage.Russian);
                Assert(Text("配置文件：E:\\пример\\настройки.json")=="Файл настроек: E:\\пример\\настройки.json","Localized diagnostic prefix preserves path");
            }

            Set(ApplicationLanguage.SimplifiedChinese);
            var data=Path.Combine(root,"data");var settingsPath=Path.Combine(data,"settings.json");
            using(var form=new MainForm(settingsPath))
            {
                Call(form,"SelectSettingsTabForSmoke",3);
                Call(form,"SelectSettingsTabForSmoke",1);
                var language=Field<ComboBox>(form,"_interfaceLanguageBox");
                var target=Field<ComboBox>(form,"_targetLanguageBox");
                Assert(language.Items.Count==3&&language.Items[2]?.ToString()=="Русский","Real interface selector offers native Russian label");
                Assert(target.Items.Contains("俄语"),"Real translation selector offers Russian target");
                var targetBefore=target.SelectedItem?.ToString();
                language.SelectedIndex=2;Call(form,"SaveSettings",true);
                Assert(ConfigurationManager.Load(settingsPath,false).InterfaceLanguage==ApplicationLanguage.Russian,"Russian interface choice persists through real settings save");
                Assert(target.SelectedItem?.ToString()==targetBefore,"Changing interface language leaves target selection untouched");
                target.SelectedItem="俄语";
                Assert((bool)Call(form,"SaveTranslationContentEditor")!,"Real translation preferences save succeeds");
                Assert(language.SelectedIndex==2,"Changing target leaves interface language untouched");
            }
            using(var form=new MainForm(settingsPath))
            {
                Call(form,"SelectSettingsTabForSmoke",3);
                Call(form,"SelectSettingsTabForSmoke",1);
                var language=Field<ComboBox>(form,"_interfaceLanguageBox");
                var target=Field<ComboBox>(form,"_targetLanguageBox");
                Assert(language.SelectedIndex==2&&target.SelectedItem?.ToString()=="俄语","Both independent language selections reload correctly");
                language.SelectedIndex=1;Call(form,"SaveSettings",true);
                Assert(target.SelectedItem?.ToString()=="俄语","Switching back to English interface preserves Russian target");
                using var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(data,"fusion-settings.json")));
                Assert(state.RootElement.GetProperty("TranslationContent").GetProperty("TargetLanguage").GetString()=="ru","Shared translation preferences persist ru rather than falling back to Chinese");
            }
            var summary=new{Status="PASS",Passed=passed,ResourceStrings=russian.Count,PersonalDataUsed=false,GameLaunches=0,GameTranslationApiCalls=0,Checks=checks};
            File.WriteAllText(Path.Combine(root,"checks.json"),JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS: {passed} checks; {russian.Count} Russian resources. No personal data or game API calls.");
            return 0;
        }
        catch(Exception error)
        {
            var failure=error is TargetInvocationException {InnerException:{} inner}?inner:error;
            File.WriteAllText(Path.Combine(root,"failure.txt"),failure.ToString());
            Console.Error.WriteLine(failure);return 1;
        }
    }
}
