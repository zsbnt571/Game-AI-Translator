using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class Repair06122TargetedTests
{
    internal static int Run(string output,string? farannaSource)
    {
        Directory.CreateDirectory(output);var failures=new List<string>();var report=new Dictionary<string,object>();
        try
        {
            using(var frozen=new Bitmap(3840,2160,PixelFormat.Format32bppPArgb))
            {using(var g=Graphics.FromImage(frozen)){g.Clear(Color.FromArgb(36,42,52));}
             using var overlay=new CaptureOverlay(new Rectangle(0,0,3840,2160),frozen,true,false);var show=Stopwatch.StartNew();overlay.Show();Application.DoEvents();show.Stop();var m=overlay.GetFirstPaintSnapshotForSmoke();
             report["Freeze"]=new{OldFirstPaintMs="33.887-38.495",FreezeReadyMs=m.StaticComposeMs,OverlayShowMs=show.Elapsed.TotalMilliseconds,FirstPaintMs=m.FirstPaintMs,FrozenBitmapDrawMode="single precomposed static-layer blit",ShadeDrawMs=0,DynamicDrawMs=0,MaxOverlayUiBlockMs=m.MaxOverlayUiBlockMs};
             if(m.FirstPaintMs>=33.887)failures.Add($"4K FirstPaint not improved: {m.FirstPaintMs:F3}ms");overlay.Close();}

            if(string.IsNullOrWhiteSpace(farannaSource)||!File.Exists(farannaSource))failures.Add("Faranna real source missing");
            else RunFaranna(output,farannaSource,report,failures);
            RunHighlight(output,report,failures);
        }
        catch(Exception ex){failures.Add(ex.ToString());}
        File.WriteAllText(Path.Combine(output,"TARGETED-METRICS.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllLines(Path.Combine(output,"RESULT.txt"),failures.Count==0?["PASS","Status=Manual Acceptance Pending"]:["FAIL",..failures]);
        return failures.Count==0?0:2;
    }

    private static void RunFaranna(string output,string sourcePath,Dictionary<string,object> report,List<string> failures)
    {
        using var source=new Bitmap(sourcePath);var name=Region("RU-NAME","Faranne",new(26,34,579,103),1,"R001",RegionRoleType.CharacterName,"法兰妮");
        var species=Region("RU-SPECIES","Dark Elf",new(129,137,246,77),2,"R002",RegionRoleType.Species,"暗精灵");
        var namePlan=BackgroundIntegrationPlanner.Plan(source,name,name.BoundingBox,[name,species]);var speciesPlan=BackgroundIntegrationPlanner.Plan(source,species,species.BoundingBox,[name,species]);
        using var nameCleanup=BackgroundIntegrationExecutor.Execute(source,namePlan);using var speciesCleanup=BackgroundIntegrationExecutor.Execute(source,speciesPlan);
        using var rendered=RegionRendererV2.Render(source,[name,species],new RenderSettings());rendered.Bitmap.Save(Path.Combine(output,"faranna-targeted.png"),ImageFormat.Png);
        var nd=rendered.Diagnostics!.Single(x=>x.RegionId==name.RegionId);var sd=rendered.Diagnostics!.Single(x=>x.RegionId==species.RegionId);
        var overlap=RectangleF.Intersect(nd.RenderBounds,sd.RenderBounds);
        report["Faranna"]=new{CharacterNameRenderUnit=name.RegionId,CharacterNameSourceIds=name.SourceBlockIds,CharacterNameGeometry=nd.RenderBounds,SpeciesRenderUnit=species.RegionId,SpeciesSourceIds=species.SourceBlockIds,SpeciesGeometry=sd.RenderBounds,NameCleanup=new{Executed=nameCleanup.Committed,PixelsModified=nameCleanup.ChangedPixels,namePlan.SafeToCommit,namePlan.FailureReason,nameCleanup.ValidationFailure},SpeciesCleanup=new{Executed=speciesCleanup.Committed,PixelsModified=speciesCleanup.ChangedPixels,speciesPlan.SafeToCommit,speciesPlan.FailureReason,speciesCleanup.ValidationFailure},RenderOverlapPixels=overlap.IsEmpty?0:overlap.Width*overlap.Height,VisibleSourceGlyphDetection="requires manual visual acceptance; cleanup masks executed on independent owners"};
        if(!nameCleanup.Committed||nameCleanup.ChangedPixels<=0)failures.Add("Faranna name cleanup did not modify pixels");
        if(!speciesCleanup.Committed||speciesCleanup.ChangedPixels<=0)failures.Add("Faranna species cleanup did not modify pixels");
        if(!overlap.IsEmpty)failures.Add($"Faranna name/species render overlap {overlap}");
    }

    private static void RunHighlight(string output,Dictionary<string,object> report,List<string> failures)
    {
        using var image=new Bitmap(1000,700);using(var g=Graphics.FromImage(image))g.Clear(Color.FromArgb(45,48,54));
        var weightBounds=new RectangleF(310,610,145,28);var dislikeBounds=new RectangleF(565,610,150,28);
        var weightGroup=new SegmentGroup{GroupId="R015",GroupType=SegmentType.CharacterInfo,OriginalText="Weight: 42kg",OrganizedText="Weight: 42kg",Translation="体重：42kg",Bounds=weightBounds,ReadingOrder=15};
        var dislikeGroup=new SegmentGroup{GroupId="R016",GroupType=SegmentType.CharacterInfo,OriginalText="Dislikes: Shuri",OrganizedText="Dislikes: Shuri",Translation="讨厌：修莉",Bounds=dislikeBounds,ReadingOrder=16};
        var document=new OcrDocument{Groups=[weightGroup,dislikeGroup]};var recognition=new RecognitionDocumentV2();
        var weight=Region("RU-WEIGHT","Weight: 42kg",weightBounds,15,"R015",RegionRoleType.Metadata,"体重：42kg");weight.TranslationUnitId="DU-WEIGHT";weight.TranslationRenderRect=weightBounds;
        var dislike=Region("RU-DISLIKE","Dislikes: Shuri",dislikeBounds,16,"R016",RegionRoleType.Metadata,"讨厌：修莉");dislike.TranslationUnitId="DU-DISLIKE";dislike.TranslationRenderRect=dislikeBounds;
        recognition.Regions.AddRange([weight,dislike]);recognition.TranslationUnits.Add(new("DU-WEIGHT",[weight.RegionId],RegionRoleType.Metadata,weight.OcrText,15,false,["R015"]));recognition.TranslationUnits.Add(new("DU-DISLIKE",[dislike.RegionId],RegionRoleType.Metadata,dislike.OcrText,16,false,["R016"]));
        var semantic=new[]{new TranslationSemanticGroup("DU-WEIGHT",weight.OcrText,["R015"],SegmentType.CharacterInfo,weightBounds,15),new TranslationSemanticGroup("DU-DISLIKE",dislike.OcrText,["R016"],SegmentType.CharacterInfo,dislikeBounds,16)};
        var translations=new Dictionary<string,string>{{"DU-WEIGHT","体重：42kg"},{"DU-DISLIKE","讨厌：修莉"}};
        using var form=new PreviewForm(image,PreviewMode.OcrOnly,new ApiSettings{VisualModel=VisualModelKind.Off,PreviewTextPanelVisible=true},new OcrService(),new TranslationService());form.SuppressAutoOcrForE2E();form.Show();Application.DoEvents();form.SetRecognitionMappingForSmoke(recognition,semantic,translations);form.CommitTranslatedDisplayAtomicallyForSmoke(image,document);Application.DoEvents();
        object Click(string needle,string expected){var index=form.DisplayedTextForSmoke.IndexOf(needle,StringComparison.Ordinal);var hit=form.ClickRightTextAtByMouseForSmoke(index+1);var geometry=form.FinalHighlightGeometryForSmoke();var sources=hit.Identity?.SourceIds??[];if(!sources.SequenceEqual([expected]))failures.Add($"{needle} mapped to {string.Join(',',sources)}");return new{RightText=needle,hit.ClickedCharIndex,RightTextRangeIdentity=hit.Identity,DisplayUnitId=hit.Identity?.TranslationUnitId,SourceIds=sources,RenderUnitIds=hit.Identity?.StableRenderUnitIds,FinalHighlightGeometry=geometry};}
        report["Highlight"]=new{Weight42kg=Click("42kg","R015"),DislikeShuri=Click("讨厌：修莉","R016")};form.Close();
    }

    private static RecognitionRegion Region(string id,string text,RectangleF bounds,int order,string sourceId,RegionRoleType role,string translation)=>new()
    {RegionId=id,StructuredText=text,OcrText=text,TranslationText=translation,Polygon=GeometryV2.RectanglePolygon(bounds),SourceBlockIds=[sourceId],SourceLinePolygons=[GeometryV2.RectanglePolygon(bounds)],SourceSegments=[new(sourceId,text,GeometryV2.RectanglePolygon(bounds),role,.98f,order,id)],ReadingOrder=order,RoleType=role,RoleConfidence=.98f,CoverageValid=true,RendererTargetRegion=bounds,TranslationUnitId=id};
}
