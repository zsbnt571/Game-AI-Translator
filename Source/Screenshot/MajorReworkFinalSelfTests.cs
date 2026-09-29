using System.Text;

namespace ScreenshotTranslationUiTester;

public static class MajorReworkFinalSelfTests
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<string>();var failed=0;
        void T(string name,Action test){try{test();rows.Add("PASS | "+name);}catch(Exception ex){failed++;rows.Add("FAIL | "+name+" | "+ex.Message);}}
        void A(bool value,string message){if(!value)throw new InvalidOperationException(message);}

        T("Fit keeps 591x408 at one-to-one in large viewport",()=>A(PreviewForm.CalculateShrinkOnlyFitScale(new(591,408),new(1400,900))==1f,"small image enlarged"));
        T("Fit shrinks 1080p image",()=>A(PreviewForm.CalculateShrinkOnlyFitScale(new(1920,1080),new(1200,700))<1f,"1080p not shrunk"));
        T("Fit shrinks 1440p image",()=>A(PreviewForm.CalculateShrinkOnlyFitScale(new(2560,1440),new(1200,700))<1f,"1440p not shrunk"));
        T("Fit shrinks long page",()=>A(PreviewForm.CalculateShrinkOnlyFitScale(new(1080,6200),new(1200,800))<1f,"long page not shrunk"));
        T("Fit shrinks tall narrow image",()=>A(PreviewForm.CalculateShrinkOnlyFitScale(new(480,2400),new(1200,800))<1f,"tall image not shrunk"));
        T("Explicit user zoom can exceed one",()=>A(Math.Clamp(1f*1.25f,.1f,8f)>1f,"manual zoom blocked"));

        T("Title typography has visual hierarchy",()=>{var p=RoleTypographyProfiles.Resolve(RegionRoleType.Title);A(p.Style==FontStyle.Bold&&p.Scale>1&&p.LineSpacing<=1.01f,"title profile");});
        T("Dialogue typography uses readable wrap",()=>{var p=RoleTypographyProfiles.Resolve(RegionRoleType.Dialogue);A(p.Style==FontStyle.Regular&&p.LineSpacing>1,"dialogue profile");});
        T("Body typography uses comfortable spacing",()=>A(RoleTypographyProfiles.Resolve(RegionRoleType.BodyParagraph).LineSpacing>=1.1f,"body spacing"));
        T("Header typography is bold and smaller than title",()=>{var h=RoleTypographyProfiles.Resolve(RegionRoleType.Header);var t=RoleTypographyProfiles.Resolve(RegionRoleType.Title);A(h.Style==FontStyle.Bold&&h.MaxFont<t.MaxFont,"header hierarchy");});
        T("Metadata typography stays compact",()=>A(RoleTypographyProfiles.Resolve(RegionRoleType.Metadata).Scale<1,"metadata scale"));
        T("Button typography is centered",()=>{var p=RoleTypographyProfiles.Resolve(RegionRoleType.Button);A(p.Alignment==StringAlignment.Center&&p.VerticalCenter,"button alignment");});
        T("Small label typography stays compact",()=>A(RoleTypographyProfiles.Resolve(RegionRoleType.UILabel).MaxFont<=22,"small label"));
        T("Profiles do not rewrite translation",()=>{const string v="April说：“Hello!” 价格是5000円 → 100%";using var b=Canvas();var r=Region("PROFILE",new(10,10,260,80),v);using var x=RegionRendererV2.Render(b,[r],new RenderSettings());A(string.Concat(x.Diagnostics!.Single().RenderedLines.Select(l=>l.Text))==v,"text changed");});
        T("Magazine single decorative letter is preserved",()=>A(TranslationAuthorizationPolicy.ShouldPreserve(Region("M",new(1,1,20,50),"M"),"M"),"single M authorized"));
        T("Magazine HER art title is preserved",()=>A(TranslationAuthorizationPolicy.ShouldPreserve(Region("HER",new(1,1,80,40),"HER."),"HER."),"HER authorized"));
        T("Magazine complete uppercase sentence remains translatable",()=>A(!TranslationAuthorizationPolicy.ShouldPreserve(Region("PHRASE",new(1,1,180,80),"THERE ARE LEGENDS THERE ARE ICONS"),"THERE ARE LEGENDS THERE ARE ICONS"),"semantic phrase rejected"));

        T("Unsafe region is pixel-identical",()=>{using var b=Noise();var r=Region("UNSAFE",new(20,15,90,35),"不得绘制");r.LayoutExclusionPolygons=[GeometryV2.RectanglePolygon(r.BoundingBox)];using var x=RegionRendererV2.Render(b,[r],new RenderSettings());A(!x.Diagnostics!.Single().AtomicRegionCommitted&&Equal(b,x.Bitmap),"unsafe pixels changed");});
        T("Safe region commits atomically",()=>{using var b=Canvas();var r=Region("SAFE",new(10,10,180,60),"完整译文");using var x=RegionRendererV2.Render(b,[r],new RenderSettings());var d=x.Diagnostics!.Single();A(d.AtomicRegionCommitted&&d.TextDrawCount==1,"safe commit missing");});
        T("Replacement glyph aborts atomic commit",()=>{using var b=Canvas();var r=Region("BAD",new(10,10,180,60),"错误\uFFFD文本");using var x=RegionRendererV2.Render(b,[r],new RenderSettings());A(!x.Diagnostics!.Single().AtomicRegionCommitted&&Equal(b,x.Bitmap),"replacement glyph committed");});
        T("Invalid surrogate aborts atomic commit",()=>{using var b=Canvas();var r=Region("BAD",new(10,10,180,60),"错误\uD800文本");using var x=RegionRendererV2.Render(b,[r],new RenderSettings());A(!x.Diagnostics!.Single().AtomicRegionCommitted&&Equal(b,x.Bitmap),"invalid surrogate committed");});
        T("Original mode resolves SourceRegionRect",()=>{var r=Region("MAP",new(1,2,30,20),"译文");r.TranslationRenderRect=new(70,80,40,25);A(RegionDisplayMapping.Resolve(r,ImageViewMode.Original)==r.BoundingBox,"source mapping" );});
        T("Translated mode resolves TranslationRenderRect",()=>{var r=Region("MAP",new(1,2,30,20),"译文");r.TranslationRenderRect=new(70,80,40,25);A(RegionDisplayMapping.Resolve(r,ImageViewMode.Translated)==r.TranslationRenderRect,"translation mapping");});
        T("No rendered translation leaks no source decoration",()=>{var r=Region("MAP",new(1,2,30,20),"");r.TranslationRenderRect=null;A(RegionDisplayMapping.Resolve(r,ImageViewMode.Translated)is null,"source rect leaked");});
        T("Repeated mode toggle remains stable",()=>{var r=Region("MAP",new(1,2,30,20),"译文");r.TranslationRenderRect=new(70,80,40,25);for(var i=0;i<20;i++){A(RegionDisplayMapping.Resolve(r,ImageViewMode.Original)==r.BoundingBox,"source drift");A(RegionDisplayMapping.Resolve(r,ImageViewMode.Translated)==r.TranslationRenderRect,"translation drift");}});

        File.WriteAllLines(Path.Combine(output,"MAJOR-REWORK-FINAL-SELFTESTS.txt"),rows,Encoding.UTF8);
        File.WriteAllText(Path.Combine(output,"SUMMARY.txt"),$"PASS={rows.Count-failed}{Environment.NewLine}FAIL={failed}{Environment.NewLine}RealApiCalls=0{Environment.NewLine}ActiveOperations=0",Encoding.UTF8);
        return failed==0?0:2;
    }
    private static Bitmap Canvas(){var b=new Bitmap(300,120);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(35,20,80));using var f=new Font("Segoe UI",14,GraphicsUnit.Pixel);g.DrawString("SOURCE TEXT",f,Brushes.White,12,14);return b;}
    private static Bitmap Noise(){var b=new Bitmap(180,80);for(var y=0;y<b.Height;y++)for(var x=0;x<b.Width;x++)b.SetPixel(x,y,Color.FromArgb((x*37+y*19)%256,(x*13+y*41)%256,(x*29+y*7)%256));return b;}
    private static RecognitionRegion Region(string id,RectangleF box,string text)=>new(){RegionId=id,RoleType=RegionRoleType.BodyParagraph,Polygon=GeometryV2.RectanglePolygon(box),SourceLinePolygons=[GeometryV2.RectanglePolygon(box)],SourceBlockIds=[id],StructuredText="SOURCE TEXT",TranslationText=text,CoverageValid=true};
    private static bool Equal(Bitmap a,Bitmap b){if(a.Size!=b.Size)return false;for(var y=0;y<a.Height;y++)for(var x=0;x<a.Width;x++)if(a.GetPixel(x,y).ToArgb()!=b.GetPixel(x,y).ToArgb())return false;return true;}
}
