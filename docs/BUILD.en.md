# Local build

[简体中文](BUILD.md) | **English**

## Requirements

- Windows x64 and .NET 8 SDK.
- The OCR helper targets .NET Framework 4.8 and references `Windows.winmd` from Windows SDK 10.0.26100.0. Install the corresponding development components.
- .NET 10 SDK if building UnrealCatalog.
- Network access for initial NuGet package restoration.

Run the commands below from the repository root. Subdirectory `global.json` files may select a different SDK: the screenshot project pins 8.0.401 with latestPatch; the legacy plugin project pins 10.0.103 with roll-forward disabled. Install the specified SDK when building from those directories, or evaluate any version change explicitly.

## OCR runtime configuration

Compilation does not install the OCR environment. To preserve existing behaviour, the source still retains a legacy development-machine runtime fallback. On a new machine, explicitly configure your runtime with a `dependencies.json` next to the executable:

```json
{"RuntimeRoot":"runtime"}
```

Relative paths resolve against the executable directory. Python environments, models and their dependencies must be prepared separately. Historical laboratory setup scripts are not a complete installer for the current application. Do not commit local environments, models or personal settings.

## Dependencies excluded from source control

[`local-dependencies.json`](local-dependencies.json) lists 50 files excluded from the imported baseline and their SHA-256 hashes. They include runtime ZIPs, DLLs, generated font pages and reference assemblies. The manifest records integrity; it does not grant redistribution rights.

If you already have an authorized Fusion 0.6.0.85 `FinalSource` directory, import these files with:

`X:\path\FinalSource` below is an example placeholder, not a required drive or a bundled dependency source.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Import-LocalDependencies.ps1 -SourceRoot 'X:\path\FinalSource'
```

The tool checks hashes and stops if an existing destination differs. Imported dependencies remain ignored by Git. Without this local source, obtain or build compatible components according to the component documentation. Empty placeholder files are not a valid substitute.

## Desktop application

From the repository root:

```powershell
dotnet restore .\Source\Screenshot\OcrHelper\ScreenshotOcrHelper.csproj
dotnet restore .\Source\CoverCapture\FusionCoverCapture.csproj
dotnet build .\Source\Screenshot\ScreenshotTranslationUiTester.csproj -c Release
```

Output defaults to `artifacts/Desktop`. Override it with `-p:FusionBuildRoot=...` if needed. The main project builds the OCR and cover-capture helpers, but their packages need a separate initial restore.

## Optional Unreal catalogue worker

```powershell
dotnet publish .\Source\UnrealCatalog\FusionUnrealCatalog.csproj -c Release -o .\artifacts\UnrealCatalog
```

This worker requires the local Oodle binary matching its project hash check. See [`Native/PROVENANCE.md`](../Source/UnrealCatalog/Native/PROVENANCE.md) for restrictions. Build the worker before the desktop application so that the application can copy the neighbouring `UnrealCatalog` output.

Rebuilding Unity IL2CPP plugins requires the corresponding BepInEx / Il2CppInterop references; use `-p:LoaderDirectory=<dependency-directory>`. Mono plugin scripts require local Unity/BepInEx/framework references. PowerShell plugin scripts resolve `dotnet` from PATH and accept `-DotNet` (and compiler scripts `-SdkVersion`, default 10.0.103). Specialized builds require an explicit `-GameRoot`; no personal installation directory is assumed. The font generator requires `--source-root` or `FUSION_FONT_BUILD_ROOT`, containing `FontSource/` and optionally `Tools/font-build-python/`.

One retained runtime exception is the legacy default in `FusionConfiguration.cs`: changing that fallback would change existing startup behaviour. Set `dependencies.json` as described above on another machine; the candidate does not silently redirect a shared OCR installation. Standard Windows/.NET Framework/SDK paths also remain platform requirements, not personal development locations.

Diagnostic inputs are now explicit. `FUSION_TEST_GAME_ROOT` must be an absolute, existing isolated-copy directory, never a drive root; game probes reject paths outside it and directory links. RuntimeProbe references the main build under `FusionBuildRoot`; SafetyInspect uses `-p:CoreDir=<dependency-directory>` or the local `artifacts/LoaderReferences` directory. Building a probe does not authorize launching a game.

Private image replay uses `FUSION_LAYOUT_EVIDENCE_ROOT`, or `test-fixtures/layout` beside the executable when absent. Those historical fixtures are not bundled. Other scoped harnesses require absolute `ST_FIX_ROOT`, `ST_FIX_READONLY_INPUT_ROOT` and `ST_FIX_SHARED_OCR_ROOT` as applicable, retaining scope/identity checks. Synthetic path strings in parser/security tests are intentionally retained.

## Offline tests

After importing local dependencies and building the main application:

```powershell
dotnet run --project .\Source\Plugin\UnityEmbedded\Tests\UnityEmbeddedTests.csproj -c Release
dotnet run --project .\Source\Plugin\tests\FungusDialogueTests\FungusDialogueTests.csproj -c Release
$env:ST_AUDIT_ROOT = Join-Path $PWD 'artifacts\audit'
dotnet .\artifacts\Desktop\ScreenshotTranslationUiTester\bin\Release\net8.0-windows\GameTranslator.dll "--data-root=$env:ST_AUDIT_ROOT\data" --disable-global-input "--safe-log-output-tests=$env:ST_AUDIT_ROOT\redaction"
```

Use a fresh audit output/data directory on each run so previous logs cannot contaminate results. These tests use synthetic credentials and offline fixtures. Do not run profile/provider integration or game probes as part of an offline test batch; they have separate external-input requirements. Unity fixtures simulate a frame lifecycle and do not establish real Unity hooking or frame-rendering compatibility.

Before distributing any binaries, consult [the redistribution policy](redistribution-policy.json). The locally imported payload ZIPs and proprietary/unknown inputs are excluded from Release; successful compilation is not distribution approval.

OCR runtimes/models are separate from compiling the desktop executable and are required for the corresponding recognition features.

## Verification scope

Successful source import, hash checks and compilation do not establish gameplay compatibility. Use game copies for runtime verification and preserve saves and existing mods. Do not commit personal configuration, game assets or diagnostic logs.
