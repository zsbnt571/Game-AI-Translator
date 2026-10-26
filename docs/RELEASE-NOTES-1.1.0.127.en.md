# Fusion R1 1.1.0 Preview 3 (build 127)

Version: 1.1.0.127  
Release identity: 1.1.0-preview.3+dialogue.5  
Tag: v1.1.0-preview.3+dialogue.5

[简体中文](RELEASE-NOTES-1.1.0.127.md) · [Русский](RELEASE-NOTES-1.1.0.127.ru.md)

These notes summarize the main user-visible changes since public v1.0.0-preview.1. Intermediate local candidates and historical tests are not separate public releases or additional games verified on this build.

## Earlier text discovery and fewer duplicate requests

Resource reading, pretranslation and exact cache hits remain the priority; live translation fills gaps. Ren’Py improves source fallback for untranslated language branches, explicit menu/exit text, safe formatting and static character names, with individual node failures isolated. Unity adds Naninovel story and read-only field sources, player-name templates, reused-control identity and late-result checks. Godot extends script/imported-resource reading and corrects clipping/restoration. RGSS, web-engine, GameMaker and WOLF resource paths also receive improvements.

The shared queue avoids duplicate requests for already prepared names and dialogue and reuses eligible short single-number labels while respecting target language, terminology and explicit retranslation. A font that is not ready no longer automatically triggers another model request.

The Godot workflow introduced in 125 and inherited by 126 acknowledges batches of exact cached entries before enabling translation. A production host with an actual neutral Godot scene covers warm/cold caches, reused controls, typewriter display of complete originals and disable/re-enable. In that scene the first enabled frame changes from original text to a cache hit. This is not complete Towns Charm acceptance or a guarantee that every entry in an arbitrarily large cache appears translated on its first frame. Unprepared text still uses ordinary discovery.

## Clear launch, translation and modification states

Pages share launch, return, restart and normal-exit actions. Ambiguous process identity or multiple candidate instances are not silently guessed. Games keep separate connections and state. A normal-close request respects a game's own save/exit decision instead of forcibly terminating it.

Supported normal-launch flows prepare a disabled translation connection without pretranslating or requiring a key. Once connected, translation can be toggled in the same process. External unbridged launches, old resident components and engine differences remain limited. Exiting an old process to load an updated component is distinct from requiring a restart for every normal toggle.

Modification requires explicit permission; opening a page or category does not grant it. Speed remains under Game modification → Common and follows that game's modification switch. Disabling modification requests 1× restoration; translation is independent. Supported Unity/Godot/Construct and RPG adapters use their own rate interfaces or logic stepping, with improvements to input drafts, acknowledgments, pauses/skipping/animation and multi-instance recovery. Fixed steps, video, audio, network waits and click-gated dialogue may not speed up proportionally.

## Preserve the default font; choose and restore custom fonts

Default mode keeps the existing adaptation. Per-game system-font/file selection, arbitrary-text preview and missing-glyph hints are available; supported adapters can apply a chosen font while running and restore the default. A failed choice preserves the prior selection and useful display.

Scoped fixes address old Unity TMP measurement contamination, excessive shrinking and some name/dialogue rendering, plus Godot clipping ancestors and original-text restoration. MansionDays native-Chinese damage, other rough/incomplete glyphs, old Unity custom atlases and complex shaping are not all resolved. Changing the default font is not evidence that these problems are fixed.

## Nine UI languages, no-key services and automatic grammar

The interface now covers Simplified/Traditional Chinese, English, Japanese, Korean, Spanish, French, German and Russian across main pages, settings, dialogs, errors, tray text, tooltips and accessibility names. New configurations follow the system with English fallback. Saved choices remain; older missing-field configurations retain compatibility behavior. Interface and target languages are separate. User names, paths and source text are not rewritten as UI labels. Build 127 normalizes ordering and selects/saves/reloads by stable language value rather than list position.

Compatible keyless services work with an empty key; a dummy value is unnecessary. The endpoint and model must still be valid. Local models require a separately deployed, running service. Deployment research does not establish built-in model management, training or an additional integrated backend.

Automatic character grammar adds no required setup. The current reliable source is a unique static standard-character binding in Ren’Py plus explicit English self-description/pronoun evidence, used for English, Spanish, French, German and Russian requests. Unknown, conflicting, dynamic or ineligible context follows the old path. Names and appearance are not used to guess gender; a speaker's identity is not imposed on an unknown addressee.

Simplified/Traditional Chinese, Japanese and Korean retain their previous path. New character-aware results use a separate versioned cache. Ordinary caches, usable translations and old role-cache files remain. Sources with unresolved issues at session start retain their existing failure/retry path.

## Failure history and open work

Progress better distinguishes discovered text, pending requests, queued items and pending writes; 100% covers discovered scope only. Generic quality rules reduce false positives from variables, controls and code identifiers while retaining reasons, retry counts and useful old translations.

The Towns Charm component-description cache can pass reassessment under the revised rule with a history receipt: **this is not an API retranslation**. The sponsor list retains a soft failure and four attempts. Successful retranslation of both records has not been demonstrated.

The [full 24-item ledger](KNOWN-ISSUES.en.md) retains original-game misses, cross-engine toggles, fonts, Ren’Py replay closures, old-cache term backfilling, Unity dynamic coverage, WOLF edits/maps and speed/focus limits. The Yes, Master! exception site is known, but the 0xC0000005 cause and fix are unconfirmed; no attribution to Fusion or the game is made. Deeper FLife2 crash work stays paused. Complete translation import/sharing and future-platform/external-tool research are not presented as new integrated features.

## Self-contained runtime maintenance

The public package has been serviced to .NET 8.0.33/10.0.14 in six locations: the desktop, cover helper, UnitySchema, UnrealCatalog, and the GameMaker copies in the application and complete source assets. Runtime servicing does not rebuild GameMaker business code or unrelated third-party components. The legacy Unity IL2CPP loader still contains .NET 6.0.7; its compatibility boundary and unsupported status remain explicit. The final DLL passed 332 interface checks and 852 shared-host checks under the bundled .NET 8.0.33 runtime in isolation, plus 2 separate package startup/read-back checks. Earlier checks are not added to these totals.

## Evidence and assets

[Validation](VALIDATION-1.1.0.127.md) separates local 127, the independent public build and historical 126/124 evidence. This 127 cycle made 0 translation API calls and launched 0 original games. The final 126 live-provider sample passed 8 definite expectations; 2 unknown-addressee cases were observations only. The first round's 2 agreement failures remain recorded. Both rounds total 20 requests and 42,353 tokens, not ten universally successful gender-identification cases.

Application: Fusion-1.1.0-Preview3.zip. Complete source: Fusion-1.1.0-Preview3-Source.zip. Use the release checksums; automatic GitHub Source code archives are not the complete dependency-bearing source asset.

The public program keeps the same version and feature scope while removing historical fixture absolute paths, applying path mapping and omitting debug symbols. The retained local installation build has a separate identity; its hash and receipts must not be confused with the public build. Keep the old installation and actual data backup, extract the full package separately, and avoid concurrent writers to the same data directory.

[Home](../README.en.md) · [Build](BUILD.en.md) · [Third parties](../THIRD_PARTY_NOTICES.md)

