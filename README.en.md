# Game AI Translator · Fusion 1.1.127

[简体中文](README.md) | **English** | [Русский](README.ru.md)

Fusion is a Windows x64 tool for in-game translation on supported engines, screenshot OCR translation, a game library and optional game tools.

## Download and start

Download **Fusion-1.1.127.zip** from [Releases](https://github.com/zsbnt571/Game-AI-Translator/releases/tag/v1.1.127), extract the entire folder and run **GameTranslator.exe**. Keep all accompanying files. The application includes its .NET, Python and OCR components; you do not need to install these separately.

1. Open **Settings → Translation profiles** and configure your compatible translation API or an already running local model service.
2. Select the translation target language, save and test the connection. Use **No API key required** only when your service needs no key.
3. Import a game and enable the translation options available for its engine. Screenshot translation is available as an alternative.

A large language model/server is not included. Provider access and any usage charges are separate. Optional Google cover-image search uses the Windows WebView2 Runtime; this is separate from the main translation components.

## Features in this version

- Nine interface languages, with system-language selection by default for new settings. Existing language choices survive the menu reorder. Interface language and translation target are independent.
- Improved resource reading, pretranslation and cache reuse across supported engine adapters.
- Improved translation controls after ordinary game launch; translation and game modification have separate controls.
- Custom-font selection, preview and restoration on supported adapters, while preserving the default font adaptation.
- **Gender agreement in translated dialogue — limited Ren'Py support.** Known speaker identity and explicit English gender/pronoun statements can guide English, Spanish, French, German and Russian translations. Other engines do not yet supply this role context. There is no additional user setup; uncertain context keeps the existing behavior.

Chinese, Japanese, Korean and ordinary translation caches retain their existing behavior. See [release notes](docs/RELEASE-NOTES-1.1.127.en.md) and [known limitations](docs/KNOWN-ISSUES.en.md).

## Update and developer downloads

Close Fusion normally, back up its actual data directory and extract the new package separately. Keep the old installation and avoid running two versions against the same data directory.

**Fusion-1.1.127-Source.zip** is the complete developer source asset. GitHub's automatic Source code archives omit binary build inputs and are not runnable application packages. [Build instructions](docs/BUILD.en.md) apply to developers, not ordinary installation.

Project-owned code is GPL-3.0-only; [third-party notices](THIRD_PARTY_NOTICES.md) cover other components. [Validation](docs/VALIDATION-1.1.127.md) describes the checks performed. Report reproducible problems through [Issues](https://github.com/zsbnt571/Game-AI-Translator/issues), excluding keys and personal data.
