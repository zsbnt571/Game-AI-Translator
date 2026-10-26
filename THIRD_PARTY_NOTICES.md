# Third-party notices / 第三方声明 / Сторонние компоненты

**Fusion 1.1.127**

Project-owned code retains the existing **GPL-3.0-only** declaration. This navigation document does not relicense third-party code, models, fonts or runtimes. Original copyright and license documents remain with the components.

项目自有代码沿用GPL-3.0-only；第三方组件继续采用各自条款。本页提供当前记录入口，不是新的授权声明。  
Собственный код сохраняет GPL-3.0-only. Сторонние компоненты сохраняют свои условия; эта страница не меняет их лицензии.

| Scope / 范围 | Records |
| --- | --- |
| .NET and NuGet | [Inventory](licenses/nuget-inventory.json), original documents under licenses/NuGet |
| Python, OCR and image processing | [Inventory](licenses/python-inventory.json), original notices inside the runtime and licenses/Python |
| Added original notices | [Version-pinned source and file hashes](LICENSE-ADDITIONS-PROVENANCE.json) |
| Unity loaders and dependencies | [Component notices](licenses/UnityEmbedded/README.md), [pinned provenance](licenses/UnityEmbedded/sources.json) |
| Unreal components | [Component records](licenses/Unreal/COMPONENTS.md) and retained original notices |
| Corresponding third-party sources | [Source guide](third-party-source/README.md), [six-archive inventory](third-party-source/ARCHIVES.json) |
| General license navigation | [License guide](licenses/README.md) |

Current notice additions cover Microsoft.Web.WebView2 1.0.2849.39, SkiaSharp and SkiaSharp.NativeAssets.Win32 3.119.4; matching pypdfium2 5.12.1 notices; and pinned ANTLR Python runtime 4.9.3, FlatBuffers 25.12.19, RapidOCR 3.9.2 and BCE Python SDK 0.9.76 licenses. The Python inventory also points to the already supplied ONNX Runtime and tqdm notices. The provenance record states the exact comparison and hashing scope.

BepInEx, UnityDoorstop, Il2CppInterop, Disarm, UndertaleModTool and Underanalyzer retain original license texts in their corresponding source materials. The BepInEx distribution copy omits exactly two Unity engine compile-reference DLLs; both upstream and distribution hashes are recorded. Other retained source members and notices are not rewritten by that trimming.

The complete source asset for this version is **Fusion-1.1.127-Source.zip**, linked from [GitHub Releases](https://github.com/zsbnt571/Game-AI-Translator/releases/tag/v1.1.127). Git source and automatic Source code archives omit binary inputs; use the complete source asset when those materials are required. Do not substitute an old 176 source attachment for this version.

Games, extracted game code, saves and proprietary Unity compile references are not granted redistribution rights by this inventory. Proprietary Oodle decompression DLLs are excluded; open-source wrapper license metadata is a separate category and remains subject to its own terms. The optional GPU components, where separately supplied, retain their original vendor notices and require a compatible user-installed driver.

File hashes establish identity, not blanket licensing permission. Original LGPL replacement/debugging rights are not restricted by this package. Earlier Fusion notices retained as historical material describe their own versions, rather than the current source asset.

[简体中文](README.md) · [English](README.en.md) · [Русский](README.ru.md)

Applicable bundled runtimes use .NET 8.0.33 and 10.0.14. The legacy IL2CPP loader still includes .NET 6.0.7 and retains its original notices. These runtime updates do not relicense any component. Runtime and file identities are recorded in the package manifests.
