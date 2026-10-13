# FUSION R1 0.6.0.176 Preview 1 — 依赖清单


普通用户需要哪个包
必选：Fusion-0.6.0.176.zip
可选：Fusion-0.6.0.176-GPU.zip
开发者：Fusion-0.6.0.176-Source.zip
SHA256SUMS.txt 用于核对下载完整性。不要把 GitHub 自动生成的 Source code 当作运行包。

完整主包已含
• 主程序与辅助工具需要的 .NET 8 / .NET 10 自包含运行时。
• 本地 Python、RapidOCR、检测/方向/识别模型、ONNX Runtime CPU 及相关 Python 包。
• CPU 图像背景处理组件与模型；默认使用不要求 NVIDIA 显卡。
• 已集成的引擎适配桥接与辅助工具、字体资源。
• VC++ x64/x86 运行库安装器（prerequisites/，不自动安装）。
• 原始第三方许可证、组件清单及需随包提供的第三方源码快照。
具体文件与版本见 package-files.sha256、licenses/、runtime 中的 dist-info，以及 third-party-source/SOURCES.json。

仍由用户提供
• 翻译 API 地址、模型、密钥与相应服务额度，或自行运行的兼容本地翻译服务。
• 游戏本体和存档。
• Windows 10/11 x64；旧辅助工具所需的系统 .NET Framework 4.8、Windows OCR 语言组件。
• 可选 GPU 加速所需的兼容 NVIDIA 显卡与驱动。
• 部分 Unreal 资源所需的另行授权 Oodle 解码组件。此专有 DLL 不在本包中。
• 可选的重型文档布局/视觉模型。默认 RapidOCR 与 CPU 路径不以这些可选组件为前提。

GPU 补充包
这个包只补充背景处理 GPU 依赖，不是独立程序，不替代主包，也不是游戏翻译 API 加速器。
退出 Fusion，解压补充包，把 runtime 合并到主程序的 Fusion-R1/runtime。
正确路径：Fusion-R1/runtime/background-gpu/site。
不要多嵌套一层 Fusion-R1/runtime/runtime。补充包不包含显卡驱动。
组件内容沿用 .169 补充包，重整了本版说明；已核对压缩包与逐文件完整性，但未宣称 .176 在全部 NVIDIA 硬件完成推理验收。
这些 NVIDIA 组件保留原许可证，不自动适用于 AMD/Intel GPU；不能使用时保留默认 CPU 配置。

开发者与许可证
项目自有代码沿用仓库 GPL-3.0-only；第三方组件遵循各自许可证。运行包附 LICENSE.txt、THIRD_PARTY_NOTICES.txt 与 licenses/。
source ZIP 提供本次 .176 的源码与构建说明。为避免把私有游戏/系统依赖带入公开材料，排除 Oodle 和 Unity 引擎编译引用；单独重编部分游戏插件需合法自行提供引用。
第三方快照不是游戏本体或游戏数据。保留许可证、版权声明与对应源码分发材料。
