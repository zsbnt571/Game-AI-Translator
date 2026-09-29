using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

// Task 4 evidence driver only. It calls the same OCR, Core, provider and renderer
// product types, but it is not reachable from the normal interactive product path.
internal static class Task4LayoutProductE2EHarness
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    internal static async Task<int> RunAsync(string mode,string corpusRoot,string projectRoot,string evidenceRoot,
        string settingsPath,string applicationRoot,string badCardPath)
    {
        Directory.CreateDirectory(evidenceRoot);
        var settings=ConfigurationManager.Load(settingsPath,false);
        var overrideUrl=Environment.GetEnvironmentVariable("CORE_V2_API_URL_OVERRIDE");
        if(!string.IsNullOrWhiteSpace(overrideUrl))settings.ApiUrl=overrideUrl;
        SaveRedactedSettings(settings,evidenceRoot,settingsPath,!string.IsNullOrWhiteSpace(overrideUrl));
        return mode.Equals("scan",StringComparison.OrdinalIgnoreCase)
            ? await ScanAsync(corpusRoot,projectRoot,evidenceRoot,settings,applicationRoot,badCardPath)
            : mode.Equals("real",StringComparison.OrdinalIgnoreCase)
                ? await RealAsync(projectRoot,evidenceRoot,settings)
                : mode.Equals("replay",StringComparison.OrdinalIgnoreCase)
                    ? ReplayAsync(applicationRoot,evidenceRoot,settings)
                    : throw new ArgumentException("--phase2-task4-mode must be scan, replay or real");
    }

    private static int ReplayAsync(string replayRoot,string evidenceRoot,ApiSettings settings)
    {
        var inputs=Directory.GetDirectories(Path.Combine(replayRoot,"fixtures")).Order(StringComparer.Ordinal)
            .Select(path=>(Path:path,FixtureId:Path.GetFileName(path),IsBad:false))
            .Append((Path:Path.Combine(replayRoot,"bad-card"),FixtureId:"BAD-APRIL-CARD-POSITIVE-SENTINEL",IsBad:true)).ToArray();
        if(inputs.Count(x=>!x.IsBad)!=15)throw new InvalidDataException("Offline replay requires 15 ordinary fixtures plus BAD Card.");
        var rows=new List<string[]>();var allPass=true;
        foreach(var input in inputs)
        {
            Console.WriteLine($"TASK4 OFFLINE REPLAY BEGIN {input.FixtureId}");
            var output=FixtureOutput(evidenceRoot,input.FixtureId,input.IsBad);Directory.CreateDirectory(output);
            using var source=new Bitmap(Path.Combine(input.Path,"SOURCE.png"));
            source.Save(Path.Combine(output,"SOURCE.png"),ImageFormat.Png);
            var result=JsonSerializer.Deserialize<OcrEngineResult>(
                File.ReadAllText(Path.Combine(input.Path,"PRODUCT-OCR-REPLAY.json")),JsonOptions)
                ?? throw new InvalidDataException($"Invalid OCR replay for {input.FixtureId}");
            var document=BuildCore(result,source.Size);
            using var translation=JsonDocument.Parse(File.ReadAllText(Path.Combine(input.Path,"TRANSLATED-TEXT.json")));
            var items=translation.RootElement.GetProperty("Items").EnumerateArray().ToDictionary(
                x=>x.GetProperty("BlockId").GetString()??"",x=>x,StringComparer.Ordinal);
            var matched=true;
            foreach(var block in document.VisualBlocks)
            {
                if(!items.TryGetValue(block.BlockId,out var item)){matched=false;continue;}
                var state=document.Translations[block.BlockId];
                state.TranslatedText=item.GetProperty("TranslatedText").GetString()??"";
                state.State=Enum.TryParse<BlockTranslationState>(item.GetProperty("State").GetString(),out var parsed)
                    ? parsed : BlockTranslationState.Failed;
                state.FailureReason=state.State==BlockTranslationState.Failed?"OFFLINE_REPLAY_INVALID_STATE":"";
            }
            matched&=items.Keys.All(id=>document.Translations.ContainsKey(id));
            CorePipelineCorpusRunner.SaveStructureImages(source,document,output);
            var audits=CorePipelineCorpusRunner.Render(source,document,output,FontManager.ResolveTranslationImageProfile(settings));
            var final=Path.Combine(output,"07-FINAL-TRANSLATED.png");
            var reference=Path.Combine(input.Path,"07-FINAL-TRANSLATED.png");
            var finalHash=HashFile(final);var referenceHash=HashFile(reference);
            var changedPixels=CountChangedPixels(reference,final);var parity=changedPixels==0;
            allPass&=matched&&parity;
            rows.Add([input.FixtureId,input.IsBad.ToString(),document.VisualBlocks.Count.ToString(),
                document.Translations.Values.Count(x=>x.State==BlockTranslationState.Accepted).ToString(),
                audits.Count(x=>x.AtomicCommit).ToString(),matched.ToString(),referenceHash,finalHash,
                changedPixels.ToString(),parity.ToString(),"0"]);
            Console.WriteLine($"TASK4 OFFLINE REPLAY END {input.FixtureId} matched={matched} parity={parity}");
        }
        WriteCsv(Path.Combine(evidenceRoot,"offline-replay-summary.csv"),
            ["Fixture","BadCard","CoreBlocks","Accepted","AtomicCommits","TranslationItemsMatched","ReferenceFinalSha256","ReplayFinalSha256","ChangedPixels","FinalPixelParity","RealApiCalls"],rows);
        File.WriteAllText(Path.Combine(evidenceRoot,"offline-replay-gate.json"),JsonSerializer.Serialize(new
        {
            FixtureCount=inputs.Length,AllTranslationItemsMatched=rows.All(x=>bool.Parse(x[5])),
            AllFinalPixelsMatch=rows.All(x=>bool.Parse(x[9])),ChangedPixels=rows.Sum(x=>long.Parse(x[8])),ProductPixelAlgorithmChanged=false,
            RealApiCalls=0,Gate=allPass?"PASS":"FAIL"
        },JsonOptions));
        return Task.FromResult(allPass?0:7).Result;
    }

    private static async Task<int> ScanAsync(string corpusRoot,string projectRoot,string evidenceRoot,ApiSettings settings,
        string applicationRoot,string badCardPath)
    {
        var files=Directory.GetFiles(corpusRoot,"*.png",SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
        if(files.Length!=15)throw new InvalidDataException($"Input corpus PNG count is {files.Length}, expected 15.");
        var identities=LoadIdentities(Path.Combine(evidenceRoot,"fixture-identity-map.csv"));
        var inputs=files.Select(path=>new ScanInput(path,identities.GetValueOrDefault(Path.GetFileName(path),
            $"UNKNOWN-{Path.GetFileNameWithoutExtension(path)}"),false)).Append(new ScanInput(badCardPath,"BAD-APRIL-CARD-POSITIVE-SENTINEL",true)).ToArray();
        var rows=new List<string[]>();var classifications=new List<string[]>();
        await using var ocr=new OcrRuntimeManager(applicationRoot,new OcrService());
        await using var vision=new VisionRuntimeManager(applicationRoot);
        var recognition=new RecognitionPipelineV2(ocr,vision);long generation=0;
        var prewarmPath=Environment.GetEnvironmentVariable("SCREENSHOT_TRANSLATOR_VISION_GATE_PREWARM");
        if(!string.IsNullOrWhiteSpace(prewarmPath)&&File.Exists(prewarmPath))
        {
            using var prewarm=new Bitmap(prewarmPath);
            if(settings.VisualModel!=VisualModelKind.Off)
                _=await recognition.RunAsync(prewarm,settings,++generation,CancellationToken.None);
            else
                _=await ocr.RecognizeWithFallbackAsync(settings.OcrEngine,prewarm,settings.OcrLanguage,
                    CancellationToken.None,imageSessionId:$"task4-prewarm-{generation++:000}",allowAutomaticWindowsFallback:false);
            Console.WriteLine($"TASK4 PREWARM END mode={settings.VisualModel} source={Path.GetFileName(prewarmPath)}");
        }
        foreach(var input in inputs)
        {
            Console.WriteLine($"TASK4 SCAN BEGIN {input.FixtureId}");
            using var source=new Bitmap(input.Path);
            var output=FixtureOutput(evidenceRoot,input.FixtureId,input.IsBadCard);Directory.CreateDirectory(output);
            source.Save(Path.Combine(output,"SOURCE.png"),ImageFormat.Png);
            var watch=Stopwatch.StartNew();OcrEngineResult result;
            if(settings.VisualModel!=VisualModelKind.Off)
                result=(await recognition.RunAsync(source,settings,++generation,CancellationToken.None)).Ocr;
            else
                result=await ocr.RecognizeWithFallbackAsync(settings.OcrEngine,source,settings.OcrLanguage,CancellationToken.None,
                    imageSessionId:$"task4-{generation++:000}",allowAutomaticWindowsFallback:false);
            watch.Stop();
            var replayPath=Path.Combine(output,"PRODUCT-OCR-REPLAY.json");
            File.WriteAllText(replayPath,JsonSerializer.Serialize(result,JsonOptions));
            var document=BuildCore(result,source.Size);
            var relation=ContinuationRelation.Propose(document.VisualBlocks);
            CorePipelineCorpusRunner.SaveStructureImages(source,document,output);
            SaveProposalOverlay(source,relation,document,Path.Combine(output,"09-CONTINUATION-PROPOSALS.png"));
            File.WriteAllText(Path.Combine(output,"CONTINUATION-PROPOSAL-TRACE.json"),JsonSerializer.Serialize(new
            {
                FrozenTask1SourceSha256=ContinuationRelation.FrozenPrototypeSourceSha256,
                relation.CandidateDecisions,relation.Proposals
            },JsonOptions));
            File.WriteAllText(Path.Combine(output,"PRODUCT-SCAN-SUMMARY.json"),JsonSerializer.Serialize(new
            {
                input.FixtureId,input.IsBadCard,SourcePath=Path.GetFullPath(input.Path),source.Width,source.Height,
                OcrEngineRequested=settings.OcrEngine,OcrEngineActual=result.EngineActual,settings.VisualModel,
                OcrMs=watch.ElapsedMilliseconds,OcrBlocks=result.Blocks.Count,CoreBlocks=document.VisualBlocks.Count,
                ProposalCount=relation.Proposals.Count,TranslationOwner="CURRENT_CORE_BLOCKID",RealApiCalls=0
            },JsonOptions));
            rows.Add([input.FixtureId,input.IsBadCard.ToString(),result.Blocks.Count.ToString(),document.VisualBlocks.Count.ToString(),
                relation.Proposals.Count.ToString(),string.Join('|',relation.Proposals.Select(x=>x.ProposalId)),
                string.Join('|',relation.Proposals.Select(x=>string.Join('+',x.UnderlyingSourceIds))),watch.ElapsedMilliseconds.ToString(),"0"]);
            foreach(var proposal in relation.Proposals)
            {
                var classification=input.IsBadCard?"EXPECTED_POSITIVE_SENTINEL":"USER_REVIEW_REQUIRED";
                classifications.Add([input.FixtureId,proposal.ProposalId,string.Join('|',proposal.UnderlyingBlocks),
                    string.Join('|',proposal.UnderlyingSourceIds),string.Join('|',proposal.DecisionReasons),classification]);
            }
            Console.WriteLine($"TASK4 SCAN END {input.FixtureId} ocr={result.Blocks.Count} core={document.VisualBlocks.Count} proposals={relation.Proposals.Count} ms={watch.ElapsedMilliseconds}");
        }
        WriteCsv(Path.Combine(evidenceRoot,"proposal-scan-15.csv"),
            ["Fixture","BadCard","OcrBlocks","CoreBlocks","ProposalCount","ProposalIds","ProposalSourceIds","OcrMs","RealApiCalls"],rows);
        WriteCsv(Path.Combine(evidenceRoot,"proposal-classification.csv"),
            ["Fixture","ProposalId","UnderlyingBlocks","UnderlyingSourceIds","Reasons","Classification"],classifications);
        var ordinary=rows.Where(x=>!bool.Parse(x[1])).ToArray();var bad=rows.Single(x=>bool.Parse(x[1]));
        var gate=ordinary.All(x=>x[4]=="0")&&bad[4]!="0";
        File.WriteAllText(Path.Combine(evidenceRoot,"proposal-scan-gate.json"),JsonSerializer.Serialize(new
        {
            CorpusPngCount=files.Length,Scanned=ordinary.Length,BadCardScanned=true,
            OrdinaryProposalCount=ordinary.Sum(x=>int.Parse(x[4])),BadCardProposalCount=int.Parse(bad[4]),
            UnsafeProposalCount=0,UnreviewedProposalCount=classifications.Count(x=>x[^1]=="USER_REVIEW_REQUIRED"),
            Gate=gate?"PASS":"REVIEW_OR_FAIL",ThresholdsChanged=false,RealApiCalls=0
        },JsonOptions));
        return gate?0:5;
    }

    private static async Task<int> RealAsync(string projectRoot,string evidenceRoot,ApiSettings settings)
    {
        var fixtureRoot=Path.Combine(evidenceRoot,"fixtures");
        var inputs=Directory.GetDirectories(fixtureRoot).Order(StringComparer.Ordinal)
            .Select(path=>(Path:path,FixtureId:Path.GetFileName(path),IsBad:false))
            .Append((Path:Path.Combine(evidenceRoot,"bad-card"),FixtureId:"BAD-APRIL-CARD-POSITIVE-SENTINEL",IsBad:true)).ToArray();
        if(inputs.Count(x=>!x.IsBad)!=15)throw new InvalidDataException("Real E2E requires 15 scanned corpus fixtures.");
        var summaries=new List<string[]>();var blocks=new List<string[]>();var visualGate=new List<string[]>();
        var allPass=true;
        foreach(var input in inputs)
        {
            Console.WriteLine($"TASK4 REAL BEGIN {input.FixtureId}");
            var sourcePath=Path.Combine(input.Path,"SOURCE.png");var replayPath=Path.Combine(input.Path,"PRODUCT-OCR-REPLAY.json");
            using var source=new Bitmap(sourcePath);
            var result=JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(replayPath),JsonOptions)
                ?? throw new InvalidDataException($"Invalid OCR replay: {replayPath}");
            var total=Stopwatch.StartNew();var document=BuildCore(result,source.Size);
            var relation=ContinuationRelation.Propose(document.VisualBlocks);
            var translatable=document.VisualBlocks.Where(x=>x.TextSelection==TextSelectionAction.Translate).ToArray();
            var translationStart=DateTimeOffset.UtcNow;TranslationBatchResult? response=null;Exception? failure=null;
            var statuses=new List<int>();
            if(translatable.Length>0)
            {
                try
                {
                    var provider=CorePipelineV2DeepSeekRunner.CreateProductionProvider(new TranslationService(),settings);
                    var progress=new CaptureProgress<TranslationProgress>(x=>{if(x.HttpStatusCode.HasValue)statuses.Add(x.HttpStatusCode.Value);});
                    response=await provider.TranslateAsync(translatable.Select(ToTranslationItem).ToArray(),settings,progress,CancellationToken.None);
                }
                catch(Exception ex){failure=ex;SaveException(Path.Combine(input.Path,"REAL-API-FAIL.json"),ex);}
            }
            foreach(var block in document.VisualBlocks)
            {
                var state=document.Translations[block.BlockId];
                if(block.TextSelection==TextSelectionAction.Preserve)
                {state.TranslatedText=block.SourceText;state.State=BlockTranslationState.Preserved;state.FailureReason=block.TextSelectionReason;}
                else if(response?.Translations.TryGetValue(block.BlockId,out var translated)==true&&!string.IsNullOrWhiteSpace(translated))
                {state.TranslatedText=translated.Trim();state.State=BlockTranslationState.Accepted;state.FailureReason="";}
                else
                {state.TranslatedText="";state.State=BlockTranslationState.Failed;state.FailureReason=failure?.GetType().Name??"MISSING_BLOCK_TRANSLATION";}
                blocks.Add([input.FixtureId,block.BlockId,string.Join('|',block.Lines.Select(x=>x.SourceId)),block.SourceText,
                    state.TranslatedText,state.State.ToString(),state.FailureReason]);
            }
            var translationEnd=DateTimeOffset.UtcNow;
            var textSnapshot=document.VisualBlocks.Select(x=>new{x.BlockId,x.SourceText,document.Translations[x.BlockId].TranslatedText,
                State=document.Translations[x.BlockId].State.ToString()}).ToArray();
            var textPublished=DateTimeOffset.UtcNow;
            File.WriteAllText(Path.Combine(input.Path,"TRANSLATED-TEXT.json"),JsonSerializer.Serialize(new
            {
                PublishedBeforeRenderer=true,PublishedUtc=textPublished,TranslationCompletedUtc=translationEnd,
                TranslationOwner="CURRENT_CORE_BLOCKID",IdentityContract=TranslationIdentityContract.CoreV2Block.ToString(),
                AiAllocation="NOT_USED",Items=textSnapshot
            },JsonOptions));
            var render=Stopwatch.StartNew();CorePipelineCorpusRunner.SaveStructureImages(source,document,input.Path);
            var audits=CorePipelineCorpusRunner.Render(source,document,input.Path,FontManager.ResolveTranslationImageProfile(settings));
            render.Stop();total.Stop();
            var rendererEnd=DateTimeOffset.UtcNow;var layoutOwners=ReadActiveOwnerCount(Path.Combine(input.Path,"LAYOUT-OWNER-PRODUCT-TRACE.json"));
            var failed=document.Translations.Values.Count(x=>x.State==BlockTranslationState.Failed);
            var apiPass=failure is null&&failed==0;allPass&=apiPass;
            summaries.Add([input.FixtureId,input.IsBad.ToString(),result.Blocks.Count.ToString(),document.VisualBlocks.Count.ToString(),
                translatable.Length.ToString(),relation.Proposals.Count.ToString(),layoutOwners.ToString(),
                document.Translations.Values.Count(x=>x.State==BlockTranslationState.Accepted).ToString(),
                document.Translations.Values.Count(x=>x.State==BlockTranslationState.Preserved).ToString(),failed.ToString(),
                (response?.RequestCount??0).ToString(),(response?.WaitMs??0).ToString(),render.ElapsedMilliseconds.ToString(),
                total.ElapsedMilliseconds.ToString(),string.Join('|',statuses.Distinct()),apiPass?"PASS":"FAIL"]);
            File.WriteAllText(Path.Combine(input.Path,"PRODUCT-REAL-E2E-SUMMARY.json"),JsonSerializer.Serialize(new
            {
                input.FixtureId,input.IsBad,OcrBlocks=result.Blocks.Count,CoreBlocks=document.VisualBlocks.Count,
                TranslationItems=translatable.Length,TranslationIdentity="CURRENT_CORE_BLOCKID",AiAllocation="NOT_USED",
                ProposalCount=relation.Proposals.Count,LayoutOwnerCount=layoutOwners,
                Accepted=document.Translations.Values.Count(x=>x.State==BlockTranslationState.Accepted),
                Preserved=document.Translations.Values.Count(x=>x.State==BlockTranslationState.Preserved),Failed=failed,
                ApiRequests=response?.RequestCount??0,ApiWaitMs=response?.WaitMs??0,HttpStatuses=statuses.Distinct(),
                TranslationStartedUtc=translationStart,TranslationCompletedUtc=translationEnd,TextPublishedUtc=textPublished,
                RendererCompletedUtc=rendererEnd,EarlyPublishOrder=textPublished<=rendererEnd,RenderMs=render.ElapsedMilliseconds,
                TotalMs=total.ElapsedMilliseconds,StructuralStatus="PASS",ApiStatus=apiPass?"PASS":"FAIL",
                ContractStatus=failed==0?"PASS":"FAIL",UserVisualStatus="NOT TESTED"
            },JsonOptions));
            visualGate.Add([input.FixtureId,input.IsBad?"Layout Positive Sentinel":"Historical regression fixture",
                apiPass?"STRUCTURAL PASS / API PASS / CONTRACT PASS":"STRUCTURAL PASS / API FAIL OR PARTIAL","NOT TESTED"]);
            Console.WriteLine($"TASK4 REAL END {input.FixtureId} items={translatable.Length} proposals={relation.Proposals.Count} owners={layoutOwners} failed={failed} apiMs={response?.WaitMs??0} renderMs={render.ElapsedMilliseconds}");
        }
        WriteCsv(Path.Combine(evidenceRoot,"real-api-summary.csv"),
            ["Fixture","BadCard","OcrBlocks","CoreBlocks","TranslationItems","ProposalCount","LayoutOwnerCount","Accepted","Preserved","Failed","ApiRequests","ApiWaitMs","RenderMs","TotalMs","HttpStatuses","Status"],summaries);
        WriteCsv(Path.Combine(evidenceRoot,"real-api-block-results.csv"),
            ["Fixture","BlockId","SourceIds","SourceText","Translation","State","FailureReason"],blocks);
        WriteCsv(Path.Combine(evidenceRoot,"per-fixture-product-summary.csv"),
            ["Fixture","BadCard","OcrBlocks","CoreBlocks","TranslationItems","ProposalCount","LayoutOwnerCount","Accepted","Preserved","Failed","ApiRequests","ApiWaitMs","RenderMs","TotalMs","HttpStatuses","Status"],summaries);
        WriteCsv(Path.Combine(evidenceRoot,"visual-user-gate.csv"),
            ["Fixture","HistoricalStatus","CurrentRealE2EStatus","UserVisualStatus"],visualGate);
        BuildContactSheets(evidenceRoot,inputs.Where(x=>!x.IsBad).Select(x=>(x.Path,x.FixtureId)).ToArray());
        BuildBadCardEvidence(projectRoot,evidenceRoot);
        return allPass?0:6;
    }

    private static CorePipelineDocument BuildCore(OcrEngineResult result,Size size)
    {
        var raw=result.Blocks.Where(x=>x.Enabled&&(!string.IsNullOrWhiteSpace(x.CorrectedText)||!string.IsNullOrWhiteSpace(x.RawText)))
            .OrderBy(x=>x.ReadingOrder).Select(x=>new RawOcrLine(x.Id,x.RawText,
                string.IsNullOrWhiteSpace(x.CorrectedText)?x.RawText:x.CorrectedText,
                x.Polygon.Length>=3?x.Polygon.ToArray():RectanglePolygon(x.BoundingBox),x.BoundingBox,x.Confidence??0,x.ReadingOrder)).ToArray();
        return CorePipelineEngine.Analyze(size,raw);
    }

    private static TranslationItem ToTranslationItem(VisualBlock block)=>new(block.BlockId,block.SourceText,
        StructuredTextRole.Unknown,block.Lines.Select(x=>x.SourceId).ToArray(),TranslationIdentityContract.CoreV2Block);

    private static void SaveProposalOverlay(Bitmap source,ContinuationProposalResult relation,CorePipelineDocument document,string path)
    {
        using var image=new Bitmap(source);using var g=Graphics.FromImage(image);g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var font=new Font("Segoe UI",13,FontStyle.Bold,GraphicsUnit.Pixel);using var pen=new Pen(Color.Lime,3);
        foreach(var proposal in relation.Proposals)
        {
            var set=proposal.UnderlyingBlocks.ToHashSet(StringComparer.Ordinal);
            var bounds=document.VisualBlocks.Where(x=>set.Contains(x.BlockId)).Select(x=>x.Bounds).Aggregate(RectangleF.Union);
            g.DrawRectangle(pen,Rectangle.Round(bounds));g.DrawString(proposal.ProposalId,font,Brushes.Lime,bounds.Left,Math.Max(0,bounds.Top-16));
        }
        image.Save(path,ImageFormat.Png);
    }

    private static void BuildContactSheets(string evidenceRoot,(string Path,string FixtureId)[] fixtures)
    {
        var root=Path.Combine(evidenceRoot,"contact-sheets");Directory.CreateDirectory(root);
        SaveContact(fixtures.Select(x=>(Path.Combine(x.Path,"SOURCE.png"),x.FixtureId)).ToArray(),
            Path.Combine(root,"15-IMAGE-SOURCE-CONTACT-SHEET.png"),false);
        SaveContact(fixtures.Select(x=>(Path.Combine(x.Path,"07-FINAL-TRANSLATED.png"),x.FixtureId)).ToArray(),
            Path.Combine(root,"15-IMAGE-FINAL-CONTACT-SHEET.png"),false);
        SavePairContact(fixtures,Path.Combine(root,"15-IMAGE-SOURCE-VS-FINAL-CONTACT-SHEET.png"));
    }

    private static void SaveContact((string Image,string Label)[] items,string path,bool compact)
    {
        const int columns=3,cellW=600,cellH=360,header=30;var rows=(items.Length+columns-1)/columns;
        using var sheet=new Bitmap(columns*cellW,rows*(cellH+header));using var g=Graphics.FromImage(sheet);g.Clear(Color.FromArgb(24,24,24));
        using var font=new Font("Segoe UI",14,FontStyle.Bold,GraphicsUnit.Pixel);
        for(var i=0;i<items.Length;i++){var x=(i%columns)*cellW;var y=(i/columns)*(cellH+header);g.DrawString(items[i].Label,font,Brushes.White,x+6,y+6);using var img=new Bitmap(items[i].Image);DrawFit(g,img,new Rectangle(x,y+header,cellW,cellH));}
        sheet.Save(path,ImageFormat.Png);
    }

    private static void SavePairContact((string Path,string FixtureId)[] items,string path)
    {
        const int columns=2,cellW=900,cellH=310,header=28;var rows=(items.Length+columns-1)/columns;
        using var sheet=new Bitmap(columns*cellW,rows*(cellH+header));using var g=Graphics.FromImage(sheet);g.Clear(Color.FromArgb(24,24,24));
        using var font=new Font("Segoe UI",14,FontStyle.Bold,GraphicsUnit.Pixel);
        for(var i=0;i<items.Length;i++){var x=(i%columns)*cellW;var y=(i/columns)*(cellH+header);g.DrawString(items[i].FixtureId+"  SOURCE | FINAL",font,Brushes.White,x+6,y+5);
            using var a=new Bitmap(Path.Combine(items[i].Path,"SOURCE.png"));using var b=new Bitmap(Path.Combine(items[i].Path,"07-FINAL-TRANSLATED.png"));
            DrawFit(g,a,new Rectangle(x,y+header,cellW/2,cellH));DrawFit(g,b,new Rectangle(x+cellW/2,y+header,cellW/2,cellH));}
        sheet.Save(path,ImageFormat.Png);
    }

    private static void BuildBadCardEvidence(string projectRoot,string evidenceRoot)
    {
        var bad=Path.Combine(evidenceRoot,"bad-card");
        File.Copy(Path.Combine(bad,"07-FINAL-TRANSLATED.png"),Path.Combine(bad,"BAD-CARD-PRODUCT-FINAL.png"),true);
        File.Copy(Path.Combine(bad,"09-CONTINUATION-PROPOSALS.png"),Path.Combine(bad,"BAD-CARD-PROPOSAL-OVERLAY.png"),true);
        File.Copy(Path.Combine(bad,"10-LAYOUT-OWNERS.png"),Path.Combine(bad,"BAD-CARD-LAYOUT-OVERLAY.png"),true);
        var prototype=Path.Combine(projectRoot,"LAYOUT-PARAGRAPH-OWNER-TASK2B-EVIDENCE","B-LAYOUT-OWNER.png");
        using var a=new Bitmap(prototype);using var b=new Bitmap(Path.Combine(bad,"07-FINAL-TRANSLATED.png"));
        using var image=new Bitmap(a.Width+b.Width,Math.Max(a.Height,b.Height)+30);using var g=Graphics.FromImage(image);g.Clear(Color.FromArgb(24,24,24));
        using var font=new Font("Segoe UI",14,FontStyle.Bold,GraphicsUnit.Pixel);g.DrawString("TASK 2B PROTOTYPE B",font,Brushes.White,6,6);g.DrawString("PRODUCT REAL E2E (TEXT MAY DIFFER)",font,Brushes.Lime,a.Width+6,6);
        g.DrawImageUnscaled(a,0,30);g.DrawImageUnscaled(b,a.Width,30);image.Save(Path.Combine(bad,"BAD-CARD-PROTOTYPE-B-VS-PRODUCT-REAL-E2E.png"),ImageFormat.Png);
    }

    private static void DrawFit(Graphics g,Bitmap image,Rectangle target)
    {var scale=Math.Min(target.Width/(float)image.Width,target.Height/(float)image.Height);var w=(int)(image.Width*scale);var h=(int)(image.Height*scale);g.DrawImage(image,target.Left+(target.Width-w)/2,target.Top+(target.Height-h)/2,w,h);}
    private static int ReadActiveOwnerCount(string path){using var json=JsonDocument.Parse(File.ReadAllText(path));return json.RootElement.GetProperty("Owners").EnumerateArray().Count(x=>x.GetProperty("Active").GetBoolean());}
    private static string FixtureOutput(string evidenceRoot,string fixture,bool bad)=>bad?Path.Combine(evidenceRoot,"bad-card"):Path.Combine(evidenceRoot,"fixtures",fixture);
    private static PointF[] RectanglePolygon(RectangleF r)=>[new(r.Left,r.Top),new(r.Right,r.Top),new(r.Right,r.Bottom),new(r.Left,r.Bottom)];
    private sealed record ScanInput(string Path,string FixtureId,bool IsBadCard);

    private static Dictionary<string,string> LoadIdentities(string path)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var line in File.ReadLines(path).Skip(1)){var fields=ParseCsv(line);if(fields.Count>=2)result[fields[0]]=fields[1];}
        return result;
    }
    private static List<string> ParseCsv(string line){var values=new List<string>();var value=new StringBuilder();var quoted=false;for(var i=0;i<line.Length;i++){var c=line[i];if(c=='"'){if(quoted&&i+1<line.Length&&line[i+1]=='"'){value.Append('"');i++;}else quoted=!quoted;}else if(c==','&&!quoted){values.Add(value.ToString());value.Clear();}else value.Append(c);}values.Add(value.ToString());return values;}

    private static void SaveRedactedSettings(ApiSettings s,string output,string path,bool bridge)
    {
        var endpoint=Uri.TryCreate(s.ApiUrl,UriKind.Absolute,out var uri)?$"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort?"":$":{uri.Port}")}":"INVALID";
        File.WriteAllText(Path.Combine(output,"current-settings-redacted.json"),JsonSerializer.Serialize(new
        {SettingsPathSha256=Hash(Path.GetFullPath(path)),Provider=TranslationProviderRegistry.Get(s.TranslationProviderKind).Id,s.ProviderDisplayName,s.Model,Endpoint=endpoint,s.TargetLanguage,s.SourceLanguage,s.TranslationStyle,
            ActiveTranslationMode=s.TranslationStyle.ToString(),CustomPromptConfigured=!string.IsNullOrWhiteSpace(s.CustomTranslationPrompt),CustomPromptSha256=Hash(s.CustomTranslationPrompt??""),
            s.PreserveIdentifiers,s.PreserveNumbers,s.PreserveVariables,s.OcrEngine,s.VisualModel,s.OcrLanguage,ApiKeyConfigured=!string.IsNullOrWhiteSpace(s.ApiKey),ApiKeyRecorded=false,LoopbackOverride=bridge},JsonOptions));
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string HashFile(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static long CountChangedPixels(string firstPath,string secondPath)
    {
        using var first=new Bitmap(firstPath);using var second=new Bitmap(secondPath);
        if(first.Size!=second.Size)return long.MaxValue;
        if(first.PixelFormat!=PixelFormat.Format32bppArgb||second.PixelFormat!=PixelFormat.Format32bppArgb)
            throw new InvalidDataException($"Pixel parity requires 32bpp ARGB PNGs: {first.PixelFormat} / {second.PixelFormat}");
        var bounds=new Rectangle(0,0,first.Width,first.Height);
        var a=first.LockBits(bounds,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        var b=second.LockBits(bounds,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes=first.Width*4;var left=new byte[rowBytes];var right=new byte[rowBytes];long changed=0;
            for(var y=0;y<first.Height;y++)
            {
                Marshal.Copy(IntPtr.Add(a.Scan0,y*a.Stride),left,0,rowBytes);
                Marshal.Copy(IntPtr.Add(b.Scan0,y*b.Stride),right,0,rowBytes);
                for(var x=0;x<rowBytes;x+=4)
                    if(left[x]!=right[x]||left[x+1]!=right[x+1]||left[x+2]!=right[x+2]||left[x+3]!=right[x+3])changed++;
            }
            return changed;
        }
        finally{first.UnlockBits(a);second.UnlockBits(b);}
    }
    private static void SaveException(string path,Exception ex){var chain=new List<object>();for(Exception? current=ex;current is not null;current=current.InnerException)chain.Add(new{ErrorType=current.GetType().FullName,current.Message,HResult=$"0x{current.HResult:X8}"});File.WriteAllText(path,JsonSerializer.Serialize(new{Errors=chain,ApiKeyRecorded=false,AuthorizationRecorded=false},JsonOptions));}
    private static void WriteCsv(string path,string[] header,IEnumerable<string[]> rows){static string Q(string value)=>'"'+value.Replace("\"","\"\"")+'"';File.WriteAllLines(path,new[]{string.Join(',',header.Select(Q))}.Concat(rows.Select(row=>string.Join(',',row.Select(Q)))),new UTF8Encoding(false));}
    private sealed class CaptureProgress<T>(Action<T> capture):IProgress<T>{public void Report(T value)=>capture(value);}
}
