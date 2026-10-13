using System.Drawing;
using System.Text.Json;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

internal static class ScopedVisualSelfTests
{
    internal static int Run(string output,string scope)
    {
        Directory.CreateDirectory(output);var rows=new List<object>();var failed=0;
        void Test(string name,Func<bool> check)
        {try{var pass=check();if(!pass)failed++;rows.Add(new{Name=name,Pass=pass});}
            catch(Exception ex){failed++;rows.Add(new{Name=name,Pass=false,Error=ex.GetType().Name});}}
        static VisualBlock Block(string[] texts,bool horizontal=false)
        {
            var lines=texts.Select((text,i)=>new NormalizedOcrLine("L"+i,text,[],
                new RectangleF(horizontal?i*110:0,horizontal?0:i*24,100,20),.9f,i,"SYNTHETIC")).ToArray();
            return new(){BlockId="TEST",Lines=lines,Bounds=lines.Select(x=>x.Bounds).Aggregate(RectangleF.Union),LayoutBehavior=BlockLayoutBehavior.Fixed};
        }
        if(scope=="FIELDS")
        {
            Test("Vertical semantic fields retain boundaries",()=>CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(Block(["BWH: 70/56/74","Likes: Seafood, books, games","Dislikes: Shuri"])));
            Test("Unicode semantic labels retain boundaries",()=>CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(Block(["身高：152 cm","体重：42 kg"])));
            Test("Genuine horizontal labeled controls remain compact",()=>!CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(Block(["Age: 19","Height: 152 cm"],true)));
            Test("Horizontal Back History controls remain compact",()=>!CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(Block(["Back","History"],true)));
            Test("Vertical prose retains existing decision",()=>!CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(Block(["What are you looking for?","Choose an item from the menu."])));
            Test("A wrapped field continuation is not invented as a new field",()=>!CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(Block(["Likes: reading books,","playing games and fish"])));
            Test("Windows paths are not semantic labels",()=>!CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(Block([@"C:\new\notes",@"D:\save\data"])));
            Test("URLs are not semantic labels",()=>!CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(Block(["https://example.test/a","https://example.test/b"])));
            Test("Single labeled line is unchanged",()=>!CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(Block(["Weight: 42 kg"])));
            Test("An empty value does not collapse a field column",()=>CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(Block(["Mass: 12 kg","Material: ","Length: 20 cm"])));
            Test("Bare headings do not establish populated fields",()=>!CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(Block(["Notes:","Warnings:"])));
        }
        else if(scope=="STYLE")
        {
            static SourceStyleBundle S(Color fill,float confidence,FontStyle weight=FontStyle.Regular,string owner="SourceStyleBundleOwner")=>
                new(fill,Color.Transparent,0,Color.Transparent,PointF.Empty,0,255,weight,
                    SourceStyleBundleOwner.ClassifyPolarity(fill),RegionRoleType.BodyParagraph,confidence,owner,"SYNTHETIC");
            Test("Accepted bright source body remains light",()=>
            {var d=SourceStyleLegibilityGuard.Evaluate(S(Color.FromArgb(254,245,243),.56f,FontStyle.Bold),Color.FromArgb(227,186,185));return !d.FillPolarityChanged&&d.Effective.FillColor.GetBrightness()>=.85f&&d.Effective.Weight==FontStyle.Bold;});
            Test("Bright source retains ordinary outline width cap",()=>
            {var d=SourceStyleLegibilityGuard.FinalizeForFontSize(SourceStyleLegibilityGuard.Evaluate(S(Color.White,.57f),Color.FromArgb(230,230,230)),Color.FromArgb(230,230,230),18);return d.Effective.OutlineWidth<=.811f;});
            Test("Readable dark source remains byte-identical",()=>
            {var source=S(Color.FromArgb(18,18,18),.57f);var d=SourceStyleLegibilityGuard.Evaluate(source,Color.White);return d.Effective.FillColor==source.FillColor&&!d.FillPolarityChanged;});
            Test("Ambiguous grey body keeps its existing readability fallback",()=>
            {var d=SourceStyleLegibilityGuard.Evaluate(S(Color.FromArgb(70,73,73),.57f),Color.FromArgb(61,71,74));return TranslationTextColorResolver.Contrast(d.Effective.FillColor,Color.FromArgb(61,71,74))>=4.5;});
            Test("Weak bright evidence remains eligible for prior fallback",()=>
            {var d=SourceStyleLegibilityGuard.Evaluate(S(Color.White,.30f),Color.White);return d.Effective.FillColor.GetBrightness()<.85f;});
            Test("Chromatic source hue is preserved",()=>
            {var source=S(Color.FromArgb(232,112,84),.9f);var d=SourceStyleLegibilityGuard.Evaluate(source,Color.FromArgb(202,95,72));return !d.FillPolarityChanged&&Math.Abs(d.Effective.FillColor.R-source.FillColor.R)<=30;});
            Test("Explicit user fill remains authoritative",()=>
            {var source=S(Color.Gold,1,owner:"UserTextColorOverride");return SourceStyleLegibilityGuard.Evaluate(source,Color.White).Effective.FillColor.ToArgb()==Color.Gold.ToArgb();});
        }
        else throw new ArgumentException("Expected FIELDS or STYLE");
        File.WriteAllText(Path.Combine(output,"SCOPED-VISUAL-SELFTESTS.json"),JsonSerializer.Serialize(new
        {Scope=scope,Pass=rows.Count-failed,Fail=failed,Rows=rows,RealApiCalls=0,UserVisualAcceptance="NOT_PASSED"},new JsonSerializerOptions{WriteIndented=true}));
        return failed==0?0:1;
    }
}
