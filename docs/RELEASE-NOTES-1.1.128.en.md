# Fusion 1.1.128

Fusion is a Windows x64 game-translation tool with in-game translation on supported engines, screenshot OCR translation, a game library and optional game tools. This release brings together the improvements made since the public **1.0.0** release.

## What changed since 1.0.0

- **Nine interface languages.** Simplified Chinese, Traditional Chinese, English, Japanese, Korean, Spanish, French, German and Russian. New settings follow the Windows language, falling back to English when needed. Saved choices survive the reordered language menu. The interface language and game translation target remain separate.
- **More text available for pretranslation.** Expanded resource reading for supported RPG Maker, Ren'Py, Unity/Naninovel, Godot, WOLF and web-engine formats. This includes more dialogue, choices, names and interface text where the engine exposes them. Cached translations are reused first; live translation fills remaining gaps.
- **Better cache reuse and fewer duplicate requests.** Improved handling of player-name variables, reused text controls and requests already in progress. Late results are checked against the current text. Progress and retry handling better distinguish completed work from queued or failed items.
- **Translation controls after normal launch.** On compatible adapters, games started through Fusion can establish a connection with translation initially disabled, then switch it on or off in the same process. Normal launch does not require an API key. Translation and game modification have independent controls.
- **Font and text-display improvements.** Added multilingual custom-font preview, missing-glyph checks, live application and restoration on supported adapters. Improved Unity text measurement and Godot clipping/name-input layout. The existing default font adaptation is retained; custom fonts are applied only when selected.
- **Game tools and speed controls.** Improved engine-specific speed handling, state restoration and per-game isolation, including RPG Maker logic-based speed paths. Ren'Py skip controls and continuous value-edit handling were improved. Available tools and speed behavior still depend on the engine.
- **Local-model setup.** Fixed the no-key option for compatible services, so a dummy API key is no longer required. Connect a running OpenAI-compatible local model server or configure a supported online API.
- **Automatic gender agreement, with cache protection.** No extra setup is needed. The shared translation path can use explicit English gender or pronoun evidence within the same original text item when translating into English, Spanish, French, German or Russian. Limited Ren'Py role context can also assist across dialogue lines. Unknown or conflicting evidence keeps the normal translation path. This is not full character recognition across all engines.

## Fixes specific to 1.1.128

Adding character information no longer hides a valid ordinary cached translation. Existing translations can still be displayed without a forced API request or retranslation. Late character information no longer discards an ordinary request already in progress, and failed retries retain usable old translations and failure details.

The new gender assistance leaves Chinese, Japanese and Korean translation paths unchanged. It does not clear or migrate existing caches.

## Download and start

Download **Fusion-1.1.128.zip**, extract the entire folder, and run **GameTranslator.exe**. The package includes .NET, Python and OCR components; these do not need separate installation. Configure your translation API or a running local model service under **Settings → Translation profiles**. A large language model/server and provider credits are not included. Optional Google cover-image search uses the Windows WebView2 Runtime.

**Fusion-1.1.128-Source.zip** is for developers. GitHub's automatic Source code archives are not the runnable application and omit some binary build inputs.

## Compatibility

This remains a preview release. Dynamic dialogue, brief original-text flashes, older font systems, replay screens, maps and speed after skipping can still vary by game. Ordinary-launch toggling requires a compatible connection established through Fusion. No full-game compatibility claim is made. See [known limitations](KNOWN-ISSUES.en.md) and the separate [validation report](VALIDATION-1.1.128.md).

Project-owned code remains GPL-3.0-only; bundled third-party components retain their own licenses.
