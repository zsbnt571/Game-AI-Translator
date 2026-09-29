# Game AI Translator 0.6.0-alpha 候选：开始使用

这是开发中的 Alpha 候选。此文档不表示已经发布或完成所有实机兼容性验证。

## 启动与平台要求

- Windows x64；本候选为框架依赖应用，需要已安装 **.NET 8 Desktop Runtime x64**。它不包含 .NET SDK，也不会自动安装运行时。
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

Unity / Unreal 安装需要对应校验通过的本地包。当前 RC 未包含 UnrealCatalog 资源提取工具，其具体二进制的分发审查尚未通过；仅提供 Unreal ZIP 和 Oodle 不足以使此功能可用。已有安装的恢复不以外部运行 ZIP 仍存在为前提，请保留原安装记录与备份。

截图翻译页面和已有记录可以打开。缺少本地 OCR 环境时，新的 OCR 截图/导入动作不可用。依赖校验通过不等于已完成真实文字识别测试。Paddle 等实验路径没有作为本版已验证功能承诺。

本候选不包含额外封面捕获辅助程序，相关自动封面功能可能不可用，可使用本地图片。没有自动下载补齐功能。

## 已知限制

RPG Maker MV/MZ、Ren'Py 已支持，仍待当前 Alpha 的实机验证；Unity Mono 可能漏翻、缺字、布局/换行或特殊 UI 异常；Unity IL2CPP 为低兼容性的实验支持，可能完全不翻译；Unreal 为中低兼容性的实验支持，可能漏翻、无法写回、UI 捕获失败或闪退；Godot 处于测试适配阶段。这些是开发阶段描述，不是统计兼容率。

修改游戏前备份原文件和存档。本候选的目录隔离及自动检查不等于全新 Windows 系统或所有真实游戏已经验证。许可证见根目录 LICENSE；具体第三方声明见 THIRD_PARTY_NOTICES.md 及随包 licenses/。
