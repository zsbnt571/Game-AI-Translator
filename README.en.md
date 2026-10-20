# Game AI Translator · Fusion R1

[简体中文](README.md) | **English** | [Русский](README.ru.md)

A Windows x64 game translation tool combining in-game translation, screenshot OCR, a game library and engine-specific game tools. Bring your own translation API.

**Current version: Fusion R1 1.0.0 Preview 1. Game modification is EXPERIMENTAL.**

1.0.0 starts a new version series. This release combines changes since the public 0.6.0.178 Russian preview; it does not mean that every engine or game has passed stable-release validation. Engine detection, text extraction, translation generation and correct in-game display are separate stages.

## Download and start

Open the [1.0.0 Preview 1 release](https://github.com/zsbnt571/Game-AI-Translator/releases/tag/v1.0.0-preview.1).

| Asset | Purpose |
| --- | --- |
| `Fusion-1.0.0.zip` | Portable Windows x64 application for end users |
| `Fusion-1.0.0-Source.zip` | Matching source, build materials, dependencies and third-party sources |
| [Fusion-0.6.0.178-GPU.zip](https://github.com/zsbnt571/Game-AI-Translator/releases/download/v0.6.0.178-preview.1/Fusion-0.6.0.178-GPU.zip) | Reused optional NVIDIA background-processing dependencies; not standalone and not an interface frame-rate upgrade |

Extract the entire main package into a new folder, then run `GameTranslator.exe`. Do not run it inside the ZIP or copy only the executable. GitHub's automatic **Source code** archives are not executable packages and do not replace the dependencies in the complete source asset.

1. In **Settings → Translation profiles**, set the API endpoint, key and model, test the connection, then save the profile.
2. In **Translation preferences**, choose the target language, source scope, style and instructions, then save those preferences.
3. Import a game and use its translation launch controls, or open **Capture translation** to translate on-screen text.

API connection settings and translation preferences save separately. Switching an API profile does not clear shared preferences. New settings apply to new captures; running games usually need a restart to fully adopt a new profile. API credits and charges depend on your provider.

## What's new

See the [complete three-language release notes](docs/RELEASE-NOTES-1.0.0.md).

### Interface and browsing

- The main window, library, details, settings, capture, history, translation management and modification pages use a consistent WPF interface, with revised controls, themes, focus cues and narrow layouts.
- The library's right side scrolls as a whole, including the heading, search, recent shelf and grid. Navigation scrolls separately. Returning from game details preserves your position.
- Game switching keeps the layout in place until the title, cover and controls are ready, reducing disappearing covers, page jumps and repeated button resets.
- Modification categories reuse loaded data and preserve selection and scrolling. Fixes address reproduced long waits, outdated responses replacing newer pages and duplicate-key errors.
- Cover capture lets you inspect the original image and banner crop. Client-area capture, black-frame checks and error feedback are improved; a failed capture keeps the previous cover.

### Usable translations and consistent names

- Meaningful player-visible foreign names and text translate by default, including characters, places, countries, skills, classes, items and quests. A name is kept unchanged when you explicitly save that preference.
- Character-entry alphabet/kana cells, shortcut keys, control tokens, placeholders, code, paths and language-selection labels remain protected.
- Usable translations appear first. Small untranslated fragments or inconsistent names receive limited repairs instead of repeatedly discarding readable results. Status and reasons are visible; automatic retries have a limit.
- Damaged controls, numbers or placeholders, and responses containing only an explanation/refusal, are still rejected as game text. Manual corrections are retained.
- Words and Names groups people, places, skills, items and other supported categories, showing originals, preferred names, evidence and status. Identified entities stay consistent while ordinary words remain context-sensitive; quest titles are not forced into ordinary dialogue.
- Rules are separate for each game and target language. Editing a name schedules related automatic translations for updating; cleaning incorrect automatic candidates preserves manual rules and imported terms.

### Read text and prepare translations

- File results, source/translation pairs, errors and progress are available together. Occurrences, unique entries, language scope and cache matches have separate counts.
- Collection from supported WOLF resources, databases and maps is improved, including deep-folder failures. Prepared translations save in batches and are reused later.
- Repairs eligible Ren’Py original dialogue/menu and Godot shared display fields that previously missed pretranslation. Some custom typewriter support is enabled only for versions with matching verified resources.
- Runtime-generated text may still need live translation. A 100% progress figure means completion of the currently discovered scope, not complete coverage of an entire game.

### Game modification — Experimental

Common actions, characters, relationships, items, variables, maps, events, locks and general data follow capabilities Fusion can actually read. An engine or game is not shown as supporting a feature merely because its structure was guessed.

- **Common** focuses on money candidates and frequent controls, without a search filter. Character attributes and relationships are grouped by object; full data has separate pages.
- **Characters** use a list/detail design. WOLF is closer to the RPG Maker layout, with original and translated names together. Only the selected object's attribute controls are created, preserving unsaved input.
- **CG/replay** candidates appear in a collapsed Common group and need interpretation. The feature does not unlock galleries automatically or identify every gallery mechanism.
- **Icons and maps:** readable RPG Maker item icons remain. Generic WOLF icon mapping is incomplete; map views display readable cells/events, not a full textured editor.
- **Speed** offers a slider, preset buttons and minus/plus controls. WOLF currently supports **0.25–100×**, and RPG Maker MV/MZ **1–100× integer acceleration**. Actual speed depends on game load; there is no universal reliable speed interface for all other engines.

Modification uses only implemented engine interfaces. Keep a save backup and check what a candidate field means before editing it. Reading a value does not establish that changing it is appropriate, and listing an engine does not certify every operation.

### English, Russian and performance

- Fills gaps on recent English/Russian pages, including dynamic counts, errors, progress and map instructions. Open pages preserve selection, scrolling and input when language changes; long headers resize or wrap.
- Advanced profiles support JSON request parameters and examples. Deleting a profile in use lets you choose a replacement.
- Reduces drawing of invisible content, repeated cover scaling and layout work for unchanged states. Persistent presentation problems can use compatible rendering for the affected window.
- **Stable 60 FPS is not promised.** High-resolution maximized windows can still be slower, and initial name preparation for large data sets can take time. Game speed and Fusion's interface frame rate are independent.

## Interface language versus translation target

| Setting | What it controls |
| --- | --- |
| Interface language: 简体中文 / English / Русский | Fusion buttons, explanations, categories, progress and errors |
| Translation target | Translated game text, names and modification fields, using the current game's profile |
| Source-language scope | Which languages in actually collected content may be translated |

For example, a Russian interface can translate a Japanese game into English. Changing the interface language does not rewrite source text, resubmit saved translations or replace a numeric draft you are editing. A fresh installation still defaults to Simplified Chinese.

Combinations such as English + Japanese limit the languages in collected content; they do not request every inactive language pack. Automatic detection helps with mixed text, but short words, names and shared characters remain ambiguous. Existing target-language text and excluded sources are protected according to the selected rules.

## Limits and validation

This release build passed **615 of 615 controlled checks**: 342 interface/localization checks and 273 modification/translation regression checks. These results do not establish extended real-gameplay or physical display-FPS acceptance.

Controlled checks, real-control screenshots, API samples and particular game tests are different evidence. None establishes all-engine extended gameplay, every DPI/monitor setup, all GPU hardware or stable 60 FPS. Unity IL2CPP, Unreal, Godot 3 and some plugins/resource formats retain limits; see [Known issues](docs/KNOWN-ISSUES.en.md).

Translated-image background processing remains experimental; OCR and embedded translation have different coverage. Provider latency and quotas, fonts, layout, image text and custom rendering affect results. Proprietary Oodle and some heavy optional models are excluded. Optional NVIDIA dependencies still use the 178 asset, without a claim of new all-hardware inference validation.

## Upgrade and data

Close Fusion and affected games, back up the old installation's actual data folder, extract the new version into a separate folder, then migrate the data you need. The default is `data` under the application directory; check the old Storage setting if customized. Keep the old version and backup, and do not let two versions write to one directory concurrently.

After migration, check the library, target/source languages and API. A different Windows account or device may require entering keys again. A style change does not automatically erase existing translations; target language and cache scope are managed separately. Do not upload populated data folders, logs, credentials, games or saves as release assets.

## Development and licensing

The matching source tag is [v1.0.0-preview.1](https://github.com/zsbnt571/Game-AI-Translator/tree/v1.0.0-preview.1). End users should use the main ZIP; developers should use the complete `Fusion-1.0.0-Source.zip` and [Build instructions](docs/BUILD.en.md).

| Directory | Contents |
| --- | --- |
| [Source/](Source/) | Desktop application, translation services and engine adapters |
| [tools/](tools/) | Build and verification tools |
| [docs/](docs/README.md) | Usage, release notes, build information and known issues |
| [licenses/](licenses/README.md) | Third-party licenses |
| [third-party-source/](third-party-source/README.md) | Index of corresponding third-party source materials |

Runtime ZIPs, DLLs and generated fonts are not stored directly in Git. The complete source asset supplies build materials. Internal bridges may retain their compatibility version numbers instead of matching the UI version.

Project-owned code uses **GPL-3.0-only**. Third-party components retain their own licenses; see [Third-party notices](THIRD_PARTY_NOTICES.md). Historical versions remain at their original tags and in [Releases](https://github.com/zsbnt571/Game-AI-Translator/releases).

Report the version, system, engine, languages, steps and a minimal example through [GitHub Issues](https://github.com/zsbnt571/Game-AI-Translator/issues). Remove credentials, tokens and personal information from logs. Games or saves are not required.
