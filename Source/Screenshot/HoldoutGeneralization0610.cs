using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class HoldoutGeneralization0610
{
    private sealed record HoldoutCase(string Id,string Structure,Size Size,IReadOnlyList<RecognitionRegion> Regions);
    internal static int Run(string output)=>RunSet(output,ValidationCasesC(),"Holdout Generalization Set C");
    internal static int RunDiscovery(string output)=>RunSet(output,DiscoveryCases(),"Discovery Holdout Set A - retained failed fixture audit");
    private static int RunSet(string output,IReadOnlyList<HoldoutCase> cases,string setName)
    {
        Directory.CreateDirectory(output);var results=new List<object>();var failures=0;
        foreach(var c in cases)
        {
            using var source=DrawSource(c);using var rendered=RegionRendererV2.Render(source,c.Regions,new RenderSettings());
            var diagnostics=rendered.Diagnostics??[];var metrics=new List<GeneralizationMetrics>();
            foreach(var region in c.Regions)
            {var d=diagnostics.First(x=>x.RegionId==region.RegionId);var others=diagnostics.Where(x=>x.RegionId!=region.RegionId).Select(x=>x.RenderBounds);metrics.Add(GeneralizationMetricEvaluator.Evaluate(region,d,c.Size,others));}
            var pass=metrics.All(x=>!x.TextMissing&&!x.TextClipping&&x.ExclusionLeakage==0&&x.RegionOverlapArea<2&&x.BottomSafetyMargin>=0&&x.WidthPreservationRatio>=.70f&&x.SourceToTranslatedFontScaleRatio>=.35f&&x.HeightInflationRatio<=1.8f&&x.InvalidWhitespaceRatio<=.95f);
            if(!pass)failures++;
            var dir=Path.Combine(output,c.Id);Directory.CreateDirectory(dir);source.Save(Path.Combine(dir,"SOURCE.png"));rendered.Bitmap.Save(Path.Combine(dir,"TRANSLATED.png"));
            File.WriteAllText(Path.Combine(dir,"METRICS.json"),JsonSerializer.Serialize(new{c.Id,c.Structure,Pass=pass,Metrics=metrics},new JsonSerializerOptions{WriteIndented=true}));
            results.Add(new{c.Id,c.Structure,Pass=pass,RegionCount=c.Regions.Count});
        }
        File.WriteAllText(Path.Combine(output,"HOLDOUT-SUMMARY.json"),JsonSerializer.Serialize(new{Set=setName,UsedForTuning=false,Cases=results,Pass=failures==0,FailureCount=failures,RealApiCalls=0,Status="Manual Acceptance Pending"},new JsonSerializerOptions{WriteIndented=true}));
        return failures==0?0:1;
    }

    private static HoldoutCase[] DiscoveryCases()=>
    [
        Case("H01","long pure text",new(900,1400),[(50,60,800,170),(50,270,800,220),(50,540,800,250),(50,850,800,260)]),
        AvatarCase("H02","top avatar then full width body",new(900,1200),false),
        AvatarCase("H03","avatar affects first two paragraphs only",new(1000,1400),true),
        Case("H04","mixed paragraph indents",new(900,1200),[(120,70,700,130),(50,250,780,170),(180,470,640,150),(70,680,760,190)]),
        Case("H05","mixed body widths",new(1100,1300),[(50,60,1000,160),(180,270,720,180),(50,510,920,210),(270,780,760,170)]),
        RoleCase("H06","dialogue and narration",new(900,1300)),
        HeaderCase("H07","header body metadata",new(1000,900)),
        Case("H08","short card",new(760,520),[(40,35,680,70),(310,130,400,140),(310,310,400,70)]),
        Case("H09","tooltip item description",new(700,500),[(35,30,300,60),(35,125,600,170),(420,335,210,50)]),
        Case("H10","multi column irregular",new(1200,900),[(45,80,500,160),(650,50,480,210),(80,330,430,190),(610,360,540,160)]),
        Case("H11","wide aspect",new(1500,650),[(60,50,520,120),(650,50,760,140),(80,260,620,170),(790,290,600,160)]),
        Case("H12","long scrolling page",new(850,2200),Enumerable.Range(0,8).Select(i=>(50,50+i*255,750,180)).ToArray())
    ];

    private static HoldoutCase[] ValidationCases()=>
    [
        Case("B01","long pure text",new(960,1550),[(65,75,820,190),(65,320,820,230),(65,610,820,270),(65,950,820,300)]),
        AvatarCase("B02","top avatar then full width body",new(980,1320),false),
        AvatarCase("B03","avatar affects first two paragraphs only",new(1080,1520),true),
        Case("B04","mixed paragraph indents",new(980,1280),[(145,80,740,145),(65,285,845,185),(205,525,660,165),(90,760,805,205)]),
        Case("B05","mixed body widths",new(1180,1420),[(60,75,1040,175),(215,315,750,195),(70,580,960,225),(295,870,770,190)]),
        RoleCase("B06","dialogue and narration",new(980,1420)),
        HeaderCase("B07","header body metadata",new(1080,980)),
        Case("B08","short card",new(820,590),[(50,45,720,75),(340,150,410,155),(340,355,410,75)]),
        Case("B09","tooltip item description",new(760,560),[(45,40,330,65),(45,145,650,185),(455,390,225,55)]),
        Case("B10","multi column irregular",new(1280,980),[(55,95,530,175),(700,65,500,220),(95,380,450,205),(655,410,570,175)]),
        Case("B11","tall aspect",new(720,1280),[(45,55,630,145),(75,255,570,185),(45,505,630,220),(105,805,520,190)]),
        Case("B12","long scrolling page",new(920,2500),Enumerable.Range(0,9).Select(i=>(65,65+i*265,790,185)).ToArray())
    ];

    private static HoldoutCase[] ValidationCasesC()=>
    [
        Case("C01","long pure text",new(1020,1620),[(75,85,860,205),(75,355,860,245),(75,665,860,285),(75,1020,860,320)]),
        AvatarCase("C02","top avatar then full width body",new(1040,1380),false),
        AvatarCase("C03","avatar affects first two paragraphs only",new(1140,1600),true),
        Case("C04","mixed paragraph indents",new(1040,1350),[(160,90,785,155),(75,315,890,195),(225,570,690,175),(100,820,850,220)]),
        Case("C05","mixed body widths",new(1240,1500),[(70,85,1090,185),(235,345,785,205),(80,625,1010,235),(315,930,800,205)]),
        RoleCase("C06","dialogue and narration",new(1040,1500)),
        HeaderCase("C07","header body metadata",new(1140,1040)),
        ShortCard("C08","short card",new(880,640)),
        Tooltip("C09","tooltip item description",new(810,610)),
        Case("C10","multi column irregular",new(1340,1040),[(65,105,555,185),(735,75,520,230),(105,410,470,215),(685,445,600,185)]),
        Case("C11","square aspect",new(980,980),[(55,60,870,140),(95,260,790,180),(55,510,870,210),(155,790,680,130)]),
        Case("C12","long scrolling page",new(980,2700),Enumerable.Range(0,10).Select(i=>(70,70+i*258,840,180)).ToArray())
    ];

    private static HoldoutCase Case(string id,string structure,Size size,IEnumerable<(int X,int Y,int W,int H)> boxes)=>new(id,structure,size,boxes.Select((b,i)=>Region($"{id}-R{i+1}",new(b.X,b.Y,b.W,b.H),i%3==0?RegionRoleType.Narration:RegionRoleType.BodyParagraph,[])).ToArray());
    private static HoldoutCase AvatarCase(string id,string structure,Size size,bool firstTwo)
    {var avatar=new RectangleF(35,35,150,190);var boxes=firstTwo?new[]{new RectangleF(215,45,720,90),new RectangleF(215,150,720,90),new RectangleF(45,300,890,190),new RectangleF(45,550,890,230)}:new[]{new RectangleF(215,45,620,140),new RectangleF(45,280,790,190),new RectangleF(45,530,790,230)};return new(id,structure,size,boxes.Select((b,i)=>Region($"{id}-R{i+1}",b,RegionRoleType.BodyParagraph,[avatar])).ToArray());}
    private static HoldoutCase RoleCase(string id,string structure,Size size)=>new(id,structure,size,[Region(id+"-N1",new(60,60,780,160),RegionRoleType.Narration,[]),Region(id+"-D1",new(130,280,650,170),RegionRoleType.Dialogue,[]),Region(id+"-N2",new(60,520,780,220),RegionRoleType.Narration,[]),Region(id+"-D2",new(130,810,650,160),RegionRoleType.Dialogue,[])]);
    private static HoldoutCase HeaderCase(string id,string structure,Size size)=>new(id,structure,size,[Region(id+"-H",new(50,45,900,80),RegionRoleType.Header,[]),Region(id+"-B",new(50,175,900,310),RegionRoleType.BodyParagraph,[]),Region(id+"-M",new(50,560,500,65),RegionRoleType.Metadata,[])]);
    private static HoldoutCase ShortCard(string id,string structure,Size size)=>new(id,structure,size,[Region(id+"-H",new(55,50,770,75),RegionRoleType.Header,[]),Region(id+"-B",new(355,165,450,170),RegionRoleType.BodyParagraph,[]),Region(id+"-M",new(355,390,450,70),RegionRoleType.Metadata,[])]);
    private static HoldoutCase Tooltip(string id,string structure,Size size)=>new(id,structure,size,[Region(id+"-L",new(50,45,350,65),RegionRoleType.UILabel,[]),Region(id+"-B",new(50,155,690,200),RegionRoleType.BodyParagraph,[]),Region(id+"-M",new(490,425,235,55),RegionRoleType.Metadata,[])]);
    private static RecognitionRegion Region(string id,RectangleF box,RegionRoleType role,IEnumerable<RectangleF> exclusions)
    {var count=Math.Max(1,(int)Math.Round(box.Height/42));var h=Math.Max(16,Math.Min(32,box.Height/count));var lines=Enumerable.Range(0,count).Select(i=>GeometryV2.RectanglePolygon(new RectangleF(box.Left,box.Top+i*h,box.Width*(i==count-1?.72f:1),h))).ToList();var translation=role switch{RegionRoleType.Header=>"陌生页面标题",RegionRoleType.Metadata=>"日期 · 分类 · 说明",RegionRoleType.UILabel=>"物品说明",_=>"这是一个从未参与规则调参的陌生页面段落，用来验证锚点、宽度、字号和局部避让是否能够保持原有阅读结构。"};return new(){RegionId=id,RoleType=role,Polygon=GeometryV2.RectanglePolygon(box),RendererTargetRegion=box,SourceLinePolygons=lines,SourceBlockIds=lines.Select((_,i)=>id+"-L"+i).ToList(),LayoutExclusionPolygons=exclusions.Select(GeometryV2.RectanglePolygon).ToList(),OcrText="unseen source",StructuredText="unseen source",TranslationText=translation,CoverageValid=true};}
    private static Bitmap DrawSource(HoldoutCase c){var b=new Bitmap(c.Size.Width,c.Size.Height);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(43,45,51));using var brush=new SolidBrush(Color.FromArgb(160,225,225,225));foreach(var r in c.Regions){foreach(var line in r.SourceLinePolygons.Select(GeometryV2.Bounds))g.FillRectangle(brush,line);foreach(var e in r.LayoutExclusionPolygons.Select(GeometryV2.Bounds)){using var imageBrush=new SolidBrush(Color.FromArgb(65,100,150));g.FillRectangle(imageBrush,e);}}return b;}
}
