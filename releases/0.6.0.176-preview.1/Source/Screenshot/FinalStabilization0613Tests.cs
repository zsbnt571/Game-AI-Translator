using System.Security.Cryptography;
using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class FinalStabilization0613Tests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var pass=0;var failures=new List<string>();
        void Test(string name,Action action){try{action();pass++;}catch(Exception ex){failures.Add($"{name}: {ex.Message}");}}
        void Equal(string expected,string actual){if(expected!=actual)throw new InvalidOperationException($"expected '{expected}', actual '{actual}'");}

        Test("A",()=>Equal("A",Name(new(InputBindingKind.Keyboard,Keys.A))));
        Test("F1",()=>Equal("F1",Name(new(InputBindingKind.Keyboard,Keys.F1))));
        Test("Escape",()=>Equal("Esc",Name(new(InputBindingKind.Keyboard,Keys.Escape))));
        Test("Ctrl T",()=>Equal("Ctrl + T",Name(new(InputBindingKind.Keyboard,Keys.T,Ctrl:true))));
        Test("modifier canonical order",()=>Equal("Ctrl + Alt + Shift + Win + T",Name(new(InputBindingKind.Keyboard,Keys.T,Ctrl:true,Alt:true,Shift:true,Win:true))));
        Test("Ctrl Shift T",()=>Equal("Ctrl + Shift + T",Name(new(InputBindingKind.Keyboard,Keys.T,Ctrl:true,Shift:true))));
        Test("Alt F2",()=>Equal("Alt + F2",Name(new(InputBindingKind.Keyboard,Keys.F2,Alt:true))));
        Test("Win key",()=>Equal("Win + R",Name(new(InputBindingKind.Keyboard,Keys.R,Win:true))));
        Test("Middle Mouse",()=>Equal("Middle Mouse",Name(new(InputBindingKind.MouseButton,MouseButton:InputMouseButton.Middle))));
        Test("Mouse 4",()=>Equal("Mouse 4",Name(new(InputBindingKind.MouseButton,MouseButton:InputMouseButton.XButton1))));
        Test("Mouse 5",()=>Equal("Mouse 5",Name(new(InputBindingKind.MouseButton,MouseButton:InputMouseButton.XButton2))));
        Test("Wheel Up",()=>Equal("Wheel Up",Name(new(InputBindingKind.MouseWheel,Wheel:InputWheelDirection.Up))));
        Test("Wheel Down",()=>Equal("Wheel Down",Name(new(InputBindingKind.MouseWheel,Wheel:InputWheelDirection.Down))));
        Test("Numpad digit",()=>Equal("Numpad 7",Name(new(InputBindingKind.Keyboard,Keys.NumPad7))));
        Test("Numpad enter",()=>Equal("Numpad Enter",Name(new(InputBindingKind.Keyboard,Keys.Enter,Numpad:true))));
        Test("OEM punctuation",()=>{Equal(";",Name(new(InputBindingKind.Keyboard,Keys.Oem1)));Equal("[",Name(new(InputBindingKind.Keyboard,Keys.Oem4)));Equal("\\",Name(new(InputBindingKind.Keyboard,Keys.Oem5)));});
        Test("left right modifiers",()=>{Equal("Left Ctrl",Name(new(InputBindingKind.Keyboard,Keys.LControlKey)));Equal("Right Shift",Name(new(InputBindingKind.Keyboard,Keys.RShiftKey)));});
        Test("clear",()=>Equal("未绑定",Name(new())));
        Test("restore default",()=>Equal("Ctrl + Alt + Z",Name(InputBindingDefaults.StartCapture)));
        Test("conflict display",()=>Equal("Ctrl + Shift + T 已被 截图翻译 与 切换结果 使用。",InputBindingConflictPresenter.Describe(new(InputBindingKind.Keyboard,Keys.T,Ctrl:true,Shift:true),"截图翻译","切换结果")));

        var settingsDir=Path.Combine(output,"settings");Directory.CreateDirectory(settingsDir);
        Test("fresh defaults",()=>{var s=ConfigurationManager.Load(Path.Combine(settingsDir,"missing.json"),false);ConfigurationManager.NormalizeAll(s);if(s.StartCaptureBinding!=InputBindingDefaults.StartCapture||s.OcrEngine!=OcrEngineKind.Rapid||s.TargetLanguage!="Simplified Chinese"||s.PreviewDefaultImage!=PreviewDefaultImage.Original)throw new InvalidOperationException("unexpected defaults");});
        Test("binding persistence not display persistence",()=>{var path=Path.Combine(settingsDir,"binding.json");var expected=new InputBinding(InputBindingKind.Keyboard,Keys.Enter,Ctrl:true,Win:true,Numpad:true);var s=new ApiSettings{StartCaptureBinding=expected};ConfigurationManager.Save(path,s);var json=File.ReadAllText(path);if(json.Contains("\"DisplayName\"",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("binding display text serialized");var loaded=ConfigurationManager.Load(path,false);if(loaded.StartCaptureBinding!=expected)throw new InvalidOperationException("binding data changed");Equal("Ctrl + Win + Numpad Enter",loaded.StartCaptureBinding!.DisplayName);});
        Test("settings save reload",()=>{var path=Path.Combine(settingsDir,"roundtrip.json");var s=new ApiSettings{TargetLanguage="ja",CustomTranslationPrompt="keep meaning",UiFontFamily="Segoe UI",PreviewAlwaysOnTop=true,StartCaptureBinding=new(InputBindingKind.MouseButton,MouseButton:InputMouseButton.XButton2,Ctrl:true)};ConfigurationManager.Save(path,s);var loaded=ConfigurationManager.Load(path,false);if(loaded.TargetLanguage!="ja"||loaded.CustomTranslationPrompt!="keep meaning"||loaded.UiFontFamily!="Segoe UI"||!loaded.PreviewAlwaysOnTop)throw new InvalidOperationException("settings roundtrip mismatch");Equal("Ctrl + Mouse 5",loaded.StartCaptureBinding!.DisplayName);});
        Test("corrupt settings preserved",()=>{var path=Path.Combine(settingsDir,"corrupt.json");File.WriteAllText(path,"{not-json");var before=Hash(path);if(ConfigurationManager.TryLoad(path,false,out _,out _))throw new InvalidOperationException("corrupt file accepted");if(before!=Hash(path))throw new InvalidOperationException("corrupt file overwritten");});
        Test("empty settings preserved",()=>{var path=Path.Combine(settingsDir,"empty.json");File.WriteAllText(path,string.Empty);var before=Hash(path);if(ConfigurationManager.TryLoad(path,false,out _,out _))throw new InvalidOperationException("empty file accepted");if(before!=Hash(path))throw new InvalidOperationException("empty file overwritten");});
        Test("no API key startup",()=>{var path=Path.Combine(settingsDir,"no-key.json");ConfigurationManager.Save(path,new ApiSettings{ApiKey=""});using var main=new MainForm(path);main.Show();Application.DoEvents();if(!main.Visible)throw new InvalidOperationException("main window did not start");main.ExitForSmoke();});
        Test("version identity",()=>{Equal("0.5.0.6.13.3-candidate-13",BuildIdentity.DisplayVersion);Equal(BuildIdentity.DisplayVersion,BuildIdentity.ProductVersion);if(BuildIdentity.StableApplicationId.Contains(BuildIdentity.DisplayVersion,StringComparison.Ordinal))throw new InvalidOperationException("stable app id contains version");});
        Test("main preview titles",()=>{var path=Path.Combine(settingsDir,"titles.json");ConfigurationManager.Save(path,new ApiSettings());using var main=new MainForm(path);using var image=new Bitmap(64,64);using var preview=new PreviewForm(image,PreviewMode.OcrOnly,new ApiSettings{VisualModel=VisualModelKind.Off},new OcrService(),new TranslationService());if(!main.Text.Contains(BuildIdentity.DisplayVersion)||!preview.Text.Contains(BuildIdentity.DisplayVersion))throw new InvalidOperationException("visible title version mismatch");main.ExitForSmoke();});

        var lines=new List<string>{$"TOTAL {pass+failures.Count} PASS {pass} FAIL {failures.Count}"};lines.AddRange(failures.Select(x=>"FAIL "+x));
        File.WriteAllLines(Path.Combine(output,"RESULT.txt"),lines);
        return failures.Count==0?0:2;
    }

    private static string Name(InputBinding binding)=>InputDisplayNameProvider.GetDisplayName(binding);
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
