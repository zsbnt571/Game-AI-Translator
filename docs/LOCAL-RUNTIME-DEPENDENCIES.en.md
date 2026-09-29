# Local runtime dependencies

[简体中文](LOCAL-RUNTIME-DEPENDENCIES.md) | **English**

Public builds do not contain local dependencies without approved redistribution rights. A SHA-256 pin establishes file integrity, not permission. The six runtime ZIPs, Unity type database and Unreal Oodle share the application-local `runtime-payloads` root, or the explicitly configured absolute `GAME_AI_TRANSLATOR_PAYLOAD_ROOT`. There is no search, fallback or network retrieval.

The two data/native dependencies also require an exact adjacent `.json` descriptor. `tools/Import-RuntimePayloads.ps1 -FlatSource -PackageId ...` imports lawfully obtained files and creates their descriptors. See [external runtime dependencies](RUNTIME-PAYLOADS.en.md) for commands and validation rules.

## Unity type database

| Field | Pinned requirement |
|---|---|
| ID | `unity-classdata` |
| Path | `runtime-payloads/unity-classdata/classdata-129e1f80f930/classdata.tpk` |
| Descriptor | Append `.json` to the file path |
| Engine / backend / architecture | `Unity` / `Any` / `Any`; this is not an executable binary |
| Pinned version identifier | `classdata-129e1f80f930`, identifying exact reviewed content |
| Bytes | 289605 |
| SHA-256 | `129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6` |
| Distribution status | EXCLUDE / public redistribution unconfirmed; lawful local provision only |

Neither public nor internal builds automatically copy this file. The old `adapters/unity/classdata.tpk` location is not a fallback. Missing or invalid content is reported as `Missing` or its specific validation state. At consumption time the parser reports the unavailable database explicitly. Resources with their own field structure and text files keep their existing path; resources needing the database may rely on existing runtime supplementation. The original AssetsTools.NET parser receives the same verified stream; its parsing algorithm is unchanged.

## Unreal Oodle

| Field | Pinned requirement |
|---|---|
| ID | `unreal-oodle` |
| Path | `runtime-payloads/unreal-oodle/oodle9-6f5d41a7892e/oo2core_9_win64.dll` |
| Descriptor | Append `.json` to the file path |
| Engine / backend / architecture | `Unreal` / `Native` / `x64` |
| Pinned version identifier | `oodle9-6f5d41a7892e`, identifying exact reviewed content |
| Bytes | 637952 |
| SHA-256 | `6f5d41a7892ea6b2db420f2458dad2f84a63901c9a93ce9497337b16c195f457` |
| Distribution status | EXCLUDE / proprietary local dependency; lawful user provision only |

The catalog tool builds without Oodle by default. Neither public nor internal builds copy it automatically. The explicit internal build switch retains the original source-file SHA check, but does not replace local import or grant distribution rights. A DLL next to the worker is not a fallback.

The application explicitly passes the shared root to the catalog worker. Before native loading, the worker checks identity, length and SHA and holds a read-only handle for the operation, then uses the original Oodle decoder interface. Missing or incorrect Oodle disables the extraction that needs it with an explicit error while the main application remains usable. No algorithm or alternate DLL is substituted. Default public builds reject stale Oodle in their output and require a separate clean output directory; they do not delete user files.

## Recovery and status

These checks are limited to dependency consumption and read-only status inspection. They are not a blanket preflight for installation, restore or uninstall. Recovery continues to use existing ownership records and backups even if local packages are absent. Source tests cover exact streams, missing/error states and file locking, not all real-game compatibility.

Python, RapidOCR, ONNX Runtime and OCR models are separately provided by users. This iteration does not bundle or download them. Final distributions still require exact allowlist and managed-resource checks.
