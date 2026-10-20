using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Split only software-owned diagnostic envelopes. Provider text and user values
// stay intact unless the entire message has an explicit catalog translation.
internal static class UiDiagnosticText
{
    internal static string? Translate(string source)
    {
        // Error wrappers contain another software diagnostic, not a user title
        // or model-generated text. Translate the reason before template matching.
        foreach(var prefix in new[]{"翻译内容保存失败：","日志摘要读取失败：","失败："})
            if(source.StartsWith(prefix,StringComparison.Ordinal)&&source.Length>prefix.Length)
                return UiLanguage.Text(prefix)+UiLanguage.Text(source[prefix.Length..]);
        // These are composed exclusively from product statuses. Split before
        // wildcard templates can consume a second, still-Chinese status.
        if(source.StartsWith("原文：",StringComparison.Ordinal)&&source.Contains(" · "))
            return string.Join(" · ",source.Split(" · ").Select(UiLanguage.Text));
        var font=Regex.Match(source,@"^(Microsoft YaHei UI|Segoe UI|Noto Sans CJK SC)：(已安装|未安装)$");
        if(font.Success)return font.Groups[1].Value+": "+UiLanguage.Text(font.Groups[2].Value);
        var godot=Regex.Match(source,@"^Godot (写入请求|发布请求|读取回包)失败（(.*?)，HResult 0x(.*?)，(.*?)）。$",RegexOptions.Singleline);
        if(godot.Success)return UiLanguage.Format($"Godot {UiLanguage.Text(godot.Groups[1].Value)}失败（{godot.Groups[2].Value}，HResult 0x{godot.Groups[3].Value}，{UiLanguage.Text(godot.Groups[4].Value)}）。");
        var sourceLanguage=Regex.Match(source,@"^原文：(.*)$");
        if(sourceLanguage.Success)return UiLanguage.Format($"原文：{UiLanguage.Text(sourceLanguage.Groups[1].Value)}");
        var ocr=Regex.Match(source,@"^(.*?) OCR 失败：(.*)$",RegexOptions.Singleline);
        if(ocr.Success)return UiLanguage.Format($"{ocr.Groups[1].Value} OCR 失败：{UiLanguage.Text(ocr.Groups[2].Value)}");
        var ocrFallback=Regex.Match(source,@"^(.*?)本次不可用，已临时使用Windows OCR：(.*?): (.*)$",RegexOptions.Singleline);
        if(ocrFallback.Success)return UiLanguage.Format($"{ocrFallback.Groups[1].Value}本次不可用，已临时使用Windows OCR：{ocrFallback.Groups[2].Value}: {UiLanguage.Text(ocrFallback.Groups[3].Value)}");
        const string recent="最近报告：",tracking="退出监测中。 ";
        if(source.StartsWith(recent,StringComparison.Ordinal))
            return UiLanguage.Format($"最近报告：{UiLanguage.Text(source[recent.Length..])}");
        if(source.StartsWith(tracking,StringComparison.Ordinal))
            return UiLanguage.Text("退出监测中。")+" "+UiLanguage.Text(source[tracking.Length..]);
        // Old saved exit reports contain a canonical summary plus optional
        // product-owned suffixes. Translate each sentence, including its status.
        foreach(string marker in new[]{" 此前已请求主动退出（"," 监测期间曾请求退出，"," 诊断文件保存失败（"})
        {
            int at=source.IndexOf(marker,StringComparison.Ordinal);
            if(at>0)return UiLanguage.Text(source[..at])+" "+UiLanguage.Text(source[(at+1)..]);
        }
        var expected=Regex.Match(source,@"^此前已请求主动退出（(.+)）。$");
        if(expected.Success)return UiLanguage.Format($"此前已请求主动退出（{UiLanguage.Text(expected.Groups[1].Value)}）。");
        var state=Regex.Match(source,@"^接入=(.*?)；翻译=(.*?)；屏幕兜底=(.*?)；失败待重试=(\d+)；操作进行中=(.*?)；存在接入限制=(.*?)$");
        if(state.Success)return UiLanguage.Format($"接入={UiLanguage.Text(state.Groups[1].Value)}；翻译={UiLanguage.Text(state.Groups[2].Value switch{"开启"=>"已开启","关闭"=>"已关闭",_=>state.Groups[2].Value})}；屏幕兜底={UiLanguage.Text(state.Groups[3].Value)}；失败待重试={state.Groups[4].Value}；操作进行中={UiLanguage.Text(state.Groups[5].Value)}；存在接入限制={UiLanguage.Text(state.Groups[6].Value)}");
        var process=Regex.Match(source,@"^PID (\d+)；退出码 (.+)$");
        if(process.Success)return UiLanguage.Format($"PID {process.Groups[1].Value}；退出码 {UiLanguage.Text(process.Groups[2].Value)}");
        var failed = Regex.Match(source, @"^测试失败 · ([\d.,]+) 秒[：:](.*)$", RegexOptions.Singleline);
        if (failed.Success)
            return UiLanguage.Format($"测试失败 · {failed.Groups[1].Value} 秒：{UiLanguage.Text(failed.Groups[2].Value)}");
        const string missing = "无法测试，请填写：";
        if (source.StartsWith(missing, StringComparison.Ordinal))
            return UiLanguage.Format($"无法测试，请填写：{string.Join(", ", source[missing.Length..].Split('、').Select(UiLanguage.Text))}");
        var exception = Regex.Match(source, @"^([A-Za-z][A-Za-z0-9.]*Exception): (.+)$", RegexOptions.Singleline);
        if (exception.Success)
            return exception.Groups[1].Value + ": " + UiLanguage.Text(exception.Groups[2].Value);
        var response = Regex.Match(source, @"^API 返回 (\d+)[：:]\s*(.*)$", RegexOptions.Singleline);
        if (response.Success)
            return UiLanguage.Format($"API 返回 {response.Groups[1].Value}：{UiLanguage.Text(response.Groups[2].Value)}");
        return null;
    }
}
