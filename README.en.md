# Game AI Translator · Fusion R1

**1.1.0 Preview 3 · build 127**  
Application: 1.1.0.127 · Release identity: 1.1.0-preview.3+dialogue.5

[简体中文](README.md) | **English** | [Русский](README.ru.md)

Fusion is a Windows x64 game translation tool with in-game translation for supported engines, screenshot OCR translation, a game library and game tools. Configure your own compatible online API or connect to an already running local model service. Game modification and generic speed control remain experimental.

## Download and start

Get the complete **Fusion-1.1.0-Preview3.zip** from [GitHub Releases](https://github.com/zsbnt571/Game-AI-Translator/releases), extract it into a new directory and run GameTranslator.exe. Keep the entire folder: the executable alone is missing required runtimes and models.

1. In **Settings → Translation profiles**, set the endpoint, model and required key, test the connection and save. Select **No API key required** for a service that needs no key; a dummy key is unnecessary.
2. Select the game's source-language scope and target translation language separately.
3. Import a game and use its available translation controls. Screenshot translation is an alternative where in-game integration is unavailable.

A successful connection, a valid translation and correct display in the game are separate results. Provider costs, latency and output quality depend on your service.

## Main changes since the previous public release

- **Resource reading and cache reuse first.** Improved engine catalogs, request deduplication for cached names and dialogue, player-variable templates and reused-control identity. With the new supported Godot protocol, exact existing translations are acknowledged before translation is enabled, reducing the original-text window for cached content.
- **Separate launch, translation and modification controls.** Supported normal-launch flows prepare a disabled translation connection so it can be switched on or off in the same process once connected. Opening a modification page does not grant modification permission. Connections, permissions and speed states remain separate for each game. External launches, old resident components and engine differences still impose limits.
- **Reversible font choices.** Default mode retains the existing adaptation. After an explicit custom-font choice, supported adapters can apply it while running, preview missing glyphs and restore the default. Older Unity versions and complex text shaping remain limited.
- **Clearer speed and failure states.** Supported engines use dedicated logic stepping or engine rate controls, with improvements to retaining the selected rate after pauses, skipping and animations. A requested multiplier is not proof of equal story speed. Translation failures retain their reasons, retry counts and usable previous text.
- **Nine interface languages.** 简体中文, 繁體中文, English, 日本語, 한국어, Español, Français, Deutsch and Русский. New configurations follow the system language with English fallback. Saved choices are retained; older configurations without the field keep their compatibility behavior. Build 127 fixes selection and saving after the list is reordered.
- **Automatic character grammar assistance.** No extra switch or character form is required. The current reader uses verifiable static Ren’Py speaker bindings and explicit English source evidence for English, Spanish, French, German and Russian requests. It does not guess from names or appearance, or apply the player's gender to other characters.

Interface language and translation target are independent. Simplified/Traditional Chinese, Japanese and Korean retain their previous translation path. New character-aware results use a separate cache, preserving ordinary caches, usable translations and failure history. Correct grammatical agreement is not guaranteed for every model or sentence.

See the [cumulative release notes](docs/RELEASE-NOTES-1.1.0.127.en.md) for details and version-specific evidence. Catalog extraction, cache hits and visible display are tested separately. “100% of discovered text” does not mean the entire game has been translated.

## Known limits

Read the [known issues and retained 24-item ledger](docs/KNOWN-ISSUES.en.md). Initial original text, missed lines, rough or incomplete glyphs, replay closures and dynamic-story coverage remain open in some games. Same-process toggling after normal launch has not received comprehensive original-game acceptance across engines. The cause of the Yes, Master! 0xC0000005 crash is unconfirmed; it is not attributed to Fusion or the game. The deeper FLife2 crash investigation remains paused.

The final public DLL passed 332 interface-language checks and 852 controlled shared-host checks using the isolated public copy’s .NET 8.0.33 runtime, with no failures. The package separately passed 2 startup/state-read checks. The local updater’s 85 synthetic checks remain separate. This cycle made 0 translation API calls and launched 0 original games. Earlier public-build results and 126 neutral-engine/provider samples are retained as historical evidence, without adding them to this cycle; see [Validation](docs/VALIDATION-1.1.0.127.md).

## Upgrade, data and source

Exit Fusion normally, back up the actual data directory shown in Storage settings, and extract the complete package separately. Keep the old version and backup. Avoid two versions writing to the same data directory. Do not blindly transfer a personal data-location pointer to another computer; changing computer or Windows account may require entering keys again.

The complete **Fusion-1.1.0-Preview3-Source.zip** includes the corresponding dependencies and build materials. Git source and GitHub's automatic Source code ZIP omit binary dependencies and are not runnable packages. See [Build instructions](docs/BUILD.en.md) and [Third-party notices](THIRD_PARTY_NOTICES.md).

Project-owned code retains GPL-3.0-only; dependencies retain their own licenses. Model-deployment studies, external tools and future-platform research do not establish integration into Fusion. Report version, engine, interface/target languages and reproduction steps through [Issues](https://github.com/zsbnt571/Game-AI-Translator/issues), removing keys and personal information from logs.

