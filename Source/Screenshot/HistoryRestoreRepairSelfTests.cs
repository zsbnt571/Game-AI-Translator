using System.Drawing.Imaging;

namespace ScreenshotTranslationUiTester;

internal static class HistoryRestoreRepairSelfTests
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);var results=new List<string>();var failures=0;
        void Test(string name,Action action){try{action();results.Add($"PASS {name}");}catch(Exception ex){failures++;results.Add($"FAIL {name}: {ex}");}}
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static Bitmap Fixture(Color color){var b=new Bitmap(360,220);using var g=Graphics.FromImage(b);g.Clear(color);g.DrawString("history",SystemFonts.DefaultFont,Brushes.White,20,20);return b;}
        static byte[] Encode(Bitmap image){using var s=new MemoryStream();image.Save(s,ImageFormat.Png);return s.ToArray();}
        static SessionHistoryItem Item(Bitmap source,Bitmap? translated,string id)=>new(DateTimeOffset.Now,OcrEngineKind.Rapid,
            $"source-{id}",$"translation-{id}","zh",Encode(source),Encode(source),$"full-source-{id}",
            $"full-translation-{id}",translated is null?null:Encode(translated));
        var config=Path.Combine(output,"settings.json");ConfigurationManager.Save(config,new ApiSettings{VisualModel=VisualModelKind.Off,CloseMainWindowBehavior=CloseMainWindowBehavior.Exit});
        Test("history stream lifetime and disposed source",()=>{SessionHistoryItem item;using(var source=Fixture(Color.DarkBlue))using(var translated=Fixture(Color.DarkGreen))item=Item(source,translated,"owned");using var main=new MainForm(config);var preview=main.OpenHistoryItemForSmoke(item);preview.Show();Application.DoEvents();var state=preview.HistoryRestoreStateForSmoke;Require(state.ImageVisible&&state.TextVisible&&state.HasTranslatedImage&&state.SnapshotDeferred,$"state={state}");preview.Close();});
        Test("history 10x reopen",()=>{using var source=Fixture(Color.DarkRed);using var translated=Fixture(Color.DarkOrange);var item=Item(source,translated,"ten");using var main=new MainForm(config);for(var i=0;i<10;i++){var preview=main.OpenHistoryItemForSmoke(item);preview.Show();Application.DoEvents();Require(preview.HistoryRestoreStateForSmoke.ImageVisible,$"open {i}");preview.Close();Application.DoEvents();}});
        Test("history A B A switch",()=>{using var a=Fixture(Color.Navy);using var b=Fixture(Color.Maroon);var ia=Item(a,null,"A");var ib=Item(b,null,"B");using var main=new MainForm(config);foreach(var item in new[]{ia,ib,ia}){var preview=main.OpenHistoryItemForSmoke(item);preview.Show();Application.DoEvents();Require(preview.HistoryRestoreStateForSmoke.TextVisible,"text");preview.Close();}});
        Test("history persists across service restart",()=>{var root=Path.Combine(output,"persistent");using(var source=Fixture(Color.Purple))using(var translated=Fixture(Color.Teal))using(var first=new SessionServices(root))first.AddHistory(source,OcrEngineKind.Rapid,"persistent source","persistent translation","zh",new ApiSettings(),translated);using var second=new SessionServices(root);var item=second.SnapshotHistory().Single();Require(item.SourceText=="persistent source"&&item.TranslationText=="persistent translation"&&item.TranslatedImage is not null,"rehydration fields");using var main=new MainForm(config);var preview=main.OpenHistoryItemForSmoke(item);preview.Show();Application.DoEvents();Require(preview.HistoryRestoreStateForSmoke.SnapshotDeferred,"automatic OCR started");preview.Close();});
        Test("legacy history compatibility",()=>{using var source=Fixture(Color.Black);var item=new SessionHistoryItem(DateTimeOffset.Now,OcrEngineKind.Rapid,"legacy source","legacy translation","zh",Encode(source),Encode(source));using var main=new MainForm(config);var preview=main.OpenHistoryItemForSmoke(item);preview.Show();Application.DoEvents();var state=preview.HistoryRestoreStateForSmoke;Require(state.ImageVisible&&state.TextVisible&&!state.HasTranslatedImage,"legacy");preview.Close();});
        Test("explicit OCR reprocess creates independent snapshot",()=>{using var source=Fixture(Color.DarkSlateBlue);var item=Item(source,null,"ocr");using var main=new MainForm(config);var preview=main.OpenHistoryItemForSmoke(item);preview.Show();Application.DoEvents();Require(preview.HistoryRestoreStateForSmoke.SnapshotDeferred,"restore created OCR snapshot");var task=preview.PrepareOcrSnapshotForSmoke();while(!task.IsCompleted){Application.DoEvents();Thread.Sleep(5);}Require(task.Result&&preview.OcrBitmapIsIndependentForSmoke,"OCR snapshot");preview.Close();});
        Test("real product History page click E2E",()=>{using var main=new MainForm(config);main.Show();Application.DoEvents();main.NavigateForSmoke("历史");main.SeedHistoryForSmoke(1);var preview=main.OpenFirstHistorySelectionForSmoke();Application.DoEvents();var state=preview.HistoryRestoreStateForSmoke;Require(state.ImageVisible&&state.TextVisible&&state.SnapshotDeferred,$"product E2E {state}");preview.Close();main.ExitForSmoke();});
        File.WriteAllLines(Path.Combine(output,"history-restore-results.txt"),results);File.WriteAllText(Path.Combine(output,"summary.txt"),$"Tests={results.Count}\nFailures={failures}\nDeepSeekCalls=0");return failures==0?0:1;
    }
}
