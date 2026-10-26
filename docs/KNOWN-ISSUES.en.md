# Known limitations

- Compatibility depends on engine, engine version and the game's implementation. Resource discovery and a full progress bar do not guarantee that every dynamic line has been found or displayed correctly.
- Some games can show original text briefly or miss text after control reuse, typewriter effects or scene transitions.
- Live translation toggling depends on an established compatible connection. An externally launched game or an older resident component may need a fresh launch to establish that connection.
- Font substitution and restoration vary across engines. Older Unity text systems, native Chinese text, complex shaping and some glyph rendering still have limitations.
- Ren'Py replay screens, dynamic Unity dialogue, WOLF continuous edits/maps and speed behavior after skipping or animation need further compatibility work. Game modification remains experimental.
- Gender-agreement assistance currently reads role context only from supported Ren'Py structures and explicit English evidence. It cannot reliably infer unknown speakers, addressees or arbitrary character identities, and it does not provide equivalent integration for other engines.
- The older Unity IL2CPP integration retains .NET 6.0.7, an unsupported runtime. The .NET 8/10 updates do not upgrade that component.

For a useful issue report, include the Fusion version, game engine/version, interface and target languages, and reproduction steps. Remove API keys and personal data from attachments. [Report an issue](https://github.com/zsbnt571/Game-AI-Translator/issues).
