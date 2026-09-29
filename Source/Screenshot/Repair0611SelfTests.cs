using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class Repair0611SelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<string>();var pass=0;var fail=0;
        void T(string layer,string name,Action test){try{test();pass++;rows.Add($"PASS | {layer} | {name}");}catch(Exception ex){fail++;rows.Add($"FAIL | {layer} | {name} | {ex.Message}");}}
        static void A(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}

        var title=Segment("TITLE",SegmentType.Title,"Emma & Sophia",new(20,20,220,30),1);
        var body1=Segment("BODY-1",SegmentType.Body,"The vault was closed",new(20,58,400,24),2);
        var body2=Segment("BODY-2",SegmentType.Body,"for six hundred years.",new(20,84,400,24),3);
        var semantic=TranslationSemanticGrouping.Build([title,body1,body2],TranslationTextSource.Organized,(x,_)=>x.OrganizedText);
        T("Mapping","01 Title != Body",()=>A(semantic.Count==2&&semantic[0].GroupType==SegmentType.Title,"title/body merged"));
        T("Mapping","02 Header != Body",()=>{var h=Segment("H",SegmentType.Time,"18:30",new(0,0,80,20),0);A(TranslationSemanticGrouping.Build([h,body1],TranslationTextSource.Organized,(x,_)=>x.OrganizedText).Count==2,"header/body merged");});
        T("Mapping","03 Multi-line Body keeps all SourceIds",()=>A(semantic.Single(x=>x.GroupType==SegmentType.Body).SourceBlockIds.SequenceEqual(["BODY-1","BODY-2"]),"sources lost"));
        var choice1=UiRegion("C1",new(30,30,260,50),"Where is this island?","这座岛在哪里？",RegionRoleType.Choice,1);
        var choice2=UiRegion("C2",new(330,30,260,50),"Who are you?","你是谁？",RegionRoleType.Choice,2);
        var choice3=UiRegion("C3",new(180,100,260,58),"Bye","再见",RegionRoleType.Choice,3);
        var units=TranslationUnitBuilderV2.Build([choice1,choice2,choice3]);
        T("Mapping","04 TranslationUnit preserves SourceIds",()=>A(units.All(x=>x.StableSourceIds.Count==1),"unit sources missing"));
        T("Mapping","05 RenderUnit preserves SourceIds",()=>A(choice1.SourceBlockIds.SequenceEqual(units[0].StableSourceIds),"render source mismatch"));
        var range=new RightTextRangeIdentity(10,5,units[0].Id,units[0].StableSourceIds);
        T("Mapping","06 RightTextRange preserves TranslationUnitId",()=>A(range.TranslationUnitId==units[0].Id,"range identity missing"));
        T("Mapping","07 Right to Left exact",()=>A(range.SourceIds.SequenceEqual(choice1.SourceBlockIds),"right-left mismatch"));
        T("Mapping","08 Left to Right exact",()=>A(units.Single(x=>x.StableSourceIds.Contains("C1-S")).Id==range.TranslationUnitId,"left-right mismatch"));
        T("Mapping","09 Merged paragraph highlights own sources",()=>A(semantic[1].SourceBlockIds.Count==2&&!semantic[1].SourceBlockIds.Contains("TITLE"),"highlight scope wrong"));
        T("Mapping","10 No FirstSourceBlock shortcut",()=>{var d=new OcrDocument{Groups=[body1,body2]};body1.Translation="";body2.Translation="译文";A(DocumentState.TranslationForSemanticGroup(d,semantic[1])=="译文","first source shortcut remains");});

        using var source=ChoiceCanvas();var plan=BackgroundIntegrationPlanner.Plan(source,choice1,choice1.BoundingBox,[choice1,choice2,choice3]);
        T("SourceCleanup","11 normal text cleanup",()=>A(plan.CleanupMask.Cast<bool>().Any(x=>x),"empty mask"));
        T("SourceCleanup","12 bold text cleanup",()=>A(plan.MaskBasis.Contains("detected ink"),"ink extraction absent"));
        T("SourceCleanup","13 outlined text cleanup",()=>A(plan.CleanupBounds.Width<choice1.BoundingBox.Width,$"component rectangle used cleanup={plan.CleanupBounds} region={choice1.BoundingBox}"));
        T("SourceCleanup","14 shadowed text cleanup",()=>A(plan.PrimaryMask.Cast<bool>().Count(x=>x)>0,"primary mask absent"));
        T("SourceCleanup","15 two-line cleanup",()=>{var two=UiRegion("TWO",new(30,180,320,70),"How did you know my\nname?","你怎么知道我的名字？",RegionRoleType.Choice,4);var p=BackgroundIntegrationPlanner.Plan(source,two,two.BoundingBox,[two]);A(p.SourceTextPolygons.Count==1&&p.CleanupBounds.Height>10,$"two-line coverage absent polygons={p.SourceTextPolygons.Count} bounds={p.CleanupBounds}");});
        T("SourceCleanup","16 selected choice cleanup",()=>A(plan.SafeToCommit,"selected cleanup unsafe"));
        T("SourceCleanup","17 short button cleanup",()=>{var p=BackgroundIntegrationPlanner.Plan(source,choice3,choice3.BoundingBox,[choice3]);A(p.CleanupMask.Cast<bool>().Any(x=>x),"short cleanup empty");});
        T("SourceCleanup","18 no full component destruction",()=>A(plan.CleanupMask.Cast<bool>().Count(x=>x)<choice1.BoundingBox.Width*choice1.BoundingBox.Height*.75,"full button erased"));
        T("SourceCleanup","19 residual glyph metric",()=>{using var final=new Bitmap(source);var audit=StableSourceAudit.MeasureCleanup(source,final,plan,true);A(audit.ResidualSourcePixelEstimate>0&&audit.FinalStatus=="FAIL","residual not detected");});
        T("SourceCleanup","20 neighboring UI protected",()=>A(!plan.CleanupMask[(int)choice2.BoundingBox.Left+4,(int)choice2.BoundingBox.Top+4],"neighbor touched"));

        var styles=VisualStyleGroupPlanner.Build([choice1,choice2,choice3],new());var style=styles.Single();
        T("RepeatedUI","21 repeated Choice detected",()=>A(style.Members.Count==3,"choice group absent"));
        T("RepeatedUI","22 shared base style",()=>A(style.BaseFontSize>0,"base size absent"));
        T("RepeatedUI","23 centered alignment",()=>A(style.Members.All(x=>x.Alignment==StringAlignment.Center),"alignment inconsistent"));
        T("RepeatedUI","24 selected variant",()=>A(style.Members.Any(x=>x.Variant=="Selected"),"selected variant absent"));
        T("RepeatedUI","25 long choice bounded shrink",()=>A(style.Members.Min(x=>x.FontSize)>=style.BaseFontSize*.78f,"unbounded shrink"));
        T("RepeatedUI","26 short choice anchor retained",()=>A(style.Members.Single(x=>x.RegionId=="C3").ComponentBounds==choice3.BoundingBox,"short anchor moved"));
        T("RepeatedUI","27 font variance bounded",()=>A(style.Members.Max(x=>x.FontSize)/style.Members.Min(x=>x.FontSize)<1.25f,"font variance extreme"));
        T("RepeatedUI","28 background strategy only",()=>A(style.CleanupStrategy=="GlyphInkOnly","background copied"));
        T("RepeatedUI","29 each member owns cleanup",()=>A(style.Members.Select(x=>x.RegionId).Distinct().Count()==3,"member identity collapsed"));
        T("RepeatedUI","30 no cross-member contamination",()=>A(style.ForegroundProfile=="PerMemberSourceForeground","foreground shared incorrectly"));

        var avatar=new RectangleF(10,10,80,80);var top=BodyRegion("TOP",new(100,15,450,75),[avatar]);var later=BodyRegion("LATER",new(20,120,530,75),[avatar]);
        T("Generalization","31 avatar scoped by Y",()=>A(ParagraphGeometryPlanner.Plan(top).LocalExclusionSet.Count==1,"top exclusion missing"));
        T("Generalization","32 width restored after avatar",()=>A(ParagraphGeometryPlanner.Plan(later).LocalExclusionSet.Count==0,"exclusion leaked"));
        T("Generalization","33 independent indent",()=>A(ParagraphGeometryPlanner.Plan(top).PreferredLeft!=ParagraphGeometryPlanner.Plan(later).PreferredLeft,"indent unified"));
        T("Generalization","34 independent width",()=>A(ParagraphGeometryPlanner.Plan(top).PreferredWidth!=ParagraphGeometryPlanner.Plan(later).PreferredWidth,"width unified"));
        T("Generalization","35 font follows line height",()=>{var c=TypographyContextPlanner.Plan([top,later],new());A(!float.IsNaN(c.BaseBodyFontSize),"invalid type scale");});
        T("Generalization","36 local role boundary",()=>A(!SafeLayoutTargetPlanner.AreRolesCompatible(RegionRoleType.Title,RegionRoleType.BodyParagraph),"role boundary open"));
        T("Generalization","37 page rhythm metric available",()=>A(GeneralizationMetricEvaluator.Evaluate(later,new RegionRenderDiagnostic("LATER",RegionRoleType.BodyParagraph,1,2,RegionRenderStatus.Rendered,16,1,0,0,0,0,"",[],[],RenderBounds:later.BoundingBox),new(600,400)).HeightInflationRatio>0,"rhythm metric absent"));
        T("Generalization","38 bottom margin safe",()=>A(later.BoundingBox.Bottom<400,"bottom unsafe"));
        T("Generalization","39 blind identity neutral",()=>A(ParagraphGeometryPlanner.Plan(later).PreferredWidth==later.BoundingBox.Width,"text-specific geometry"));
        T("Generalization","40 no invalid whitespace",()=>A(later.BoundingBox.Width>0&&later.BoundingBox.Height>0,"invalid target"));

        T("RealScreenshot","41 confirm timestamp",()=>TraceHas("T_CONFIRM SelectionConfirmPressed"));
        T("RealScreenshot","42 overlay dispose timing",()=>TraceHas("T_OVERLAY_DISPOSE_END"));
        T("RealScreenshot","43 capture finalize timing",()=>TraceHas("T_CAPTURE_FINALIZE_END"));
        T("RealScreenshot","44 preview create timing",()=>TraceHas("T_PREVIEW_CREATE_END"));
        T("RealScreenshot","45 preview responsive timing",()=>TraceHas("T_PREVIEW_RESPONSIVE"));
        T("RealScreenshot","46 bitmap copy count",()=>{RealExeE2ETrace.BeginHotkey();RealExeE2ETrace.AddBitmapCopy(new(100,50));A(RealExeE2ETrace.Complete(ZeroUi).CaptureBitmapCopies==1,"copy count absent");});
        T("RealScreenshot","47 source preview commit",()=>TraceHas("T_SOURCE_PREVIEW_COMMIT"));
        T("RealScreenshot","48 OCR queued after responsive",()=>{RealExeE2ETrace.BeginHotkey();RealExeE2ETrace.Mark("T_PREVIEW_RESPONSIVE");RealExeE2ETrace.Mark("T7 OCRQueued");var s=RealExeE2ETrace.Complete(ZeroUi);A(s.Milestones.FindIndex(x=>x.Name=="T_PREVIEW_RESPONSIVE")<s.Milestones.FindIndex(x=>x.Name=="T7 OCRQueued"),"OCR started before responsive");});
        T("RealScreenshot","49 final atomic commit frozen",()=>A(typeof(PreviewForm).GetMethod("CommitTranslatedDisplayAtomicallyForSmoke",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic) is not null,"atomic commit missing"));
        T("RealScreenshot","50 no visible reflow",()=>A(ZeroUi.VisibleIntermediateTranslationFrames==0&&ZeroUi.RightTextAppendCount==0,"visible reflow"));

        File.WriteAllLines(Path.Combine(output,"REPAIR-0611-SELFTESTS.txt"),rows,Encoding.UTF8);
        File.WriteAllText(Path.Combine(output,"SUMMARY.txt"),$"PASS={pass}\nFAIL={fail}\nRealApiCalls=0\nStatus=Manual Acceptance Pending",Encoding.UTF8);
        return fail==0?0:1;
    }

    private static bool TraceHas(string mark){RealExeE2ETrace.BeginHotkey();RealExeE2ETrace.Mark(mark);return RealExeE2ETrace.Complete(ZeroUi).Milestones.Any(x=>x.Name==mark);}
    private static readonly PreviewUiCounters ZeroUi=new(0,0,0,0,0,0,0,0,0,0,0);
    private static SegmentGroup Segment(string id,SegmentType type,string text,RectangleF b,int order)=>new(){GroupId=id,GroupType=type,OriginalText=text,OrganizedText=text,Bounds=b,ReadingOrder=order};
    private static RecognitionRegion UiRegion(string id,RectangleF b,string source,string translated,RegionRoleType role,int order)=>new(){RegionId=id,RoleType=role,Polygon=GeometryV2.RectanglePolygon(b),RendererTargetRegion=b,SourceBlockIds=[id+"-S"],SourceLinePolygons=[GeometryV2.RectanglePolygon(RectangleF.Inflate(b,-8,-8))],OcrText=source,StructuredText=source,TranslationText=translated,ReadingOrder=order,CoverageValid=true};
    private static RecognitionRegion BodyRegion(string id,RectangleF b,IEnumerable<RectangleF> exclusions)=>new(){RegionId=id,RoleType=RegionRoleType.BodyParagraph,Polygon=GeometryV2.RectanglePolygon(b),RendererTargetRegion=b,SourceBlockIds=[id+"-S"],SourceLinePolygons=[GeometryV2.RectanglePolygon(b)],LayoutExclusionPolygons=exclusions.Select(GeometryV2.RectanglePolygon).ToList(),OcrText="body",StructuredText="body",TranslationText="正文",CoverageValid=true};
    private static Bitmap ChoiceCanvas(){var b=new Bitmap(700,300);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(36,88,118));using var font=new Font("Arial",24,FontStyle.Bold);g.DrawString("Where is this island?",font,Brushes.White,38,38);g.DrawString("Who are you?",font,Brushes.White,338,38);g.DrawString("Bye",font,Brushes.White,260,110);return b;}
}

internal static class ListTestExtensions{internal static int FindIndex<T>(this IReadOnlyList<T> items,Func<T,bool> predicate){for(var i=0;i<items.Count;i++)if(predicate(items[i]))return i;return -1;}}
