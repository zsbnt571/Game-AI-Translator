# Game AI Translator v0.6.0-alpha：开始使用

**v0.6.0-alpha 已于 2026-09-30 发布为预发布 Alpha**，目前仍在开发；发布不代表所有实机兼容性验证已完成。

## 下载

- [Release 页面与发布说明](https://github.com/zsbnt571/Game-AI-Translator/releases/tag/v0.6.0-alpha)
- [Game-AI-Translator-v0.6.0-alpha.zip（Windows x64）](https://github.com/zsbnt571/Game-AI-Translator/releases/download/v0.6.0-alpha/Game-AI-Translator-v0.6.0-alpha.zip)
- [SHA256SUMS.txt](https://github.com/zsbnt571/Game-AI-Translator/releases/download/v0.6.0-alpha/SHA256SUMS.txt)：用于核对 ZIP、manifest 及附带的法律声明。

按下述平台要求准备运行时，并完整解压 ZIP，保留包内目录结构。

## 私人测试的已验证范围

已完成主程序独立启动和有限 Unity Mono 流程验收，包括真实 API 翻译、游戏内写回、连续对白与菜单翻译、重启后的翻译缓存保留观察及恢复原版；截图捕获、OCR、截图 API 翻译、图片导入、应用重启后的历史读取也已验证。验收仅覆盖私人测试中实际执行的流程，不代表全引擎或全游戏兼容；缓存保留不代表已测得命中率或重启后完全不调用 API。详见上述 Release 说明。

## 启动与平台要求

- Windows x64；本版 Alpha 为框架依赖应用，需要已安装 **.NET 8 Desktop Runtime x64**。它不包含 .NET 运行时或 SDK，也不会自动安装运行时。
- **.NET Framework 4.8** 仅在使用 Windows OCR 辅助程序时需要；它不是主程序的额外启动条件。RapidOCR 使用下述用户提供的 Python 环境。
- 解压到当前用户可写的独立目录，运行 `GameTranslator.exe`。程序默认在旁边的 `data/` 保存设置、游戏库和记录，不读取原开发目录。
- API 凭据由用户填写，使用当前 Windows 用户加密后保存在本机。保存设置不会测试连接；不要把自己的 `data/`、日志或游戏目录作为软件包上传。
- 本版没有完整的 Unity / Unreal 运行包或 OCR 环境。缺少这些可选依赖时，设置、翻译方案和游戏库仍可打开；对应安装或识别功能显示缺失并暂停使用。

## 检查本地依赖

打开 **设置 → 运行依赖 → 检查本地依赖**。游戏详情也显示当前引擎的依赖状态。状态包括 Ready、Missing、Invalid version、Wrong architecture、Wrong backend、Hash mismatch、Not required。

引擎依赖使用 `runtime-payloads/`，OCR 使用 `runtime/`。只从明确目录读取，不自动下载、不搜索其他安装、不静默换版本。用户须合法取得完全匹配的依赖。安装校验再次检查文件，界面显示 Ready 不替代安装时的完整性检查，也不保证所有真实游戏兼容。

- 引擎运行 ZIP：[本地运行包说明](RUNTIME-PAYLOADS.md)
- classdata 与 Oodle：[本地依赖说明](LOCAL-RUNTIME-DEPENDENCIES.md)
- Python / RapidOCR / ONNX Runtime / 模型：[OCR 依赖说明](OCR-LOCAL-DEPENDENCIES.md)

当前不分发六个混合运行 ZIP、classdata、Oodle、Python、RapidOCR、ONNX Runtime、OCR 模型及生成字体。外置不改变其许可状态，不要自行把这些文件加入公开安装包。

## 可用范围与缺失功能

游戏库支持选择或拖入 EXE；仅添加游戏不会安装翻译或启动游戏。RPG Maker MV/MZ、Ren'Py 等不需要上述运行 ZIP 的路径保留；相同引擎的不同游戏仍可能有完全不同的表现。

Unity / Unreal 安装需要对应校验通过的本地包。本版 Alpha 未包含 UnrealCatalog 资源提取工具，其具体二进制的分发审查尚未通过；仅提供 Unreal ZIP 和 Oodle 不足以使此功能可用。已有安装的恢复不以外部运行 ZIP 仍存在为前提，请保留原安装记录与备份。

截图翻译页面和已有记录可以打开。缺少本地 OCR 环境时，新的 OCR 截图/导入动作不可用。依赖校验通过不等于已完成真实文字识别测试。复杂截图布局可能出现 OCR 文本排序问题，某些逐词或分段翻译效果也可能不理想。Paddle 等实验路径没有作为本版已验证功能承诺。

本版 Alpha 不包含 CoverCapture 封面捕获辅助程序，依赖它的自动封面功能不可用，可使用本地图片。没有自动下载补齐功能。

## 已知限制

RPG Maker MV/MZ、Ren'Py 已支持，仍待当前 Alpha 的实机验证；Unity Mono 可能漏翻、缺字、布局/换行或特殊 UI 异常；Unity IL2CPP 为低兼容性的实验支持，可能完全不翻译；Unreal 为中低兼容性的实验支持，可能漏翻、无法写回、UI 捕获失败或闪退；Godot 处于测试适配阶段。这些是开发阶段描述，不是统计兼容率。

修改游戏前备份原文件和存档。本版 Alpha 的目录隔离及自动检查不等于全新 Windows 系统或所有真实游戏已经验证。许可证见根目录 LICENSE；具体第三方声明见 THIRD_PARTY_NOTICES.md 及随包 licenses/。
