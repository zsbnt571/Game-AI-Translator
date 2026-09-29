using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static class NativeCardCaptionLayoutSelfTests
{
    internal static void Run(string output, Action<string, Action> test)
    {
        var style = new SourceStyleBundle(Color.FromArgb(29,28,28),Color.Transparent,0,
            Color.Transparent,PointF.Empty,0,255,FontStyle.Regular,SourceStylePolarity.Dark,
            RegionRoleType.Caption,.95f,"SyntheticCaption","");
        static NormalizedOcrLine Line(string id,string text,RectangleF box,int order) =>
            new(id,text,GeometryV2.RectanglePolygon(box),box,.98f,order,"");
        static VisualBlock Block(bool same=false,string first="Strike",string second="43-48 damage.")
        {
            var a=Line("caption",first,new(118,65,104,19),0);
            var b=Line("effect",second,new(118,same?68:95,104,20),1);
            return new(){BlockId="caption",Lines=[a,b],Bounds=RectangleF.Union(a.Bounds,b.Bounds),LayoutBehavior=BlockLayoutBehavior.Flow};
        }
        static Bitmap Image(bool bounded=true,Color? paper=null)
        {
            var image=new Bitmap(420,260);using var g=Graphics.FromImage(image);g.Clear(bounded?Color.FromArgb(35,80,90):Color.FromArgb(220,212,194));
            if(bounded){using var brush=new SolidBrush(paper??Color.FromArgb(220,212,194));g.FillRectangle(brush,78,56,184,145);}return image;
        }
        NativeVisualLayoutPlan Plan(Bitmap image,VisualBlock? b=null,string text="剑击\n造成43-48点伤害。",IReadOnlyList<VisualBlock>? all=null,SourceStyleBundle? st=null)
        {b??=Block();using var g=Graphics.FromImage(image);return NativeVisualLayout.PlanCard(g,image,b,all??[b],text,"Microsoft YaHei UI",FontStyle.Regular,18,"Left",st??style);}
        static void Assert(bool okay,string reason){if(!okay)throw new Exception(reason);}
        test("Paper caption effect retains source preference and distinct title",()=>
        {using var image=Image();var p=Plan(image);Assert(p.Applied&&p.FontSize==18&&p.Lines.Count==2,p.Reason);Assert(p.Lines[0].Text=="剑击"&&p.Lines[1].Text=="造成43-48点伤害。"&&p.Alignment=="SourcePanelCenter","Caption/effect structure lost");});
        test("Paper effect may wrap fully downward without smaller type",()=>
        {using var image=Image();const string value="剑击\n造成43-48点伤害，并且继续保持额外效果。";var p=Plan(image,text:value);Assert(p.Applied&&p.FontSize==18&&p.Lines.Count>2&&string.Concat(p.Lines.Skip(1).Select(x=>x.Text))==value.Split('\n')[1],p.Reason);});
        test("Paper caption does not claim an unbounded light background",()=>
        {using var image=Image(false);Assert(!Plan(image).Applied,"Unbounded background became a card");});
        test("Paper caption rejects dark and saturated card surfaces",()=>
        {foreach(var c in new[]{Color.FromArgb(70,65,58),Color.FromArgb(230,175,50)}){using var image=Image(paper:c);Assert(!Plan(image).Applied,"Unsupported paper polarity/material accepted");}});
        test("Paper caption rejects same baseline labels",()=>
        {using var image=Image();Assert(!Plan(image,Block(true)).Applied,"Same baseline got stacked");});
        test("Paper caption rejects metadata and prose",()=>
        {using var image=Image();foreach(var b in new[]{Block(first:"Age: 19",second:"Weight: 50"),Block(first:"A sentence.",second:"Contains 48 items.")})Assert(!Plan(image,b).Applied,"Metadata/prose entered caption owner");});
        test("Paper caption retains control and dialogue ownership",()=>
        {using var image=Image();var b=Block();b.RoleHint="PossibleControl";Assert(!Plan(image,b).Applied,"Control moved");Assert(!Plan(image,st:style with{Role=RegionRoleType.Dialogue}).Applied,"Dialogue moved");});
        test("Paper caption never guesses a missing accepted hard row",()=>
        {using var image=Image();Assert(!Plan(image,text:"剑击造成43-48点伤害。").Applied,"Missing row was guessed");});
        test("Paper caption overflow preserves text and declines",()=>
        {using var image=Image();var text="剑击\n"+string.Concat(Enumerable.Repeat("造成43-48点伤害。",25));var p=Plan(image,text:text);Assert(!p.Applied&&p.LayoutInput==text,"Overflow consumed or shrank text");});
        test("Paper caption avoids protected neighbor glyphs",()=>
        {using var image=Image();var b=Block();var n=new VisualBlock{BlockId="neighbor",Lines=[Line("n","icon",new(235,94,18,23),0)],Bounds=new(235,94,18,23),LayoutBehavior=BlockLayoutBehavior.Fixed};Assert(!Plan(image,b,all:[b,n]).Applied,"Neighbor entered expanded ink");});
        test("Paper caption antialias barriers block new ink corridor",()=>
        {
            foreach(var offset in new[]{0f,.25f,.5f,.75f})
            {using var image=Image();using(var g=Graphics.FromImage(image)){g.SmoothingMode=SmoothingMode.AntiAlias;using var pen=new Pen(Color.FromArgb(100,100,100),1);g.DrawLine(pen,240+offset,90,240+offset,124);}Assert(!Plan(image).Applied,"AA obstacle accepted at phase "+offset);}
        });
        test("Paper caption source pixels and bounds remain immutable",()=>
        {using var image=Image();var b=Block();var before=Hash(image);var bounds=b.Bounds;_ = Plan(image,b);Assert(Hash(image)==before&&b.Bounds==bounds,"Source was mutated");});
        Actual(output,test);
    }
    private static void Actual(string output,Action<string,Action> test)
    {
        var root=ValidationPaths.LayoutEvidenceRoot;
        var input=Path.Combine(root,@"scoped-fix\R64-review-fix-20260904-180559\inputs\fixed-full37\fixtures\OLD-009");
        var savedRoot=Path.Combine(root,@"visual-native-recovery\C3-QQ-20260905-035147\runs\NATIVE-A4-native-fixed-full37-combo-regression\results\fixtures\OLD-009");
        if(!Directory.Exists(savedRoot))return;
        test("Actual six paper cards retain preferred size and complete hard rows",()=>
        {
            var json=new JsonSerializerOptions{PropertyNameCaseInsensitive=true,WriteIndented=true,Converters={new JsonStringEnumConverter()}};
            using var source=new Bitmap(Path.Combine(input,"SOURCE.png"));using var graphics=Graphics.FromImage(source);
            var ocr=JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(Path.Combine(input,"PRODUCT-OCR-REPLAY.json")),json)!;
            var doc=CorePipelineEngine.Analyze(source.Size,ocr.Blocks.Where(b=>b.Enabled&&b.Polygon.Length>=3).Select((b,i)=>new RawOcrLine(b.Id,b.RawText,b.CorrectedText,b.Polygon,b.BoundingBox,b.Confidence??0,i)).ToArray());
            using var trace=JsonDocument.Parse(File.ReadAllText(Path.Combine(savedRoot,"TEXT-FIT-TRACE.json")));
            using var styles=JsonDocument.Parse(File.ReadAllText(Path.Combine(savedRoot,"SOURCE-STYLE-BUNDLE-TRACE.json")));
            var result=new List<object>();var plans=new List<NativeVisualLayoutPlan>();
            using var preview=new Bitmap(Path.Combine(savedRoot,"06-POST-RESTORATION.png"));using var pg=Graphics.FromImage(preview);
            foreach(var block in doc.VisualBlocks.Where(b=>b.Lines.Count==2))
            {
                var tr=trace.RootElement.EnumerateArray().Single(x=>x.GetProperty("BlockId").GetString()==block.BlockId);
                var st=styles.RootElement.GetProperty("Blocks").EnumerateArray().Single(x=>x.GetProperty("BlockId").GetString()==block.BlockId);
                var style=new SourceStyleBundle(Color.FromArgb(unchecked((int)Convert.ToUInt32(st.GetProperty("SourceFill").GetString()![1..],16))),Color.Transparent,0,Color.Transparent,PointF.Empty,0,255,(FontStyle)st.GetProperty("Weight").GetInt32(),(SourceStylePolarity)st.GetProperty("Polarity").GetInt32(),(RegionRoleType)st.GetProperty("Role").GetInt32(),st.GetProperty("Confidence").GetSingle(),st.GetProperty("Owner").GetString()!,"");
                var text=tr.GetProperty("AcceptedMapping").GetString()!;var preferred=tr.GetProperty("PreferredFontSize").GetSingle();
                var plan=NativeVisualLayout.PlanCard(graphics,source,block,doc.VisualBlocks,text,"Microsoft YaHei UI",style.Weight,preferred,tr.GetProperty("Alignment").GetString()!,style);
                result.Add(new{block.BlockId,block.SourceText,Accepted=text,Style=style,Plan=plan});
                if(plan.Applied)
                {
                    plans.Add(plan);using var font=FontManager.CreatePixel("Microsoft YaHei UI",plan.FontSize,style.Weight);using var brush=new SolidBrush(style.FillColor);
                    foreach(var line in plan.Lines){using var path=new GraphicsPath();path.AddString(line.Text,font.FontFamily,(int)font.Style,font.Size,line.Bounds.Location,StringFormat.GenericTypographic);pg.FillPath(brush,path);}
                    if(plan.FontSize!=preferred||plan.Lines[0].Text!=text.Split('\n')[0]||string.Concat(plan.Lines.Skip(1).Select(l=>l.Text))!=text.Split('\n')[1])throw new Exception("Literal hard row/preference changed");
                }
            }
            File.WriteAllText(Path.Combine(output,"CAPTION-OLD-009-PLANS.json"),JsonSerializer.Serialize(result,json));
            preview.Save(Path.Combine(output,"CAPTION-OLD-009-OWNER-PREVIEW.png"),ImageFormat.Png);
            if(plans.Count!=6)throw new Exception("Expected six actual card captions, applied="+plans.Count);
        });
    }
    private static string Hash(Bitmap image){using var stream=new MemoryStream();image.Save(stream,ImageFormat.Png);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray()));}
}
