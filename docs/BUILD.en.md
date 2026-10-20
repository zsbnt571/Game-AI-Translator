# Fusion R1 1.0.0 Preview 1 — Source and build

[简体中文](BUILD.md) · [Documentation](README.md)

The matching tag is [v1.0.0-preview.1](https://github.com/zsbnt571/Game-AI-Translator/tree/v1.0.0-preview.1). End users should download `Fusion-1.0.0.zip`; developers should use the complete `Fusion-1.0.0-Source.zip` release asset.

## Obtain the materials

1. Download the complete source asset from [this release](https://github.com/zsbnt571/Game-AI-Translator/releases/tag/v1.0.0-preview.1) and verify its SHA-256 against the accompanying checksum information.
2. Fully extract into a new writable directory. Enter the root containing `Source`, `Dependencies`, `tools`, `licenses` and `third-party-source`.
3. Prepare the environment below and run the script from that root. Preserve old build outputs and use a fresh extraction for another build.

A Git clone or GitHub's automatic Source code ZIP omits required runtime ZIPs, DLLs, generated fonts and development inputs; it does not replace the full source asset. See [SOURCE-MANIFEST.json](../SOURCE-MANIFEST.json) for source/dependency provenance and hashes, and [git-source-index.json](../git-source-index.json) for the Git product-source index. Use each manifest's declared scope; later documentation changes do not imply product-source changes.

## Environment and entry point

- Windows x64 and the .NET 8 SDK for the desktop program.
- The .NET 10 SDK for UnrealCatalog.
- .NET Framework 4.8 developer references and Windows SDK 10.0.26100.0 metadata for legacy net48 helpers.
- Network access for the first NuGet restore. Independent native-bridge or game-plugin rebuilds also need the project's C++/engine toolchain and legally obtained compile references.

The main project is `Source/Screenshot/ScreenshotTranslationUiTester.csproj`. Its historical project filename is retained; it builds the current `GameTranslator` application. The application entry-point version is 1.0.0, while internal bridges/helpers may retain their own compatibility versions.

From the extracted source root:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Build-Desktop.ps1
```

[tools/Build-Desktop.ps1](../tools/Build-Desktop.ps1) accepts `-DotNet` to select a dotnet executable. It creates `Build` and refuses to overwrite existing output, builds required helpers, then publishes the Windows x64 desktop program and modern helpers. net48 features still depend on platform components. Starting at the root avoids historical subdirectory `global.json` files unexpectedly selecting another SDK.

This script does not rebuild every engine plugin from scratch and does not establish a complete fresh-developer-machine rebuild. SDK, path and environment differences can change binaries; byte-for-byte reproducibility is not promised.

## Inputs and redistribution boundaries

The complete source materials contain product code, redistributable build inputs and corresponding third-party sources/licenses. Independent plugin rebuilds require extra toolchains; including existing adapter payloads does not mean they were all rebuilt from scratch for this release.

The public materials exclude:

- Proprietary `oo2core_9_win64.dll`. The main tool can build without it, with limits on the corresponding Unreal decompression support.
- Unity compile references `UnityEngine.dll` and `UnityEngine.UI.dll`. Supply legal local references to the relevant rebuild script; possessing references does not authorize redistributing game assemblies.
- Games, game assemblies, saves, personal API settings, logs, real-game fixtures and historical machine-specific build output.

Some old diagnostic tools retain historical fixture paths; the referenced game materials are not included. Do not run historical installation scripts or real-game probes without an explicitly designated isolated directory. The normal application uses its current data-storage setting.

**Build output is not a complete public distribution.** Packaging still requires verified OCR/image runtimes, models, licenses, component manifests, removal of user data and archive checks. Do not publish the entire Build folder as an application package.

This release build passed **613 of 613 controlled checks**: 342 interface/localization checks and 271 modification/translation regression checks. These results do not establish extended real-gameplay or physical display-FPS acceptance. A clean Windows developer machine, every independently rebuilt plugin and byte-for-byte reproducibility are outside the default claim.

Project-owned code uses GPL-3.0-only; third parties retain their own licenses. See [Third-party notices](../THIRD_PARTY_NOTICES.md) and [Known issues](KNOWN-ISSUES.en.md).
