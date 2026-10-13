namespace ScreenshotTranslationUiTester;

// A projection of observed facts, not a screen reader. Unknown rendering stays
// unknown even after every known source has a cached translation.
internal sealed record TranslationProgressPresentation(string Summary,string NextStep,string[] Details)
{
    internal static string Overview(EmbeddedTranslationQueueSnapshot s)
    {
        // RuntimeOutsideDirectory is disjoint and deduplicated; runtime texts
        // already in the catalog must not be added again.
        int total=Math.Max(0,s.DirectoryTexts)+Math.Max(0,s.RuntimeOutsideDirectory);
        int done=Math.Clamp(s.DirectoryCachedTexts,0,Math.Max(0,s.DirectoryTexts))+Math.Clamp(s.RuntimeOutsideDirectoryCached,0,Math.Max(0,s.RuntimeOutsideDirectory));
        int remaining=Math.Max(0,total-done),active=Math.Min(remaining,Math.Max(0,s.ActiveRequests));
        int retry=Math.Min(remaining-active,Math.Max(0,s.RetryScheduledTexts)+Math.Max(0,s.RetryStoppedTexts));
        string result=total==0?"翻译进度　等待可译文字":$"翻译进度　{Math.Floor(100d*done/total):0}%";
        if(total>0&&s.CatalogReading)result+="（已读部分）";
        result+=$"\n已有译文 {done:N0} / {total:N0} 条";
        result+=$"\n正在翻译 {active:N0} 条 · 排队 {Math.Max(0,remaining-active-retry):N0} 条";
        result+=s.CatalogReading?"\n正在读取更多文字，数量和进度会继续变化":"\n按已发现的可译文本统计";
        var limits=new List<string>();
        if(s.RetryStoppedTexts>0)limits.Add($"暂未译成 {s.RetryStoppedTexts:N0} 条（可查看原因并重试）");
        if(s.RetryScheduledTexts>0)limits.Add($"等待重试 {s.RetryScheduledTexts:N0} 条");
        if(s.ReadOnlyTexts>0)limits.Add($"暂不能原位显示 {s.ReadOnlyTexts:N0} 条（只能读取）");
        if(s.FontBlockedBindings is >0)limits.Add($"字体受限 {s.FontBlockedBindings:N0} 处");
        if(s.CatalogIncomplete)limits.Add($"未完整读取 {s.CatalogIssueCount:N0} 处来源");
        if(limits.Count>0)result+="\n"+string.Join(" · ",limits);
        result+="\n图片内文字、尚未识别的文字：数量未知";
        if(s.BridgePaused)result+="\n游戏文字更新暂缓，恢复后继续应用译文";
        return result;
    }
    internal static TranslationProgressPresentation Describe(EmbeddedTranslationQueueSnapshot s)
    {
        string[] reasons=(s.FailureCodes??[]).Select(TranslationAttemptStatus.Reason).Distinct().ToArray();
        string summary,step;
        if(s.RetryStoppedTexts>0)
        {summary=$"{s.RetryStoppedTexts} 条未得到可用译文，自动重试已停止。";step="查看“翻译状态”中的原因；处理后点击“重试失败”，或到“译文与术语”更正单条文字。";}
        else if(s.RetryScheduledTexts>0)
        {summary=$"{s.RetryScheduledTexts} 条暂未翻译成功，正在等待自动重试。";step=reasons.Length>0?string.Join("；",reasons)+"。已有译文继续保留。":"已有译文继续保留，可在“翻译状态”查看详情。";}
        else if(s.FontBlockedBindings is >0)
        {summary=$"{s.FontBlockedBindings} 处文字的字体暂不能显示译文。";step="重新翻译通常不能解决缺字。请保留当前画面并导出翻译状态报告。";}
        else if(s.BridgePaused)
        {summary="游戏内文字刷新暂未应答，后台翻译仍在继续。";step="返回游戏后会继续尝试应用译文；若长期没有恢复，请导出翻译状态报告。";}
        else if(s.ActiveRequests>0)
        {summary=$"正在处理 {s.ActiveRequests} 条文字。";step="当前收到的文字优先处理，已有译文继续使用。";}
        else if(s.PendingBridgeEntries>0||s.NativeWritebackPending is >0||s.ActiveCachedWaitingBindings is >0)
        {summary="已有译文正在等待游戏应用。";step="先返回游戏查看文字；若仍保留原文，请导出翻译状态报告，无需清空译文。";}
        else if(s.RuntimeCapturedTexts==0&&s.ReadOnlyTexts==0)
        {summary=s.CatalogReading?"正在读取游戏文字，尚未收到运行中的文字。":"尚未收到运行中的文字。";step="进入包含菜单或对话的页面；若仍没有变化，请导出翻译状态报告。";}
        else if(s.RuntimeEligibleTexts==0&&s.ReadOnlyTexts>0)
        {summary="已读到文字，当前通道只能提供单独的译文。";step="可查看运行时译文窗口；这些文字尚不能在游戏原位置替换。";}
        else if(s.RuntimeEligibleTexts==0&&s.RuntimeExcludedTexts>0)
        {summary="收到的文字被当前原文语言设置筛选。";step="检查翻译方案中的原文语言；游戏混用多种语言时可选“自动”或“混合语言”。";}
        else if(s.CatalogReading)
        {summary="已接收游戏文字，正在继续读取文本目录。";step="当前文字继续翻译；目录读取完成不代表整个游戏都已覆盖。";}
        else if(s.RuntimeEligibleTexts>s.RuntimeCachedTexts||s.DirectoryTexts>s.DirectoryCachedTexts)
        {summary="已收到文字，正在排队准备译文。";step="当前文字优先处理，可在“翻译状态”查看各阶段数量。";}
        else
        {summary="已为当前接入收到的可译文字准备译文。";step="实际显示以游戏画面为准。若同页仍有原文，请保留画面并导出翻译状态报告。";}
        if((s.RetryScheduledTexts>0||s.RetryStoppedTexts>0)&&(s.FailureCodes??[]).Contains("provider-authentication"))
            step="翻译接口身份验证或权限失败。请到翻译方案的“编辑”中检查配置和账户权限，再开启翻译。";
        string Number(int? value)=>value?.ToString()??"游戏未提供此信息";
        var details=new List<string>
        {
            $"提前提取：{s.DirectoryTexts} 条；已有译文 {s.DirectoryCachedTexts} 条。",
            $"运行中收到：{s.RuntimeCapturedTexts} 条；可译 {s.RuntimeEligibleTexts} 条；已有译文 {s.RuntimeCachedTexts} 条。",
            $"目录以外：{s.RuntimeOutsideDirectory} 条；已有译文 {s.RuntimeOutsideDirectoryCached} 条。",
            $"按原文语言筛选：目录 {s.CatalogExcludedTexts} 条，运行中 {s.RuntimeExcludedTexts} 条；仅变量或控制符 {s.RuntimeNonTextTexts} 条。",
            $"接口处理：{s.ActiveRequests} 条；等待重试 {s.RetryScheduledTexts} 条；停止重试 {s.RetryStoppedTexts} 条。",
            $"游戏缓存已接收：{s.BridgeStoredTexts} 条；等待缓存确认 {s.PendingBridgeEntries} 条。",
            $"当前控件取用译文：{Number(s.ActiveRenderedBindings)}；有缓存但待确认：{Number(s.ActiveCachedWaitingBindings)}。",
            $"游戏报告待更新控件：{Number(s.NativeWritebackPending)}；字体受阻控件：{Number(s.FontBlockedBindings)}。",
            $"累计文字更新：{Number(s.WritebacksConfirmed)}；累计写回失败：{Number(s.WritebacksFailed)}。（含重复刷新，不是接口请求次数。）"
        };
        if(s.CatalogReading)details.Add("文本目录仍在读取，当前数量可能继续增加。");
        if(s.CatalogRetryPending)details.Add("游戏文本目录读取暂未完成，将自动重试；当前文字继续处理。");
        if(s.CatalogIncomplete)details.Add($"部分来源尚未完整读取（已记录 {s.CatalogIssueCount} 处限制或错误），运行时继续补译。");
        if(s.ReadOnlyTexts>0)details.Add($"另有 {s.ReadOnlyTexts} 条只能读取、不能内嵌替换的文字。");
        if(reasons.Length>0)details.Add("本次失败原因："+string.Join("；",reasons)+"。");
        details.Add("以上数量仅描述本次接入收到的文字；没有收到的文字无法计数。缓存和控件回执不能代替画面验收。");
        return new(summary,step,details.ToArray());
    }
}
