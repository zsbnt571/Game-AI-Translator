# Build and source materials — 1.1.128

[简体中文](BUILD.md) · [Русский](BUILD.ru.md)

## Choose the right materials

- **Fusion-1.1.128.zip** is the complete runnable application.
- **Fusion-1.1.128-Source.zip** is the complete source asset with corresponding redistributable dependencies, original notices and build materials.
- **Git source and GitHub's automatic Source code archives** omit binary dependencies. They are useful for review and contributions, but are not the complete set of ready-to-build inputs.

Start with the complete source asset from the same version. Do not mix historical payloads or arbitrary DLLs from game directories. Initial NuGet restore may need network access. A complete source asset does not establish that all toolchains can rebuild everything offline from scratch.

Project-owned code retains GPL-3.0-only; dependencies, models and binaries have their own terms. See [Third-party notices](../THIRD_PARTY_NOTICES.md) and the [source inventory](../third-party-source/README.md).

## Desktop build

The principal environment is Windows x64. The desktop targets .NET 8; UnrealCatalog needs an SDK supporting .NET 10. Relevant legacy helpers also use .NET Framework 4.8 developer references and Windows SDK 10.0.26100.0. Independent native bridges and plugins have their own toolchains; a successful desktop build does not certify that they were all rebuilt.

From the source root containing Source and tools:

    powershell -ExecutionPolicy Bypass -File .\tools\Build-Desktop.ps1

Use -DotNet to select a dotnet executable. The script creates a fresh Build directory and refuses existing output. The historical main project is Source/Screenshot/ScreenshotTranslationUiTester.csproj; it builds GameTranslator.


This entry point uses some bundled prebuilt adapter resources. It does not rebuild every Unity plugin, native bridge, runtime or model from scratch, and does not establish byte-for-byte reproducibility or a full clean-machine rebuild.

For the release build, publish the desktop, cover helper and UnitySchema with `RuntimeFrameworkVersion=8.0.33`; the script also explicitly passes `UnitySchemaRuntimeVersion=8.0.33` to the main project. Publish UnrealCatalog with `RuntimeFrameworkVersion=10.0.14`. Use the same properties for Restore and Publish. Updating the system runtime does not update a self-contained package. The supplied GameMaker tool is serviced separately with the .NET 10.0.14 runtime in both the source dependency and application copies; this does not rebuild its business code. The legacy IL2CPP loader retains .NET 6.0.7 and must not be switched to another major runtime without compatibility work.

The script builds the OCR, cover and Yukar prerequisites first, then publishes CoverCapture, UnitySchema and UnrealCatalog before the main application. The cover project must restore and publish its win-x64 assets before the main copy targets run. Duplicate helper builds are skipped in the main publish. `Build/Publish` contains the desktop output, while `Build/Helpers` contains the three separate helper outputs. Assemble a complete package as described below.

## Supply Unity references locally

Public materials exclude five compile references under Source/Plugin/References: mscorlib.dll, System.dll, System.Core.dll, UnityEngine.dll and UnityEngine.UI.dll, as well as game assemblies. The first three historical Mono references are retained privately because their exact provenance was not pinned; they are not distributed in this public source. Obtain matching references locally from an authorized source when independently rebuilding those plugins. Do not commit or redistribute them.

The legacy plugin entry point accepts an external reference directory:

    $referenceDirectory = Read-Host 'Local compile-reference directory'
    powershell -ExecutionPolicy Bypass -File .\Source\Plugin\build.ps1 -ReferenceDirectory $referenceDirectory -OutputDirectory .\Build-local\LegacyPlugin

Provide the framework references, UnityEngine.dll, UnityEngine.UI.dll and BepInEx.dll required by that script. It also accepts -DotNet and -SdkVersion; choose an installed compatible compiler.

The Mono6 project exposes these actual properties:

    $frameworkReferences = Read-Host 'Local framework and UnityEngine reference directory'
    $loaderReferences = Read-Host 'Local BepInEx Mono6 loader reference directory'
    dotnet build .\Source\Plugin\UnityEmbedded\FusionUnityMono6.csproj -p:FrameworkReferenceDirectory="$frameworkReferences" -p:LoaderDirectory="$loaderReferences" -o .\Build-local\UnityMono6

These properties select paths; they do not download references or grant redistribution rights. The project's other declared inputs are still required.

The older UnityEmbedded/build-mono.ps1 has a different limit: -FrameworkReferenceRoot redirects only framework DLLs, not UnityEngine/BepInEx; Harmony uses a fixed relative reference path. Do not treat it as a complete external Unity-reference selector. Supply the files it actually expects only in a private build copy.

## Optional checks and deployment boundaries

Two historical layout self-tests read external fixtures only when FUSION_LAYOUT_FIXTURE_ROOT is explicitly set. Original game materials are not included publicly. Without matching authorized fixtures, these checks must not be counted as executed passes. Ordinary application startup and the normal build do not require them.

Local-model deployment research describes running an external service. Configuring a compatible endpoint does not establish integrated model downloads, training, service management or every tool mentioned in that research. Android and other platform/tool studies likewise do not constitute delivered support.

## Assemble a runnable package

A build output is not automatically a complete portable distribution. Assembly also requires matching runtimes, OCR/image dependencies and models, original licenses, a clean relative data-location default, current documentation and file checksums. Personal profiles, keys, populated caches, saves, game resources, proprietary Oodle decompression DLLs and machine-specific deployment receipts are excluded.

Original third-party notices and corresponding-source records remain. The BepInEx distribution source copy explicitly omits two Unity compile references, so its distribution hash differs from the upstream ZIP hash; see the [source guide](../third-party-source/README.md). Inventory hashes identify files and do not replace upstream licenses.

[Home](../README.en.md)


Ordinary users should run the complete application package. External Unity/framework references described above are developer build inputs, not prerequisites for launching Fusion.
