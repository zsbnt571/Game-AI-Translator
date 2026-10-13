using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class BlindHoldout0611
{
    private sealed record Case(string Id,Bitmap Source,List<RecognitionRegion> Regions);
    internal static int Run(string output,string set="A")
    {
        Directory.CreateDirectory(output);var cases=set=="D"?CreateCasesD():set=="C"?CreateCasesC():set=="B"?CreateCasesB():CreateCases();var results=new List<object>();var panels=new List<(Bitmap Source,Bitmap Translated,string Id)>();var failed=0;
        try
        {
            foreach(var c in cases)
            {
                var dir=Path.Combine(output,c.Id);Directory.CreateDirectory(dir);c.Source.Save(Path.Combine(dir,"ORIGINAL.png"));
                using var rendered=RegionRendererV2.Render(c.Source,c.Regions,new RenderSettings{MinFontSize=9,FontFamily="Microsoft YaHei UI"});
                rendered.Bitmap.Save(Path.Combine(dir,"TRANSLATED.png"));var diagnostics=rendered.Diagnostics??[];
                using(var maskImage=new Bitmap(c.Source.Width,c.Source.Height)){using(var mg=Graphics.FromImage(maskImage))mg.Clear(Color.Black);foreach(var region in c.Regions){var plan=BackgroundIntegrationPlanner.Plan(c.Source,region,region.BoundingBox,c.Regions);for(var y=plan.CleanupBounds.Top;y<plan.CleanupBounds.Bottom;y++)for(var x=plan.CleanupBounds.Left;x<plan.CleanupBounds.Right;x++)if(plan.CleanupMask[x,y])maskImage.SetPixel(x,y,Color.White);}maskImage.Save(Path.Combine(dir,"SOURCE-GLYPH-MASK.png"));}
                File.WriteAllText(Path.Combine(dir,"RENDER-DIAGNOSTICS.json"),JsonSerializer.Serialize(diagnostics,new JsonSerializerOptions{WriteIndented=true}));
                var pass=diagnostics.Count==c.Regions.Count&&diagnostics.All(x=>x.RenderStatus!=RegionRenderStatus.Failed&&x.TextDrawCount==1);
                if(!pass)failed++;var styles=VisualStyleGroupPlanner.Build(c.Regions,new());StableSourceAudit.WriteStyleAudit(Path.Combine(dir,"VISUAL-STYLE-GROUP-AUDIT.json"),styles);
                results.Add(new{c.Id,Pass=pass,RegionCount=c.Regions.Count,Rendered=diagnostics.Count(x=>x.RenderStatus==RegionRenderStatus.Rendered),Failed=diagnostics.Count(x=>x.RenderStatus==RegionRenderStatus.Failed),Warnings=rendered.Warnings.Select(x=>x.Message)});
                panels.Add((new Bitmap(c.Source),new Bitmap(rendered.Bitmap),c.Id));
            }
            using var sheet=ContactSheet(panels);sheet.Save(Path.Combine(output,$"BLIND-HOLDOUT-{set}-CONTACT-SHEET.png"));
            File.WriteAllText(Path.Combine(output,$"BLIND-HOLDOUT-{set}.json"),JsonSerializer.Serialize(new{Set=set,FirstRun=true,UsedForTuning=false,Pass=failed==0,Cases=results,RealApiCalls=0,Status="Manual Acceptance Pending"},new JsonSerializerOptions{WriteIndented=true}));
            return failed==0?0:1;
        }
        finally{foreach(var c in cases)c.Source.Dispose();foreach(var p in panels){p.Source.Dispose();p.Translated.Dispose();}}
    }

    private static List<Case> CreateCases()=>
    [
        Page("A01-avatar-long",true,false),Page("A02-independent-indents",false,false),Page("A03-two-column",false,true),
        Choices(),Dialogue(),Tooltip()
    ];
    private static List<Case> CreateCasesB()=>
    [
        Page("B01-portrait-article",true,true),Page("B02-staggered-notes",false,false),
        Simple("B03-dialogue-panel",Color.FromArgb(36,65,88),new(45,300,630,135),"Navigator: Keep the lantern covered until sunrise.","领航员：日出之前把灯遮好。",RegionRoleType.Dialogue),
        Simple("B04-inventory-tooltip",Color.FromArgb(72,54,30),new(315,80,335,155),"Tide Key\nOpens a door below the western pier.","潮汐钥匙\n可以打开西侧码头下方的门。",RegionRoleType.UILabel),
        ChoicesB(),Page("B06-asymmetric-columns",false,true)
    ];
    private static List<Case> CreateCasesC()=>
    [
        PageC(),
        Simple("C02-radio-log",Color.FromArgb(34,54,70),new(55,285,610,150),"Quartermaster: The northern lift is offline until morning.","军需官：北侧升降机停运至明早。",RegionRoleType.Dialogue),
        Simple("C03-relic-tooltip",Color.FromArgb(78,55,32),new(300,95,350,145),"Glass Sigil\nWarm when the hidden passage is near.","玻璃徽记\n靠近暗道时会发热。",RegionRoleType.UILabel),
        ChoicesC()
    ];
    private static List<Case> CreateCasesD()=>
    [
        PageD("D01-unseen-rail-journal",false,new[]{new RectangleF(48,112,620,52),new RectangleF(72,204,566,64),new RectangleF(44,318,625,56)},new[]{"The midnight rail stopped beneath a station omitted from every map.","Only one passenger stepped down, carrying a clock that counted backward.","By morning the platform and its name had vanished."},new[]{"午夜列车停在一座所有地图都未标出的车站下方。","只有一名乘客下车，怀里抱着一只倒着走的钟。","天亮时，站台和它的名字都消失了。"}),
        PageD("D02-unseen-orchard-record",false,new[]{new RectangleF(38,105,285,82),new RectangleF(386,105,285,82),new RectangleF(76,255,555,78)},new[]{"Every winter the eastern trees bore glass fruit.","The western rows remained green under snow.","No gardener admitted planting either grove."},new[]{"每到冬天，东侧树木便结出玻璃果实。","西侧果树在雪下依旧常青。","没有园丁承认种下过任何一片果园。"}),
        PageD("D03-unseen-avatar-dispatch",true,new[]{new RectangleF(142,108,520,62),new RectangleF(55,226,610,64),new RectangleF(88,348,560,58)},new[]{"Mira returned from the coast with salt on her coat and no shadow.","Her report described a second moon beneath the water.","The council sealed the harbor before dusk."},new[]{"米拉从海岸归来，外套沾满盐霜，却没有影子。","她的报告描述了水面下的第二轮月亮。","议会在黄昏前封锁了港口。"}),
        Simple("D04-unseen-dialogue",Color.FromArgb(42,62,82),new(58,312,604,128),"Engineer: Do not restart the core until the blue gauge is empty.","工程师：蓝色仪表归零前不要重启核心。",RegionRoleType.Dialogue),
        ChoicesD(),IrregularD()
    ];
    private static Case PageD(string id,bool avatar,RectangleF[] boxes,string[] en,string[] zh){var b=new Bitmap(720,520);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(35,39,47));if(avatar){using var a=new SolidBrush(Color.FromArgb(125,164,184));g.FillEllipse(a,28,24,88,88);}using var f=new Font("Segoe UI",17,FontStyle.Italic,GraphicsUnit.Pixel);using var tf=new Font("Segoe UI",24,FontStyle.Bold,GraphicsUnit.Pixel);g.DrawString("UNFILED ACCOUNT",tf,Brushes.White,avatar?142:42,30);var rs=new List<RecognitionRegion>{R(id+"-T",new(avatar?142:42,30,400,38),"UNFILED ACCOUNT","未归档记录",RegionRoleType.Title,0)};for(var i=0;i<boxes.Length;i++){g.DrawString(en[i],f,Brushes.Gainsboro,boxes[i]);var r=R(id+"-P"+i,boxes[i],en[i],zh[i],RegionRoleType.BodyParagraph,i+1);if(avatar)r.LayoutExclusionPolygons=[GeometryV2.RectanglePolygon(new(28,24,88,88))];rs.Add(r);}return new(id,b,rs);}
    private static Case ChoicesD(){var b=new Bitmap(720,520);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(32,48,78));using var f=new Font("Segoe UI",18,FontStyle.Bold,GraphicsUnit.Pixel);var boxes=new[]{new RectangleF(48,62,292,58),new RectangleF(380,62,292,58),new RectangleF(48,190,292,58),new RectangleF(380,190,292,58),new RectangleF(214,332,292,66)};var en=new[]{"Tune the receiver","Search the lower deck","Ask about the storm","Keep the signal open","Disconnect"};var zh=new[]{"调谐接收器","搜索下层甲板","询问风暴","保持信号畅通","断开连接"};var rs=new List<RecognitionRegion>();for(var i=0;i<boxes.Length;i++){using var p=new SolidBrush(Color.FromArgb(74,132,178));g.FillEllipse(p,boxes[i]);g.DrawString(en[i],f,Brushes.White,boxes[i].Left+18,boxes[i].Top+15);rs.Add(R("DCHOICE-"+i,boxes[i],en[i],zh[i],RegionRoleType.Choice,i));}return new("D05-unseen-choice",b,rs);}
    private static Case IrregularD(){var b=new Bitmap(720,520);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(25,29,35));using var p=new SolidBrush(Color.FromArgb(92,65,44));g.FillPolygon(p,[new PointF(330,72),new PointF(665,105),new PointF(620,300),new PointF(292,270)]);using var f=new Font("Segoe UI",18,FontStyle.Bold,GraphicsUnit.Pixel);g.DrawString("Ash Lens\nReveals footprints left by spirits.",f,Brushes.White,new RectangleF(340,125,270,100));return new("D06-unseen-irregular-ui",b,[R("D06-R",new(320,100,320,175),"Ash Lens\nReveals footprints left by spirits.","灰烬透镜\n可以显现灵体留下的足迹。",RegionRoleType.UILabel,0)]);}

    private static Case PageC()
    {
        var b=new Bitmap(720,520);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(38,42,49));using var title=new Font("Segoe UI",25,FontStyle.Bold,GraphicsUnit.Pixel);using var body=new Font("Segoe UI",17,FontStyle.Italic,GraphicsUnit.Pixel);
        g.DrawString("THE LAST LIGHTHOUSE",title,Brushes.White,42,28);var boxes=new[]{new RectangleF(42,105,620,48),new RectangleF(65,190,270,60),new RectangleF(385,190,270,60),new RectangleF(85,305,550,58)};var en=new[]{"The keeper recorded one final tide before the lamps went dark.","Salt covered every brass key.","No ship answered the bell.","At sunrise, a fresh set of footprints crossed the empty gallery."};var zh=new[]{"守塔人记下最后一次潮汐后，灯火便熄灭了。","每把黄铜钥匙都覆着盐霜。","没有船回应钟声。","日出时，一串新脚印穿过空荡的回廊。"};var regions=new List<RecognitionRegion>{R("C01-T",new(42,28,420,42),"THE LAST LIGHTHOUSE","最后的灯塔",RegionRoleType.Title,0)};for(var i=0;i<boxes.Length;i++){g.DrawString(en[i],body,Brushes.Gainsboro,boxes[i]);regions.Add(R("C01-P"+i,boxes[i],en[i],zh[i],RegionRoleType.BodyParagraph,i+1));}return new("C01-unseen-lighthouse",b,regions);
    }
    private static Case ChoicesC()
    {
        var b=new Bitmap(720,520);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(46,38,76));using var f=new Font("Segoe UI",19,FontStyle.Bold,GraphicsUnit.Pixel);var boxes=new[]{new RectangleF(70,70,250,60),new RectangleF(400,70,250,60),new RectangleF(70,210,250,60),new RectangleF(400,210,250,60)};var en=new[]{"Open the ledger","Follow the footprints","Wake the engineer","Leave quietly"};var zh=new[]{"打开账簿","追踪脚印","叫醒工程师","悄悄离开"};var regions=new List<RecognitionRegion>();for(var i=0;i<boxes.Length;i++){using var bg=new SolidBrush(Color.FromArgb(96,124,194));g.FillEllipse(bg,boxes[i]);var s=g.MeasureString(en[i],f);g.DrawString(en[i],f,Brushes.White,boxes[i].Left+(boxes[i].Width-s.Width)/2,boxes[i].Top+16);regions.Add(R("CCHOICE-"+i,boxes[i],en[i],zh[i],RegionRoleType.Choice,i));}return new("C04-unseen-actions",b,regions);
    }

    private static Case Page(string id,bool avatar,bool columns)
    {
        var b=new Bitmap(720,520);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(42,45,52));using var f=new Font("Segoe UI",16,FontStyle.Italic,GraphicsUnit.Pixel);using var title=new Font("Segoe UI",23,FontStyle.Bold,GraphicsUnit.Pixel);
        g.DrawString("THE ARCHIVE OF WINTER",title,Brushes.White,avatar?125:35,24);if(avatar){using var br=new SolidBrush(Color.FromArgb(120,155,190));g.FillEllipse(br,25,20,80,80);}
        var regions=new List<RecognitionRegion>{R(id+"-T",new(avatar?125:35,24,430,36),"THE ARCHIVE OF WINTER","冬日档案",RegionRoleType.Title,0)};
        var lines=columns?new[]{new RectangleF(35,105,290,42),new RectangleF(380,105,290,42),new RectangleF(55,180,270,42),new RectangleF(400,180,270,42)}:new[]{new RectangleF(avatar?125:35,105,avatar?545:620,42),new RectangleF(55,180,590,42),new RectangleF(35,255,620,42),new RectangleF(75,330,560,42)};
        var en=new[]{"A locked observatory waited above the harbor.","No footsteps crossed the powdered floor.","The brass instruments still faced north.","At dawn the glass began to sing."};var zh=new[]{"一座封闭的天文台守候在港湾之上。","没有脚步穿过覆着粉尘的地面。","黄铜仪器依然朝向北方。","黎明时，玻璃开始低声鸣响。"};
        for(var i=0;i<lines.Length;i++){g.DrawString(en[i],f,Brushes.Gainsboro,lines[i]);var r=R(id+"-P"+i,lines[i],en[i],zh[i],RegionRoleType.BodyParagraph,i+1);if(avatar)r.LayoutExclusionPolygons=[GeometryV2.RectanglePolygon(new(25,20,80,80))];regions.Add(r);}
        return new(id,b,regions);
    }

    private static Case Choices()
    {
        var b=new Bitmap(720,520);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(30,70,94));using var f=new Font("Segoe UI",20,FontStyle.Bold,GraphicsUnit.Pixel);var regions=new List<RecognitionRegion>();
        var boxes=new[]{new RectangleF(70,70,250,58),new RectangleF(400,70,250,58),new RectangleF(70,190,250,58),new RectangleF(400,190,250,58),new RectangleF(235,330,250,66)};var en=new[]{"Ask about the gate","Inspect the signal","Who sent the letter?","Leave the tower","Goodbye"};var zh=new[]{"询问大门","检查信号","是谁寄来的信？","离开高塔","再见"};
        for(var i=0;i<boxes.Length;i++){using var bg=new SolidBrush(Color.FromArgb(70,150,190));g.FillEllipse(bg,boxes[i]);var size=g.MeasureString(en[i],f);g.DrawString(en[i],f,Brushes.White,boxes[i].Left+(boxes[i].Width-size.Width)/2,boxes[i].Top+12);regions.Add(R("CHOICE-"+i,boxes[i],en[i],zh[i],RegionRoleType.Choice,i));}
        return new("A04-repeated-choice",b,regions);
    }
    private static Case ChoicesB()
    {
        var b=new Bitmap(720,520);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(52,42,86));using var f=new Font("Segoe UI",18,FontStyle.Bold,GraphicsUnit.Pixel);var regions=new List<RecognitionRegion>();
        var boxes=new[]{new RectangleF(55,55,280,54),new RectangleF(385,55,280,54),new RectangleF(55,170,280,54),new RectangleF(385,170,280,54),new RectangleF(220,315,280,72)};var en=new[]{"Read the sealed note","Question the witness","Return to the bridge","Wait for nightfall","End conversation"};var zh=new[]{"阅读密封的纸条","询问目击者","返回舰桥","等待夜幕降临","结束对话"};
        for(var i=0;i<boxes.Length;i++){using var bg=new SolidBrush(Color.FromArgb(105,125,205));g.FillEllipse(bg,boxes[i]);var size=g.MeasureString(en[i],f);g.DrawString(en[i],f,Brushes.White,boxes[i].Left+(boxes[i].Width-size.Width)/2,boxes[i].Top+11);regions.Add(R("BCHOICE-"+i,boxes[i],en[i],zh[i],RegionRoleType.Choice,i));}
        return new("B05-repeated-actions",b,regions);
    }
    private static Case Dialogue()=>Simple("A05-game-dialogue",Color.FromArgb(62,43,72),new(60,330,600,110),"Captain: We leave before the second bell.","队长：我们在第二次钟响前离开。",RegionRoleType.Dialogue);
    private static Case Tooltip()=>Simple("A06-tooltip",Color.FromArgb(78,60,38),new(260,155,390,125),"Moon Compass\nPoints toward the last safe harbor.","月光罗盘\n指向最后一处安全港湾。",RegionRoleType.UILabel);
    private static Case Simple(string id,Color bg,RectangleF box,string en,string zh,RegionRoleType role){var b=new Bitmap(720,520);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(28,31,38));using var panel=new SolidBrush(bg);g.FillRectangle(panel,box);using var f=new Font("Segoe UI",20,FontStyle.Bold,GraphicsUnit.Pixel);g.DrawString(en,f,Brushes.White,RectangleF.Inflate(box,-14,-12));return new(id,b,[R(id+"-R",box,en,zh,role,0)]);}
    private static RecognitionRegion R(string id,RectangleF box,string source,string translation,RegionRoleType role,int order)=>new(){RegionId=id,RoleType=role,Polygon=GeometryV2.RectanglePolygon(box),RendererTargetRegion=box,SourceBlockIds=[id+"-S"],SourceLinePolygons=[GeometryV2.RectanglePolygon(RectangleF.Inflate(box,-6,-5))],OcrText=source,StructuredText=source,TranslationText=translation,ReadingOrder=order,CoverageValid=true};
    private static Bitmap ContactSheet(IReadOnlyList<(Bitmap Source,Bitmap Translated,string Id)> panels){const int w=360,h=260;var sheet=new Bitmap(w*2,h*panels.Count);using var g=Graphics.FromImage(sheet);g.Clear(Color.White);using var label=new Font("Segoe UI",11,FontStyle.Bold,GraphicsUnit.Pixel);for(var i=0;i<panels.Count;i++){g.DrawImage(panels[i].Source,new Rectangle(0,i*h,w,h));g.DrawImage(panels[i].Translated,new Rectangle(w,i*h,w,h));g.DrawString(panels[i].Id+" ORIGINAL",label,Brushes.Yellow,5,i*h+5);g.DrawString("TRANSLATED",label,Brushes.Yellow,w+5,i*h+5);}return sheet;}
}
