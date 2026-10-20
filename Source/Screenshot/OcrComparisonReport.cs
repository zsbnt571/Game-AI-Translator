namespace ScreenshotTranslationUiTester;

public sealed partial class OcrComparisonForm
{
    // Localize only report labels and software diagnostics. OCR text and model
    // identifiers remain exact, including Chinese text in an English/Russian UI.
    internal static string FormatResult(OcrEngineResult result)
    {
        var lines=new List<string>();
        void Add(string caption,object? value)=>lines.Add(UiLanguage.Text(caption)+": "+value);
        string Status(bool available)=>UiLanguage.Text(available?"可用":"不可用");
        Add("质量比较",UiLanguage.Text(result.ValidForQualityComparison?"结果有效":"结果无效：使用了回退引擎"));
        Add("请求引擎",result.EngineRequested);Add("实际引擎",result.EngineActual);
        Add("图像会话",result.ImageSessionId);Add("请求标识",result.RequestId);Add("图像哈希",result.ImageHash);
        Add("引擎实例",result.EngineInstanceId);Add("模型",result.ModelName);Add("模型版本",result.ModelVersion);
        Add("总耗时",result.TotalMilliseconds+" ms");Add("模型加载",result.ModelLoadMilliseconds+" ms");
        Add("文字校正",result.CorrectionMilliseconds+" ms");Add("预处理重试",result.PreprocessRetryMilliseconds+" ms");
        Add("检测文本块",result.Blocks.Count);
        Add("置信度",result.ConfidenceAvailableBlockCount==0?Status(false):result.ConfidenceAvailableBlockCount+"/"+result.Blocks.Count);
        Add("低置信度文本块",result.ConfidenceAvailableBlockCount==0?Status(false):result.LowConfidenceBlockCount);
        Add("已校正文本块",result.CorrectedBlockCount);Add("校正次数",result.CorrectionCount);
        Add("歧义文本块",result.AmbiguousBlockCount);Add("疑似误识别",result.FalsePositiveSuspectCount);
        Add("重试次数",result.PreprocessRetryCount);Add("重试原因",UiLanguage.Text(result.RetryReason));
        Add("重试预处理",result.RetryPreprocessType);Add("重试原文",result.RetryRawText);Add("采用文本",result.RetryChosenText);
        Add("字符候选",Status(result.CharacterTopKAvailable));Add("工作集",(result.WorkingSetBytes/1024d/1024d).ToString("0.0")+" MiB");
        Add("质量评估",UiLanguage.Text("尚未评级"));Add("使用回退",UiLanguage.Text(result.FallbackUsed?"是":"否"));
        Add("回退原因",UiLanguage.Text(result.FallbackReason));
        if(!result.ValidForQualityComparison)lines.Add(UiLanguage.Text("本次实际引擎与请求不一致，不可用于该引擎质量比较。"));
        lines.Add("\n=== "+UiLanguage.Text("OCR 原文")+" ===\n"+result.RawText);
        lines.Add("\n=== "+UiLanguage.Text("校正后原文")+" ===\n"+result.CorrectedText);
        foreach(var block in result.Blocks)
        {
            foreach(var correction in block.Corrections)
                lines.Add($"[{block.Id}] {correction.Original} → {correction.Corrected} | {UiLanguage.Text(correction.Reason)} | {correction.Confidence}");
            if(block.UnresolvedAmbiguity)
                lines.Add($"[{block.Id}] "+UiLanguage.Text("尚未解决的歧义")+": "+block.RawText+" | "+block.AmbiguityCandidates);
        }
        return string.Join("\n",lines);
    }
}
