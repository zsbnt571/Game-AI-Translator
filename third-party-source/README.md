# Corresponding third-party sources — Fusion 1.1.0.127

The complete **Fusion-1.1.0-Preview3-Source.zip** contains the corresponding third-party source archives under third-party-source. Obtain it from the [matching release](https://github.com/zsbnt571/Game-AI-Translator/releases/tag/v1.1.0-preview.3%2Bdialogue.5).

Git source and GitHub's automatic Source code archives omit binary build inputs and archived dependency bodies. They retain source references and inventories; they are not replacements for the complete source asset.

| Component | Pinned source |
| --- | --- |
| BepInEx | 5b766a3b7f6c164d4798924a93f3acf4db769d06 |
| UnityDoorstop | 4.5.0 |
| Il2CppInterop | 1.5.3 |
| Disarm | 8574010015c8c56bb7faad7e407c50b0abe9cc72 |
| UndertaleModTool | 0.9.2.0 |
| Underanalyzer | 4ff50a866b4c1a7acee8cebe6a56d6a48709b453 |

[SOURCES.json](SOURCES.json) records four pinned upstream source downloads. [ARCHIVES.json](ARCHIVES.json) records all six distributed archive hashes. Original licenses and build instructions remain inside the archives. The main application's GPL declaration does not replace those licenses.

## BepInEx distribution-copy distinction

Exactly two Unity engine compile-reference binaries are omitted from the BepInEx distribution source copy:

- Runtimes/Unity/BepInEx.Unity.IL2CPP/lib/UnityEngine.CoreModule.dll
- Runtimes/Unity/BepInEx.Unity.IL2CPP/lib/UnityEngine.InputLegacyModule.dll

All other upstream members, source content and notices are preserved without content edits. The original upstream archive SHA-256 is **CCEA1256EF0552F51BE640BF5E38F45667D3F95D609EA95CE386338A952993D0**. The trimmed distribution archive SHA-256 is **4E53A4650DE500B1CD9C5E85669644BD5DC5E1EE40D12A66070A308A1E9EBA0E**. The upstream URL therefore does not produce the trimmed archive's hash.

Rebuilding those upstream Unity components requires legally obtained matching Unity references supplied locally. Do not commit or redistribute game assemblies or restricted Unity compile references.

当前完整源码附件为Fusion-1.1.0-Preview3-Source.zip，不再使用旧176附件作为当前入口。BepInEx仅裁剪上述两份Unity引用，原下载与分发副本的哈希分开记录；其余源码和许可保持原文。

Полный архив текущей версии — Fusion-1.1.0-Preview3-Source.zip. Старый архив 176 не заменяет его. В BepInEx удалены только две указанные ссылки Unity; исходный и распространяемый хеши различаются, остальной код и лицензии сохранены.

[Third-party notices](../THIRD_PARTY_NOTICES.md) · [Build instructions](../docs/BUILD.en.md)

Runtime servicing covers the six .NET 8/10 locations described in [Validation](../docs/VALIDATION-1.1.0.127.md). The legacy IL2CPP loader still includes .NET 6.0.7 and retains its own pinned notices; this round does not relicense or upgrade that runtime. Serviced runtime versions, notices and file identities are recorded separately from the retained legacy loader.
