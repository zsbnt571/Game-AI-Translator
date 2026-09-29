using System.Text.Json;
using GameAiTranslator.Runtime;

namespace ScreenshotTranslationUiTester;

// The catalogue pins existing packages; it does not grant redistribution permission.
internal static class RuntimePayloadProvider
{
    internal const string PackageVersion = "0.6.0.85";
    internal const string RootEnvironmentVariable = LocalRuntimeDependency.RootEnvironmentVariable;
    internal sealed record Package(string Id, string Engine, string Backend, string Architecture,
        string Version, string FileName, long Bytes, string Sha256);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<Package[]> Packages = new(ReadCatalog);

    private static Package[] ReadCatalog()
    {
        using var stream = typeof(RuntimePayloadProvider).Assembly.GetManifestResourceStream("Fusion.RuntimePayloadCatalog.json")
            ?? throw new InvalidDataException("缺少运行依赖版本清单。");
        return JsonSerializer.Deserialize<Package[]>(stream, JsonOptions)
            ?? throw new InvalidDataException("运行依赖版本清单无效。");
    }

    internal static string ResolveRoot() => LocalRuntimeDependency.ResolveRoot();
    internal static Stream OpenUnity(string backend, string architecture) =>
        Open(UnityPackageId(backend, architecture), "Unity", backend, architecture, PackageVersion);

    private static string UnityPackageId(string backend, string architecture) => (backend, architecture) switch
    {
        ("Mono", "x86") => "unity-mono-x86", ("Mono", "x64") => "unity-mono-x64",
        ("IL2CPP", "x64") => "unity-il2cpp-x64",
        _ => throw new IOException("没有匹配此 Unity 后端与位数的翻译组件。")
    };

    internal static Stream Open(string id, string engine, string backend, string architecture, string version)
    {
        var expected = Descriptor(id);
        ValidateRequest(expected, engine, backend, architecture, version);
        return LocalRuntimeDependency.OpenVerified(ResolveRoot(), expected);
    }

    internal static DependencyStatus Check(string id)
    {
        try
        {
            var expected = Descriptor(id);
            return LocalRuntimeDependency.Check(ResolveRoot(), expected, DisplayName(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        { return Failure(id, ex); }
    }

    internal static DependencyStatus Check(string id, string engine, string backend, string architecture, string version)
    {
        try { ValidateRequest(Descriptor(id), engine, backend, architecture, version); return Check(id); }
        catch (Exception ex) when (ex is IOException or ArgumentException or JsonException or InvalidOperationException)
        { return Failure(id, ex); }
    }

    internal static IReadOnlyList<DependencyStatus> GetOverview() => new[]
    {
        "unity-mono-x86", "unity-mono-x64", "unity-il2cpp-x64", "unity-legacy",
        "unity-specialized", "unreal-runtime", "unity-classdata", "unreal-oodle"
    }.Select(Check).ToArray();

    // Callers select the existing installer first, then pass its package ID. This does not
    // infer game identity or change adapter selection, and is never a restore preflight.
    internal static IReadOnlyList<DependencyStatus> GetRequiredFor(string engine, string backend, string architecture, string? adapterPackageId = null)
    {
        if (engine.Equals("Unity", StringComparison.OrdinalIgnoreCase))
        {
            if (backend is not ("Mono" or "IL2CPP")) return new[] { RequestFailure(DependencyState.WrongBackend, "Wrong backend — 未支持的 Unity 后端。") };
            if (architecture is not ("x86" or "x64") || (backend == "IL2CPP" && architecture != "x64"))
                return new[] { RequestFailure(DependencyState.WrongArchitecture, "Wrong architecture — 没有匹配此位数的运行包。") };
            return new[] { Check(adapterPackageId ?? UnityPackageId(backend, architecture), "Unity", backend, architecture, PackageVersion) };
        }
        if (engine.Equals("Unreal", StringComparison.OrdinalIgnoreCase))
            return new[] { Check(adapterPackageId ?? "unreal-runtime", "Unreal", "Native", architecture, PackageVersion) };
        if (engine is "RPG Maker" or "RPG Maker MV" or "RPG Maker MZ" or "RenPy" or "Ren'Py" or "Godot" or "Cocos" or "Electron")
            return new[] { RequestFailure(DependencyState.NotRequired, "Not required — 此引擎安装路径不需要这些外部运行包。") };
        return new[] { RequestFailure(DependencyState.Invalid, "Invalid — 未确认此引擎的依赖要求。") };
        DependencyStatus RequestFailure(DependencyState state, string text) => new(engine, engine, state, text, "", engine, backend, architecture, "");
    }

    private static RuntimeDependencyDescriptor Descriptor(string id)
    {
        if (id == LocalRuntimeDependency.ClassData.Id) return LocalRuntimeDependency.ClassData;
        if (id == LocalRuntimeDependency.Oodle.Id) return LocalRuntimeDependency.Oodle;
        var p = Packages.Value.SingleOrDefault(package => package.Id == id) ?? throw new IOException("未登记的运行依赖包：" + id);
        return new(p.Id, p.Engine, p.Backend, p.Architecture, p.Version, p.FileName, p.Bytes, p.Sha256);
    }

    private static void ValidateRequest(RuntimeDependencyDescriptor expected, string engine, string backend, string architecture, string version)
    {
        if (expected.Engine != engine) throw new DependencyValidationException(DependencyState.Invalid, "Invalid — 依赖引擎不匹配：" + expected.Id);
        if (expected.Version != version) throw new DependencyValidationException(DependencyState.InvalidVersion, "Invalid version — 需要 " + expected.Version);
        if (expected.Architecture != architecture) throw new DependencyValidationException(DependencyState.WrongArchitecture, "Wrong architecture — 需要 " + expected.Architecture);
        if (expected.Backend != backend) throw new DependencyValidationException(DependencyState.WrongBackend, "Wrong backend — 需要 " + expected.Backend);
    }

    private static DependencyStatus Failure(string id, Exception ex) => new(id, DisplayName(id),
        ex is DependencyValidationException validation ? validation.State : DependencyState.Invalid, ex.Message, "", "", "", "", "");
    private static string DisplayName(string id) => id switch
    {
        "unity-mono-x86" => "Unity Mono x86", "unity-mono-x64" => "Unity Mono x64",
        "unity-il2cpp-x64" => "Unity IL2CPP x64", "unity-legacy" => "旧 Unity 适配运行包",
        "unity-specialized" => "专用 Unity 适配运行包", "unreal-runtime" => "Unreal 运行包",
        "unity-classdata" => "Unity 类型数据库 (classdata.tpk)", "unreal-oodle" => "Unreal Oodle x64", _ => id
    };
}
