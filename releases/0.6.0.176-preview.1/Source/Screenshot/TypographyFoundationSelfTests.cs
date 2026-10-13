using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class TypographyFoundationSelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var pass=0;var fail=0;var log=new StringBuilder();
        void Test(string name,Action action){try{action();pass++;log.AppendLine($"PASS {name}");}catch(Exception ex){fail++;log.AppendLine($"FAIL {name}: {ex.Message}");}}
        void True(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        Test("product default resolves",()=>True(FontManager.IsInstalled(FontManager.ProductDefaultFamily),"default not installed"));
        foreach(var script in Enum.GetValues<TypographyScript>())
            Test($"fallback {script}",()=>True(FontManager.ResolveFallbackChain(script).Count>0,"empty chain"));
        Test("missing custom preserved and falls back",()=>{var s=new ApiSettings{UiFontMode=UiFontMode.Custom,UiFontFamily="Definitely Missing Typography Font"};ConfigurationManager.NormalizeAll(s);var p=FontManager.ResolveUiProfile(s);True(s.UiFontFamily=="Definitely Missing Typography Font"&&FontManager.IsInstalled(p.PrimaryFamily),"missing font handling");});
        Test("preview independent custom",()=>{var s=new ApiSettings{UiFontMode=UiFontMode.CurrentDefault,PreviewTextFontMode=PreviewTextFontMode.Custom,PreviewTextFontFamily="Segoe UI",PreviewTextFontSize=13};var p=FontManager.ResolvePreviewProfile(s);True(p.Size==13&&FontManager.IsInstalled(p.PrimaryFamily),"preview profile");});
        Test("ui runtime apply",()=>{using var form=new Form();var label=new Label{Text="Title",Font=new Font(SystemFonts.DefaultFont.FontFamily,14,FontStyle.Bold)};form.Controls.Add(label);var s=new ApiSettings{UiFontMode=UiFontMode.Custom,UiFontFamily="Segoe UI",UiFontSize=12};FontManager.ApplyUi(form,s);True(form.Font.Name.Equals("Segoe UI",StringComparison.OrdinalIgnoreCase)&&label.Font.Bold,"runtime apply");});
        Test("preview runtime apply",()=>{using var text=new RichTextBox();var s=new ApiSettings{PreviewTextFontMode=PreviewTextFontMode.Custom,PreviewTextFontFamily="Segoe UI",PreviewTextFontSize=15};FontManager.ApplyPreviewText(text,s);True(text.Font.Name.Equals("Segoe UI",StringComparison.OrdinalIgnoreCase)&&Math.Abs(text.Font.Size-15)<.1,"preview apply");});
        Test("unsupported style safe",()=>{using var font=FontManager.Create("Segoe UI",10,FontStyle.Bold|FontStyle.Italic);True(font.Size>0,"font creation");});
        Test("follow ui",()=>{var s=new ApiSettings{UiFontMode=UiFontMode.Custom,UiFontFamily="Segoe UI",UiFontSize=12,PreviewTextFontMode=PreviewTextFontMode.FollowUiFont};var p=FontManager.ResolvePreviewProfile(s);True(p.Size==12&&p.PrimaryFamily.Equals("Segoe UI",StringComparison.OrdinalIgnoreCase),"follow ui");});
        Test("translation profile does not mutate renderer",()=>{var s=new ApiSettings{OverlayFontMode=OverlayFontMode.Custom,OverlayFontFamily="Segoe UI",OverlayFontSize=14};var r=new RenderSettings();var before=r.FontFamily;var p=FontManager.ResolveTranslationImageProfile(s);True(p.Size==14&&r.FontFamily==before,"renderer mutated");});
        Test("mixed runs preserve text",()=>{var text="Hello 简體 繁體 日本語 한국어 😀 ★";var p=FontManager.ResolveTranslationImageProfile(new ApiSettings());var runs=FontManager.ResolveTranslationFontRuns(text,p);True(string.Concat(runs.Select(x=>x.Text))==text,"text changed");});
        Test("config migration",()=>{var s=new ApiSettings{TypographyConfigVersion=0,UiFontMode=UiFontMode.CurrentDefault};ConfigurationManager.NormalizeAll(s);True(s.TypographyConfigVersion==1&&s.UiFontMode==UiFontMode.CurrentDefault,"migration");});
        Test("snapshot typography version",()=>{var s=new ApiSettings{TypographyConfigVersion=1};True(ApiSettingsSnapshot.Copy(s).TypographyConfigVersion==1,"snapshot");});
        Test("integrity valid multilingual",()=>True(TextIntegrityGuard.Inspect("English 简體 繁體 日本語 한국어 😀 ★").Count==0,"false positive"));
        Test("integrity replacement",()=>True(TextIntegrityGuard.Inspect("bad\uFFFDtext").Any(x=>x.Code=="ReplacementCharacter"),"replacement missed"));
        Test("integrity surrogate",()=>True(TextIntegrityGuard.Inspect("bad\uD800text").Any(x=>x.Code=="InvalidSurrogate"),"surrogate missed"));
        File.WriteAllText(Path.Combine(output,"typography-results.txt"),log+$"TOTAL {pass+fail} PASS {pass} FAIL {fail}\n");
        return fail==0?0:1;
    }
}
