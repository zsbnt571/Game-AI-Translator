using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Product errors use the selected UI language. Unknown OS/provider diagnostics
// belong in the log, never in place of a localized user-facing explanation.
internal static class UiOperationText
{
    private static readonly HashSet<string> Logged=[];
    internal static string Diagnostic(string diagnostic)
    {
        string safe=SafeDiagnosticOutput.Redact(diagnostic);
        lock(Logged){if(Logged.Count>=128)Logged.Clear();if(Logged.Add(safe))AppLog.Write("ui-diagnostic",safe);}
        return ErrorText(safe);
    }
    internal static string Error(Exception error)
    {
        AppLog.Write("ui-operation",SafeDiagnosticOutput.ExceptionSummary(error));
        return ErrorText(SafeDiagnosticOutput.ExceptionSummary(error));
    }
    internal static string ErrorText(string diagnostic)
    {
        string message=Regex.Replace(diagnostic,@"^[A-Za-z][A-Za-z0-9.]*Exception: ","");
        if(UiLanguage.HasText(message))return UiLanguage.Text(message);
        var http=Regex.Match(diagnostic,@"(?:API 返回|HTTP(?: status)?|status code[: ]*)\s*(\d{3})",RegexOptions.IgnoreCase);
        if(http.Success)return UiLanguage.Format($"API 请求失败（HTTP {http.Groups[1].Value}）。请检查连接设置。详情已记录到日志。");
        if(diagnostic.Contains("OperationCanceledException",StringComparison.Ordinal))return UiLanguage.Text("已停止");
        if(diagnostic.Contains("TimeoutException",StringComparison.Ordinal)||diagnostic.Contains("TaskCanceledException",StringComparison.Ordinal))return UiLanguage.Text("请求超时，请稍后重试。");
        if(diagnostic.Contains("UnauthorizedAccessException",StringComparison.Ordinal))return UiLanguage.Text("无法访问文件，请检查保存位置的写入权限。");
        if(diagnostic.Contains("IOException",StringComparison.Ordinal))return UiLanguage.Text("文件操作失败，请检查保存位置和可用空间。详情已记录到日志。");
        return UiLanguage.Text("操作未完成，请检查设置后重试。详情已记录到日志。");
    }
}
