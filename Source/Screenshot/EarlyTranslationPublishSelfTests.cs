namespace ScreenshotTranslationUiTester;

internal static class EarlyTranslationPublishSelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<string>();var failed=0;
        void Test(string name,Action action){try{action();rows.Add($"PASS {name}");}catch(Exception ex){failed++;rows.Add($"FAIL {name}: {ex.Message}");}}
        static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static OcrDocument Document(string text)=>new(){Groups=[new SegmentGroup{GroupId="G1",GroupType=SegmentType.Body,OriginalText="source",OrganizedText="source",Translation=text,Bounds=new(10,10,160,40),ReadingOrder=1}],FullTranslation=text,TranslationStale=false};
        PreviewForm Form(Bitmap image,bool cache=false)=>new(image,PreviewMode.OcrOnly,new ApiSettings{VisualModel=VisualModelKind.Off,TranslationCacheEnabled=cache},new OcrService(),new TranslationService());

        Test("translation text publishes without renderer bitmap",()=>{using var image=new Bitmap(200,100);using var form=Form(image);form.SetDocumentForSmoke(Document("立即显示"));form.PublishTranslationTextForSmoke();Assert(form.DisplayedTextForSmoke.Contains("立即显示"),"right text not published");Assert(!form.TranslatedAvailableForSmoke,"renderer bitmap was unexpectedly required");});
        Test("cache hit shaped result publishes before image",()=>{using var image=new Bitmap(200,100);using var form=Form(image,true);form.SetDocumentForSmoke(Document("缓存译文"));form.PublishTranslationTextForSmoke();Assert(form.DisplayedTextForSmoke=="缓存译文","cache text delayed");});
        Test("renderer failure cannot erase published text",()=>{using var image=new Bitmap(200,100);using var form=Form(image);form.SetDocumentForSmoke(Document("保留译文"));form.PublishTranslationTextForSmoke();form.SetTranslatedDisplayForSmoke(null,"RendererFailed");Assert(form.DisplayedTextForSmoke=="保留译文","renderer failure cleared text");});
        Test("newer translation supersedes older",()=>{using var image=new Bitmap(200,100);using var form=Form(image);form.SetDocumentForSmoke(Document("旧译文"));form.PublishTranslationTextForSmoke();form.SetDocumentForSmoke(Document("新译文"));form.PublishTranslationTextForSmoke();Assert(form.DisplayedTextForSmoke=="新译文","new generation did not supersede old text");});
        Test("final bitmap commit preserves early text",()=>{using var image=new Bitmap(200,100);using var translated=new Bitmap(200,100);using var form=Form(image);var document=Document("先文字后译图");form.SetDocumentForSmoke(document);form.PublishTranslationTextForSmoke();form.CommitTranslatedDisplayAtomicallyForSmoke(translated,document);Assert(form.DisplayedTextForSmoke=="先文字后译图"&&form.TranslatedAvailableForSmoke,"final commit changed early text");});

        File.WriteAllLines(Path.Combine(output,"early-translation-publish-selftests.txt"),rows.Append($"RESULT {(failed==0?"PASS":"FAIL")} failed={failed}"));
        return failed==0?0:1;
    }
}
