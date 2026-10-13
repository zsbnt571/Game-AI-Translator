using System.Diagnostics;
using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class SnapshotOwnershipFixSelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var rows=new List<string>();var pass=0;var fail=0;
        void Test(string name,Action action)
        {
            try{action();pass++;rows.Add("PASS | "+name);}
            catch(Exception ex){fail++;rows.Add("FAIL | "+name+" | "+ex.Message);}
        }
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}

        Test("Deferred clipboard bitmap is an independent GDI object",()=>
        {
            using var captured=Fixture(1280,720);
            using var clipboard=MainForm.CloneCaptureForDeferredClipboard(captured);
            Require(!ReferenceEquals(captured,clipboard),"shared managed bitmap");
            clipboard.SetPixel(0,0,Color.Magenta);
            Require(captured.GetPixel(0,0).ToArgb()!=Color.Magenta.ToArgb(),"shared pixel storage");
        });

        Test("Clipboard Graphics cannot block OCR snapshot clone",()=>
        {
            using var captured=Fixture(1920,1080);
            using var clipboard=MainForm.CloneCaptureForDeferredClipboard(captured);
            using var graphics=Graphics.FromImage(clipboard);
            graphics.DrawLine(Pens.White,0,0,clipboard.Width-1,clipboard.Height-1);
            for(var i=0;i<200;i++)
            {
                using var snapshot=new Bitmap(captured);
                Require(snapshot.Size==captured.Size,$"snapshot size {i}");
            }
        });

        Test("Parallel clipboard work and OCR clones stay isolated",()=>
        {
            using var captured=Fixture(1600,900);
            using var clipboard=MainForm.CloneCaptureForDeferredClipboard(captured);
            var gate=new ManualResetEventSlim(false);
            var clipboardTask=Task.Run(()=>
            {
                gate.Wait();
                for(var i=0;i<100;i++)
                    using(var g=Graphics.FromImage(clipboard))g.FillRectangle(Brushes.Navy,i%100,i%80,20,12);
            });
            var snapshotTask=Task.Run(()=>
            {
                gate.Wait();
                for(var i=0;i<100;i++){using var snapshot=new Bitmap(captured);}
            });
            gate.Set();Task.WaitAll(clipboardTask,snapshotTask);
        });

        Test("Captured source remains usable after clipboard copy disposal",()=>
        {
            using var captured=Fixture(800,450);
            using(var clipboard=MainForm.CloneCaptureForDeferredClipboard(captured)){}
            for(var i=0;i<50;i++){using var snapshot=new Bitmap(captured);}
        });

        Test("Reusable overlay survives repeated prepare and double release",()=>
        {
            using var overlay=new CaptureOverlay(reusableLifecycle:true);
            using var source=Fixture(640,360);
            for(var i=0;i<100;i++)
            {
                overlay.PrepareReusableCaptureForTest(source,Stopwatch.GetTimestamp());
                overlay.ReleaseReusableCapture();
                overlay.ReleaseReusableCapture();
            }
        });

        Test("Rejected reusable capture leaves release idempotent",()=>
        {
            using var overlay=new CaptureOverlay(reusableLifecycle:true);
            var disposed=Fixture(32,32);disposed.Dispose();
            try{overlay.PrepareReusableCaptureForTest(disposed,Stopwatch.GetTimestamp());}
            catch(ArgumentException){}
            overlay.ReleaseReusableCapture();
            overlay.ReleaseReusableCapture();
        });

        File.WriteAllLines(Path.Combine(output,"SNAPSHOT-OWNERSHIP-SELFTESTS.txt"),rows,Encoding.UTF8);
        File.WriteAllText(Path.Combine(output,"SUMMARY.txt"),$"PASS={pass}\nFAIL={fail}\nRealApiCalls=0\nStatus=Manual 20-run Pending",Encoding.UTF8);
        return fail==0?0:1;
    }

    private static Bitmap Fixture(int width,int height)
    {
        var bitmap=new Bitmap(width,height);
        using var graphics=Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(31,47,73));
        graphics.DrawString("ownership fixture",SystemFonts.DefaultFont,Brushes.White,20,20);
        return bitmap;
    }
}
