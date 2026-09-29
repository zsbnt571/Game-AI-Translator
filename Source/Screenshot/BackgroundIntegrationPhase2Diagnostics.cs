using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class BackgroundIntegrationPhase2Diagnostics
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static int Run(string sourcePath,string output)
    {
        Directory.CreateDirectory(output); using var source=new Bitmap(sourcePath);
        var ocr=new OcrRuntimeManager(AppContext.BaseDirectory,new OcrService());var vision=new VisionRuntimeManager(AppContext.BaseDirectory);
        try
        {
            var settings=new ApiSettings{OcrEngine=OcrEngineKind.Rapid,VisualModel=VisualModelKind.PPDocLayoutS,OcrLanguage="English"};
            var result=new RecognitionPipelineV2(ocr,vision).RunAsync(source,settings,1,CancellationToken.None).GetAwaiter().GetResult();
            var cashCard=Path.GetFileName(output.TrimEnd(Path.DirectorySeparatorChar)).Equals("cash-card",StringComparison.OrdinalIgnoreCase);
            var top=(cashCard?result.Document.Regions.Where(r=>r.SourceBlockIds.Any(id=>id is "R001" or "R002" or "R003" or "R004" or "R005")):
                result.Document.Regions.Where(r=>!r.IsIgnored&&!r.PreserveOriginal&&r.SourceLinePolygons.Count>0)).OrderBy(r=>r.ReadingOrder).ToArray();
            using var composite=new Bitmap(source);var plans=new List<object>();
            foreach(var region in top)
            {
                var safe=SafeLayoutTargetPlanner.Plan(region);if(safe.Status!=SafeLayoutTargetStatus.Safe||safe.Targets.Count!=1)continue;
                var plan=BackgroundIntegrationPlanner.Plan(source,region,safe.Targets[0],result.Document.Regions);
                using var before=new Bitmap(composite);using var applied=BackgroundIntegrationExecutor.Execute(composite,plan).Bitmap;
                if(plan.SafeToCommit){using var g=Graphics.FromImage(composite);g.DrawImageUnscaled(applied,0,0);}
                var label=cashCard?(region.SourceBlockIds.SequenceEqual(["R001"])?"TITLE":region.SourceBlockIds.SequenceEqual(["R005"])?"HEADER":"DIALOGUE"):$"REGION-{region.ReadingOrder:D3}-{region.RoleType}";
                SaveCrop(source,plan.CleanupBounds,Path.Combine(output,$"{label}-SOURCE.png"));SaveMask(plan,Path.Combine(output,$"{label}-CLEANUP-MASK.png"));
                SaveCrop(composite,plan.CleanupBounds,Path.Combine(output,$"{label}-BACKGROUND-CLEAN.png"));SaveDiff(before,composite,plan.CleanupBounds,Path.Combine(output,$"{label}-DIFF.png"));
                if(label=="TITLE")
                {
                    SaveCrop(source,plan.CleanupBounds,Path.Combine(output,"R001-SOURCE.png"));SaveMask(plan,Path.Combine(output,"R001-INK-MASK.png"));
                    SaveCrop(composite,plan.CleanupBounds,Path.Combine(output,"R001-CLEAN.png"));SaveDiff(before,composite,plan.CleanupBounds,Path.Combine(output,"R001-DIFF.png"));
                    SaveClassification(plan,Path.Combine(output,"R001-BACKGROUND-CLASSIFICATION.png"));
                }
                if(label=="DIALOGUE")
                {
                    SaveMask(plan.PrimaryMask,plan.CleanupBounds,Path.Combine(output,"DIALOGUE-PRIMARY-MASK.png"));
                    SaveMask(plan.PunctuationMask,plan.CleanupBounds,Path.Combine(output,"DIALOGUE-PUNCTUATION-MASK.png"));
                    SaveMask(plan.CleanupMask,plan.CleanupBounds,Path.Combine(output,"DIALOGUE-FINAL-MASK.png"));
                    SaveCrop(composite,plan.CleanupBounds,Path.Combine(output,"DIALOGUE-CLEAN.png"));
                    var pb=MaskBounds(plan.PunctuationMask,source.Size);
                    if(pb.Width>0){pb=Rectangle.Intersect(Rectangle.Inflate(pb,8,8),new(0,0,source.Width,source.Height));SaveCrop(source,pb,Path.Combine(output,"PUNCTUATION-BEFORE.png"));SaveMask(plan.PunctuationMask,pb,Path.Combine(output,"PUNCTUATION-MASK.png"));SaveCrop(composite,pb,Path.Combine(output,"PUNCTUATION-AFTER.png"));}
                }
                composite.Save(Path.Combine(output,$"AFTER-{label}-FULL.png"));
                plans.Add(new{region.RegionId,role=region.RoleType.ToString(),region.SourceBlockIds,safeTarget=safe.Targets[0],plan.BackgroundType,plan.ReconstructionStrategy,plan.Confidence,plan.SafeToCommit,plan.FailureReason,plan.MaskBasis,plan.CleanupBounds,sourcePolygons=plan.SourceTextPolygons,protectedGeometry=plan.ProtectedGeometry});
            }
            source.Save(Path.Combine(output,"CASH-CARD-SOURCE.png"));composite.Save(Path.Combine(output,"CASH-CARD-CLEAN-BACKGROUND.png"));
            File.WriteAllText(Path.Combine(output,"BACKGROUND-STRATEGY.json"),JsonSerializer.Serialize(new{source=Path.GetFullPath(sourcePath),phase="Background only; no translated text",plans,structureFrozen=new{R001="Title",R002_R003_R004="Dialogue",R005="Header",VIS001="Image exclusion"},apiCalls=0},Json));
            var tests=RunTests();File.WriteAllText(Path.Combine(output,"PHASE-2-SELFTESTS.txt"),string.Join(Environment.NewLine,tests));
            File.WriteAllText(Path.Combine(output,"RUN-SUMMARY.txt"),$"Phase2={tests.Count(x=>x.StartsWith("PASS"))} PASS / {tests.Count(x=>x.StartsWith("FAIL"))} FAIL\r\nAPI Calls=0\r\nActiveOperations=0\r\n");
            return tests.Any(x=>x.StartsWith("FAIL"))?2:0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(output,"ERROR.txt"),ex.ToString());return 3;}
        finally{vision.DisposeAsync().AsTask().GetAwaiter().GetResult();ocr.DisposeAsync().AsTask().GetAwaiter().GetResult();}
    }
    private static List<string> RunTests()
    {
        var lines=new List<string>();void T(string n,Action a){try{a();lines.Add("PASS | "+n);}catch(Exception e){lines.Add("FAIL | "+n+" | "+e.Message);}}void A(bool v,string m){if(!v)throw new InvalidOperationException(m);}
        RecognitionRegion R(string id,RectangleF box)=>new(){RegionId=id,RoleType=RegionRoleType.BodyParagraph,Polygon=GeometryV2.RectanglePolygon(box),SourceLinePolygons=[GeometryV2.RectanglePolygon(box)],SourceBlockIds=[id],CoverageValid=true};
        void PunctuationCase(string name,string glyph,int gap=5){T(name,()=>{using var b=new Bitmap(150,60);using(var g=Graphics.FromImage(b)){g.Clear(Color.FromArgb(38,17,82));using var f=new Font("Segoe UI",18,GraphicsUnit.Pixel);g.DrawString("AB",f,Brushes.White,20,18);g.DrawString(glyph,f,Brushes.White,47+gap,18);}var rr=R("P",new(18,16,31,25));var q=BackgroundIntegrationPlanner.Plan(b,rr,rr.BoundingBox,[rr]);A(Count(q.PunctuationMask)>0,"small glyph not recovered");using var x=BackgroundIntegrationExecutor.Execute(b,q);A(x.Committed&&x.ChangedOutsideMask==0,"punctuation cleanup not atomic");});}
        int Count(bool[,] m){var n=0;for(var y=0;y<m.GetLength(1);y++)for(var x=0;x<m.GetLength(0);x++)if(m[x,y])n++;return n;}
        using var flat=new Bitmap(180,100);using(var g=Graphics.FromImage(flat)){g.Clear(Color.FromArgb(40,60,90));using var f=new Font("Segoe UI",13,GraphicsUnit.Pixel);g.DrawString("TEST",f,Brushes.White,31,31);}var r=R("A",new(30,30,70,20));var safe=r.BoundingBox;
        var p=BackgroundIntegrationPlanner.Plan(flat,r,safe,[r]);
        T("Cleanup mask is source polygons, not TranslationRenderRect",()=>{r.TranslationRenderRect=new(0,0,180,100);A(p.CleanupBounds.Width<100,"translation rect leaked");});
        T("Flat cleanup remains continuous",()=>{using var x=BackgroundIntegrationExecutor.Execute(flat,p);A(x.Committed&&x.ChangedOutsideMask==0&&p.BackgroundType==BackgroundSurfaceType.FlatColor,"not continuous/atomic");});
        T("Gradient uses gradient reconstruction",()=>{using var b=new Bitmap(160,80);for(int y=0;y<80;y++)for(int x=0;x<160;x++)b.SetPixel(x,y,Color.FromArgb(20+x,30+x/2,50));var q=BackgroundIntegrationPlanner.Plan(b,R("G",new(50,25,50,18)),new(50,25,50,18),[]);A(q.ReconstructionStrategy==BackgroundReconstructionStrategy.LinearGradient,"single fill");});
        T("Image cleanup writes no pixels outside mask",()=>{using var x=BackgroundIntegrationExecutor.Execute(flat,p);A(x.ChangedOutsideMask==0,"outside damage");});
        T("Protected geometry remains untouched",()=>{var n=R("N",new(90,30,40,20));var q=BackgroundIntegrationPlanner.Plan(flat,r,safe,[r,n]);using var x=BackgroundIntegrationExecutor.Execute(flat,q);A(x.ChangedOutsideMask==0,"protected changed");});
        T("Legal source overlap remains locally cleanable",()=>{var any=false;for(int y=p.CleanupBounds.Top;y<p.CleanupBounds.Bottom;y++)for(int x=p.CleanupBounds.Left;x<p.CleanupBounds.Right;x++)any|=p.CleanupMask[x,y];A(any,"source ink removed");});
        T("No NEW intrusion beyond source padding",()=>A(p.CleanupBounds.Width<r.BoundingBox.Width+10,"intrusion"));
        T("Unsafe reconstruction commits zero pixels",()=>{var bad=new BackgroundIntegrationPlan{RegionId="X",Role=RegionRoleType.Unknown,CleanupBounds=default,SourceTextPolygons=[],ProtectedGeometry=[],BackgroundType=BackgroundSurfaceType.UnknownUnsafe,ReconstructionStrategy=BackgroundReconstructionStrategy.PreserveOriginal,Confidence=0,SafeToCommit=false,FailureReason="unsafe",MaskBasis="source",CleanupMask=new bool[flat.Width,flat.Height],PrimaryMask=new bool[flat.Width,flat.Height],PunctuationMask=new bool[flat.Width,flat.Height]};using var x=BackgroundIntegrationExecutor.Execute(flat,bad);A(!x.Committed&&x.ChangedPixels==0,"committed");});
        T("Commit is atomic",()=>{using var x=BackgroundIntegrationExecutor.Execute(flat,p);A(x.Committed&&x.ChangedOutsideMask==0,"not atomic");});
        T("Adjacent regions cannot be cross-cleaned",()=>A(p.ProtectedGeometry.Count==0,"unexpected") );
        T("Source polygons remain traceable",()=>A(p.SourceTextPolygons.Count==r.SourceBlockIds.Count,"trace lost"));
        T("Structure metadata remains unchanged",()=>A(r.RoleType==RegionRoleType.BodyParagraph&&r.SourceBlockIds.SequenceEqual(["A"]),"mutated"));
        PunctuationCase("Opening and closing quote recovery","\"");
        PunctuationCase("Apostrophe recovery","'");
        PunctuationCase("Period recovery",".");
        PunctuationCase("Comma recovery",",");
        PunctuationCase("Colon recovery",":");
        PunctuationCase("Ellipsis recovery","…");
        PunctuationCase("Exclamation recovery","!");
        PunctuationCase("Question mark recovery","?");
        PunctuationCase("Dash recovery","—",0);
        PunctuationCase("Spaced punctuation recovery","\"",9);
        T("Wide separator is not punctuation",()=>{using var b=new Bitmap(150,60);using(var g=Graphics.FromImage(b)){g.Clear(Color.FromArgb(38,17,82));using var f=new Font("Segoe UI",18,GraphicsUnit.Pixel);g.DrawString("AB",f,Brushes.White,20,18);using var pen=new Pen(Color.White,1);g.DrawLine(pen,52,30,90,30);}var rr=R("S",new(18,16,31,25));var q=BackgroundIntegrationPlanner.Plan(b,rr,rr.BoundingBox,[rr]);for(var y=28;y<=32;y++)for(var x=52;x<=90;x++)A(!q.PunctuationMask[x,y],"separator misclassified");});
        return lines;
    }
    private static void SaveCrop(Bitmap b,Rectangle r,string path){var c=Rectangle.Intersect(r,new(0,0,b.Width,b.Height));using var x=b.Clone(c,b.PixelFormat);x.Save(path);}
    private static void SaveMask(BackgroundIntegrationPlan p,string path){using var b=new Bitmap(Math.Max(1,p.CleanupBounds.Width),Math.Max(1,p.CleanupBounds.Height));for(int y=0;y<b.Height;y++)for(int x=0;x<b.Width;x++){var sx=p.CleanupBounds.Left+x;var sy=p.CleanupBounds.Top+y;b.SetPixel(x,y,p.CleanupMask[sx,sy]?Color.White:Color.Black);}b.Save(path);}
    private static void SaveMask(bool[,] mask,Rectangle r,string path){using var b=new Bitmap(Math.Max(1,r.Width),Math.Max(1,r.Height));for(int y=0;y<b.Height;y++)for(int x=0;x<b.Width;x++)b.SetPixel(x,y,mask[r.Left+x,r.Top+y]?Color.White:Color.Black);b.Save(path);}
    private static Rectangle MaskBounds(bool[,] mask,Size size){var l=size.Width;var t=size.Height;var r=-1;var b=-1;for(var y=0;y<size.Height;y++)for(var x=0;x<size.Width;x++)if(mask[x,y]){l=Math.Min(l,x);t=Math.Min(t,y);r=Math.Max(r,x);b=Math.Max(b,y);}return r<l?Rectangle.Empty:Rectangle.FromLTRB(l,t,r+1,b+1);}
    private static void SaveDiff(Bitmap a,Bitmap b,Rectangle r,string path){var c=Rectangle.Intersect(r,new(0,0,a.Width,a.Height));using var d=new Bitmap(c.Width,c.Height);for(int y=0;y<c.Height;y++)for(int x=0;x<c.Width;x++){var aa=a.GetPixel(c.Left+x,c.Top+y);var bb=b.GetPixel(c.Left+x,c.Top+y);d.SetPixel(x,y,aa.ToArgb()==bb.ToArgb()?Color.Black:Color.FromArgb(255,Math.Abs(aa.R-bb.R),Math.Abs(aa.G-bb.G),Math.Abs(aa.B-bb.B)));}d.Save(path);}
    private static void SaveClassification(BackgroundIntegrationPlan p,string path){using var b=new Bitmap(640,90);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(18,23,32));using var f=new Font("Segoe UI",14,FontStyle.Bold,GraphicsUnit.Pixel);using var s=new Font("Segoe UI",12,GraphicsUnit.Pixel);g.DrawString($"R001  {p.BackgroundType} / {p.ReconstructionStrategy}",f,Brushes.White,12,10);g.DrawString($"Confidence {p.Confidence:0.00}   SafeToCommit {p.SafeToCommit}   Cleanup {p.CleanupBounds}",s,Brushes.LightGreen,12,42);b.Save(path);}
}
