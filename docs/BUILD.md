# Fusion R1 1.0.0 Preview 1 — 源码与构建

[English](BUILD.en.md) · [文档首页](README.md)

对应标签为 [v1.0.0-preview.1](https://github.com/zsbnt571/Game-AI-Translator/tree/v1.0.0-preview.1)。普通用户下载 `Fusion-1.0.0.zip`，无需构建；开发者使用发布页的完整 `Fusion-1.0.0-Source.zip`。

## 获取材料

1. 从 [本版发布页](https://github.com/zsbnt571/Game-AI-Translator/releases/tag/v1.0.0-preview.1) 下载完整源码附件，并按随附校验信息核对 SHA-256。
2. 完整解压到新的可写目录，进入含有 `Source`、`Dependencies`、`tools`、`licenses`、`third-party-source` 的根目录。
3. 按下方环境准备，从该根目录执行脚本。保留旧构建输出，使用新解压目录进行下一次构建。

Git 克隆和 GitHub 自动生成的 Source code ZIP 不包含全部运行时 ZIP、DLL、生成字体与开发依赖，不能代替完整源码附件。源码与依赖文件的来源/哈希见 [SOURCE-MANIFEST.json](../SOURCE-MANIFEST.json)，Git 产品源码索引见 [git-source-index.json](../git-source-index.json)。清单的适用范围以其中声明为准，不把后续文档修改当成产品源码差异。

## 环境与入口

- Windows x64；主程序使用 .NET 8 SDK。
- UnrealCatalog 项目另需 .NET 10 SDK。
- 旧 net48 辅助项目需要 .NET Framework 4.8 开发引用与 Windows SDK 10.0.26100.0 元数据。
- 首次 NuGet 还原需要联网。独立重编 native bridge 或游戏插件另需各项目的 C++/引擎开发环境与合法编译引用。

主程序项目：`Source/Screenshot/ScreenshotTranslationUiTester.csproj`。虽然保留历史项目文件名，生成的是当前 `GameTranslator` 程序。产品入口版本为 1.0.0；桥接及辅助组件可以保留各自的兼容版本号。

从源码包根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Build-Desktop.ps1
```

脚本位于 [tools/Build-Desktop.ps1](../tools/Build-Desktop.ps1)，可使用 `-DotNet` 指定 dotnet 路径。脚本创建 `Build` 并拒绝覆盖已存在的输出，先构建所需辅助项目，再发布 Windows x64 主程序和现代辅助程序。net48 功能仍依赖平台组件。从根目录启动可避免历史子目录 `global.json` 意外改变 SDK 选择。

该脚本不等于从零重编所有引擎插件，也不证明全新开发机上的全套重建已完成。不同 SDK、路径和环境可能产生不同二进制；不承诺逐字节可复现。

## 输入与分发边界

完整源码材料包括本版产品源码、构建使用的可分发依赖以及对应第三方来源/许可。独立重建插件需要额外工具链；既有适配 payload 的随附不等于它已在本次从零重编。

以下内容不随公开材料提供：

- 专有 `oo2core_9_win64.dll`。主工具可在没有该文件时构建，但相应 Unreal 解压能力受限。
- Unity 引擎编译引用 `UnityEngine.dll`、`UnityEngine.UI.dll`。需要单独重编时，通过相关脚本指定合法的本地引用；拥有引用不等于有权再次分发游戏程序集。
- 游戏文件、游戏程序集、存档、个人 API 设置、日志、真实游戏测试素材与本机历史输出。

少数历史诊断工具含旧测试路径，这些是 fixture 引用而非随包素材。未经指定隔离目录，不要运行历史安装脚本或真实游戏探针。正常程序使用当前数据存储设置。

**构建输出不是完整发布目录。** 发布仍需组装并核验 OCR/图像运行时、模型、许可证、组件清单，排除用户数据，再检查完整压缩包。不要直接把整个 Build 目录作为用户安装包。

本次发布构建完成 **615 / 615 项受控检查，全部通过**，包含 342 项界面与语言检查和 273 项修改/翻译回归检查。这些结果不等于真实游戏长期游玩或物理屏幕帧率验收。全新 Windows 开发机、全部插件独立重编和逐字节可复现不在默认声明范围内。

项目自有代码采用 GPL-3.0-only；第三方组件遵循各自许可证。见 [第三方声明](../THIRD_PARTY_NOTICES.md) 和 [已知限制](KNOWN-ISSUES.md)。
