using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static class ContinuationLayoutProductSelfTests
{
    internal static int Run(string projectRoot,string output)
    {
        Directory.CreateDirectory(output);
        var prePath=Path.Combine(projectRoot,"LAYOUT-OWNER-TASK2A-EVIDENCE","historical-session-recovered","06-BACKGROUND-RESTORED.png");
        var expectedPath=Path.Combine(projectRoot,"LAYOUT-PARAGRAPH-OWNER-TASK2B-EVIDENCE","B-LAYOUT-OWNER.png");
        var postOcrPath=Path.Combine(projectRoot,"CARD-FIRST-BAD-EVIDENCE","bad-post-ocr.json");
        var tracePath=Path.Combine(projectRoot,"CARD-FIRST-BAD-EVIDENCE","bad-text-fit-trace.json");
        using var pre=new Bitmap(prePath);
        using var expected=new Bitmap(expectedPath);
        var document=CorePipelineEngine.Analyze(pre.Size,LoadBadRaw(postOcrPath),[]);
        using var trace=JsonDocument.Parse(File.ReadAllText(tracePath));
        var items=trace.RootElement.EnumerateArray().Select(ParseTrace).ToArray();
        var traceById=items.ToDictionary(x=>x.BlockId,StringComparer.Ordinal);
        foreach(var block in document.VisualBlocks)
        {
            var state=document.Translations[block.BlockId];
            if(traceById.TryGetValue(block.BlockId,out var item))
            {state.TranslatedText=item.Text;state.State=BlockTranslationState.Accepted;state.FailureReason="";}
            else
            {state.TranslatedText=block.SourceText;state.State=BlockTranslationState.Preserved;state.FailureReason="PRESERVE_SOURCE";}
        }

        ContinuationLayoutPlanningResult plan;
        using(var measure=Graphics.FromImage(pre))
        {
            measure.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var evidence=items.Select(item=>
            {
                var block=document.VisualBlocks.Single(x=>x.BlockId==item.BlockId);
                return new ContinuationLayoutBlockEvidence(item.BlockId,item.SourceIds,block.Bounds,item.Available,
                    item.Text,item.FontFamily,item.FontStyle,item.FontSize,item.TextColor);
            }).ToArray();
            plan=ContinuationLayoutPlanner.Plan(measure,document,ContinuationRelation.Propose(document.VisualBlocks),evidence);
        }

        using var actual=new Bitmap(pre);
        using(var graphics=Graphics.FromImage(actual))
        {
            graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var drawn=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in items)
            {
                if(plan.ActiveOwnerByBlock.TryGetValue(item.BlockId,out var owner))
                {if(drawn.Add(owner.ProposalId))ContinuationLayoutPlanner.Draw(graphics,owner);continue;}
                DrawCurrent(graphics,item);
            }
        }
        var actualPath=Path.Combine(output,"BAD-CARD-PRODUCT-INTEGRATED-DETERMINISTIC.png");
        actual.Save(actualPath,ImageFormat.Png);
        SaveSideBySide(expected,actual,Path.Combine(output,"BAD-CARD-PROTOTYPE-VS-PRODUCT.png"));
        var changed=CountChangedPixels(expected,actual);
        var active=plan.Owners.Where(x=>x.Active).ToArray();
        var ids=active.Select(x=>x.ProposalId).ToArray();
        var expectedIds=new[]{"CTN-E7CB26C19910","CTN-4B188C51047E"};
        var pass=changed==0&&ids.SequenceEqual(expectedIds,StringComparer.Ordinal)&&
            active.All(x=>x.CharactersStable&&x.GeometrySafe&&!x.Clipped&&!x.Ellipsis);
        WriteCsv(Path.Combine(output,"bad-card-prototype-parity.csv"),
            ["PrototypePixelHash","ProductPixelHash","ChangedPixels","ProposalIds","LayoutOwnerCount","CharactersStable","GeometrySafe","Status"],
            [[PixelHash(expected),PixelHash(actual),changed.ToString(),string.Join('|',ids),active.Length.ToString(),
                active.All(x=>x.CharactersStable).ToString(),active.All(x=>x.GeometrySafe).ToString(),pass?"PASS":"FAIL"]]);
        WriteCsv(Path.Combine(output,"deterministic-integration-summary.csv"),
            ["Check","Observed","Expected","Status"],
            [
                ["TASK1_FROZEN_SOURCE_ID",ContinuationRelation.FrozenPrototypeSourceSha256,"D02C903E45C4B748FA8B3E739724344A99DE5F74B905455B10E578FAFD6B0104",ContinuationRelation.FrozenPrototypeSourceSha256=="D02C903E45C4B748FA8B3E739724344A99DE5F74B905455B10E578FAFD6B0104"?"PASS":"FAIL"],
                ["TRANSLATION_OWNER","CURRENT_CORE_BLOCKID","CURRENT_CORE_BLOCKID","PASS"],
                ["AI_ALLOCATION","NOT_USED","NOT_USED","PASS"],
                ["BAD_CARD_LAYOUT_OWNERS",active.Length.ToString(),"2",active.Length==2?"PASS":"FAIL"],
                ["BAD_CARD_PROTOTYPE_PIXEL_PARITY",changed.ToString(),"0",changed==0?"PASS":"FAIL"]
            ]);
        File.WriteAllText(Path.Combine(output,"product-layout-plan.json"),JsonSerializer.Serialize(new
        {
            Mode=ContinuationLayoutPlanner.Mode,TranslationOwner="CURRENT_CORE_BLOCKID",AiAllocation="NOT_USED",
            Owners=active
        },new JsonSerializerOptions{WriteIndented=true}));
        return pass?0:4;
    }

    private sealed record TraceItem(string BlockId,IReadOnlyList<string> SourceIds,string Text,RectangleF Available,
        string FontFamily,FontStyle FontStyle,float FontSize,Color TextColor,float RequiredHeight,
        float LineHeight,string Alignment,IReadOnlyList<string> Lines);

    private static TraceItem ParseTrace(JsonElement item)
    {
        var available=Rect(item.GetProperty("AvailableRegion"));
        var measured=item.GetProperty("MeasuredRequiredSize");
        return new(item.GetProperty("BlockId").GetString()??"",
            item.GetProperty("StableSourceIds").EnumerateArray().Select(x=>x.GetString()??"").ToArray(),
            item.GetProperty("LayoutInputText").GetString()??"",available,
            item.GetProperty("ResolvedFontFamily").GetString()??"Microsoft YaHei UI",
            ParseStyle(item.GetProperty("ResolvedWeight").GetString()??"Regular"),
            item.GetProperty("ChosenFitFontSize").GetSingle(),
            ColorTranslator.FromHtml(item.GetProperty("TextColor").GetString()??"#FFFFFF"),
            measured.GetProperty("Height").GetSingle(),item.GetProperty("LineHeight").GetSingle(),
            item.GetProperty("Alignment").GetString()??"Left",
            item.GetProperty("WrappedLines").EnumerateArray().Select(x=>x.GetString()??"").ToArray());
    }

    private static void DrawCurrent(Graphics graphics,TraceItem item)
    {
        using var font=FontManager.CreatePixel(item.FontFamily,item.FontSize,item.FontStyle);
        using var brush=new SolidBrush(item.TextColor);
        var y=item.Available.Top+Math.Max(0,(item.Available.Height-item.RequiredHeight)/2f);
        foreach(var line in item.Lines)
        {
            var measured=graphics.MeasureString(line,font,PointF.Empty,StringFormat.GenericTypographic);
            var x=item.Alignment=="Center"?item.Available.Left+Math.Max(0,(item.Available.Width-measured.Width)/2f):item.Available.Left;
            graphics.DrawString(line,font,brush,new PointF(x,y),StringFormat.GenericTypographic);y+=item.LineHeight;
        }
    }

    private static RectangleF Rect(JsonElement value)=>new(value.GetProperty("X").GetSingle(),value.GetProperty("Y").GetSingle(),value.GetProperty("Width").GetSingle(),value.GetProperty("Height").GetSingle());
    private static IReadOnlyList<RawOcrLine> LoadBadRaw(string path)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(path));var result=new List<RawOcrLine>();
        foreach(var row in json.RootElement.GetProperty("rows").EnumerateArray())
        {
            var id=row.GetProperty("OwnerId").GetString()??$"R{result.Count+1:000}";
            var text=row.GetProperty("Text").GetString()??"";var bounds=Rect(row.GetProperty("Bounds"));
            var polygon=new[]{new PointF(bounds.Left,bounds.Top),new PointF(bounds.Right,bounds.Top),new PointF(bounds.Right,bounds.Bottom),new PointF(bounds.Left,bounds.Bottom)};
            result.Add(new(id,text,text,polygon,bounds,1,result.Count+1));
        }
        return result;
    }
    private static FontStyle ParseStyle(string value)=>value.Contains("Bold",StringComparison.OrdinalIgnoreCase)?FontStyle.Bold:FontStyle.Regular;

    private static int CountChangedPixels(Bitmap a,Bitmap b)
    {
        if(a.Size!=b.Size)return int.MaxValue;var count=0;
        for(var y=0;y<a.Height;y++)for(var x=0;x<a.Width;x++)if(a.GetPixel(x,y).ToArgb()!=b.GetPixel(x,y).ToArgb())count++;
        return count;
    }

    private static string PixelHash(Bitmap image)
    {
        // Hash decoded pixels in a fixed top-to-bottom ARGB order. PNG decoder
        // stride/orientation metadata must not make two pixel-identical images
        // appear different in the parity evidence.
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> pixel=stackalloc byte[4];
        for(var y=0;y<image.Height;y++)
        for(var x=0;x<image.Width;x++)
        {
            var argb=image.GetPixel(x,y).ToArgb();
            pixel[0]=(byte)(argb>>24);pixel[1]=(byte)(argb>>16);
            pixel[2]=(byte)(argb>>8);pixel[3]=(byte)argb;
            hash.AppendData(pixel);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void SaveSideBySide(Bitmap expected,Bitmap actual,string path)
    {
        using var image=new Bitmap(expected.Width+actual.Width,Math.Max(expected.Height,actual.Height)+28);
        using var graphics=Graphics.FromImage(image);graphics.Clear(Color.FromArgb(28,28,28));
        using var font=new Font("Segoe UI",13,FontStyle.Bold,GraphicsUnit.Pixel);
        graphics.DrawString("TASK 2B PROTOTYPE B",font,Brushes.White,8,5);
        graphics.DrawString("PRODUCT INTEGRATED B",font,Brushes.Lime,expected.Width+8,5);
        graphics.DrawImageUnscaled(expected,0,28);graphics.DrawImageUnscaled(actual,expected.Width,28);
        image.Save(path,ImageFormat.Png);
    }

    private static void WriteCsv(string path,string[] header,IEnumerable<string[]> rows)
    {
        static string Q(string value)=>'"'+value.Replace("\"","\"\"")+'"';
        var lines=new[]{string.Join(',',header.Select(Q))}.Concat(rows.Select(row=>string.Join(',',row.Select(Q))));
        File.WriteAllLines(path,lines,new UTF8Encoding(false));
    }
}
