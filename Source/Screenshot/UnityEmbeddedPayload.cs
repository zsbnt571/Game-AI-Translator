namespace ScreenshotTranslationUiTester;

internal static class UnityEmbeddedPayload
{
    internal static Stream Open(string backend,string architecture)
    {
        string name=backend switch{"Mono" when architecture=="x64"=>"UnityEmbeddedMono64.zip","Mono" when architecture=="x86"=>"UnityEmbeddedMono32.zip","IL2CPP" when architecture=="x64"=>"UnityEmbeddedIl2Cpp64.zip",_=>throw new IOException("没有匹配此 Unity 后端与位数的翻译组件。")};
        var assembly=typeof(UnityEmbeddedPayload).Assembly;string? resource=assembly.GetManifestResourceNames().SingleOrDefault(r=>r.EndsWith("."+name,StringComparison.Ordinal));
        if(resource is null)throw new IOException("此版本尚未包含 Unity 内嵌翻译载荷。");return assembly.GetManifestResourceStream(resource)??throw new IOException("无法读取 Unity 翻译载荷。");
    }
}
