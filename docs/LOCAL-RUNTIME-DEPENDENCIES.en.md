# Local runtime dependencies

[简体中文](LOCAL-RUNTIME-DEPENDENCIES.md)

The default public build does not contain local runtime dependencies whose redistribution has not been approved. Availability on a development machine does not authorize bundling. SHA-256 pins establish version integrity, not redistribution permission. Consult the runtime package manifest for package-specific lookup rules.

## Unity type database

When needed, users may lawfully supply `classdata.tpk` at `<application-directory>/adapters/unity/classdata.tpk`. `<application-directory>` means the main executable directory, not a game directory.

- Exact length: 289,605 bytes.
- SHA-256: `129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6`.
- Public builds do not automatically copy it. The explicit internal build option `-p:FusionIncludeLocalDependencies=true` may copy a local file; that output is not a public distribution package.
- A missing or invalid file produces a specific type-database warning. Assets with their own type structure and text files continue through existing extraction paths; other assets may require the existing runtime translation path.
- The original AssetsTools.NET parser receives the same verified file stream. There is no parser rewrite, directory search, or automatic download.

## Unreal local decoding dependency

The optional catalogue tool retains its existing Oodle decoding interface. Users must lawfully supply the pinned file at `<application-directory>/tools/unreal-catalog/oo2core_9_win64.dll`, beside `FusionUnrealCatalog.exe`.

- Exact length: 637,952 bytes.
- SHA-256: `6f5d41a7892ea6b2db420f2458dad2f84a63901c9a93ce9497337b16c195f457`.
- The default catalogue tool build succeeds without Oodle and does not copy Oodle to build or publish output.
- Explicit internal builds with `-p:FusionIncludeLocalDependencies=true` retain the original build-time SHA check and may copy a local DLL. This does not grant distribution permission.
- At runtime, the tool checks length and SHA before native loading. Missing or changed files stop that catalogue extraction with a dependency error; no different decoder or alternate DLL is used.
- A read-only handle remains open throughout processing to reject concurrent modification or replacement.
- A default public build rejects output directories already containing Oodle and asks for a separate clean output directory; it does not delete user-provided files.

These checks occur in the code that consumes each dependency, not as a global preflight for installation, restoration, or uninstallation. Restoration still relies on existing installation records and backups. Final public packages require a separate allowlist and embedded-resource audit; successful local execution is not distribution approval.
