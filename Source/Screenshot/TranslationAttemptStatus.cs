namespace ScreenshotTranslationUiTester;

internal sealed class TranslationHttpException(int status,string message):InvalidOperationException(message)
{
    internal int StatusCode {get;}=status;
}
internal static class TranslationAttemptStatus
{
    // A malformed batch should not be resubmitted in the same shape forever.
    // Transport/auth failures retain ordinary batching and the same backoff.
    internal static bool ContentRejected(string code)=>code is "placeholder-mismatch" or "quality-rejected" or "numeric-mismatch" or "term-mismatch" or "protected-term-mismatch" or "menu-name-mismatch" or "language-boundary" or "untranslated-content" or "target-script-mismatch" or "nontranslation-output" or "format-artifact";
    internal static bool RetryIndividually(string code)=>ContentRejected(code)||code is "missing-entry" or "invalid-response" or "empty-result";
    internal static string Reason(string code)=>code switch
    {
        "provider-rate-limited"=>"翻译接口限流",
        "provider-authentication"=>"翻译接口身份验证或权限失败",
        "provider-timeout"=>"翻译接口超时",
        "provider-failed"=>"翻译接口请求失败",
        "empty-result"=>"翻译接口返回空译文",
        "missing-entry"=>"批量回复缺少本条译文",
        "invalid-response"=>"翻译回复格式不正确",
        "placeholder-mismatch"=>"译文缺少、重复或改动了保护标记（变量、数字或控制符）",
        "numeric-mismatch"=>"译文中的数字与原文不一致",
        "term-mismatch"=>"译文未使用统一译名，或改动了要求保留原文的词语",
        "protected-term-mismatch"=>"译文改动了明确要求保留原文的标识",
        "menu-name-mismatch"=>"译文改动了菜单中应保留的名称",
        "language-boundary"=>"译文改动了不应翻译的语言片段",
        "untranslated-content"=>"需要翻译的内容仍保留原文",
        "target-script-mismatch"=>"译文出现与目标语言不符的文字",
        "nontranslation-output"=>"接口返回了解释或拒答，未得到正文译文",
        "format-artifact"=>"译文存在格式残留",
        "quality-rejected"=>"译文未通过自动质量检查",
        _=>"未得到可用译文"
    };
    internal static string Issue(string code,int attempts)=>Reason(code)+
        (attempts>=4?"；自动重试已停止，可在译文管理中重试。":"；将自动重试。")+"原有译文保留。";
    internal static TranslationWorkspaceStore.Issue Evidence(string source,string code,int attempts,string reply,
        IReadOnlyDictionary<string,string> terms,bool soft=false,string? displayValue=null)
    {
        string detail=code=="term-mismatch"
            ?"本次适用词条："+System.Text.Json.JsonSerializer.Serialize(TranslationGlossary.Applicable(terms,[source]))
            :code=="untranslated-content"?"检查仍未翻译的外语片段；可用部分保留，完整原文照抄不视为完成。"
            :Reason(code);
        return new(source,(soft?"已有可用译文，待改善：":"")+Issue(code,attempts),DateTime.UtcNow)
            {Code=code,Reply=reply,Detail=detail,Attempts=attempts,Soft=soft,
             TextHash=TextHash(displayValue??reply),TermStamp=TranslationGlossary.Stamp(terms,source)};
    }
    internal static string TextHash(string text)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    internal static string Summary(int retrying,int exhausted)=>
        (retrying>0?$" · 自动重试中 {retrying} 条":"")+
        (exhausted>0?$" · 自动重试已停止 {exhausted} 条，可手动重试":"");
}
