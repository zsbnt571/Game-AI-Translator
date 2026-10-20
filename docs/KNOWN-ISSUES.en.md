# Fusion R1 1.0.0 Preview 1 — Known issues and validation limits

[简体中文](KNOWN-ISSUES.md) | [Русский](KNOWN-ISSUES.ru.md) | [Home](../README.en.md)

**Game modification is experimental. Detecting an engine, reading data, completing a write and sustained gameplay are separate validation stages.**

## Translation and text coverage

- Pretranslation covers text with identifiable provenance that was actually read and belongs to the selected language scope. Runtime-generated text, image text, custom drawing, unknown archives and some scripts may remain missing. 100% refers only to the currently discovered scope.
- Eligible original display-text collection is improved for Ren’Py and Godot. This does not guarantee every hidden control, plugin or inactive language pack. Some GameMaker resources still require sufficient evidence of the language actually in use.
- WOLF resources, paired databases, deep-folder collection and some custom typewriter paths have fixes. Custom typewriter support requires verified resource combinations and does not cover every version. Some currently displayed sentences need to be displayed again or the game restarted before using a new translation.
- Small untranslated fragments or naming mismatches keep usable translations and receive limited repair attempts. Automatic requests stop at the retry limit; inspect the reason in translation management. Damaged formatting, numbers or placeholders, and explanation/refusal-only answers are still rejected as text.
- Automatic naming depends on provenance. Unusual titles, different entities sharing a name, multilingual aliases and short words can remain ambiguous. Ordinary words should not become fixed wording for every context. Manual corrections and imported rules are retained and existing conflicts need review.
- Changing style, instructions or target language does not rewrite all old caches. Targets are managed separately, and updating related automatic translations after a name edit takes processing time.
- New translations depend on API latency, quota and model output. API success does not validate fonts, wrapping, display or narrative meaning. Historical reports of missing glyphs, delayed display or unexpected game exits are not all established as resolved by this interface check.

## Game modification — Experimental

- Money, attributes, relationships and CG/replay groups can contain candidates requiring review. A field name alone does not prove its purpose. There is no automatic unlock-all-gallery operation.
- Only readable categories are displayed. Different game structures mean WOLF, RPG Maker, Ren’Py, Unreal and other engines do not gain identical tools. Unity IL2CPP, Unreal, Godot 3 and versions not covered here retain compatibility limits.
- The current WOLF speed interface accepts 0.25–100×; RPG Maker MV/MZ accepts 1–100× integer acceleration. Requesting 100× does not guarantee actual speed, audiovisual synchronization or extended stability. Other engines lack universal support. Speed does not change Fusion's interface frame rate.
- Readable RPG Maker item icons remain available. Generic WOLF icon mapping is incomplete. Maps display readable cells/events rather than a complete textured map or editor.
- Category changes and background updates preserve selection, scrolling and unsaved input, but initial collection/name preparation for large databases can remain slow. Reproduced waits and duplicate-key paths were fixed; not every wait in every game is eliminated.
- Keep a save backup before editing. Runtime edits may not persist to saves; loading a save, switching objects or rebuilding game data may require a refresh. Locking, undo and persistence depend on the specific interface.
- RPG Maker component recovery only adopts Fusion files confirmed by installation records, component hashes and backup hashes. Other mods, modified components and inconsistent backups still trigger protection.

## Interface, performance and dependencies

- Stable 60 FPS is not promised; high-resolution maximized windows can remain significantly slower. Reduced redraws and cover/category caches address identified paths, not every physical display-frame scenario.
- English/Russian checks cover recent real WPF controls, dynamic text, narrow layouts and language switching. Game translations use the chosen target; interface localization does not certify Russian fonts and wrapping in every game.
- Specialized viewers, helpers and compatibility entry points may still use existing controls/platform components. The WPF update does not rewrite every underlying component.
- If WPF display fails on a particular machine, close Fusion and use `GameTranslator.exe --legacy-ui` for the compatibility interface. Do not run two versions writing to the same data directory.
- Screenshot OCR, image background processing and embedded translation have different coverage. Translated-image background processing remains experimental and can leave background/layout artifacts.
- Optional NVIDIA dependencies reuse the 178 asset without new all-hardware inference acceptance. Proprietary Oodle, some heavy optional models and GPU drivers are excluded. Legacy helpers still use Windows platform components.
- No complete clean-Windows-VM, all-DPI/theme/monitor, accessibility or all-engine extended-gameplay validation is claimed.

## This release build

This release build passed **613 of 613 controlled checks**: 342 interface/localization checks and 271 modification/translation regression checks. These results do not establish extended real-gameplay or physical display-FPS acceptance.

Historical checks do not substitute for validating this release. Controlled tests, resource checks, real-control screenshots and actual game runs are recorded separately. See the [three-language release notes](RELEASE-NOTES-1.0.0.md).

Reports should include the version, engine, interface/target languages and steps. Remove credentials and personal information from logs. Public uploads of games or saves are not required.
