namespace ScreenshotTranslationUiTester;

internal static class ModelManagerOperations
{
    internal static bool IsInstalled(string path) => Directory.Exists(path) || File.Exists(path);

    internal static bool RequiredStartupComponentsReady(bool rapidReady, bool hotKeyRegistered) =>
        rapidReady && hotKeyRegistered;

    internal static bool RapidNativePathTooLong(string applicationRoot)
    {
        var capi=Path.Combine(FusionRuntime.Root,"venv","Lib","site-packages","onnxruntime","capi");
        var expected=Path.Combine(capi,"onnxruntime_pybind11_state.pyd");
        return Path.GetFullPath(expected).Length>=260 || Directory.Exists(capi) && Directory.EnumerateFiles(capi,"*pybind11*.pyd")
            .Any(path=>Path.GetFullPath(path).Length>=260);
    }

    internal const string LongPathMessage="安装路径过长，包内 OCR 原生组件不支持此路径。请将整个程序解压到短目录（如 C:\\GameAITranslator）；在原长路径重新修复不能解决。未修改系统设置。";

    internal static string ResolveRapidPython(string applicationRoot)
    {
        return OcrDependencyChecker.ResolvePython(FusionRuntime.Root);
    }

    internal static string QuarantinePath(string path) => path + ".quarantine";

    internal static bool MoveToQuarantine(string path)
    {
        if (FusionRuntime.IsSharedPath(path)) return false;
        var quarantine = QuarantinePath(path);
        if (!IsInstalled(path) || IsInstalled(quarantine)) return false;
        if (Directory.Exists(path)) Directory.Move(path, quarantine);
        else File.Move(path, quarantine);
        return true;
    }

    internal static bool RestoreFromQuarantine(string path)
    {
        if (FusionRuntime.IsSharedPath(path)) return false;
        var quarantine = QuarantinePath(path);
        if (IsInstalled(path) || !IsInstalled(quarantine)) return false;
        if (Directory.Exists(quarantine)) Directory.Move(quarantine, path);
        else File.Move(quarantine, path);
        return true;
    }

    internal static string ResolveVisionRoot(string applicationRoot)
    {
        return Path.Combine(applicationRoot,"vision-runtime");
    }
}
