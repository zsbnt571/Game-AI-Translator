# Licenses for Fusion 1.1.128

This directory retains original third-party notices and component inventories. Fusion's own code retains GPL-3.0-only; the terms for each dependency, model, font or runtime remain component-specific.

- [.NET/NuGet inventory](nuget-inventory.json): versions and original documents under NuGet.
- [Python inventory](python-inventory.json): installed components and original notices in the runtime or this directory.
- [Added original notices and provenance](../LICENSE-ADDITIONS-PROVENANCE.json): exact versions, upstream sources and document hashes.
- [Current third-party overview](../THIRD_PARTY_NOTICES.md).
- [Corresponding source guide](../third-party-source/README.md) and [archive inventory](../third-party-source/ARCHIVES.json).

Use **Fusion-1.1.128-Source.zip** from the [matching release](https://github.com/zsbnt571/Game-AI-Translator/releases/tag/v1.1.128) for the complete source materials and dependencies. GitHub's automatic Source code archive is not that complete asset.

本目录保存第三方原始许可及库存。完整源码材料使用当前发布的Fusion-1.1.128-Source.zip；旧176下载说明仅属于历史记录。更新库存不改写上游许可证，也不是新的授权。

Здесь сохранены оригинальные лицензии и списки компонентов. Полные материалы находятся в Fusion-1.1.128-Source.zip соответствующего выпуска; старые ссылки 176 относятся только к истории. Обновление списка не меняет лицензии.

The documented BepInEx source distribution removes two Unity compile-reference DLLs while retaining the remaining source and notices. Its upstream and distribution hashes differ for that reason. Restricted Unity/game assemblies and proprietary Oodle DLLs must not be added to these public materials.

Applicable bundled runtimes use .NET 8.0.33 and 10.0.14. The legacy IL2CPP loader still includes .NET 6.0.7 and retains its original notices. These runtime updates do not relicense any component. Runtime and file identities are recorded in the package manifests.
