using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public static class RendererArchitectureV2SelfTests
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<object>();var failed=0;
        void Test(string id,Action body){try{body();rows.Add(new{id,status="PASS"});}catch(Exception ex){failed++;rows.Add(new{id,status="FAIL",error=ex.Message});}}
        static void A(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        Test("A_1TU_1Source",()=>A(Plan(Line("A",10,10,100,20)).Clusters.Count==1,"cluster count"));
        Test("B_3Contiguous_1Cluster",()=>A(Plan(Line("A",10,10,100,20),Line("B",10,34,100,20),Line("C",10,58,100,20)).Clusters.Count==1,"contiguous split"));
        Test("C_TitleBody_2Clusters",()=>A(Plan(Line("A",10,10,100,20,RegionRoleType.Title),Line("B",10,40,100,20)).Clusters.Count==2,"title merged"));
        Test("D_CaptionBody_2Clusters",()=>A(Plan(Line("A",10,10,100,14,RegionRoleType.Caption),Line("B",300,180,200,30)).Clusters.Count==2,"caption merged"));
        Test("E_4Separated",()=>A(Plan(Line("A",10,10,80,15),Line("B",300,10,80,15),Line("C",10,200,80,15),Line("D",300,200,80,15)).Clusters.Count==4,"separated merged"));
        Test("F_MultipleButtons",()=>A(Plan(Line("A",10,10,100,30,RegionRoleType.Button),Line("B",10,45,100,30,RegionRoleType.Button)).Clusters.Count==2,"buttons merged"));
        Test("G_LongParagraph",()=>A(Plan(Enumerable.Range(0,8).Select(i=>Line("L"+i,20,20+i*22,300,18)).ToArray()).Clusters.Count==1,"paragraph split"));
        Test("H_SmallCaption",()=>A(Plan(Line("A",20,20,120,10,RegionRoleType.Caption)).Regions.Count==1,"caption dropped"));
        Test("I_LowContrastGeometryIndependent",()=>A(Plan(Line("A",20,20,120,18)).Clusters.Count==1,"style guessed"));
        Test("J_OutlinedDialogue",()=>A(Plan(Line("A",20,20,250,28,RegionRoleType.Dialogue)).Regions.Count==1,"dialogue dropped"));
        Test("K_OneSegmentContiguousSources",()=>A(Plan([Line("A",10,10,100,20),Line("B",10,34,100,20)],true).Regions.Single().SourceBlockIds.Count==2,"lineage lost"));
        Test("L_OneSegmentSeparatedClusters",()=>{var p=Plan([Line("A",10,10,100,20),Line("B",500,300,100,20)],true);A(p.Diagnostics.Any(x=>x.Outcome==VisualClusterRenderOutcome.TRANSLATION_ALLOCATION_SPANS_MULTIPLE_CLUSTERS)&&p.Regions.Count==0,"unsafe span admitted");});
        Test("M_MultipleSegmentsOneCluster",()=>{var p=Plan([Line("A",10,10,100,20),Line("B",10,34,100,20)],false,true);A(p.Regions.Count==1&&p.Regions[0].TranslationText=="译A译B","segments not combined");});
        Test("N_AllocationMissing",()=>{var d=Doc([Line("A",10,10,100,20)],false,false);d.TranslationAllocations.Clear();var p=VisualClusterRenderPlannerV2.Plan(d,new(640,360));A(p.Diagnostics.Any(x=>x.Outcome==VisualClusterRenderOutcome.TRANSLATION_ALLOCATION_FAILED),"missing allocation silent");});
        Test("O_UnsafeOwnerOverlap",()=>{var p=Plan([Line("A",10,10,100,20),Line("B",500,300,100,20)],true);A(p.Regions.Count==0,"unsafe owner committed");});
        Test("P_LayoutFailureBeforeCleanup",()=>{using var b=new Bitmap(80,25);var p=Plan(Line("A",2,2,5,5));p.Regions[0].TranslationText=new string('译',300);using var r=RegionRendererV2.Render(b,p.Regions,new RenderSettings());A(!r.Diagnostics!.Single().AtomicRegionCommitted&&r.Diagnostics.Single().TextDrawCount==0,"partial commit");});
        File.WriteAllText(Path.Combine(output,"renderer-architecture-v2-synthetic.json"),JsonSerializer.Serialize(new{passed=16-failed,failed,tests=rows},new JsonSerializerOptions{WriteIndented=true}));
        return failed==0?0:2;
    }
    private sealed record L(string Id,float X,float Y,float W,float H,RegionRoleType Role);
    private static L Line(string id,float x,float y,float w,float h,RegionRoleType role=RegionRoleType.BodyParagraph)=>new(id,x,y,w,h,role);
    private static VisualClusterRenderPlan Plan(params L[] lines)=>Plan(lines,false,false);
    private static VisualClusterRenderPlan Plan(IReadOnlyList<L> lines,bool sameSegment=false,bool multipleSegments=false)=>VisualClusterRenderPlannerV2.Plan(Doc(lines,sameSegment,multipleSegments),new(640,360));
    private static RecognitionDocumentV2 Doc(IReadOnlyList<L> lines,bool sameSegment,bool multipleSegments)
    {
        var d=new RecognitionDocumentV2{TranslationMappingMode="SOURCE_ALIGNED_MAPPING_V1"};var ids=lines.Select(x=>x.Id).ToArray();
        var region=new RecognitionRegion{RegionId="R",Polygon=GeometryV2.RectanglePolygon(RectangleF.FromLTRB(lines.Min(x=>x.X),lines.Min(x=>x.Y),lines.Max(x=>x.X+x.W),lines.Max(x=>x.Y+x.H))),
            SourceBlockIds=ids.ToList(),SourceLinePolygons=lines.Select(x=>GeometryV2.RectanglePolygon(new(x.X,x.Y,x.W,x.H))).ToList(),
            SourceSegments=lines.Select((x,i)=>new SourceSegmentGeometry(x.Id,x.Id,GeometryV2.RectanglePolygon(new(x.X,x.Y,x.W,x.H)),x.Role,1,i,"R")).ToList(),
            RoleType=lines[0].Role,ReadingOrder=0,TranslationUnitId="TU",TranslationText="译文",CoverageValid=true};d.Regions.Add(region);
        d.TranslationUnits.Add(new("TU",["R"],lines[0].Role,"source",0,false,ids));
        foreach(var (x,i) in lines.Select((x,i)=>(x,i)))d.SourceIdentities.Add(new(x.Id,x.Id,GeometryV2.RectanglePolygon(new(x.X,x.Y,x.W,x.H)),new(x.X,x.Y,x.W,x.H),new(x.X,x.Y,x.W,x.H),x.Id,x.Role,i,"P"));
        if(sameSegment)d.TranslationAllocations["TU"]=[new("TU",ids,"译文",0,1,"S1")];
        else if(multipleSegments)d.TranslationAllocations["TU"]=lines.Select((x,i)=>new TranslationAllocationSegment("TU",[x.Id],"译"+x.Id,i,1,"S"+i)).ToArray();
        else if(lines.Count>0)d.TranslationAllocations["TU"]=[new("TU",[ids[0]],"译文",0,1,"S1")];
        return d;
    }
}
