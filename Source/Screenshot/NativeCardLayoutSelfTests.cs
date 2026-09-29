using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static class NativeCardLayoutSelfTests
{
    internal static void Run(string output,Action<string,Action> test)
    {
        Synthetic(test);
        NativeCardCaptionLayoutSelfTests.Run(output, test);
        ActualScopeBoundaries(output, test);
        var baseRoot=ValidationPaths.LayoutEvidenceRoot;
        var fixedRoot=Path.Combine(baseRoot,@"scoped-fix\R64-review-fix-20260904-180559\inputs\fixed-full37\fixtures");
        var traceRoot=Path.Combine(baseRoot,@"visual-native-recovery\C3-QQ-20260905-035147\runs\NATIVE-A1-native-fixed-priority8-combined\results\fixtures");
        if(!Directory.Exists(traceRoot))return;
        var options=new JsonSerializerOptions{WriteIndented=true,PropertyNameCaseInsensitive=true};
        options.Converters.Add(new JsonStringEnumConverter());
        foreach(var id in new[]{"OLD-016","OLD-017","OLD-018"})
        test("Actual source card body and field geometry "+id,()=>
        {
            using var source=new Bitmap(Path.Combine(fixedRoot,id,"SOURCE.png"));
            using var graphics=Graphics.FromImage(source);
            var ocr=JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(Path.Combine(fixedRoot,id,"PRODUCT-OCR-REPLAY.json")),options)!;
            var document=CorePipelineEngine.Analyze(source.Size,ocr.Blocks.Where(x=>x.Enabled&&x.Polygon.Length>=3).Select((x,i)=>new RawOcrLine(x.Id,x.RawText,x.CorrectedText,x.Polygon,x.BoundingBox,x.Confidence??0,i)).ToArray());
            using var trace=JsonDocument.Parse(File.ReadAllText(Path.Combine(traceRoot,id,"TEXT-FIT-TRACE.json")));
            using var styles=JsonDocument.Parse(File.ReadAllText(Path.Combine(traceRoot,id,"SOURCE-STYLE-BUNDLE-TRACE.json")));
            var result=new List<object>();var applied=0;var body=false;
            using var preview=new Bitmap(Path.Combine(traceRoot,id,"06-POST-RESTORATION.png"));
            using var pg=Graphics.FromImage(preview);
            foreach(var block in document.VisualBlocks)
            {
                var saved=trace.RootElement.EnumerateArray().FirstOrDefault(x=>x.GetProperty("BlockId").GetString()==block.BlockId);
                if(saved.ValueKind==JsonValueKind.Undefined)continue;
                var styleSaved=styles.RootElement.GetProperty("Blocks").EnumerateArray().FirstOrDefault(x=>x.GetProperty("BlockId").GetString()==block.BlockId);
                if(styleSaved.ValueKind==JsonValueKind.Undefined)continue;
                var style=new SourceStyleBundle(C(styleSaved,"SourceFill"),C(styleSaved,"SourceOutline"),styleSaved.GetProperty("OutlineWidth").GetSingle(),
                    Color.Transparent,PointF.Empty,0,255,(FontStyle)styleSaved.GetProperty("Weight").GetInt32(),
                    (SourceStylePolarity)styleSaved.GetProperty("Polarity").GetInt32(),(RegionRoleType)styleSaved.GetProperty("Role").GetInt32(),
                    styleSaved.GetProperty("Confidence").GetSingle(),styleSaved.GetProperty("Owner").GetString()!,styleSaved.GetProperty("Evidence").GetString()!);
                var text=saved.GetProperty("AcceptedMapping").GetString()!;var preferred=saved.GetProperty("PreferredFontSize").GetSingle();
                var plan=NativeVisualLayout.PlanCard(graphics,source,block,document.VisualBlocks,text,"Microsoft YaHei UI",style.Weight,preferred,saved.GetProperty("Alignment").GetString()!,style);
                var bg=SourceStyleBundleOwner.EstimateSourceBackground(source,block.Lines.Select(x=>x.Polygon).ToArray(),block.Bounds);
                var guarded=SourceStyleLegibilityGuard.FinalizeForFontSize(SourceStyleLegibilityGuard.Evaluate(style,bg),bg,plan.FontSize);
                var support=NativeVisualTypography.RefineBodySupport(source,block,guarded,plan.FontSize);
                result.Add(new{block.BlockId,block.SourceText,Accepted=text,Style=style,Plan=plan,Support=support});
                if(!plan.Applied)continue;
                applied++;body|=plan.Kind=="CardBody";
                if(!plan.CharactersPreserved||!plan.SourceGeometryUnchanged)throw new Exception("Source/content contract changed");
                if(plan.Kind=="IndependentFields" && plan.Lines.Count!=block.Lines.Count)throw new Exception("Field rows merged");
                if(plan.Kind=="CardBody" && plan.Alignment!=saved.GetProperty("Alignment").GetString())throw new Exception("Source body alignment changed");
                using var font=FontManager.CreatePixel("Microsoft YaHei UI",plan.FontSize,style.Weight);
                var effective=support.Decision.Effective;
                foreach(var line in plan.Lines)NativeVisualTypography.DrawSupportedBody(pg,line.Text,font,line.Bounds.Location,support);
                VerifyActualInkSeparation(source.Size,plan,font,support);
                if(plan.Kind=="CardBody")
                {
                    foreach(var control in new[]{style with {Owner="UserTextColorOverride"},style with{OutlineColor=Color.Black},
                        style with{Polarity=SourceStylePolarity.Dark,FillColor=Color.Black},style with{Confidence=.3f}})
                        if(NativeVisualTypography.RefineBodySupport(source,block,guarded with{Source=control},plan.FontSize).Applied)
                            throw new Exception("Protected source style was overridden");
                }
            }
            preview.Save(Path.Combine(output,"CARD-"+id+"-OWNER-PREVIEW.png"),ImageFormat.Png);
            File.WriteAllText(Path.Combine(output,"CARD-"+id+"-PLAN.json"),JsonSerializer.Serialize(result,options));
            if(!body)throw new Exception("Real body not improved; applied="+applied);
        });
    }
    private static void ActualScopeBoundaries(string output,Action<string,Action> test)
    {
        var root=ValidationPaths.LayoutEvidenceRoot;
        var run=Path.Combine(root,@"visual-native-recovery\C3-QQ-20260905-035147\runs\NATIVE-A4-native-fixed-full37-combo-regression\results\fixtures");
        if(!Directory.Exists(run))return;
        foreach(var id in new[]{"NEW-018","NEW-019","NEW-007","NEW-011","OLD-004"})
        test("Actual paragraph scope preserves dialogue/leading rows "+id,()=>
        {
            var json=new JsonSerializerOptions{PropertyNameCaseInsensitive=true,WriteIndented=true,Converters={new JsonStringEnumConverter()}};
            var input=Path.Combine(root,@"scoped-fix\R64-review-fix-20260904-180559\inputs\fixed-full37\fixtures",id);
            using var source=new Bitmap(Path.Combine(input,"SOURCE.png"));using var g=Graphics.FromImage(source);
            var ocr=JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(Path.Combine(input,"PRODUCT-OCR-REPLAY.json")),json)!;
            var doc=CorePipelineEngine.Analyze(source.Size,ocr.Blocks.Where(b=>b.Enabled&&b.Polygon.Length>=3).Select((b,i)=>new RawOcrLine(b.Id,b.RawText,b.CorrectedText,b.Polygon,b.BoundingBox,b.Confidence??0,i)).ToArray());
            using var trace=JsonDocument.Parse(File.ReadAllText(Path.Combine(run,id,"TEXT-FIT-TRACE.json")));
            using var styles=JsonDocument.Parse(File.ReadAllText(Path.Combine(run,id,"SOURCE-STYLE-BUNDLE-TRACE.json")));
            var rows=new List<object>();var retained=0;var protectedRows=0;var lowerParagraphRetained=false;
            foreach(var block in doc.VisualBlocks.Where(b=>b.Lines.Count>=3))
            {
                var tr=trace.RootElement.EnumerateArray().FirstOrDefault(x=>x.GetProperty("BlockId").GetString()==block.BlockId);
                var st=styles.RootElement.GetProperty("Blocks").EnumerateArray().FirstOrDefault(x=>x.GetProperty("BlockId").GetString()==block.BlockId);
                if(tr.ValueKind==JsonValueKind.Undefined||st.ValueKind==JsonValueKind.Undefined)continue;
                var style=new SourceStyleBundle(C(st,"SourceFill"),C(st,"SourceOutline"),st.GetProperty("OutlineWidth").GetSingle(),Color.Transparent,PointF.Empty,0,255,(FontStyle)st.GetProperty("Weight").GetInt32(),(SourceStylePolarity)st.GetProperty("Polarity").GetInt32(),(RegionRoleType)st.GetProperty("Role").GetInt32(),st.GetProperty("Confidence").GetSingle(),st.GetProperty("Owner").GetString()!,"");
                var text=tr.GetProperty("AcceptedMapping").GetString()!;
                var plan=NativeVisualLayout.PlanCard(g,source,block,doc.VisualBlocks,text,"Microsoft YaHei UI",style.Weight,tr.GetProperty("PreferredFontSize").GetSingle(),tr.GetProperty("Alignment").GetString()!,style);
                rows.Add(new{block.BlockId,block.SourceText,Accepted=text,Plan=plan});
                if(plan.Applied)retained++;
                if(id=="OLD-004" && block.BlockId=="BLK-FA5B717C3EF0")
                {
                    lowerParagraphRetained=plan.Applied && plan.Kind=="CardBody" && plan.VerticalAnchor=="SourceTop" &&
                        plan.FontSize>=tr.GetProperty("PreferredFontSize").GetSingle() && plan.FontSize<=tr.GetProperty("PreferredFontSize").GetSingle()*1.35f && plan.CharactersPreserved &&
                        plan.SourceGeometryUnchanged && Math.Abs(plan.SafeLineRects[0].Top-block.Bounds.Top-1)<.1f;
                }
                if(plan.Reason is "DISTINCT_LEADING_ROW_RETAINS_HARD_BOUNDARY")
                {protectedRows++;if(plan.Applied||plan.LayoutInput!=text)throw new Exception("Protected hard-row group changed");}
            }
            File.WriteAllText(Path.Combine(output,"SCOPE-"+id+".json"),JsonSerializer.Serialize(rows,json));
            if(id is "NEW-018" or "NEW-019"){if(protectedRows!=1)throw new Exception("Actual lower dialogue not excluded");}
            if(id=="NEW-007"&&retained<1)throw new Exception("Ordinary biography lost card owner");
            if(id=="NEW-011"&&(protectedRows!=2||retained<1))throw new Exception($"Expected two author rows protected and plain body retained, protected={protectedRows},retained={retained}");
            if(id=="OLD-004"&&!lowerParagraphRetained)throw new Exception("Ordinary lower-page paragraph lost its source-top anchor");
        });
    }
    private static void Synthetic(Action<string,Action> test)
    {
        const string text="这段文字应保持完整的内容与原来的对齐关系。\n分行只用于页面排版，不应该挤成又小又窄的文字列。\n图像中的人物与其他控件也必须保持原样。";
        static NormalizedOcrLine Line(string id,string text,RectangleF bounds,int order)=>new(id,text,GeometryV2.RectanglePolygon(bounds),bounds,.98f,order,"");
        static VisualBlock Body(bool fields=false,bool sameBaseline=false,bool sourceGap=false)
        {
            var lines=Enumerable.Range(0,fields?3:4).Select(i=>Line("s"+i,fields?new[]{"Age: 19","Height: 180","Weight: 50"}[i]:"A normal source sentence for this paragraph.",
                new RectangleF(80+(fields?i*6:0),70+(sameBaseline?i*2:i*32)+(sourceGap&&i==3?35:0),fields?220:360,26),i)).ToArray();
            return new(){BlockId="synthetic",Lines=lines,Bounds=lines.Select(x=>x.Bounds).Aggregate(RectangleF.Union),LayoutBehavior=BlockLayoutBehavior.Flow};
        }
        static Bitmap Image(Color color){var b=new Bitmap(600,330);using var g=Graphics.FromImage(b);g.Clear(color);return b;}
        var style=new SourceStyleBundle(Color.White,Color.FromArgb(232,207,208),2,Color.Transparent,PointF.Empty,0,255,FontStyle.Bold,SourceStylePolarity.Light,RegionRoleType.BodyParagraph,.9f,"SyntheticSource","");
        NativeVisualLayoutPlan Plan(Bitmap image,VisualBlock block,string accepted=text,string alignment="Center",IReadOnlyList<VisualBlock>? all=null)
        {using var g=Graphics.FromImage(image);return NativeVisualLayout.PlanCard(g,image,block,all??[block],accepted,"Microsoft YaHei UI",FontStyle.Bold,20,alignment,style);}
        static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
        test("Card body keeps centered anchor and complete Unicode",()=>
        {
            using var image=Image(Color.FromArgb(230,187,187));var b=Body();var p=Plan(image,b,text+"𠮷");
            Assert(p.Applied&&p.CharactersPreserved&&p.FontSize==20,"Complete readable centered plan failed");
            Assert(p.Lines.All(l=>Math.Abs((l.Bounds.Left+l.Bounds.Right)/2-(b.Bounds.Left+b.Bounds.Right)/2)<.1),"Center moved");
        });
        test("Card left body keeps first source anchor",()=>
        {
            using var image=Image(Color.FromArgb(230,187,187));var b=Body();var p=Plan(image,b,text,"Left");
            Assert(p.Applied&&p.Lines.All(l=>l.Bounds.Left>=b.Bounds.Left&&l.Bounds.Left<=b.Bounds.Left+3),"Left anchor drifted");
            Assert(p.VerticalAnchor=="SourceTop","Body silently vertically centered");
        });
        test("Ordinary wide paragraphs retain the same owner at upper and lower positions",()=>
        {
            foreach(var top in new[]{70f,700f,850f})
            {
                using var image=new Bitmap(600,1000);using(var g=Graphics.FromImage(image))g.Clear(Color.FromArgb(230,187,187));
                var lines=Enumerable.Range(0,4).Select(i=>Line("p"+i,"A normal full-width source sentence for this paragraph.",new(50,top+i*32,480,26),i)).ToArray();
                var b=new VisualBlock{BlockId="position-control",Lines=lines,Bounds=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union),LayoutBehavior=BlockLayoutBehavior.Flow};
                var p=Plan(image,b,text,"Left");
                Assert(p.Applied&&p.VerticalAnchor=="SourceTop"&&p.FontSize>=20&&p.FontSize<=27&&p.CharactersPreserved&&Math.Abs(p.SafeLineRects[0].Top-top-1)<.1f,"Page position changed paragraph ownership: "+top);
                Assert(Math.Abs(p.SafeLineRects[0].Top-top-1)<.1f,"Source first-line anchor drifted: "+top);
            }
        });
        test("Distinct speaker row stays protected regardless of upper or lower position",()=>
        {
            foreach(var top in new[]{70f,850f})
            {
                using var image=new Bitmap(600,1000);using(var g=Graphics.FromImage(image))g.Clear(Color.FromArgb(230,187,187));
                var lines=Enumerable.Range(0,4).Select(i=>Line("speaker"+i,i==0?"Speaker":"A normal full-width source sentence for this paragraph.",new(50,top+i*32,i==0?120:480,26),i)).ToArray();
                var b=new VisualBlock{BlockId="speaker-control",Lines=lines,Bounds=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union),LayoutBehavior=BlockLayoutBehavior.Flow};
                const string accepted="说话人\n这是必须保留独立首行的完整正文。";
                var p=Plan(image,b,accepted,"Left");
                Assert(!p.Applied&&p.Reason=="DISTINCT_LEADING_ROW_RETAINS_HARD_BOUNDARY"&&p.LayoutInput==accepted,"Speaker hard boundary changed at "+top);
            }
        });
        test("Explicit blank paragraphs remain hard boundaries",()=>
        {
            using var image=Image(Color.FromArgb(230,187,187));var p=Plan(image,Body(),"第一段保持原样。\n\n第二段仍然独立。");
            Assert(p.Applied&&p.LayoutInput.Contains("\n\n"),"Explicit paragraph break collapsed");
        });
        test("Source paragraph gaps require explicit mapping",()=>
        {using var image=Image(Color.FromArgb(230,187,187));Assert(!Plan(image,Body(sourceGap:true)).Applied,"Distinct source paragraphs merged");});
        test("Same baseline colon controls keep their owner",()=>
        {using var image=Image(Color.FromArgb(230,187,187));Assert(!Plan(image,Body(fields:true,sameBaseline:true),"年龄：19\n身高：180\n体重：50").Applied,"Horizontal controls became stacked fields");});
        test("Missing translated field row cannot change structure",()=>
        {using var image=Image(Color.FromArgb(230,187,187));Assert(!Plan(image,Body(fields:true),"年龄：19\n体重：50").Applied,"Missing field mapping accepted");});
        test("Title and button keep their original owners",()=>
        {using var image=Image(Color.FromArgb(230,187,187));foreach(var role in new[]{"PossibleTitle","PossibleControl"}){var b=Body();b.RoleHint=role;Assert(!Plan(image,b).Candidate,"Control/title changed owner");}});
        test("Card planner cannot cross an illustration barrier",()=>
        {
            using var image=Image(Color.FromArgb(230,187,187));var b=Body();using(var g=Graphics.FromImage(image))g.FillRectangle(Brushes.Black,258,60,5,160);
            Assert(!Plan(image,b).Applied,"Illustration crossed by centered lines");
        });
        test("Card planner protects neighboring source text",()=>
        {
            using var image=Image(Color.FromArgb(230,187,187));var b=Body();var nb=new VisualBlock{BlockId="neighbor",Lines=[Line("other","Other",new(220,68,70,150),0)],Bounds=new(220,68,70,150),LayoutBehavior=BlockLayoutBehavior.Fixed};
            Assert(!Plan(image,b,all:[b,nb]).Applied,"Neighbor text was covered");
        });
        test("Card overflow cannot shrink below readability floor",()=>
        {using var image=Image(Color.FromArgb(230,187,187));var huge=string.Concat(Enumerable.Repeat(text,20));var p=Plan(image,Body(),huge);Assert(!p.Applied&&p.LayoutInput==huge,"Overflow clipped or consumed accepted text");});
        test("Low contrast unsupported white source keeps legibility guard",()=>
        {
            using var image=Image(Color.FromArgb(248,248,248));var b=Body();using(var g=Graphics.FromImage(image))using(var font=FontManager.CreatePixel("Arial",22,FontStyle.Bold))foreach(var l in b.Lines)g.DrawString(l.SourceText,font,Brushes.White,l.Bounds.Location);
            var guard=SourceStyleLegibilityGuard.Evaluate(style,Color.FromArgb(248,248,248));
            Assert(!NativeVisualTypography.RefineBodySupport(image,b,guard,20).Applied,"Very low contrast source support removed");
        });
    }
    private static void VerifyActualInkSeparation(Size size,NativeVisualLayoutPlan plan,Font font,NativeBodySupportDecision support)
    {
        var claimed=new HashSet<Point>();
        foreach(var line in plan.Lines)
        {
            using var layer=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(layer))NativeVisualTypography.DrawSupportedBody(g,line.Text,font,line.Bounds.Location,support);
            var bounds=Rectangle.Intersect(Rectangle.Ceiling(RectangleF.Inflate(line.Bounds,4,4)),new Rectangle(Point.Empty,size));
            for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
                if(layer.GetPixel(x,y).A>0 && !claimed.Add(new(x,y)))throw new Exception("Actual adjacent line glyphs overlap at "+x+","+y);
        }
    }
    private static Color C(JsonElement element,string key)=>Color.FromArgb(unchecked((int)Convert.ToUInt32(element.GetProperty(key).GetString()![1..],16)));
}
