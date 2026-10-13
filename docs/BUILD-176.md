# .176 Preview 1 构建与源码范围 / Build and source scope

本文件适用于本次 Release 的 `Fusion-R1-0.6.0.176-preview.1-source.zip`。用户启动程序应下载 with-runtime 主包，无需构建。旧版 Alpha 文档针对旧基线。

## 目录

- `Source/`：本次产品源码、项目与引擎桥接源码。
- `Dependencies/`：已随产品使用的可分发 GameMaker 工具/字体和相关辅助依赖。
- `licenses/`、`third-party-source/`：保留上游许可与本次包对应的第三方快照。
- `SOURCE-MANIFEST.json`：文件来源、哈希与明确排除项。
- `tools/Build-Desktop.ps1`：从本目录执行的主程序与辅助工具构建脚本。
- `docs/`：用户说明和当前限制。Source 下保留的旧说明为历史记录，不是本版安装说明。

主程序与桥接的产品源码未为这次打包重写。源码快照与冻结的测试产物建立哈希对应；不同 SDK/构建路径可能产生不同二进制，不能声称逐字节可复现。

## 构建环境

Windows x64；.NET 8 SDK；构建 UnrealCatalog 需要 .NET 10 SDK；旧 net48 辅助项目需要 .NET Framework 4.8 开发引用与 Windows SDK 10.0.26100.0 元数据。首次 NuGet 还原需要联网。

脚本从包根目录调用，避免子目录历史 `global.json` 改变 SDK 选择。构建 native bridge 另需适当的 Visual Studio C++ 工具链，见各桥接目录 Build 脚本。

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Build-Desktop.ps1
```

输出在新建的 `Build` 目录。脚本拒绝覆盖已有 Build。主程序和三个现代辅助工具以 Windows x64 自包含方式发布；net48 辅助仍依赖平台组件。构建脚本本轮已核对语法，未声称在全新开发机从零完成全套重建。

## 不分发的输入

- 专有 `oo2core_9_win64.dll`：不在源码或运行包中；主工具可在不附带它时构建，但相应 Unreal 解压能力受限。
- Unity 引擎编译引用 `UnityEngine.dll`、`UnityEngine.UI.dll`：不随公开材料提供；单独重编旧游戏插件时通过构建脚本参数提供合法的本地引用目录。
- 游戏文件、游戏程序集、存档、个人 API 设置、日志和真实游戏测试素材。
- 本机历史构建目录、obj/bin、调试符号和解释器缓存。

源码中的少数历史诊断辅助含旧测试目录常量；这些是测试 fixture 引用，不随附对应素材，也不作为正常运行依赖。不要未经指定隔离目录就运行真实游戏探针或历史安装脚本。主程序的用户数据使用当前 data 路径设置。

本包提供编译所引用的既有适配 payload 与其源码/许可材料。独立重建插件需要额外的引擎开发环境；带入已授权引用不能当作允许再次分发游戏程序集。

## 分发边界

构建输出不等于可发布目录。还需添加已核验的本地 OCR/图像运行时、模型、许可和组件清单，排除数据目录，并重新验证压缩包。
不要沿用“整个 Build 文件夹直接打包”的方式。发布对应的是清单中的冻结 .176 产物；源码包只供开发，不覆盖它。

