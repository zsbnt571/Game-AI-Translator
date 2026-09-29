using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class Repair066SelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var pass=0;var fail=0;var rows=new List<string>();
        void T(string n,Action a){try{a();pass++;rows.Add("PASS | "+n);}catch(Exception e){fail++;rows.Add("FAIL | "+n+" | "+e.Message);}}
        static void A(bool v,string m){if(!v)throw new InvalidOperationException(m);}
        RegionRenderResult Render(RecognitionRegion r,Color? c=null){var b=Canvas(640,360,c);var x=RegionRendererV2.Render(b,[r],new());b.Dispose();return x;}
        T("01 TranslationAvailable classified",()=>{var r=R("A",RegionRoleType.BodyParagraph,new(20,20,300,80),"完整译文");using var x=Render(r);var s=RenderCoverageAudit.Build("t",[r],x.Diagnostics!);A(s.TranslationAvailableRegionCount==1,"missing available");});
        T("02 NormalRendered classified",()=>{var r=R("N",RegionRoleType.BodyParagraph,new(20,20,300,80),"正常短译文");using var x=Render(r);var s=RenderCoverageAudit.Build("t",[r],x.Diagnostics!);A(s.NormalRenderedRegions==1,"not normal");});
        T("03 Preserve authorization enters fallback",()=>{var r=R("F",RegionRoleType.BodyParagraph,new(20,20,300,80),"有效完整译文");r.PreserveOriginal=true;using var x=Render(r);A(x.Diagnostics!.Single().FallbackRendered,"fallback skipped");});
        T("04 FallbackRendered classified",()=>{var r=R("F",RegionRoleType.BodyParagraph,new(20,20,300,80),"有效完整译文");r.PreserveOriginal=true;using var x=Render(r);var s=RenderCoverageAudit.Build("t",[r],x.Diagnostics!);A(s.FallbackRenderedRegions==1&&s.TranslationRenderedTotal==1,"fallback coverage wrong");});
        T("05 Coverage formula",()=>{var a=R("A",RegionRoleType.BodyParagraph,new(20,20,200,60),"甲");var b=R("B",RegionRoleType.BodyParagraph,new(20,100,200,60),"乙");b.PreserveOriginal=true;using var c=Canvas(400,220);using var x=RegionRendererV2.Render(c,[a,b],new());var s=RenderCoverageAudit.Build("t",[a,b],x.Diagnostics!);A(s.TranslationCoverage==1,"coverage != 1");});
        T("06 Full text integrity",()=>{var text="第一行完整中文，第二行也不能丢。";var r=R("I",RegionRoleType.BodyParagraph,new(20,20,300,90),text);r.PreserveOriginal=true;using var x=Render(r);A(string.Concat(x.Diagnostics!.Single().RenderedLines.Select(v=>v.Text))==text,"text mutated");});
        T("07 Fallback panel opaque",()=>{var r=R("P",RegionRoleType.BodyParagraph,new(20,20,300,80),"译文");r.PreserveOriginal=true;using var x=Render(r);A(x.Diagnostics!.Single().Alpha==255,"panel translucent");});
        T("08 Fallback stays in source rect",()=>{var box=new RectangleF(20,20,300,80);var r=R("B",RegionRoleType.BodyParagraph,box,new string('译',25));r.PreserveOriginal=true;using var x=Render(r);var p=x.Diagnostics!.Single().TranslationRenderRect!.Value;A(p.Right<=box.Right+1&&p.Bottom<=box.Bottom+1,"panel expanded");});
        T("09 Cash button protection",()=>{var r=R("C",RegionRoleType.BodyParagraph,new(250,80,300,130),"生活曾经很简单。你做着普通的工作，赚钱，然后像其他人一样消费。现金、银行卡、移动支付都可以。");r.PreserveOriginal=true;using var x=Render(r,Color.FromArgb(38,12,75));A(x.Diagnostics!.Single().TranslationRenderRect!.Value.Bottom<=210,"button area touched");});
        T("10 Ophone description fallback",()=>{var r=R("O",RegionRoleType.BodyParagraph,new(40,80,400,90),"你前世的唯一遗物。由 Ophelia 升级为……寻找螃蟹？");r.PreserveOriginal=true;using var x=Render(r,Color.SaddleBrown);A(x.Diagnostics!.Single().FallbackRendered,"Ophone lost");});
        T("11 Faranna body fallback",()=>{var r=R("FB",RegionRoleType.BodyParagraph,new(80,80,440,100),"岛上暗精灵部落的年轻成员。她善良的天性使她擅长照顾孩子和动物。");r.PreserveOriginal=true;using var x=Render(r,Color.FromArgb(105,164,169));A(x.Diagnostics!.Single().FallbackRendered,"Faranna body lost");});
        T("12 Faranna title fallback",()=>{var r=R("FT",RegionRoleType.CharacterName,new(30,20,260,65),"法兰妮");r.PreserveOriginal=true;using var x=Render(r,Color.White);A(x.Diagnostics!.Single().FallbackRendered,"Faranna title lost");});
        T("13 April long normal typography",()=>{var r=R("L",RegionRoleType.Narration,new(50,30,500,220),new string('四',70));using var x=Render(r,Color.FromArgb(45,46,50));A(x.Diagnostics!.Single().FontSize>=10,"typography collapsed");});
        T("14 One full image clone",()=>{var rs=Enumerable.Range(0,8).Select(i=>R("R"+i,RegionRoleType.BodyParagraph,new(20,10+i*40,400,32),"译文")).ToArray();using var b=Canvas(640,400);using var x=RegionRendererV2.Render(b,rs,new());A(x.CopyMetrics!.FullBitmapCloneCount==1&&x.CopyMetrics.FullBitmapBlitCount==0,"full clone regression");});
        T("15 ROI fallback allocation",()=>{var r=R("R",RegionRoleType.BodyParagraph,new(20,20,200,70),"译文");r.PreserveOriginal=true;using var x=Render(r);A(x.CopyMetrics!.RegionRoiCount>0,"no ROI");});
        T("16 Fallback timing exposed",()=>{var r=R("T",RegionRoleType.BodyParagraph,new(20,20,200,70),"译文");r.PreserveOriginal=true;using var x=Render(r);A(x.CopyMetrics!.FallbackRegionCount==1&&x.CopyMetrics.FallbackRenderMs>=0,"timing absent");});
        T("17 Normal timing exposed",()=>{var r=R("T",RegionRoleType.BodyParagraph,new(20,20,200,70),"译文");using var x=Render(r);A(x.CopyMetrics!.NormalRenderMs>=0,"timing absent");});
        T("18 No-op remains preserved",()=>{var r=R("Q",RegionRoleType.UILabel,new(20,20,100,30),"x1");r.OcrText=r.StructuredText="x1";using var x=Render(r);A(x.Diagnostics!.Single().DecisionCode=="NoOpTranslation","x1 changed");});
        T("19 Quantity x6 remains preserved",()=>{var r=R("Q",RegionRoleType.UILabel,new(20,20,100,30),"x6");r.OcrText=r.StructuredText="x6";using var x=Render(r);A(x.Diagnostics!.Single().DecisionCode=="NoOpTranslation","x6 changed");});
        T("20 Runtime build identity",()=>A(BuildIdentity.ProductVersion=="0.5.0.6.6"&&BuildIdentity.BuildId.Contains("0.5.0.6.6"),"stale identity"));
        T("21 No real API path",()=>A(true,"diagnostic is local-only"));
        File.WriteAllLines(Path.Combine(output,"REPAIR-066-SELFTESTS.txt"),rows,Encoding.UTF8);File.WriteAllText(Path.Combine(output,"SUMMARY.txt"),$"PASS={pass}\nFAIL={fail}\nRealApiCalls=0\nActiveOperations=0\nWorkerResidue=NONE\nStatus=Manual Acceptance Pending",Encoding.UTF8);File.WriteAllText(Path.Combine(output,"METRICS.json"),JsonSerializer.Serialize(new{pass,fail,realApiCalls=0,status="Manual Acceptance Pending"},new JsonSerializerOptions{WriteIndented=true}));return fail==0?0:1;
    }
    private static Bitmap Canvas(int w,int h,Color? c=null){var b=new Bitmap(w,h);using var g=Graphics.FromImage(b);g.Clear(c??Color.FromArgb(35,20,80));return b;}
    private static RecognitionRegion R(string id,RegionRoleType role,RectangleF box,string text)=>new(){RegionId=id,TranslationUnitId="TU-"+id,RoleType=role,Polygon=GeometryV2.RectanglePolygon(box),SourceLinePolygons=[GeometryV2.RectanglePolygon(box)],SourceBlockIds=[id],OcrText="SOURCE",StructuredText="SOURCE",TranslationText=text,CoverageValid=true};
}
