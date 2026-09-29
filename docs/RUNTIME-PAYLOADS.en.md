# External runtime dependencies

[简体中文](RUNTIME-PAYLOADS.md) | **English**

Public desktop builds do not embed runtime ZIPs. RPG Maker, Ren'Py and Godot source bridge scripts remain embedded. This change grants no redistribution rights and does not make every engine available out of the box.

## Pinned versions and locations

`Source/Screenshot/RuntimePayloadCatalog.json` is compiled into the application. It pins package ID, engine, backend, architecture, package version, filename, byte length and SHA-256. Package version `0.6.0.85` identifies the existing adapter payloads, not a future Release tag.

| Package ID | Consumer | Distribution status |
|---|---|---|
| unity-mono-x86 | Unity Mono x86 | EXCLUDE; authorized local input only |
| unity-mono-x64 | Unity Mono x64 | EXCLUDE; authorized local input only |
| unity-il2cpp-x64 | Unity IL2CPP x64 | EXCLUDE; authorized local input only |
| unity-legacy | Existing legacy Unity Mono installer | EXCLUDE; authorized local input only |
| unity-specialized | Existing specialized Unity adapter | EXCLUDE; authorized local input only |
| unreal-runtime | Unreal x64 bridge | EXCLUDE; authorized local input only |

The default location is `runtime-payloads/<package-id>/<package-version>/<file-name>` beside the application, with an adjacent `<file-name>.json` descriptor. An explicit absolute `GAME_AI_TRANSLATOR_PAYLOAD_ROOT` overrides this root. There is no directory search, download, silent version substitution or embedded ZIP fallback.

If you lawfully possess dependencies matching the checked-in integrity catalogue, use PowerShell 7 from the source repository root:

```powershell
# Example paths only. DestinationRoot must be an absolute local directory outside release staging.
pwsh -File ./tools/Import-RuntimePayloads.ps1 -SourceRoot 'X:\local-source-copy' -DestinationRoot 'X:\local-runtime-payloads'
$env:GAME_AI_TRANSLATOR_PAYLOAD_ROOT = 'X:\local-runtime-payloads'
```

The importer reads the existing `Source/` dependency locations, validates all six inputs before creating output, and refuses differing destination files. At runtime, descriptor fields, byte length and SHA-256 are checked against the compiled catalogue. A descriptor cannot authorize its own hash. Verification and installation use the same read-only file handle.

These files remain local-only. Moving them out of the assembly does not permit including them in public archives or installers. A future approved package requires an explicit version/catalogue update, regression checks and redistribution review.

## Installation and recovery

- New installation: the provider returns a verified stream at the former resource-read point. Missing, mismatched or corrupted input cannot begin new installation writes.
- Restore/uninstall: existing ownership records and backups are used without requiring the external ZIP.
- Interrupted transactions: the existing recovery order remains. Recovery can restore backups before a subsequent new installation fails for a missing package; do not interpret this as a guarantee of no filesystem changes during recovery.
- Identical dependencies retain the existing downstream paths, configuration generation, ownership, backup and adapter logic. Offline equivalence does not establish compatibility with every real game.

## Other local inputs and public artifacts

See [local runtime dependencies](LOCAL-RUNTIME-DEPENDENCIES.en.md) for classdata and Oodle paths, failure behavior and integrity checks. The desktop no longer copies an adjacent UnrealCatalog output directory wholesale. Build and audit that tool separately. OCR environments and models are not imported by the build.

A successful default build is not a distributable package. `FusionIncludeLocalDependencies=true` is an explicit isolated internal-build option that may copy classdata/Oodle; that output must not be released. It never re-enables the six embedded ZIPs.

Final artifacts must pass the exact-file allowlist and managed-resource checks in `tools/Test-ReleaseCandidate.ps1`. The checked-in `docs/release-allowlist.json` initially approves reviewed licence text only, not new DLLs. Each build requires independent review of file hashes, provenance, notices and embedded resources. Unknown files and EXCLUDE content fail closed.
