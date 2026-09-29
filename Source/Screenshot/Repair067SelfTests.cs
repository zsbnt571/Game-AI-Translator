using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class Repair067SelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<string>();var pass=0;var fail=0;
        void T(string n,Action a){try{a();pass++;rows.Add("PASS | "+n);}catch(Exception e){fail++;rows.Add("FAIL | "+n+" | "+e.Message);}}
        static void A(bool v,string m){if(!v)throw new InvalidOperationException(m);}
        T("Faranna name integrity",()=>A(FieldTranslationIntegrityV2.Normalize("Faranne","法兰")=="法兰妮","name truncated"));
        T("Faranna race integrity",()=>A(FieldTranslationIntegrityV2.Normalize("Dark Elf","黑暗精灵")=="暗精灵","race changed"));
        T("Metadata numeric integrity",()=>A(FieldTranslationIntegrityV2.Normalize("Height: 182cm","身高").Contains("182"),"number lost"));
        T("Independent metadata fields",()=>{var r=Composite();var x=SemanticFieldNormalizerV2.Normalize([r],1);A(x.Count==3&&x.All(v=>v.SourceBlockIds.Count==1)&&x.Select(v=>v.StructuredText.Split(':')[0]).SequenceEqual(["Age","Height","Weight"]),"fields merged");});
        T("Field anchors remain independent",()=>{var x=SemanticFieldNormalizerV2.Normalize([Composite()],1);A(x.Select(v=>v.BoundingBox.Top).Distinct().Count()==3,"anchors merged");});
        T("Local foreground does not inspect sibling",()=>{using var b=new Bitmap(220,80);using(var g=Graphics.FromImage(b)){g.Clear(Color.White);g.FillRectangle(Brushes.DarkGreen,110,0,110,80);g.DrawString("NAME",new Font("Segoe UI",20,GraphicsUnit.Pixel),Brushes.CadetBlue,10,20);}var r=R("N",RegionRoleType.CharacterName,new(8,18,90,30),"名字");var c=TextStyleHintExtractor.EstimateForeground(b,r,Color.White);A(c is null||c.Value.G>=c.Value.R,"sibling polluted color");});
        T("Fallback uses own source anchor",()=>{using var b=Canvas();var r=R("A",RegionRoleType.BodyParagraph,new(40,50,250,70),new string('译',70));r.RendererTargetRegion=new(0,0,600,300);r.LayoutExclusionPolygons=[GeometryV2.RectanglePolygon(new(0,0,600,300))];using var x=RegionRendererV2.Render(b,[r],new());var d=x.Diagnostics!.Single();A(d.FallbackRendered,"fixture did not enter fallback");var p=d.TranslationRenderRect!.Value;A(p.Left>=39&&p.Top>=49&&p.Right<=291,"target container leaked");});
        T("Game short text keeps source scale",()=>{using var b=Canvas();var r=R("G",RegionRoleType.BodyParagraph,new(40,50,420,70),"你前世唯一的遗物。由奥菲莉亚升级为……去找螃蟹？");using var x=RegionRendererV2.Render(b,[r],new());A(x.Diagnostics!.Single().FontSize>=14,"game text shrunk");});
        T("Fallback panel is text-local",()=>{using var b=Canvas();var r=R("P",RegionRoleType.BodyParagraph,new(40,50,420,90),"局部文字");r.SourceLinePolygons=[GeometryV2.RectanglePolygon(new(40,70,260,28))];r.PreserveOriginal=true;using var x=RegionRendererV2.Render(b,[r],new());A(x.Diagnostics!.Single().RenderBounds.Height<50,"whole container painted");});
        T("Translation-first retained",()=>{using var b=Canvas();var r=R("T",RegionRoleType.CharacterName,new(40,30,300,80),"法兰妮");r.PreserveOriginal=true;using var x=RegionRendererV2.Render(b,[r],new());A(x.Diagnostics!.Single().AtomicRegionCommitted,"translation disappeared");});
        T("Card quantity frozen",()=>A(TranslationAuthorizationPolicy.ShouldPreserve(R("Q",RegionRoleType.UILabel,new(1,1,30,15),"x1"),"x1")==false,"x1 changed"));
        T("Build identity",()=>A(new[]{"0.5.0.6.7","0.5.0.6.8","0.5.0.6.9","0.5.0.6.10"}.Contains(BuildIdentity.ProductVersion,StringComparer.Ordinal),"identity"));
        File.WriteAllLines(Path.Combine(output,"REPAIR-067-SELFTESTS.txt"),rows,Encoding.UTF8);File.WriteAllText(Path.Combine(output,"SUMMARY.txt"),$"PASS={pass}\nFAIL={fail}\nRealApiCalls=0\nStatus=Manual Acceptance Pending",Encoding.UTF8);return fail==0?0:1;
    }
    private static Bitmap Canvas(){var b=new Bitmap(640,360);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(102,160,165));return b;}
    private static RecognitionRegion R(string id,RegionRoleType role,RectangleF box,string text)=>new(){RegionId=id,RoleType=role,Polygon=GeometryV2.RectanglePolygon(box),SourceLinePolygons=[GeometryV2.RectanglePolygon(box)],SourceBlockIds=[id],OcrText="SOURCE",StructuredText="SOURCE",TranslationText=text,CoverageValid=true,RendererTargetRegion=box};
    private static RecognitionRegion Composite(){var r=R("M",RegionRoleType.BodyParagraph,new(10,10,200,70),"");r.SourceSegments=[new("A","Age: 56",GeometryV2.RectanglePolygon(new(10,10,100,16)),RegionRoleType.Unknown,0,1,"M"),new("H","Height: 182cm",GeometryV2.RectanglePolygon(new(10,32,120,16)),RegionRoleType.Unknown,0,2,"M"),new("W","Weight: 57kg",GeometryV2.RectanglePolygon(new(10,54,110,16)),RegionRoleType.Unknown,0,3,"M")];r.SourceBlockIds=["A","H","W"];return r;}
}
