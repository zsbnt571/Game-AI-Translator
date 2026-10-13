using System.Diagnostics;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class TextColorPhase3Diagnostics
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    internal static int Run(string sourcePath,string output)
    {
        Directory.CreateDirectory(output);using var source=new Bitmap(sourcePath);
        var ocr=new OcrRuntimeManager(AppContext.BaseDirectory,new OcrService());var vision=new VisionRuntimeManager(AppContext.BaseDirectory);
        try
        {
            var settings=new ApiSettings{OcrEngine=OcrEngineKind.Rapid,VisualModel=VisualModelKind.PPDocLayoutS,OcrLanguage="English"};
            var result=new RecognitionPipelineV2(ocr,vision).RunAsync(source,settings,1,CancellationToken.None).GetAwaiter().GetResult();
            var sample=Directory.GetParent(Path.GetFullPath(sourcePath))?.Name??"sample";
            foreach(var r in result.Document.Regions)r.TranslationText=Translation(r,sample);
            var common=new RenderSettings{FontFamily="Microsoft YaHei UI",TextColorMode=TextColorMode.Auto,Shadow=false,Outline=false,StrokeWidth=RendererStrokeWidth.None,BackgroundStrategy=TranslationOverlayBackgroundStyle.Automatic};
            var old=new RenderSettings{FontFamily=common.FontFamily,TextColorMode=TextColorMode.Auto,Shadow=false,Outline=false,StrokeWidth=RendererStrokeWidth.None,BackgroundStrategy=common.BackgroundStrategy,DiagnosticUseLegacySourceTextColor=true,DiagnosticUseLegacyRoleTypography=true};
            using var oldResult=RegionRendererV2.Render(source,result.Document.Regions,old);var renderClock=Stopwatch.StartNew();using var current=RegionRendererV2.Render(source,result.Document.Regions,common);renderClock.Stop();
            using var cleanBackground=RegionRendererV2.Render(source,result.Document.Regions,new RenderSettings{FontFamily=common.FontFamily,TextColorMode=TextColorMode.Auto,Shadow=false,Outline=false,StrokeWidth=RendererStrokeWidth.None,BackgroundStrategy=common.BackgroundStrategy,DiagnosticSkipText=true});
            RegionRenderDecisionAudit.Save(Path.Combine(output,"REGION-RENDER-DECISIONS.json"),result.Document,current,0);
            RegionRenderDecisionAudit.SaveTitles(Path.Combine(output,"TITLE-RENDER-DECISIONS.json"),result.Document,current);
            var typography=TypographyContextPlanner.Plan(result.Document.Regions,common);
            var typographyRows=(current.Diagnostics??[]).Where(d=>typography.RegionFontSizes.ContainsKey(d.RegionId)).Select(d=>new{
                d.RegionId,Role=d.Role.ToString(),SourceHeight=result.Document.Regions.First(r=>r.RegionId==d.RegionId).SourceLinePolygons.Select(GeometryV2.Bounds).Select(x=>x.Height).DefaultIfEmpty(0).Average(),
                FinalFontSize=d.FontSize,Style=TypographyContextPlanner.ResolveStyle(result.Document.Regions.First(r=>r.RegionId==d.RegionId),typography).ToString(),
                CommitStatus=d.AtomicRegionCommitted,PreserveReason=d.AtomicRegionCommitted?"":d.FallbackReason}).ToArray();
            File.WriteAllText(Path.Combine(output,"APRIL-LONG-TYPOGRAPHY-CONTEXT.json"),JsonSerializer.Serialize(new{typography.ContextId,typography.SourceMedianHeight,typography.BaseBodyFontSize,
                AllowedScaleRange=new{Min=typography.AllowedMinFontSize,Max=typography.AllowedMaxFontSize},typography.LongForm,typography.Reason,Regions=typographyRows},Json));
            var coverage=RenderCoverageAudit.Build(sample,result.Document.Regions,current.Diagnostics??[]);RenderCoverageAudit.Write(Path.Combine(output,"COVERAGE-SUMMARY.json"),coverage);
            File.WriteAllText(Path.Combine(output,"TITLE-ANCHOR-DRIFT.json"),JsonSerializer.Serialize((current.Diagnostics??[]).Where(d=>d.Role==RegionRoleType.Title).Select(d=>new{
                d.RegionId,d.SourceTextStartX,d.SourceTextStartY,d.RenderTextStartX,d.RenderTextStartY,DeltaX=d.AnchorDeltaX,DeltaY=d.AnchorDeltaY,d.Alignment,d.ExpansionDirection,
                WithinTolerance=Math.Abs(d.AnchorDeltaX)<=Math.Max(6,d.FontSize*.6f)&&d.RenderTextStartX>=4}),Json));
            File.WriteAllText(Path.Combine(output,"RENDER-PERFORMANCE.json"),JsonSerializer.Serialize(new{renderMs=renderClock.ElapsedMilliseconds,regionCount=result.Document.Regions.Count,pixelArea=(long)source.Width*source.Height,
                current.CopyMetrics,current.ResourceMetrics,resourceBudget="single render / below-normal worker priority",uiCommitCount=1,
                stages=new{backgroundIntegration="included",layout="included",fontApply="not part of image render",previewRefresh="single final commit in runtime",rightTextRebuild="single final commit in runtime"}},Json));
            File.WriteAllText(Path.Combine(output,"FARANNA-BACKGROUND-CLASSIFICATION.json"),JsonSerializer.Serialize(new{sample,regions=(current.Diagnostics??[]).Select(d=>new{d.RegionId,d.Role,d.BackgroundSurfaceType,d.BackgroundVariance,SafeToCommit=d.AtomicRegionCommitted,d.FallbackReason})},Json));
            source.Save(Path.Combine(output,"SOURCE.png"));cleanBackground.Bitmap.Save(Path.Combine(output,"CLEAN-BACKGROUND.png"));oldResult.Bitmap.Save(Path.Combine(output,"TEXTCOLOR-CURRENT.png"));current.Bitmap.Save(Path.Combine(output,"TEXTCOLOR-NEW.png"));
            using(var comparison=new Bitmap(source.Width*2,source.Height+30)){using var g=Graphics.FromImage(comparison);g.Clear(Color.Black);using var labelFont=new Font("Segoe UI",12,FontStyle.Bold,GraphicsUnit.Pixel);g.DrawString("DIAGNOSTIC / SYNTHETIC TRANSLATION — LEGACY COLOR",labelFont,Brushes.White,4,4);g.DrawString("DIAGNOSTIC / SYNTHETIC TRANSLATION — CLEAN-BG COLOR",labelFont,Brushes.White,source.Width+4,4);g.DrawImageUnscaled(oldResult.Bitmap,0,30);g.DrawImageUnscaled(current.Bitmap,source.Width,30);comparison.Save(Path.Combine(output,"TEXTCOLOR-COMPARISON.png"));}
            var currentDiagnostics=current.Diagnostics??[];var oldDiagnostics=oldResult.Diagnostics??[];
            var rows=currentDiagnostics.Select(n=>{var o=oldDiagnostics.FirstOrDefault(x=>x.RegionId==n.RegionId);return new{n.RegionId,role=n.Role.ToString(),backgroundRGBA=n.BackgroundColor,oldAutoColor=o?.TextColor,newColor=n.TextColor,contrastBefore=o?.TextContrast,contrastAfter=n.TextContrast,n.TextColorReason,n.BackgroundVariance};}).ToArray();
            File.WriteAllText(Path.Combine(output,"TEXT-COLOR-RESOLUTION.json"),JsonSerializer.Serialize(new{source=Path.GetFullPath(sourcePath),translationSource="DIAGNOSTIC / SYNTHETIC TRANSLATION; hard-coded fixture in TextColorPhase3Diagnostics; not cache, not saved translation, not API output",pipeline="Final clean background -> TranslationTextColorResolver -> draw",regions=rows,realApiCalls=0},Json));
            File.WriteAllText(Path.Combine(output,"DIAGNOSTIC-SYNTHETIC-TRANSLATION.txt"),"All Chinese text in this Phase 3 evidence is a hard-coded diagnostic fixture. It is not a formal translation result. Real API calls: 0.\r\n");
            var tests=Tests();File.WriteAllLines(Path.Combine(output,"PHASE-3-SELFTESTS.txt"),tests);return tests.Any(x=>x.StartsWith("FAIL"))?2:0;
        }
        finally{vision.DisposeAsync().AsTask().GetAwaiter().GetResult();ocr.DisposeAsync().AsTask().GetAwaiter().GetResult();}
    }
    private static string Translation(RecognitionRegion r,string sample)
    {
        var source=string.IsNullOrWhiteSpace(r.StructuredText)?r.OcrText:r.StructuredText;
        if(source.Contains("处理完成",StringComparison.Ordinal)||source.Contains("OCR",StringComparison.OrdinalIgnoreCase)&&source.Contains("API",StringComparison.OrdinalIgnoreCase))return source;
        if(source.Any(c=>c is >= '\u3400' and <= '\u9fff')&&!source.Any(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))return source;
        source=source.TrimStart();
        if(sample.Equals("cash-card",StringComparison.OrdinalIgnoreCase))
        {if(r.SourceBlockIds.Contains("R002")||r.SourceBlockIds.Contains("R003")||r.SourceBlockIds.Contains("R004"))return "先生，很抱歉，我们不再接受您的现金了……您需要在我体内射精才能完成交易。";return r.SourceBlockIds.FirstOrDefault() switch{"R001"=>"我的现金被拒了，所以现在改用其他方式支付","R005"=>"剧情简介",_=>r.RoleType==RegionRoleType.BodyParagraph?"生活曾经很简单。你做着普通工作，赚钱，然后像其他人一样在商店消费。现金、银行卡、移动支付都可以，购物平淡得令人无聊。":Fallback(r)};}
        return sample.ToLowerInvariant() switch
        {
            "april-card"=>source switch{"April |"=>"四月｜","21 | Your Ex-Girl"=>"21岁｜你的前女友","Your bitchy ex-gf is back in your life"=>"你的刻薄前女友又回来了",_=>r.RoleType switch{RegionRoleType.Title=>"刻薄前任｜四月·托雷斯",RegionRoleType.Dialogue=>"“哎呀呀，看来你一点都没变。”",RegionRoleType.Header=>"四月｜21岁｜你的前女友",RegionRoleType.BodyParagraph=>"还记得你的前女友吗？我是说那个……",_=>Fallback(r,source)}},
            "mouth-card"=>r.RoleType switch{RegionRoleType.Title=>"墙洞中的嘴",RegionRoleType.BodyParagraph=>"冬天，你搬进森林深处的一间廉价公寓。这里安静、多雪，远离城市。几周后，你注意到墙上有一个洞。",_=>Fallback(r,source)},
            "april-long"=>source switch
            {
                "April"=>"四月",
                var s when s.StartsWith("18:30",StringComparison.OrdinalIgnoreCase)=>"18:30｜2026年8月1日｜四月宿舍，BCT大学",
                var s when s.StartsWith("April somehow",StringComparison.OrdinalIgnoreCase)=>"四月说服颜易和她一起参加聚会，只为重温旧时光。宿舍里弥漫着香草身体喷雾和香水的味道，暖金色的灯光落满房间。四月站在墙边的全身镜前，一手叉腰，另一只手拿着手机，捕捉精心摆出的角度。",
                var s when s.StartsWith("\"Fuuuck",StringComparison.OrdinalIgnoreCase)=>"“操……”她呼出一口气，目光掠过镜中的自己。“我现在看起来真他妈性感。”",
                var s when s.StartsWith("And she was right",StringComparison.OrdinalIgnoreCase)=>"她说得没错。黑色短上衣紧贴胸部，牛仔短裤剪裁得很高，拉链故意敞开。她花了四十五分钟把橙色头发弄得像刚从陌生人的床上滚下来。",
                var s when s.StartsWith("A knock came",StringComparison.OrdinalIgnoreCase)=>"敲门声响起。三下急促的敲击让四月露出顽皮的笑容。她把手机扔到床上，在镜子里整理了一下衣服，然后穿着靴子走向门口。",
                var s when s.StartsWith("\"Well, well",StringComparison.OrdinalIgnoreCase)=>"“哎呀，哎呀……”她打开门，目光从颜易身上缓缓移过。“看看谁真的来了。”",
                var s when s.StartsWith("She stepped back",StringComparison.OrdinalIgnoreCase)=>"她后退一步，让门开得更宽，朝他勾了勾手指。“进来吧，别让人看见我在门口等你。”",
                _=>Fallback(r,source)
            },
            "magazine"=>source switch{"THE EMPRESS"=>"女皇","KREATOR1"=>"创作者","TikTok: 33mil Inslagrum: 77mi OnlyFans: 0"=>"短视频：3300万｜照片墙：7700万｜粉丝站：0",_=>r.RoleType switch{RegionRoleType.BodyParagraph=>"神话级配对：她达到了最高标准。",RegionRoleType.Header=>"传奇、偶像与最佳",_=>Fallback(r,source)}},
            "faranna"=>source switch
            {
                var s when s.StartsWith("Faran",StringComparison.OrdinalIgnoreCase)=>"法兰妮",
                var s when s.StartsWith("Dark Elf",StringComparison.OrdinalIgnoreCase)=>"暗精灵",
                var s when s.StartsWith("A young member",StringComparison.OrdinalIgnoreCase)=>"岛上暗精灵部落的年轻成员。她对自己的狩猎和烹饪技巧过于自信，但真正出色的是后者。她善良的天性使她擅长照顾孩子和动物，但面对人类时，她的脾气就会显露出来。",
                var s when s.StartsWith("She has a pet wolf",StringComparison.OrdinalIgnoreCase)=>"她有一只宠物狼，强壮到可以驮着一个人行走。",
                var s when s.StartsWith("Age",StringComparison.OrdinalIgnoreCase)&&s.Contains("Height",StringComparison.OrdinalIgnoreCase)=>"年龄：56岁（在精灵中算年轻！）；身高：182厘米；体重：57公斤",
                var s when s.StartsWith("Age",StringComparison.OrdinalIgnoreCase)=>"年龄：56岁（在精灵中算年轻！）",
                var s when s.StartsWith("Height",StringComparison.OrdinalIgnoreCase)=>"身高：182厘米",
                var s when s.StartsWith("Weight",StringComparison.OrdinalIgnoreCase)=>"体重：57公斤",
                var s when s.StartsWith("BWH",StringComparison.OrdinalIgnoreCase)=>"三围：90/61/91",
                var s when s.StartsWith("Likes",StringComparison.OrdinalIgnoreCase)=>"喜欢：烹饪、动物、阿拉",
                var s when s.StartsWith("Dislikes",StringComparison.OrdinalIgnoreCase)=>"讨厌：人类",
                _=>Fallback(r,source)
            },
            "item-ui"=>source.Contains("only belonging",StringComparison.OrdinalIgnoreCase)||source.Contains("Ophelia",StringComparison.OrdinalIgnoreCase)||source.Contains("遗物",StringComparison.Ordinal)?"你前世的唯一遗物。由 Ophelia 升级为……寻找螃蟹？":Fallback(r,source),
            "marlee"=>source.StartsWith("Marlee",StringComparison.OrdinalIgnoreCase)?"马莉｜诊所":source.Contains("40")?"40万":source.StartsWith("She hasn",StringComparison.OrdinalIgnoreCase)?"从那以后，她再也没有离开你。":"你在雨巷发现了这只瑟瑟发抖的小猫，并把她带回家照顾。",
            "trans-influencer"=>source.StartsWith("Trans Influencer",StringComparison.OrdinalIgnoreCase)?"跨性别网红":source.Contains("34")?"34万":source.StartsWith("Edgerover",StringComparison.OrdinalIgnoreCase)?"埃奇罗弗，主要活跃在短视频平台。":source.StartsWith("\"Can",StringComparison.OrdinalIgnoreCase)?"“异性恋男性可以吗？”":"“跨性别女性是女性吗？”",
            _=>Fallback(r,source)
        };
        static string Fallback(RecognitionRegion region,string? value=null)
        {var text=string.IsNullOrWhiteSpace(value)?region.StructuredText:value;if(text.Length<=16)return text;return region.RoleType switch{RegionRoleType.Button=>"继续",RegionRoleType.Metadata or RegionRoleType.UILabel or RegionRoleType.CharacterName=>text,RegionRoleType.Title=>"标题",RegionRoleType.Header=>"简介",RegionRoleType.Dialogue=>"“示例对话译文。”",RegionRoleType.Narration=>"示例叙述译文。",_=>"示例正文译文。"};}
    }
    private static List<string> Tests(){var x=new List<string>();void T(string n,Action a){try{a();x.Add("PASS | "+n);}catch(Exception e){x.Add("FAIL | "+n+" | "+e.Message);}}void A(bool v,string m){if(!v)throw new InvalidOperationException(m);}using var dark=new Bitmap(80,40);using(var g=Graphics.FromImage(dark))g.Clear(Color.FromArgb(28,12,70));using var light=new Bitmap(80,40);using(var g=Graphics.FromImage(light))g.Clear(Color.FromArgb(245,245,245));
        T("Dark background selects readable foreground",()=>A(TranslationTextColorResolver.Resolve(dark,new(0,0,80,40),RegionRoleType.BodyParagraph).ContrastRatio>=4.5,"contrast"));
        T("Light background selects readable foreground",()=>A(TranslationTextColorResolver.Resolve(light,new(0,0,80,40),RegionRoleType.BodyParagraph).ContrastRatio>=4.5,"contrast"));
        T("Title threshold",()=>A(TranslationTextColorResolver.Resolve(dark,new(0,0,80,40),RegionRoleType.Title).ContrastRatio>=3,"contrast"));
        T("Button threshold",()=>A(TranslationTextColorResolver.Resolve(dark,new(0,0,80,40),RegionRoleType.Button).ContrastRatio>=3,"contrast"));
        T("Metadata remains readable",()=>A(TranslationTextColorResolver.Resolve(dark,new(0,0,80,40),RegionRoleType.Metadata).ContrastRatio>=4.5,"contrast"));
        T("Style hint cannot override readability",()=>A(TranslationTextColorResolver.Resolve(dark,new(0,0,80,40),RegionRoleType.BodyParagraph,Color.FromArgb(45,20,80)).ContrastRatio>=4.5,"hint won"));
        T("Output alpha is opaque",()=>A(TranslationTextColorResolver.Resolve(dark,new(0,0,80,40),RegionRoleType.Dialogue).Alpha==255,"alpha"));
        T("Resolver uses clean bitmap bounds",()=>A(TranslationTextColorResolver.Resolve(light,new(10,10,20,10),RegionRoleType.BodyParagraph).BackgroundColor.GetBrightness()>.8,"wrong sample"));
        RecognitionRegion Region(string id,RectangleF box,string text)=>new(){RegionId=id,RoleType=RegionRoleType.BodyParagraph,Polygon=GeometryV2.RectanglePolygon(box),SourceLinePolygons=[GeometryV2.RectanglePolygon(box)],SourceBlockIds=[id],StructuredText="TEST",TranslationText=text,CoverageValid=true};
        T("Background safe enters text draw",()=>{using var b=new Bitmap(180,80);using(var g=Graphics.FromImage(b)){g.Clear(Color.FromArgb(35,20,80));using var f=new Font("Segoe UI",14,GraphicsUnit.Pixel);g.DrawString("TEST",f,Brushes.White,21,21);}var r=Region("SAFE",new(20,20,90,38),"完整译文");using var rr=RegionRendererV2.Render(b,[r],new RenderSettings());var d=rr.Diagnostics!.Single();A(d.AtomicRegionCommitted&&d.TextDrawCount==1,$"safe region did not draw: {d.FallbackReason}/{d.RenderStatus}");});
        T("Background unsafe enters explicit readable fallback",()=>{using var b=Noise();var r=Region("UNSAFE",new(20,15,90,35),"完整译文");r.LayoutExclusionPolygons=[GeometryV2.RectanglePolygon(r.BoundingBox)];using var rr=RegionRendererV2.Render(b,[r],new RenderSettings());var d=rr.Diagnostics!.Single();A(d.FallbackRendered&&d.AtomicRegionCommitted&&string.Concat(d.RenderedLines.Select(x=>x.Text))==r.TranslationText,"unsafe path did not preserve full translation");});
        T("Background reconstruction failure routes to fallback",()=>{using var b=Noise();var r=Region("FAIL",new(20,15,90,35),"完整覆盖");r.LayoutExclusionPolygons=[GeometryV2.RectanglePolygon(r.BoundingBox)];using var rr=RegionRendererV2.Render(b,[r],new RenderSettings());var d=rr.Diagnostics!.Single();A(d.FallbackAttempted&&d.FallbackRendered&&d.NormalRenderFailureReason.Length>0,"failure did not route to explicit fallback");});
        T("Unsafe region fallback does not block safe sibling",()=>{using var b=Noise();using(var g=Graphics.FromImage(b)){g.FillRectangle(Brushes.MidnightBlue,115,10,65,50);g.DrawString("TEST",new Font("Segoe UI",12,GraphicsUnit.Pixel),Brushes.White,122,20);}var bad=Region("BAD",new(20,15,70,35),"不画");bad.LayoutExclusionPolygons=[GeometryV2.RectanglePolygon(bad.BoundingBox)];var good=Region("GOOD",new(120,15,55,35),"正常");using var rr=RegionRendererV2.Render(b,[bad,good],new RenderSettings());var bd=rr.Diagnostics!.Single(d=>d.RegionId=="BAD");var gd=rr.Diagnostics!.Single(d=>d.RegionId=="GOOD");A(bd.AtomicRegionCommitted&&gd.AtomicRegionCommitted&&RectangleF.Intersect(bd.RenderBounds,gd.RenderBounds).IsEmpty,$"mixed commit failed: BAD={bd.RenderStatus}; GOOD={gd.RenderStatus}");});
        T("Translation characters survive layout",()=>{using var b=new Bitmap(320,120);using(var g=Graphics.FromImage(b)){g.Clear(Color.Navy);g.DrawString("TEST",new Font("Segoe UI",12,GraphicsUnit.Pixel),Brushes.White,20,20);}const string value="逐字符ABC123，标点！不得删减。";var r=Region("INTEGRITY",new(18,18,270,70),value);using var rr=RegionRendererV2.Render(b,[r],new RenderSettings());var drawn=string.Concat(rr.Diagnostics!.Single().RenderedLines.Select(l=>l.Text));A(drawn==value,"translation changed");});
        return x;
        static Bitmap Noise(){var b=new Bitmap(180,80);var random=new Random(17);for(var y=0;y<b.Height;y++)for(var xx=0;xx<b.Width;xx++)b.SetPixel(xx,y,Color.FromArgb(random.Next(256),random.Next(256),random.Next(256)));return b;}
    }
}
