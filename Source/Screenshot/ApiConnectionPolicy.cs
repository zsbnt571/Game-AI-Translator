namespace ScreenshotTranslationUiTester;

// Every host entry uses the same connection contract, including local servers.
internal static class ApiConnectionPolicy
{
    internal const string NoCredentialHelp = "仅当接口无需鉴权时勾选，并将密钥留空；已填写的密钥仍会发送。本地模型需先启动兼容 OpenAI 的服务，再填写接口地址和模型。";
    internal static bool HasKey(ApiSettings settings) => !string.IsNullOrWhiteSpace(settings.ApiKey);
    internal static List<string> Missing(ApiSettings settings)
    {
        var missing = new List<string>();
        if (!Uri.TryCreate(settings.ApiUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            missing.Add("有效的 http/https 接口地址");
        if (string.IsNullOrWhiteSpace(settings.Model)) missing.Add("模型");
        if (!settings.AllowEmptyApiKey && !HasKey(settings)) missing.Add("API 密钥");
        return missing;
    }
    internal static bool IsConfigured(ApiSettings settings) => Missing(settings).Count == 0;
    internal static void Validate(ApiSettings settings)
    {
        var missing = Missing(settings);
        if (missing.Count > 0)
            throw new InvalidOperationException("翻译接口尚缺：" + string.Join("、", missing) + "。若服务无需鉴权，请勾选“无需密钥（本地／免鉴权）”。");
    }
    internal static string HttpFailure(int status, bool hasKey, string detail)
    {
        var hint = status switch
        {
            401 or 403 when !hasKey => "服务拒绝了免鉴权请求。请确认该接口允许无密钥访问；需要鉴权时请填写有效密钥。",
            401 or 403 => "服务拒绝了鉴权或访问权限，请检查密钥及模型权限。",
            404 => "服务未找到请求的接口或模型，请核对接口地址和模型名称。",
            429 => "服务限制了请求，请检查额度或稍后重试。",
            >= 500 => "翻译服务发生错误，请检查服务状态或稍后重试。",
            _ => "翻译服务拒绝了请求，请核对接口配置和请求参数。"
        };
        return $"HTTP {status} · {hint}" + (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail);
    }
}
