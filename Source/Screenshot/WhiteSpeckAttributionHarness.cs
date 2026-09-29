using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class WhiteSpeckAttributionHarness
{
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};

    internal static int Run(string outputRoot,string inputPath,string settingsPath)
    {
        Directory.CreateDirectory(outputRoot);
        var settings=ConfigurationManager.Load(settingsPath,false);
        settings.VisualModel=VisualModelKind.Off;
        settings.PreviewWindowSizingMode=PreviewWindowSizingMode.Fixed;
        settings.FixedPreviewWidth=1280;settings.FixedPreviewHeight=800;
        settings.PreviewDefaultText=PreviewDefaultText.Hidden;
        settings.PreviewTextPanelVisible=false;settings.PreviewAlwaysOnTop=false;
        using var source=new Bitmap(inputPath);
        var sourceHash=PixelHash(source);
        using var form=new PreviewForm(source,PreviewMode.OcrOnly,settings,new OcrService(),new TranslationService());
        form.SuppressAutoOcrForE2E();
        form.ShowInTaskbar=false;form.Opacity=0;form.StartPosition=FormStartPosition.Manual;
        form.Location=new Point(-32000,-32000);form.Show();Application.DoEvents();
        var snapshot=form.PrepareOcrSnapshotForSmoke();
        var deadline=DateTime.UtcNow.AddSeconds(15);
        while(!snapshot.IsCompleted&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(5);}
        if(!snapshot.IsCompletedSuccessfully||!snapshot.Result)throw new InvalidOperationException("Preview snapshot preparation failed.");
        Application.DoEvents();

        using var sourceClone=form.CloneSourceImageForTest();
        using var savedCurrent=form.CloneVisibleImageForTest();
        sourceClone.Save(Path.Combine(outputRoot,"SOURCE-CLONE.png"),ImageFormat.Png);
        savedCurrent.Save(Path.Combine(outputRoot,"SAVED-CURRENT.png"),ImageFormat.Png);
        var cloneHash=PixelHash(sourceClone);var savedHash=PixelHash(savedCurrent);
        var sourceCloneDiff=CountDifferentPixels(source,sourceClone);
        var savedDiff=CountDifferentPixels(source,savedCurrent);

        form.SelectZoomPresetForSmoke(1);Application.DoEvents();
        using var oneToOne=form.CapturePictureSurfaceForSmoke();
        oneToOne.Save(Path.Combine(outputRoot,"PREVIEW-100-PERCENT.png"),ImageFormat.Png);
        var oneToOneDiff=source.Size==oneToOne.Size?CountDifferentPixels(source,oneToOne):-1;
        var oneToOneHash=PixelHash(oneToOne);

        form.SetZoomScaleForSmoke(.84f);Application.DoEvents();
        using var eightyFour=form.CapturePictureSurfaceForSmoke();
        eightyFour.Save(Path.Combine(outputRoot,"PREVIEW-84-PERCENT.png"),ImageFormat.Png);
        var eightyFourCandidates=FindUnsupportedBrightSpecks(source,eightyFour);
        var eightyFourHash=PixelHash(eightyFour);

        form.ResetFitForSmoke();Application.DoEvents();
        var fitHashes=new List<string>();var candidateCounts=new List<int>();var candidateSamples=new List<object>();
        Size fitSize=Size.Empty;
        for(var run=1;run<=5;run++)
        {
            form.ResetFitForSmoke();Application.DoEvents();
            using var fit=form.CapturePictureSurfaceForSmoke();fitSize=fit.Size;
            fit.Save(Path.Combine(outputRoot,$"PREVIEW-FIT-{run:D2}.png"),ImageFormat.Png);
            fitHashes.Add(PixelHash(fit));
            var candidates=FindUnsupportedBrightSpecks(source,fit);
            candidateCounts.Add(candidates.Count);
            if(run==1)candidateSamples.AddRange(candidates.Take(100).Select(x=>(object)new{x.X,x.Y,x.Luminance,x.SourceNeighborhoodMax,x.ComponentPixels}));
        }
        using(var window=new Bitmap(form.ClientSize.Width,form.ClientSize.Height,PixelFormat.Format32bppArgb))
        {form.DrawToBitmap(window,form.ClientRectangle);window.Save(Path.Combine(outputRoot,"PREVIEW-WINDOW-FIT.png"),ImageFormat.Png);}
        form.Close();Application.DoEvents();

        var sourceUnchanged=sourceHash==PixelHash(source);
        var cloneExact=sourceHash==cloneHash&&sourceCloneDiff==0;
        var savedExact=sourceHash==savedHash&&savedDiff==0;
        var oneToOneExact=sourceHash==oneToOneHash&&oneToOneDiff==0;
        var fitDeterministic=fitHashes.Distinct(StringComparer.Ordinal).Count()==1;
        var unsupportedSpecks=Math.Max(candidateCounts.Max(),eightyFourCandidates.Count);
        var attribution=!sourceUnchanged||!cloneExact||!savedExact?"BITMAP_MUTATION"
            :unsupportedSpecks>0?"PREVIEW_SCALING":"NO_REPRODUCTION";
        File.WriteAllText(Path.Combine(outputRoot,"WHITE-SPECK-ATTRIBUTION.json"),JsonSerializer.Serialize(new
        {
            Input=Path.GetFullPath(inputPath),SourceSize=source.Size,FitSize=fitSize,
            SourcePixelHash=sourceHash,SourceClonePixelHash=cloneHash,SavedCurrentPixelHash=savedHash,
            Preview100PixelHash=oneToOneHash,SourceUnchanged=sourceUnchanged,SourceCloneExact=cloneExact,
            SavedBitmapExact=savedExact,Preview100Exact=oneToOneExact,Preview100DifferentPixels=oneToOneDiff,
            Preview84PixelHash=eightyFourHash,Preview84Size=eightyFour.Size,
            Preview84UnsupportedBrightSpecks=eightyFourCandidates.Count,FitDeterministic=fitDeterministic,
            FitHashes=fitHashes,UnsupportedBrightSpeckComponents=candidateCounts,
            CandidateDefinition="fit luminance >=245, mapped source neighborhood max <220, connected component <=8 pixels",
            CandidateSamples=candidateSamples,Attribution=attribution,CaptureModuleChanged=false,
            CursorModuleChanged=false,RealApiCalls=0,Candidate="NO"
        },JsonOptions));
        File.WriteAllText(Path.Combine(outputRoot,"WHITE-SPECK.csv"),
            "Input,SourceUnchanged,SourceCloneExact,SavedBitmapExact,Preview100Exact,Preview84UnsupportedSpecks,FitDeterministic,UnsupportedPreviewSpecks,Attribution,CaptureChanged,CursorChanged\r\n"+
            Csv(Path.GetFullPath(inputPath),sourceUnchanged,cloneExact,savedExact,oneToOneExact,eightyFourCandidates.Count,fitDeterministic,unsupportedSpecks,attribution,false,false)+"\r\n",new UTF8Encoding(true));
        return attribution=="BITMAP_MUTATION"?41:0;
    }

    private static List<Speck> FindUnsupportedBrightSpecks(Bitmap source,Bitmap display)
    {
        using var sourcePixels=ReadOnlyBitmapPixelBuffer.Create(source);
        using var displayPixels=ReadOnlyBitmapPixelBuffer.Create(display);
        var candidate=new bool[display.Width*display.Height];
        var scaleX=source.Width/(double)display.Width;var scaleY=source.Height/(double)display.Height;
        for(var y=0;y<display.Height;y++)for(var x=0;x<display.Width;x++)
        {
            var shown=Luminance(displayPixels.GetPixel(x,y));if(shown<245)continue;
            var sx=(x+.5)*scaleX-.5;var sy=(y+.5)*scaleY-.5;
            var radiusX=Math.Max(2,(int)Math.Ceiling(scaleX));var radiusY=Math.Max(2,(int)Math.Ceiling(scaleY));var max=0d;
            for(var yy=Math.Max(0,(int)Math.Floor(sy)-radiusY);yy<=Math.Min(source.Height-1,(int)Math.Ceiling(sy)+radiusY);yy++)
            for(var xx=Math.Max(0,(int)Math.Floor(sx)-radiusX);xx<=Math.Min(source.Width-1,(int)Math.Ceiling(sx)+radiusX);xx++)
                max=Math.Max(max,Luminance(sourcePixels.GetPixel(xx,yy)));
            if(max<220)candidate[y*display.Width+x]=true;
        }
        var seen=new bool[candidate.Length];var result=new List<Speck>();
        for(var y=0;y<display.Height;y++)for(var x=0;x<display.Width;x++)
        {
            var start=y*display.Width+x;if(!candidate[start]||seen[start])continue;
            var queue=new Queue<Point>();var points=new List<Point>();queue.Enqueue(new(x,y));seen[start]=true;
            while(queue.Count>0)
            {
                var p=queue.Dequeue();points.Add(p);
                foreach(var n in new[]{new Point(p.X-1,p.Y),new Point(p.X+1,p.Y),new Point(p.X,p.Y-1),new Point(p.X,p.Y+1)})
                {if(n.X<0||n.Y<0||n.X>=display.Width||n.Y>=display.Height)continue;var index=n.Y*display.Width+n.X;if(candidate[index]&&!seen[index]){seen[index]=true;queue.Enqueue(n);}}
            }
            if(points.Count>8)continue;
            var first=points[0];var shown=Luminance(displayPixels.GetPixel(first.X,first.Y));
            var sx=(first.X+.5)*scaleX-.5;var sy=(first.Y+.5)*scaleY-.5;var max=0d;
            for(var yy=Math.Max(0,(int)Math.Floor(sy)-2);yy<=Math.Min(source.Height-1,(int)Math.Ceiling(sy)+2);yy++)
            for(var xx=Math.Max(0,(int)Math.Floor(sx)-2);xx<=Math.Min(source.Width-1,(int)Math.Ceiling(sx)+2);xx++)max=Math.Max(max,Luminance(sourcePixels.GetPixel(xx,yy)));
            result.Add(new(first.X,first.Y,shown,max,points.Count));
        }
        return result;
    }

    private static double Luminance(Color color)=>.2126*color.R+.7152*color.G+.0722*color.B;
    private static int CountDifferentPixels(Bitmap left,Bitmap right)
    {
        if(left.Size!=right.Size)return -1;using var a=ReadOnlyBitmapPixelBuffer.Create(left);using var b=ReadOnlyBitmapPixelBuffer.Create(right);var count=0;
        for(var y=0;y<a.Height;y++)for(var x=0;x<a.Width;x++)if(a.GetPixel(x,y).ToArgb()!=b.GetPixel(x,y).ToArgb())count++;
        return count;
    }
    private static string PixelHash(Bitmap bitmap)
    {
        using var pixels=ReadOnlyBitmapPixelBuffer.Create(bitmap);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var row=new byte[pixels.Width*4];
        for(var y=0;y<pixels.Height;y++){for(var x=0;x<pixels.Width;x++){var argb=pixels.GetPixel(x,y).ToArgb();BitConverter.TryWriteBytes(row.AsSpan(x*4,4),argb);}hash.AppendData(row);}
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string Csv(params object[] values)=>string.Join(',',values.Select(value=>'"'+Convert.ToString(value)!.Replace("\"","\"\"")+'"'));
    private sealed record Speck(int X,int Y,double Luminance,double SourceNeighborhoodMax,int ComponentPixels);
}
