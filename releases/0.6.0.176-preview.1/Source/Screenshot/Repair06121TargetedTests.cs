using System.Reflection;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class Repair06121TargetedTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var failures=new List<string>();var checks=new List<object>();
        void Check(string name,bool pass,string detail=""){checks.Add(new{name,pass,detail});if(!pass)failures.Add(name+(detail.Length>0?": "+detail:""));}
        try
        {
            Check("App identity display version",BuildIdentity.DisplayVersion=="0.5.0.6.12.1");
            Check("Stable single-instance application id",SingleInstanceCoordinator.DiagnosticMutexName.Contains(BuildIdentity.StableApplicationId,StringComparison.Ordinal));
            var prompt=TranslationPromptBuilder.BuildSystemPrompt(new ApiSettings{TargetLanguage="zh-CN",TranslationStyle=TranslationStyle.GameLocalization});
            Check("CharacterName role-aware localization",prompt.Contains("CharacterName must be naturally localized",StringComparison.Ordinal));
            Check("Species phrase role-aware translation",prompt.Contains("Species is a complete race/species phrase",StringComparison.Ordinal));
            Check("No Faranna dictionary",!prompt.Contains("Faranna",StringComparison.OrdinalIgnoreCase));

            var name=Region("name","Faranna",new(40,30,180,32),1,"N");
            var species=Region("species","Dark Elf",new(40,68,180,26),2,"S");
            var body=Region("body","A young member of the dark elven tribe who is confident in hunting and cooking.",new(40,112,500,90),3,"B");
            var all=new[]{name,species,body};var nr=RegionRoleClassifierV2.Classify(name,all);var sr=RegionRoleClassifierV2.Classify(species,all);
            Check("CharacterName classified independently",nr.Role==RegionRoleType.CharacterName,nr.Role.ToString());
            Check("Species classified independently",sr.Role==RegionRoleType.Species,sr.Role.ToString());
            name.RoleType=nr.Role;species.RoleType=sr.Role;
            var units=TranslationUnitBuilderV2.Build(all);
            Check("Name and Species separate translation units",units.Count==3&&units[0].RoleType==RegionRoleType.CharacterName&&units[1].RoleType==RegionRoleType.Species);
            Check("Name and Species are not preserve-original",!units[0].PreserveOriginal&&!units[1].PreserveOriginal);

            using(var bitmap=new Bitmap(640,360))
            {
                using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.FromArgb(45,47,52));g.DrawString("Faranna",SystemFonts.DefaultFont,Brushes.White,40,30);}
                var owner1=Region("owner-1","Faranna",new(40,30,180,32),1,"DUP");owner1.RoleType=RegionRoleType.CharacterName;owner1.TranslationText="法兰妮";
                var owner2=Region("owner-2","Faranna context",new(35,25,230,50),2,"DUP");owner2.RoleType=RegionRoleType.Title;owner2.TranslationText="法兰妮背景";
                using var rendered=RegionRendererV2.Render(bitmap,[owner1,owner2],new RenderSettings());
                var duplicate=rendered.Diagnostics?.Any(x=>x.DecisionCode=="DuplicateVisibleRenderOwner")==true;
                Check("One SourceId one visible render owner",duplicate);
                var committed=rendered.Diagnostics?.Count(x=>x.AtomicRegionCommitted&&x.Coverage.Any(c=>c.SourceLineId=="DUP"))??0;
                Check("VisibleRenderOwnerCount(SourceId)<=1",committed<=1,$"count={committed}");
            }

            using(var choice=new Bitmap(700,220))
            {
                using(var g=Graphics.FromImage(choice)){g.Clear(Color.FromArgb(50,150,205));using var f=new Font("Arial",28,FontStyle.Bold);g.DrawString("How did you know my name?",f,Brushes.White,40,70);}
                var r=Region("choice","How did you know my name?",new(38,68,445,42),1,"CHOICE");r.RoleType=RegionRoleType.Choice;r.TranslationText="你怎么知道我的名字？";
                var plan=BackgroundIntegrationPlanner.Plan(choice,r,r.BoundingBox,[r]);using var cleaned=BackgroundIntegrationExecutor.Execute(choice,plan);
                Check("Real cleanup path executes",plan.SafeToCommit&&cleaned.Committed,plan.FailureReason+cleaned.ValidationFailure);
                Check("Cleanup actual modified pixels > 0",cleaned.ChangedPixels>0,$"changed={cleaned.ChangedPixels}");
                var maskPixels=0;for(var y=plan.CleanupBounds.Top;y<plan.CleanupBounds.Bottom;y++)for(var x=plan.CleanupBounds.Left;x<plan.CleanupBounds.Right;x++)if(plan.CleanupMask[x,y])maskPixels++;
                checks.Add(new{name="Cleanup trace",pass=true,detail=JsonSerializer.Serialize(new{DisplayUnitId=r.RegionId,SourceIds=r.SourceBlockIds,CleanupPolicy=plan.MaskBasis,CleanupExecuted=cleaned.Committed,CleanupMaskPixelCount=maskPixels,ActualModifiedPixelCount=cleaned.ChangedPixels,RenderCommitted=cleaned.Committed,ResidualSourceEstimate="synthetic=none by changed glyph mask"})});
            }

            using(var frozen=new Bitmap(1920,1080))using(var overlay=new CaptureOverlay(new Rectangle(0,0,1920,1080),frozen,true,true))
            {
                overlay.Show();Application.DoEvents();overlay.ResetPointerPerformanceCountersForSmoke();var method=typeof(CaptureOverlay).GetMethod("OnMouseMove",BindingFlags.Instance|BindingFlags.NonPublic)!;
                for(var i=0;i<120;i++)method.Invoke(overlay,[new MouseEventArgs(MouseButtons.None,0,100+i*3%1500,100+i*7%800,0)]);
                Application.DoEvents();var m=overlay.GetPointerPerformanceSnapshot();
                Check("MouseMove no bitmap clone",m.BitmapCloneDuringMouseMoveCount==0);
                Check("MouseMove no DXGI acquire",m.DXGIFrameAcquireAfterFreezeCount==0);
                Check("MouseMove no full overlay invalidate",m.FullOverlayInvalidateCount==0,$"count={m.FullOverlayInvalidateCount}");
                Check("MouseMove no full 4K redraw",m.FullFreezeBitmapRedrawCount==0,$"count={m.FullFreezeBitmapRedrawCount}");
                checks.Add(new{name="Pointer performance",pass=true,detail=JsonSerializer.Serialize(new{m.OverlayMouseMoveCount,m.P50Ms,m.P95Ms,m.MaxMs,m.OverlayPaintP95Ms,m.FullOverlayInvalidateCount,m.FullFreezeBitmapRedrawCount,m.BitmapCloneDuringMouseMoveCount,m.DXGIFrameAcquireAfterFreezeCount,m.MaxScreenshotOverlayUiBlockMs})});overlay.Close();
            }

            var baseline=Path.Combine(output,"baseline-0612");var baselineCode=Repair0612TargetedTests.Run(baseline);Check("6.12 capture and exact mapping regression",baselineCode==0);
        }
        catch(Exception ex){failures.Add(ex.ToString());}
        File.WriteAllText(Path.Combine(output,"TARGETED-RESULTS.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllLines(Path.Combine(output,"RESULT.txt"),failures.Count==0?["PASS","Status=Manual Acceptance Pending"]:["FAIL",..failures]);
        return failures.Count==0?0:2;
    }

    private static RecognitionRegion Region(string id,string text,RectangleF bounds,int order,string sourceId)=>new()
    {
        RegionId=id,StructuredText=text,OcrText=text,Polygon=GeometryV2.RectanglePolygon(bounds),SourceBlockIds=[sourceId],
        SourceLinePolygons=[GeometryV2.RectanglePolygon(bounds)],ReadingOrder=order,RoleConfidence=.9f,CoverageValid=true
    };
}
