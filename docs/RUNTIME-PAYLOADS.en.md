# External runtime dependencies

[简体中文](RUNTIME-PAYLOADS.md) | **English**

Public application builds do not embed runtime ZIPs. The existing RPG Maker, Ren'Py and Godot source bridges remain in place. Missing dependencies affect the features that require them; the application and settings can start independently. A ready dependency is neither a compatibility guarantee nor permission to redistribute it.

## Fixed directory layout

The default root is `<application-directory>/runtime-payloads`, relative to the actual desktop application. The distribution contains none of the following payload files. After obtaining them lawfully, users import them locally:

```text
runtime-payloads/
  unity-mono-x86/0.6.0.85/UnityEmbeddedMono32.zip
  unity-mono-x64/0.6.0.85/UnityEmbeddedMono64.zip
  unity-il2cpp-x64/0.6.0.85/UnityEmbeddedIl2Cpp64.zip
  unity-legacy/0.6.0.85/UnityMono.zip
  unity-specialized/0.6.0.85/<file-name-from-pinned-catalog>.zip
  unreal-runtime/0.6.0.85/UnrealRuntime.zip
  unity-classdata/classdata-129e1f80f930/classdata.tpk
  unreal-oodle/oodle9-6f5d41a7892e/oo2core_9_win64.dll
```

Every file requires an adjacent `<complete-file-name>.json`, for example `UnityEmbeddedMono32.zip.json`. Its fields are `id`, `engine`, `backend`, `architecture`, `version`, `fileName`, `bytes` and `sha256`. These are compared against compiled pins; a sidecar cannot approve a different hash or version.

`Source/Screenshot/RuntimePayloadCatalog.json` pins the six ZIPs. `0.6.0.85` identifies the existing runtime packages, not the public Release tag. The shared dependency reader pins classdata and Oodle. Their `classdata-…` and `oodle9-…` versions identify exact file content rather than claiming an unknown upstream version.

An explicit absolute local `GAME_AI_TRANSLATOR_PAYLOAD_ROOT` overrides the default. Only that root is used: no default-directory fallback, whole-machine search, download, alternative version, or embedded ZIP fallback. When launched by the application, the Unreal catalog worker receives the same explicit root. Set the variable explicitly when invoking the worker manually.

## Local import

Use PowerShell 7 from the extracted application directory (or source root); users do not need to clone the source. The application package provides `tools/Import-RuntimePayloads.ps1` and `metadata/runtime-payload-catalog.json`. These paths are examples; substitute a directory containing files you lawfully possess. The destination must not be a Release staging directory.

```powershell
# Reads exact filenames from the supplied flat directory; does not search.
./tools/Import-RuntimePayloads.ps1 -SourceRoot 'X:\local-dependencies' -FlatSource `
  -DestinationRoot 'X:\application\runtime-payloads' -PackageId 'unity-mono-x64'

# The type database and Oodle use the same importer and root.
./tools/Import-RuntimePayloads.ps1 -SourceRoot 'X:\local-dependencies' -FlatSource `
  -DestinationRoot 'X:\application\runtime-payloads' -PackageId 'unity-classdata','unreal-oodle'
```

Without `-FlatSource`, the importer reads the known source dependency locations. Omitting `-PackageId` selects only the original six ZIPs. All selected inputs are validated before destination files are created; existing different content is not overwritten. The importer generates sidecars and runtime verification remains mandatory. Never include the imported user directory in a public package.

Inside a source copy the importer uses the source catalogue; otherwise it reads only the package's fixed `metadata/runtime-payload-catalog.json`. An explicit `-CatalogPath '<catalogue-file-path>'` is also supported. It does not search for dependencies. Changing the import catalogue cannot change the application's compiled version/SHA pins or make an unapproved package Ready.

## Dependency states

| State | Meaning |
|---|---|
| Ready | File, descriptor, fixed version/backend/architecture, length and SHA-256 match; only this dependency is ready to read |
| Missing | The pinned file or adjacent descriptor is absent |
| Invalid version | Requested or described version differs from the compiled pin |
| Wrong architecture | Architecture differs; the non-executable type database uses `Any` |
| Wrong backend | Backend differs |
| Hash mismatch | Descriptor integrity constraint, file length or actual SHA-256 differs |
| Not required | The current installation path does not require these packages |
| Invalid | Identity, metadata, linked path or another validation failed |

Inspection creates no dependency directory and does not modify games. Status is not an installation-success record. Installation revalidates and reads the same held, read-only stream. Linked files and directories are rejected.

## Installation, recovery and distribution

- New installations obtain the validated stream at the existing payload-read location. Missing or invalid packages cannot begin new installation writes.
- Restore uses existing ownership records and backups without requiring external packages to remain present.
- Interrupted transaction recovery keeps its existing order. It may restore old backups before a new installation fails for a missing package; this is not a guarantee that every operation makes zero writes.
- This layer does not select or rename adapters. Existing generic, legacy and specialized installers select their existing package.
- Offline equivalence of the same embedded and external package is not full real-game validation.
- All six mixed runtime ZIPs remain **EXCLUDE**. Local classdata/Oodle use grants no redistribution permission; see [local dependencies](LOCAL-RUNTIME-DEPENDENCIES.en.md).
- OCR environments and models have separate explicit local configuration. They are neither bundled nor automatically downloaded in this iteration.

The final distribution must pass an exact file allowlist, license/NOTICE checks and managed-resource inspection. Unknown or EXCLUDE content blocks packaging.
