namespace ScreenshotTranslationUiTester;

internal enum CatalogIssueKind { Failed, Unsupported, Limited, Incomplete }
internal sealed record CatalogReadIssue(CatalogIssueKind Kind,string Source,string Reason);
internal sealed record CatalogReadReport(bool Completed,CatalogReadIssue[] Issues)
{
    // This describes the bounded reader, never whole-game coverage, translation,
    // bridge loading or visible writeback. No report may enable screen capture.
    internal string Summary(int files,int texts)
    {
        var parts=new List<string>{Completed&&Issues.Length==0?$"已完成本次来源扫描（读取 {files} 个文件）":$"来源扫描部分完成（读取 {files} 个文件）"};
        foreach(var pair in new[]{(CatalogIssueKind.Failed,"读取或解析失败"),(CatalogIssueKind.Unsupported,"格式或结构暂不支持"),(CatalogIssueKind.Limited,"达到读取上限"),(CatalogIssueKind.Incomplete,"未能完整检查")})
        {
            int count=Issues.Count(i=>i.Kind==pair.Item1);
            if(count>0)parts.Add($"{pair.Item2} {count} 处");
        }
        if(texts==0)parts.Add(Completed&&Issues.Length==0?"已读来源未发现符合条件的文字":"当前未提取到文字，不代表游戏没有文字");
        if(Issues.Length>0)
        {
            var first=Issues[0];string detail=(first.Source+"："+first.Reason).Replace('\r',' ').Replace('\n',' ');
            parts.Add(detail.Length>160?detail[..160]+"…":detail);
        }
        return string.Join("；",parts);
    }
}
