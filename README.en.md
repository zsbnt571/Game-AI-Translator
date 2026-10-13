# Game AI Translator · Fusion R1

[简体中文](README.md) | **English**

A Windows game translation tool combining in-game translation, screenshot OCR translation and a game library. Bring your own API provider, share translation preferences across workflows, and control which languages are translated in actually encountered content.

**Current preview: 0.6.0.177 Preview 1.** This is a public Windows x64 portable preview. Engine detection, text extraction, translation generation and successful in-game display are separate stages.

## Download and start

Open the [0.6.0.177 Preview 1 release](https://github.com/zsbnt571/Game-AI-Translator/releases/tag/v0.6.0.177-preview.1) and download its assets:

| Asset | Purpose |
| --- | --- |
| `Fusion-0.6.0.177.zip` | Complete application with the default CPU dependency set |
| `Fusion-0.6.0.177-GPU.zip` | Optional NVIDIA background-processing dependencies; not standalone |
| `Fusion-0.6.0.177-Source.zip` | Version-specific developer source/build materials |
| `SHA256SUMS.txt` | Download integrity checks |

Fully extract the main ZIP and run `Fusion-0.6.0.177/GameTranslator.exe`. Keep its companion files. GitHub's automatic **Source code** downloads are not executable packages.

1. **Settings > Translation profiles**: configure your endpoint, API key and model, test, then save.
2. Scroll to **Translation preferences on the same page**: set target, source scope, style and instructions, then **Save preferences**.
3. Import a game and use its translation launch controls, or open **Capture translation**.

API profiles and shared preferences save independently. Switching API profiles does not reset your target/style. New preferences apply to new captures and restarted games. The UI language is independent of the translation target.

See [Usage](docs/USAGE-176.en.md). Offline Chinese and English guides are also inside the application ZIP.

## Features

- In-game text collection/replacement through integrated adapters, translation caches and terminology.
- Local screenshot OCR, translated text and preview; translated-image background processing is experimental.
- Game import/launch, cover artwork, categories and per-game API profile selection.
- Automatic, single-source and custom multi-source translation, preserving target-language portions, excluded sources, variables and code.
- Natural, Game-oriented, Cultural localization, Literary and Custom styles. Meaning and context take priority; short menu labels may be identical across styles.
- Engine-specific game/resource tools within their implemented scope, not universal game editing.

## Choosing source languages

**English + Japanese allows those languages in collected content; it does not request translation of every unused Japanese pack.**

| Mode | Best for | Trade-off |
| --- | --- | --- |
| Automatic | Unknown or genuinely mixed sources | Less setup; short/ambiguous text and names can still be misclassified |
| One language | Deliberately translating only one source | Other identified languages stay, including actual mixed dialogue |
| Custom combination | Known mixtures such as English with occasional Japanese | Selected sources translate; others stay; no primary/secondary ordering |

Existing English normally stays with an English target; existing Japanese stays with a Japanese target. Automatic can translate actually encountered Korean; an English + Japanese combination normally preserves it.

The earlier .176 Ren'Py/Godot active-locale samples did not collect inactive-pack-exclusive strings in bulk. This is not a universal guarantee for every engine, mod, hidden control or resource pretranslation workflow.

## Dependencies and limitations

The main package bundles .NET runtimes, Python, RapidOCR/models, CPU background-processing dependencies, integrated adapters and licenses. Users do not need to install Python or the .NET 8 Desktop Runtime first.

API service/credits, games, Windows platform components and optional GPU drivers remain user-provided. Windows OCR/legacy helpers use OS .NET Framework 4.8. Proprietary Oodle and some heavy optional models are excluded. See [Dependencies](docs/DEPENDENCIES-176.en.md).

Unity IL2CPP, Unreal and engines not retested here retain compatibility limits. Mods can conflict, first translations depend on provider latency, and translated-image rendering may show artifacts. No clean-Windows VM acceptance has been completed. See [Known issues](docs/KNOWN-ISSUES.en.md).

## Upgrade and privacy

Back up the actual old data directory after closing Fusion and affected games, then copy data into the new `Fusion-0.6.0.177/data`. Check the old Storage setting if customized. Keep the old application and pre-upgrade backup; do not share one writable directory between versions.

Verify library, target/source choices and API connectivity after migration. Encrypted keys may require re-entry on a different Windows account/device. Style changes do not clear previous translations.

The public package has no personal keys, game library/history, games or saves. Share only original release archives, not a populated data directory.

## Development and licenses

The main branch's [Source/](Source) contains the current **0.6.0.177 Preview 1** product code. [tools/Build-Desktop.ps1](tools/Build-Desktop.ps1) is its build entry point. Each tracked product source file matches the published source asset; see the [source checksum index](git-source-index.json).

| Directory | Contents |
| --- | --- |
| [Source/](Source) | Current desktop, screenshot translation and engine adapter source |
| [tools/](tools) | Build tools for the current version |
| [docs/](docs/README.md) | Current usage, build, dependency and known-issue guides |
| [licenses/](licenses/README.md) | Third-party licenses and component inventories |
| [third-party-source/](third-party-source/README.md) | Index of third-party sources shipped in the source asset |

Runtime ZIPs, DLLs and generated font resources are release assets, not Git files. For development, download the complete matching `Fusion-0.6.0.177-Source.zip` and follow [Build instructions](docs/BUILD-176.md). End users should download Fusion-0.6.0.177.zip above.

The historical Alpha source and documentation remain available through their original tag, commits and release. See [Version history](docs/HISTORY.md).

Project-owned code remains **GPL-3.0-only**. Third parties retain their own licenses; the package includes notices, component inventories and corresponding third-party source materials. See [Third-party notices](THIRD_PARTY_NOTICES.md).

Report version, OS/game/engine, source/target settings, steps and a minimal example. Redact credentials and personal content from logs. Games and saves are not required.


Bug reports and feature suggestions are welcome on [GitHub Issues](https://github.com/zsbnt571/Game-AI-Translator/issues).
