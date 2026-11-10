# Game AI Translator · Fusion 1.1.165

[简体中文](README.md) | **English** | [Русский](README.ru.md)

Fusion is a Windows x64 application for supported-engine game translation, screenshot OCR, game-library management and experimental AI tools.

## Download and start

Download **Fusion-1.1.165.zip** from the [1.1.165 release](https://github.com/zsbnt571/Game-AI-Translator/releases/tag/v1.1.165), extract the complete folder and run **GameTranslator.exe**. This is a prerelease.

1. Configure your compatible API or already-running local model service in **Settings → Translation profiles**, then test the connection.
2. Select the target language, prompt preset and optional glossary packs, then save.
3. Import a game and use the available engine adapter or screenshot translation.

The package includes .NET, Python and OCR components, but no large language model. Provider access and charges are separate. Some web features need the Windows WebView2 Runtime.

## Current features

Nine-language UI and recent notifications/errors; six prompt presets and six glossary packs; rich notes and tag browsing; engine icon appearance; screenshots and silent video recording; centralized shortcut settings; a translation test workspace and experimental AI assistants.

See the [cumulative release notes](docs/RELEASE-NOTES-1.1.165.md) and [validation scope](docs/VALIDATION-1.1.165.md). The final application passed 3,967 controlled checks with zero failures. This does not certify every game or model.

## Data, updates and source

Default application data lives in `data`, chats in `AI chat`, screenshots in `data/screenshot` and recordings in `data/video` beside the application. Custom data locations continue to follow their configuration.

Before updating, exit through the tray menu and back up the whole old installation. Extract the new package separately and preserve your own data. Do not run two versions against the same data. Public archives contain no personal keys, library data or chat records.

Developers should download **Fusion-1.1.165-Source.zip**; GitHub's generated Source code archives omit binary build inputs. [Build instructions](docs/BUILD.en.md) explain the toolchains and the unverified full auxiliary-component rebuild.

Project-owned code is GPL-3.0-only. Dependencies retain their own licenses; see [third-party notices](THIRD_PARTY_NOTICES.md). Report reproducible problems through [Issues](https://github.com/zsbnt571/Game-AI-Translator/issues), without secrets or personal data.
