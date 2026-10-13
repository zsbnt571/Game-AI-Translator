using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class PreviewViewportSelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<string>();var failed=0;
        void Test(string name,Action body){try{body();rows.Add("PASS | "+name);}catch(Exception ex){failed++;rows.Add("FAIL | "+name+" | "+ex.Message);}}
        void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        PreviewForm Open(Size imageSize)
        {
            using var image=new Bitmap(imageSize.Width,imageSize.Height);
            var form=new PreviewForm(image,PreviewMode.OcrOnly,new ApiSettings{VisualModel=VisualModelKind.Off},new OcrService(),new TranslationService());
            form.SuppressAutoOcrForE2E();form.RestoreHistoryState("source","translation",null);form.Show();Application.DoEvents();return form;
        }

        Test("100 percent selector enters actual-size state",()=>{using var form=Open(new(1600,1200));form.SelectZoomPresetForSmoke(1);Application.DoEvents();Require(!form.FitViewForSmoke&&Math.Abs(form.ViewZoomForSmoke-1f)<.0001f,$"fit={form.FitViewForSmoke} zoom={form.ViewZoomForSmoke}");Require(form.VisibleImageBoundsForSmoke.Size==new Size(1600,1200),$"picture={form.VisibleImageBoundsForSmoke.Size}");Require(form.ImageSizeModeForSmoke==PictureBoxSizeMode.Normal,$"mode={form.ImageSizeModeForSmoke}");Require(form.ZoomSelectionForSmoke=="100%",$"selector={form.ZoomSelectionForSmoke}");});
        Test("fit selector returns whole image to viewport",()=>{using var form=Open(new(1600,1200));form.SelectZoomPresetForSmoke(1);form.SelectZoomPresetForSmoke(0);Application.DoEvents();var picture=form.VisibleImageBoundsForSmoke.Size;var viewport=form.ImageViewportForSmoke;Require(form.FitViewForSmoke,$"fit={form.FitViewForSmoke}");Require(picture.Width<=viewport.Width&&picture.Height<=viewport.Height,$"picture={picture} viewport={viewport}");Require(form.PanOffsetForSmoke==Point.Empty,$"scroll={form.PanOffsetForSmoke}");Require(form.ZoomSelectionForSmoke=="适应窗口",$"selector={form.ZoomSelectionForSmoke}");});
        Test("manual zoom scales the complete bitmap and scroll extent",()=>{using var form=Open(new(1600,1200));form.SelectZoomPresetForSmoke(1);var anchor=new Point(form.ImageViewportForSmoke.Width/2,form.ImageViewportForSmoke.Height/2);form.ZoomForSmoke(1.25f,anchor);Application.DoEvents();Require(form.VisibleImageBoundsForSmoke.Size==new Size(2000,1500),$"picture={form.VisibleImageBoundsForSmoke.Size}");Require(form.ScrollExtentForSmoke==new Size(2000,1500),$"extent={form.ScrollExtentForSmoke}");Require(Math.Abs(form.DisplayScaleForSmoke-1.25f)<.0001f,$"scale={form.DisplayScaleForSmoke}");Require(form.ZoomSelectionForSmoke=="125%",$"selector={form.ZoomSelectionForSmoke}");});
        Test("zoom keeps the image point under the pointer",()=>{using var form=Open(new(1600,1200));form.SelectZoomPresetForSmoke(1);var anchor=new Point(Math.Max(20,form.ImageViewportForSmoke.Width/3),Math.Max(20,form.ImageViewportForSmoke.Height/3));var before=form.ImagePointAtViewportForSmoke(anchor);var beforeBounds=form.VisibleImageBoundsForSmoke;var beforeScroll=form.PanOffsetForSmoke;form.ZoomForSmoke(1.25f,anchor);Application.DoEvents();var after=form.ImagePointAtViewportForSmoke(anchor);Require(Math.Abs(before.X-after.X)<=1.1f&&Math.Abs(before.Y-after.Y)<=1.1f,$"anchor={anchor} viewport={form.ImageViewportForSmoke} before={before} bounds={beforeBounds} scroll={beforeScroll} after={after} bounds={form.VisibleImageBoundsForSmoke} scroll={form.PanOffsetForSmoke}");});
        Test("scrolling does not collapse zoom extent",()=>{using var form=Open(new(1600,1200));form.SelectZoomPresetForSmoke(1);var anchor=new Point(form.ImageViewportForSmoke.Width/2,form.ImageViewportForSmoke.Height/2);form.ZoomForSmoke(1.25f,anchor);var before=form.ScrollExtentForSmoke;form.ZoomForSmoke(1.1f,anchor);Application.DoEvents();var expected=new Size((int)Math.Round(1600*form.ViewZoomForSmoke),(int)Math.Round(1200*form.ViewZoomForSmoke));Require(form.ScrollExtentForSmoke==expected,$"before={before} after={form.ScrollExtentForSmoke} expected={expected}");});

        File.WriteAllLines(Path.Combine(output,"PREVIEW-VIEWPORT-SELFTESTS.txt"),rows,Encoding.UTF8);
        File.WriteAllText(Path.Combine(output,"SUMMARY.txt"),$"PASS={rows.Count-failed}{Environment.NewLine}FAIL={failed}{Environment.NewLine}RealApiCalls=0{Environment.NewLine}RendererChanges=0{Environment.NewLine}CaptureChanges=0",Encoding.UTF8);
        return failed==0?0:1;
    }
}
