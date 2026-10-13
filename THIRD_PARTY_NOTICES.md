# 第三方声明 / Third-party notices

本页对应已发布的 **Fusion R1 0.6.0.176 Preview 1**。项目自有代码采用 [GPL-3.0-only](LICENSE)；第三方代码、工具、模型和运行时保留各自的版权与许可条款。

The .176 preview is published. Project-owned code is GPL-3.0-only; third-party components retain their own terms. A project license or file hash does not replace an upstream license.

## 组件与原始声明 / Components and original notices

| 范围 / Scope | 记录与许可 / Records and licenses |
| --- | --- |
| .NET / NuGet dependencies | [Inventory](licenses/nuget-inventory.json), [original licenses](licenses/NuGet) |
| Python / OCR / CPU image processing | [Inventory](licenses/python-inventory.json), [RapidOCR license](licenses/RapidOCR-Apache-2.0.txt); original package metadata/licenses also remain inside the runtime distribution |
| Unity loaders and dependencies | [Components and licenses](licenses/UnityEmbedded/README.md), [pinned provenance](licenses/UnityEmbedded/sources.json) |
| Unreal adapter and catalog | [Components](licenses/Unreal/COMPONENTS.md), [UE4SS](licenses/Unreal/UE4SS-MIT.txt), [CUE4Parse](licenses/Unreal/CUE4Parse-Apache-2.0.txt) |
| Native adapter dependencies | [Unity notices](licenses/UnityNative), [Wolf notices](licenses/WolfNative), [WolfTextCatalog](licenses/WolfTextCatalog.LICENSE.txt) |
| Other extraction/font components | [Defold](licenses/DefoldCatalogNotice.txt), [Godot Smaz](licenses/GodotSmazNotice.txt), [GameMaker font](licenses/GameMakerFont-OFL.txt) |
| Corresponding third-party sources | [Source archive index](third-party-source/README.md), [complete source asset](https://github.com/zsbnt571/Game-AI-Translator/releases/download/v0.6.0.176-preview.1/Fusion-0.6.0.176-Source.zip) |

UndertaleModTool CLI 0.9.2.0 is an unmodified upstream executable under GPL-3.0, invoked as a separate process. Its source and Underanalyzer submodule source are included in the full source asset. The Underanalyzer revision is `4ff50a866b4c1a7acee8cebe6a56d6a48709b453`. The distributed tool retains its own license and build materials; no game assets or extracted game code are distributed.

BepInEx, UnityDoorstop and Il2CppInterop source snapshots retain their original licenses. Loader binaries are supplied as separate runtime components; component inventories and notices identify the versions. Rights to modify/replace LGPL components and reverse engineer for debugging such changes remain intact, with no additional restrictions imposed by this package. Disarm revision `8574010015c8c56bb7faad7e407c50b0abe9cc72` carries the [retained MIT license](licenses/UnityEmbedded/Disarm.txt).

The optional GPU supplement retains its original NVIDIA/CUDA/cuDNN library license files. It requires a compatible user-installed NVIDIA GPU/driver; no driver is bundled. The supplement accelerates fine background processing for screenshot image translation and is not required for ordinary OCR or in-game text translation.

## 分发范围 / Distribution scope

- 完整主包携带当前 CPU 路径的本地依赖与其许可；GPU 包为独立可选补充。详见 [依赖说明](docs/DEPENDENCIES-176.md) / [Dependencies](docs/DEPENDENCIES-176.en.md)。
- 专有 Oodle `oo2core_9_win64.dll` 和 Unity 游戏引擎编译引用不随公开包分发。Oodle 压缩资源需要另行合法准备的兼容解码器。
- 游戏、游戏程序集、存档、API 密钥、个人设置和使用历史不在分发范围内。
- Git 仓库保留产品源码与声明；运行时、模型、生成字体、二进制及第三方源码归档通过对应 Release 附件提供。构建方法见 [构建说明](docs/BUILD-176.md)。

Proprietary Oodle and Unity game-engine compile reference assemblies are excluded. No games, saves, credentials or personal histories are distributed. Release artifacts carry their own original runtime notices; source archives carry the corresponding third-party source materials. Historical Alpha packaging decisions remain in the [Alpha tag](https://github.com/zsbnt571/Game-AI-Translator/tree/v0.6.0-alpha), not the current dependency guide.
