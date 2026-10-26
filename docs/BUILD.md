# 构建与源码材料 — 1.1.128

[English](BUILD.en.md) · [Русский](BUILD.ru.md)

## 选择正确的材料

- **Fusion-1.1.128.zip** 是完整可运行程序包。
- **Fusion-1.1.128-Source.zip** 是完整源码资产，附带该版本对应的可分发依赖、原始许可与构建材料。
- **Git仓库及GitHub自动Source code归档** 不包含完整二进制依赖。它们适合查看代码和提交变更，不等于开箱即用的全部构建输入。

从同一版本的完整源码资产开始最直接。不要混用历史版本的载荷或任意游戏目录中的DLL。第一次NuGet还原可能需要网络；完整源码资产不等于已经证明所有工具链均可完全离线、从零重建。

项目自有代码沿用GPL-3.0-only；第三方源码、模型和二进制各有许可。见[当前第三方导航](../THIRD_PARTY_NOTICES.md)和[源码库存](../third-party-source/README.md)。

## 桌面构建

主要环境为Windows x64，桌面工程面向.NET 8；UnrealCatalog需要支持.NET 10的SDK。相关旧版辅助工程还使用.NET Framework 4.8开发引用与Windows SDK 10.0.26100.0。独立原生桥及插件有各自工具链，不能由主程序编译成功推定全部已重建。

在包含Source与tools的源码根目录执行：

    powershell -ExecutionPolicy Bypass -File .\tools\Build-Desktop.ps1

可通过-DotNet选择dotnet程序。脚本创建新的Build目录，已有输出时拒绝覆盖。主工程的历史文件名是Source/Screenshot/ScreenshotTranslationUiTester.csproj，生成的应用名称是GameTranslator。

此入口使用随源码保留的部分已编译适配资源；不会从零重建每个Unity插件、原生桥、运行库和模型。当前构建结果不等于已证明逐字节可复现或在全新系统上完整重建。公开程序包的检查范围见[验证记录](VALIDATION-1.1.128.md)。

发布构建对主程序、封面辅助和UnitySchema使用 `RuntimeFrameworkVersion=8.0.33`；脚本也向主工程显式传入 `UnitySchemaRuntimeVersion=8.0.33`。UnrealCatalog使用 `RuntimeFrameworkVersion=10.0.14`。Restore和Publish必须使用相同属性；仅更新系统运行时不会更新自包含程序包。GameMaker随附工具在对应源码依赖目录和程序包目录内单独服务其.NET 10.0.14运行时，不因此重编译业务工具。旧IL2CPP加载器内的.NET 6.0.7保持原版本，不能跨主版本直接替换。

脚本先构建OCR、封面和Yukar前置组件，再依次发布CoverCapture、UnitySchema、UnrealCatalog，最后发布主程序。封面组件必须先用win-x64还原并发布，主程序的封面复制目标才能找到对应资产；主程序跳过已独立完成的辅助构建。`Build/Publish`是桌面输出，三个独立辅助输出位于`Build/Helpers`，仍需按下文组装完整程序包。

## Unity引用只在本地提供


旧插件入口支持外部引用目录：

    $referenceDirectory = Read-Host 'Local compile-reference directory'
    powershell -ExecutionPolicy Bypass -File .\Source\Plugin\build.ps1 -ReferenceDirectory $referenceDirectory -OutputDirectory .\Build-local\LegacyPlugin

目录应包含该脚本要求的框架引用、UnityEngine.dll、UnityEngine.UI.dll和BepInEx.dll。脚本还支持-DotNet、-SdkVersion，应选择已安装的兼容编译器。

Mono6工程支持以下真实属性：

    $frameworkReferences = Read-Host 'Local framework and UnityEngine reference directory'
    $loaderReferences = Read-Host 'Local BepInEx Mono6 loader reference directory'
    dotnet build .\Source\Plugin\UnityEmbedded\FusionUnityMono6.csproj -p:FrameworkReferenceDirectory="$frameworkReferences" -p:LoaderDirectory="$loaderReferences" -o .\Build-local\UnityMono6

这些参数只指定引用位置，不会获取引用或授予分发许可。工程仍需其声明的其他资源。

更早的UnityEmbedded/build-mono.ps1具有不同限制：-FrameworkReferenceRoot只改变框架DLL位置，不重定向UnityEngine/BepInEx；Harmony也使用既定相对引用。不能把它描述成完整的外部Unity引用选择器。仅在私人构建副本中提供它实际要求的本地文件。

## 可选检查与部署边界

两个历史布局自测只有在明确设置FUSION_LAYOUT_FIXTURE_ROOT时才尝试读取外部素材；相关原始游戏素材不随公开包提供。未提供合法匹配素材时，不应把这两项记作已运行通过。普通应用启动与普通构建不需要它们。

本地模型的部署研究只说明如何运行外部服务。配置兼容端点并不代表Fusion集成了模型下载、训练、模型服务管理或该研究中出现的所有工具。Android及其他平台/工具研究同样不构成已交付支持。

## 组装程序包

编译输出不是自动完成的便携发行包。完整组装还要使用同版本的运行库、OCR/图像处理依赖和模型、原始许可证、干净的相对数据位置配置、当前文档及文件校验清单。私有配置、API密钥、用户缓存、存档、游戏资源、专有Oodle解压DLL和本机部署收据不属于公开材料。

公开源码保留第三方原始声明及对应源码信息。BepInEx分发源码副本明确移除两份Unity编译引用，因而分发哈希与原上游压缩包哈希不同；详见[源码导航](../third-party-source/README.md)。库存哈希用于确认身份，不替代上游许可。

[返回首页](../README.md)


仅编译独立旧插件时，需要在本地提供合法取得的 mscorlib.dll、System.dll、System.Core.dll、UnityEngine.dll、UnityEngine.UI.dll 等匹配引用。普通用户运行完整程序包不需要这些编译材料。
