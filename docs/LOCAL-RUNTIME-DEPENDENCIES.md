# 本地运行依赖

[English](LOCAL-RUNTIME-DEPENDENCIES.en.md)

公开构建不包含尚未批准再分发的本地依赖。SHA-256 仅确认固定文件的完整性，不代表分发授权。六个运行 ZIP、Unity 类型数据库和 Unreal Oodle 统一使用主程序旁的 `runtime-payloads` 根目录，或显式绝对目录 `GAME_AI_TRANSLATOR_PAYLOAD_ROOT`；不搜索其他位置、不回退、不联网。

两种原生/数据依赖与 ZIP 一样需要准确的相邻 `.json` 描述文件。使用 `tools/Import-RuntimePayloads.ps1 -FlatSource -PackageId ...` 导入自己合法取得的文件可同时生成描述文件。命令示例和完整规则见[外部运行依赖](RUNTIME-PAYLOADS.md)。

## Unity 类型数据库

| 字段 | 固定要求 |
|---|---|
| ID | `unity-classdata` |
| 路径 | `runtime-payloads/unity-classdata/classdata-129e1f80f930/classdata.tpk` |
| 描述文件 | 同路径加 `.json` |
| 引擎 / 后端 / 位数 | `Unity` / `Any` / `Any`；此文件不是可执行二进制 |
| 固定版本标识 | `classdata-129e1f80f930`，用于识别已核对的准确内容 |
| 字节数 | 289605 |
| SHA-256 | `129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6` |
| 当前分发状态 | EXCLUDE / 未确认公开再分发；仅合法本地提供 |

公开或内部构建均不自动复制该文件。旧的 `adapters/unity/classdata.tpk` 路径不再作为备用来源。缺失或不匹配时状态显示 `Missing` 或准确的错误状态，功能读取时明确记录类型数据库不可用。自带字段结构的资源及文本文件仍走既有路径；需要类型数据库的资源可能只能使用已有运行时补译。已校验的同一流仍交给原 AssetsTools.NET 解析器，没有改写解析算法。

## Unreal Oodle

| 字段 | 固定要求 |
|---|---|
| ID | `unreal-oodle` |
| 路径 | `runtime-payloads/unreal-oodle/oodle9-6f5d41a7892e/oo2core_9_win64.dll` |
| 描述文件 | 同路径加 `.json` |
| 引擎 / 后端 / 位数 | `Unreal` / `Native` / `x64` |
| 固定版本标识 | `oodle9-6f5d41a7892e`，用于识别已核对的准确内容 |
| 字节数 | 637952 |
| SHA-256 | `6f5d41a7892ea6b2db420f2458dad2f84a63901c9a93ce9497337b16c195f457` |
| 当前分发状态 | EXCLUDE / 专有本地依赖；仅合法自行提供 |

默认可编译目录工具而不提供 Oodle。公开或内部构建均不自动复制 Oodle；显式内部构建开关仍保留既有源文件 SHA 校验，但不能代替本地导入，也不授予分发许可。工具旁的同名 DLL 不再作为备用来源。

主程序启动目录工具时明确传递共享依赖根目录。工具仍在原生加载前核验元数据、长度、SHA，并保持只读句柄直到此次处理结束，继续调用原 Oodle 解码接口。缺失或错误时需要 Oodle 的资源提取失败并提示，主程序仍可运行；不换算法、不尝试其他 DLL。默认公开构建遇到旧输出中残留的 Oodle 会报错，并要求独立干净输出目录，不自动删除用户文件。

## 恢复与状态

上述检查只用于实际依赖读取与只读状态展示，不作为全部安装、恢复或卸载的统一前置检查。恢复继续使用已有安装记录和备份，无需本地运行包存在。源码测试覆盖准确文件流、缺失提示、错包拒绝和句柄锁定；不等同于全部真实游戏验证。

OCR 的 Python、RapidOCR、ONNX Runtime 和模型另行由用户合法提供，本轮不打包、不自动下载。最终公开包仍必须通过允许清单和程序集资源检查。
