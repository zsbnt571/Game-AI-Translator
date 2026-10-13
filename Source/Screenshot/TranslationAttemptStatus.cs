namespace ScreenshotTranslationUiTester;

internal sealed class TranslationHttpException(int status,string message):InvalidOperationException(message)
{
    internal int StatusCode {get;}=status;
}
internal static class TranslationAttemptStatus
{
    // A malformed batch should not be resubmitted in the same shape forever.
    // Transport/auth failures retain ordinary batching and the same backoff.
    internal static bool RetryIndividually(string code)=>code is "missing-entry" or "invalid-response" or "placeholder-mismatch" or "quality-rejected" or "empty-result";
    internal static string Reason(string code)=>code switch
    {
        "provider-rate-limited"=>"翻译接口限流",
        "provider-authentication"=>"翻译接口身份验证或权限失败",
        "provider-timeout"=>"翻译接口超时",
        "provider-failed"=>"翻译接口请求失败",
        "empty-result"=>"翻译接口返回空译文",
        "missing-entry"=>"批量回复缺少本条译文",
        "invalid-response"=>"翻译回复格式不正确",
        "placeholder-mismatch"=>"译文的变量或控制符校验未通过",
        "quality-rejected"=>"译文未通过自动质量检查",
        _=>"未得到可用译文"
    };
    internal static string Issue(string code,int attempts)=>Reason(code)+
        (attempts>=4?"；自动重试已停止，可在译文管理中重试。":"；将自动重试。")+"原有译文保留。";
    internal static string Summary(int retrying,int exhausted)=>
        (retrying>0?$" · 自动重试中 {retrying} 条":"")+
        (exhausted>0?$" · 自动重试已停止 {exhausted} 条，可手动重试":"");
}
