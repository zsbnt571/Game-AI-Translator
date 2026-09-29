# 本地构建

**简体中文** | [English](BUILD.en.md)

## 环境

- Windows x64，.NET 8 SDK。
- OCR 子项目使用 .NET Framework 4.8，并引用 Windows SDK 10.0.26100.0 的 `Windows.winmd`；安装对应开发组件后构建。
- 如果构建 UnrealCatalog，还需 .NET 10 SDK。
- 首次还原 NuGet 包需要网络。

SDK 注意：从仓库根目录执行以下命令。部分子目录的 `global.json` 固定 SDK：截图项目为 8.0.401（latestPatch），旧插件项目为 10.0.103（禁止自动滚动）。若进入子目录构建，需安装其指定 SDK，或在明确评估后调整版本。

## OCR 运行环境

编译成功不代表 OCR 环境已安装。为保留当前程序行为，源码仍保留旧开发环境的默认运行时路径；新环境必须在可执行文件旁创建 `dependencies.json` 显式指定自己的运行时目录：

```json
{"RuntimeRoot":"runtime"}
```

相对路径按可执行文件目录解析。OCR Python 环境、模型及相关依赖需自行准备；历史实验室安装脚本不能视为当前应用完整安装器。不要提交自己的运行环境、模型或含个人设置的配置文件。

## 不随源码上传的依赖

`local-dependencies.json` 列出导入基线中排除的 50 个文件及 SHA-256。这些文件包括运行时 ZIP、第三方 DLL、生成字体页和程序集。清单是完整性记录，不授予分发许可。

如果已有获授权的 Fusion 0.6.0.85 `FinalSource`，可以导入依赖：

以下 `X:\path\FinalSource` 是示例占位符，不要求使用该盘符，也不代表仓库提供了依赖来源。

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Import-LocalDependencies.ps1 -SourceRoot 'X:\path\FinalSource'
```

导入工具校验每个文件哈希；若已有文件内容不同会停止，不覆盖它。导入文件保持 Git 忽略状态，不会提交。没有这些依赖时，需按各组件文档自行取得或构建兼容组件；不能通过创建空文件绕过构建错误。

## 主程序

```powershell
dotnet restore .\Source\Screenshot\OcrHelper\ScreenshotOcrHelper.csproj
dotnet restore .\Source\CoverCapture\FusionCoverCapture.csproj
dotnet build .\Source\Screenshot\ScreenshotTranslationUiTester.csproj -c Release
```

默认输出位于仓库 `artifacts/Desktop`。可通过 `-p:FusionBuildRoot=...` 指定其他位置。主项目会构建 OCR 与封面捕获工具，但首次构建前需分别还原它们的包。

## 可选 Unreal 资源工具

```powershell
dotnet publish .\Source\UnrealCatalog\FusionUnrealCatalog.csproj -c Release -o .\artifacts\UnrealCatalog
```

该工具依赖本地 Oodle 原生库；必须符合项目的哈希检查，具体来源限制见其 `Native/PROVENANCE.md`。先生成此工具，再构建主程序，主项目才能复制相邻 `UnrealCatalog` 输出。

Unity IL2CPP 插件重新编译需要对应 BepInEx / Il2CppInterop 引用，可通过 `-p:LoaderDirectory=<dependency-directory>` 指定。Mono 插件脚本需要本地 Unity/BepInEx/框架引用。PowerShell 插件脚本从 PATH 定位 dotnet，也可通过 `-DotNet` 指定；直接编译脚本接受 `-SdkVersion`（默认 10.0.103）。专用构建必须传入 `-GameRoot`。字体生成需指定 `--source-root` 或 `FUSION_FONT_BUILD_ROOT`，该目录含 `FontSource/` 及可选的 `Tools/font-build-python/`。

保留一项实际运行例外：`FusionConfiguration.cs` 的旧运行时默认路径。更改它会改变现有启动行为，因此本轮未替换。在其他机器上按上述方式设置 `dependencies.json`，候选不会静默重定向共享 OCR 环境。标准 Windows/.NET Framework/SDK 路径仍属于平台要求，不是个人开发目录。

诊断输入已参数化。`FUSION_TEST_GAME_ROOT` 必须是现存、绝对路径的隔离副本目录，不能是盘符根；游戏探针拒绝越界和目录链接。RuntimeProbe 从 `FusionBuildRoot` 引用主程序，SafetyInspect 可传入 `-p:CoreDir=<dependency-directory>`，默认使用本地 `artifacts/LoaderReferences`。构建探针不等于授权启动游戏。

私人图片回放使用 `FUSION_LAYOUT_EVIDENCE_ROOT`，未设置时使用程序旁 `test-fixtures/layout`；仓库不包含这些历史素材。其他作用域诊断按需要求绝对路径的 `ST_FIX_ROOT`、`ST_FIX_READONLY_INPUT_ROOT`、`ST_FIX_SHARED_OCR_ROOT`，仍检查范围和身份。解析／安全测试中的虚构路径有意保留。

## 离线测试

导入本地依赖并构建主程序后：

```powershell
dotnet run --project .\Source\Plugin\UnityEmbedded\Tests\UnityEmbeddedTests.csproj -c Release
dotnet run --project .\Source\Plugin\tests\FungusDialogueTests\FungusDialogueTests.csproj -c Release
$env:ST_AUDIT_ROOT = Join-Path $PWD 'artifacts\audit'
dotnet .\artifacts\Desktop\ScreenshotTranslationUiTester\bin\Release\net8.0-windows\GameTranslator.dll "--data-root=$env:ST_AUDIT_ROOT\data" --disable-global-input "--safe-log-output-tests=$env:ST_AUDIT_ROOT\redaction"
```

每次使用新的审查输出/数据目录，避免旧日志污染结果。以上测试使用合成凭据和离线 fixture；不要把 profile/provider 集成或游戏探针加入离线批次，它们需要独立的外部输入。Unity fixture 模拟帧生命周期，不代表真实挂钩与画面渲染已验证。

分发任何二进制前须对照[再分发政策](redistribution-policy.json)。本地导入的 payload ZIP、专有或来源不明输入排除出 Release；构建成功不是分发许可。

## 验证范围

源码导入和哈希检查不等于各游戏实机兼容性验证。运行验证应使用游戏副本，保留存档与已有 Mod。禁止把个人配置、游戏素材和诊断日志加入提交。
