# Game AI Translator 0.6.0-alpha candidate: getting started

This is an Alpha candidate under development. This document does not mean it has been released or that all games have been retested.

## Start and platform requirements

- Windows x64 with **.NET 8 Desktop Runtime x64** already installed. This is a framework-dependent application; it does not include the SDK or automatically install the runtime.
- **.NET Framework 4.8** is required only when using the Windows OCR helper; it is not an additional requirement for starting the main application. RapidOCR uses the user-provided Python environment described below.
- Extract into a separate, writable directory and run `GameTranslator.exe`. Settings, the library and records default to the adjacent `data/` directory. The original development installation is not required.
- Supply your own API credentials. They are encrypted locally for the current Windows user. Saving settings does not test the connection. Never upload your own data, logs or game directories as an application package.
- Unity / Unreal payload bundles and the OCR environment are optional and not included. Settings, translation profiles and the library remain accessible when these inputs are missing; affected installation or recognition actions show their missing state and are unavailable.

## Check local dependencies

Open **Settings → Runtime dependencies → Check local dependencies** (currently labelled `设置 → 运行依赖 → 检查本地依赖`). Game details also show the selected engine's dependency state: Ready, Missing, Invalid version, Wrong architecture, Wrong backend, Hash mismatch, or Not required.

Engine inputs belong under `runtime-payloads/`; OCR uses `runtime/`. There is no automatic download, search of other installations, or silent version fallback. Obtain exact matching dependencies lawfully. Installation revalidates files; a Ready display does not replace that check or establish game compatibility.

- Engine ZIPs: [runtime payloads](RUNTIME-PAYLOADS.en.md)
- classdata / Oodle: [local dependencies](LOCAL-RUNTIME-DEPENDENCIES.en.md)
- Python / RapidOCR / ONNX Runtime / models: [OCR dependencies](OCR-LOCAL-DEPENDENCIES.en.md)

The candidate does not redistribute the six mixed runtime ZIPs, classdata, Oodle, Python, RapidOCR, ONNX Runtime, OCR models or generated fonts. Externalization does not grant redistribution permission; do not add these files to a public installer.

## Available scope and missing features

The library accepts an EXE selected or dropped into it. Adding a game does not install translation or start the game. Existing RPG Maker MV/MZ and Ren'Py paths that do not need the external runtime ZIPs remain present. Different games using one engine may behave very differently.

Unity / Unreal installation needs the corresponding verified local payload. This RC does not include the UnrealCatalog resource extraction helper because the exact helper binaries have not passed distribution review. Supplying the Unreal ZIP and Oodle alone does not enable that feature. Restoring an existing installation does not require the original runtime ZIP to remain available; preserve its installation record and backups.

The Screenshot Translator page and existing records can open without OCR. New OCR capture/import actions are unavailable until the local environment is present. Dependency validation is not a real recognition test. Experimental paths such as Paddle are not advertised as verified for this candidate.

The additional cover capture helper is omitted. Related automatic cover capture may be unavailable; local images can still be used. Missing helpers are not automatically downloaded.

## Known limitations

RPG Maker MV/MZ and Ren'Py currently have relatively high compatibility. Unity Mono may have missing translations/characters, layout/wrapping and special UI issues. Unity IL2CPP remains experimental with low compatibility and may fail to translate entirely. Unreal remains experimental with medium-low compatibility, incomplete capture/replacement and possible crashes. Godot is in testing. These are development assessments, not measured compatibility rates.

Back up game files and saves before modification. Isolated-directory and automated checks do not establish clean-Windows or universal game compatibility. See the root LICENSE, THIRD_PARTY_NOTICES.md and bundled licenses/ for applicable terms.
