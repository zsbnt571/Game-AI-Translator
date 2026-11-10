namespace ScreenshotTranslationUiTester;

// Read current queue state each time the UI renders. A retry request is not a
// success receipt, and another action replaces this feedback in the session.
internal static class TranslationRetryFeedback
{
    internal static string Describe(EmbeddedTranslationQueueSnapshot snapshot,bool enabled,bool restoring,int failed)
    {
        if(restoring)return UiLanguage.Text("正在关闭翻译。尚未解决的失败记录和已有译文仍保留。");
        if(!enabled)return UiLanguage.Text("翻译已关闭。尚未解决的失败记录和已有译文仍保留。");
        if(snapshot.RetryScheduledTexts>0||snapshot.ActiveRequests>0)
            return UiLanguage.Format($"正在处理翻译：{Math.Max(0,snapshot.ActiveRequests):N0} 条；等待重试：{Math.Max(0,snapshot.RetryScheduledTexts):N0} 条。当前对话优先，已有译文保留。");
        int remaining=Math.Max(Math.Max(0,failed),snapshot.RetryStoppedTexts);
        if(remaining>0)
            return UiLanguage.Format($"本轮重试已停止，仍有 {remaining:N0} 条失败记录。可在“翻译状态”查看原因；已有译文保留。");
        return UiLanguage.Text("本轮重试已结束，当前没有待重试的失败项。实际显示以游戏画面为准。");
    }
}
