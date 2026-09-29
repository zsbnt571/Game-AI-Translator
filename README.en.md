# Game AI Translator

[简体中文](README.md) | **English**

Game AI Translator is an AI game translation tool for Windows. The current target is **v0.6.0-alpha**, based on Fusion R1 **0.6.0.85**, with screenshot translation, engine-specific in-game translation, a game library and selected game-data editing features.

## Feature overview

| Feature | How it works | Scope |
| --- | --- | --- |
| In-game translation | Reads resource or runtime text and replaces it through an engine component | Requires support for the engine and text system |
| Screenshot translation | Captures a region or imports an image, runs OCR, and displays translations and experimental translated images | Independent of in-game integration; capture and OCR limitations still apply |
| Game-data editing | Reads and modifies game state through a data bridge | Selected RPG Maker MV/MZ and Ren’Py data |
| Game library and diagnostics | Manages game entries, profiles and launch state; records exits for games launched through the tool | Diagnostic information does not automatically establish crash causes |

“Component connected,” “translation available,” and “text successfully replaced in the game” are different states. Screenshot results and the fallback translation window do not establish successful in-game replacement.

## Engine compatibility

The project is under development. Support does not guarantee every game, UI or text component will work. Ratings are qualitative summaries of current user feedback, not measured coverage. Compatibility is evaluated by engine family, but individual games may behave differently.

| Engine / runtime | Compatibility | Status | Known limitations |
| --- | --- | --- | --- |
| RPG Maker MV/MZ | Supported / under validation | ⚠️ Alpha validation | Standard-text adapters exist; current Alpha core workflows still need real-game validation. Custom plugins, text systems and packaging may require additional support. This does not cover every RPG Maker generation |
| Ren’Py | Supported / under validation | ⚠️ Alpha validation | Standard dialogue and text adapters exist; current Alpha core workflows still need real-game validation. Custom scripts, interfaces and data structures may differ |
| Unity Mono | Medium | ⚠️ Usable in some games | Missing translations or glyphs, text-box sizing, wrapping and special menu/tutorial/skill UI compatibility vary |
| Unity IL2CPP | Low | 🧪 Experimental | Some games do not translate at all; loader integration, metadata compatibility and text capture need further work |
| Unreal Engine | Medium-low | 🧪 Experimental | Some games translate, but UI capture failures, missing text, incomplete replacement and crash reports remain; crash causes need individual diagnosis |
| Godot | Not fully rated | 🧪 Test integration | Extraction and runtime bridges exist; text latency, font-size and layout concerns remain |
| Other / unknown engines | Unknown | ❓ Unverified | Detection or integration may fail; a library engine label alone does not establish support |

These descriptions are not per-engine real-game acceptance results for this Alpha. Earlier positive feedback does not replace installation, translation and restoration verification of the current candidate.

**Mono and IL2CPP are common Unity runtime backends, not Unreal Engine categories.**

Results depend on:

- Engine versions and internal changes introduced by game updates.
- Custom UI/text controls and dynamically generated text.
- Runtime differences such as Mono versus IL2CPP.
- Protected assemblies, resources or other data, and unusual packaging.
- Fonts, text-box layout and wrapping implementation.
- Existing mods or third-party loaders.

Two games using the same engine can behave differently. The goal is reusable engine-level support with fewer game-specific fixes; universal compatibility has not been achieved.

## Alpha feature scope

The desktop requires the user-installed .NET 8 Desktop Runtime (Windows x64). Settings, API configuration, the library and dependency status are available independently. Unity/Unreal payloads and the OCR environment are not bundled or automatically downloaded. Missing dependencies disable the corresponding feature while the application remains usable.

- RPG Maker/Ren’Py bridges are project source; real-game compatibility remains under validation.
- Unity Mono/IL2CPP installation requires lawfully supplied, pinned runtime packages with matching hashes.
- The Unreal resource catalogue worker is not included, so new resource extraction is unavailable. Existing valid catalogues remain subject to the original adapter checks; runtime integration still needs the external payload.
- Automatic cover capture is not included; importing local cover images remains available.
- The Screenshot Translator page opens, but OCR requires separately and lawfully prepared Python, RapidOCR, ONNX Runtime and models at the pinned versions.

See the [Alpha startup guide](docs/ALPHA-START.en.md), [engine dependencies](docs/RUNTIME-PAYLOADS.en.md) and [OCR dependencies](docs/OCR-LOCAL-DEPENDENCIES.en.md). Back up original files and saves before modifying a game.

## In-game translation

Select a game and translation profile, then apply translation or use “Translate and launch.” Exact steps depend on the adapter and connection state.

- Resource catalogue extraction and runtime capture complement each other. Currently visible text should take priority over background catalogue translation; first-time translations still wait for the service response.
- Source language, target language and translation service are configurable. Automatic selection includes an English-first, Japanese-second policy; it should not treat every language catalogue as one source language.
- Extraction, translation and runtime counters may include menu text and resource strings: **entry count is not dialogue count**.
- Some Unreal text can be read and translated without a verified replacement identity. It may only be available in the fallback translation window.
- Existing loader files, such as `doorstop_config.ini`, can conflict with installation. Current protection refuses to overwrite mismatched existing files. General mod coexistence is not implemented, but refusal does not prove that the mod itself is inherently incompatible.

## Game-data editing: which games can use it?

Editing is based on **engine and recognized data structures**. A complete, individually verified editing-support list is not available. Translation support does not imply editing support.

| Engine | Implemented features | Boundaries |
| --- | --- | --- |
| Standard RPG Maker MV/MZ desktop projects | Common values such as money; characters, items, weapons, armour, variables and switches; setting/locking eligible fields; map viewing and teleportation; map/common/running event operations; party recovery and battle actions; undo last edit and save backups | Requires a connection and a loaded game/save. Some actions depend on battle or map state; custom plugin data may not be recognized |
| Ren’Py | Money, items, recognized CG unlock records and variables; numeric search, changed-value filters and batch actions; Chinese labels/notes and favourites; undo last edit and save-backup access | Categories are hints, not verified meanings. Only recognized writable values can be changed; custom galleries are not guaranteed to unlock |
| Unity Mono / IL2CPP, Unreal, Godot and others | No general game-data editing page integration yet | Successful translation does not establish editing support |

Typical workflow: save and exit → choose “Modify and launch” → enter the game or load a save → refresh data → check variable names and current values → apply edits. Translation and editing can be enabled separately.

Backups, component restoration and undo each have limited scopes; they cannot reverse every game event. Keep an independent save backup before editing. Values recalculated by the game may override manual changes.

## Screenshot translation

This is a separate path for games without working in-game integration, text drawn into images, or individual untranslated screens. It reads the captured image; it does not add an in-game adapter.

1. Choose a screenshot translation profile and check the OCR environment and translation-service configuration.
2. Use “Start capture” or a configured hotkey to select a region, or import an image.
3. Inspect OCR text, cleaned source text and translation in the preview; adjust recognition or translation settings as needed.
4. View the original or experimental translated image, copy text/images and save images. Search saved records by date or source/translated text and reopen them.

OCR recognizes text locally. OCR-only use does not require a translation API. AI translation requires a configured endpoint, model and key; relevant text is sent to that configured external service. The current product path uses RapidOCR. The source also contains Paddle/Windows experimental and diagnostic paths; historical documentation does not mean every option is available in the current interface. OCR runtimes and models must be prepared separately.

**Limitations:** small or stylized fonts, low contrast, occlusion, motion and complex layouts can cause missed characters or incorrect reading order. Experimental translated images may have imperfect source-text removal, font sizing, placement or wrapping. This is not guaranteed zero-latency frame-by-frame translation, and some full-screen or protected windows may not be captured.

## Current development priorities

Priorities include visible-dialogue scheduling, reusable text capture/replacement, UI responsiveness, missing glyphs/layout, mod-loader coexistence and exit/crash diagnostics. Compatibility needs ongoing validation across engine versions, text systems and runtime environments. Successful tests do not establish support for an entire engine family.

Games launched through the tool can produce exit diagnostics, but an exit code, including zero, does not establish a root cause or guarantee normal exit.

Passing source checks and builds does not mean all gameplay issues are fixed. Additional [known issues](docs/KNOWN-ISSUES.md) are recorded in Chinese.

## Source layout

| Directory | Contents |
| --- | --- |
| `Source/Screenshot` | Desktop UI, capture/OCR, translation service, game library and bridge management |
| `Source/Embedded` | Embedded translation management |
| `Source/Plugin` | Unity components and related tools |
| `Source/UnrealBridge` | Unreal runtime bridge |
| `Source/UnrealCatalog` | Unreal resource text tools |
| `Source/CoverCapture` | Game cover capture |

## Build

This is a **source repository, not a complete runnable distribution**. Third-party binaries, generated font resources and runtime component packages are excluded. A fresh clone cannot immediately build everything. See the [build guide](docs/BUILD.en.md) and [local dependency manifest](docs/local-dependencies.json).

The desktop project uses .NET 8 / Windows Forms. Helpers may require .NET Framework 4.8, Windows SDK or .NET 10.

## Data and credentials

API keys are configured locally in the application. Personal settings, caches, history images, diagnostic reports, games, saves and test recordings are excluded from the repository. Build outputs and local dependencies are ignored by Git.

## Version and licensing

This independent candidate starts from a frozen baseline, with additional log-redaction fixes and parameterized build/diagnostic paths. Use the baseline here and current source as the reference. Source preparation and passing offline tests do not establish gameplay validation for every title.

Project-owned code is licensed under **GNU GPL v3 (GPL-3.0-only)**. See [LICENSE](LICENSE) for the full standard text. Third-party code and dependencies retain their own copyright and licence terms; the project licence does not relicense them. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for purpose, source, licence files and distribution scope. Binaries, game files and proprietary resources without verified permission are excluded from the source repository and Release distributions.
