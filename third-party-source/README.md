# 第三方源码 / Third-party source

对应源码归档随 [完整 .176 源码附件](https://github.com/zsbnt571/Game-AI-Translator/releases/download/v0.6.0.176-preview.1/Fusion-0.6.0.176-Source.zip) 的 `Fusion-R1-Source/third-party-source/` 提供。仓库保留 [固定来源与校验记录](SOURCES.json)；归档本体位于 Release 附件。

包含 UndertaleModTool 0.9.2.0、Underanalyzer `4ff50a866b4c1a7acee8cebe6a56d6a48709b453`、BepInEx `5b766a3`、UnityDoorstop 4.5.0、Il2CppInterop 1.5.3 和 Disarm `8574010`。`SOURCES.json` 记录其中四份固定源码下载的来源和 SHA-256；全部六份归档的校验值见 [归档清单](ARCHIVES.json)，已核对与发布附件一致。

These matching upstream archives are included in the complete source asset. [SOURCES.json](SOURCES.json) records four pinned upstream downloads; the [archive index](ARCHIVES.json) records all six archive hashes verified against the published source asset. Original licenses and build instructions remain inside the archives. See [Third-party notices](../THIRD_PARTY_NOTICES.md).

## Fusion 1.0.0 packaging adjustment / 打包调整

The BepInEx source archive in this release is a distribution copy with exactly two Unity engine compile-reference binaries removed: `UnityEngine.CoreModule.dll` and `UnityEngine.InputLegacyModule.dll` from `Runtimes/Unity/BepInEx.Unity.IL2CPP/lib/`. Every other upstream member, source file and license notice is retained without content changes. `ARCHIVES.json` and `SOURCES.json` record both the original upstream hash and the distributed hash; the original upstream URL therefore does not hash to the trimmed distribution copy. To rebuild the affected upstream Unity components, provide legally obtained matching Unity references yourself.

本次公开源码附件排除上述两份 Unity 编译引用，未修改其余上游源码或许可证。两份索引分别记录原始下载与本次分发副本的哈希；重新编译相应 Unity 部分时，需要自行合法提供匹配的引用程序集。
