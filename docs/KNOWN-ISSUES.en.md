# Known limitations

- Compatibility depends on engine, engine version and the game's implementation. Resource discovery and a full progress bar do not guarantee that every dynamic line has been found or displayed correctly.
- Some games can show original text briefly or miss text after control reuse, typewriter effects or scene transitions.
- Live translation toggling depends on an established compatible connection. An externally launched game or an older resident component may need a fresh launch to establish that connection.
- Font substitution and restoration vary across engines. Older Unity text systems, native Chinese text, complex shaping and some glyph rendering still have limitations.
- Ren'Py replay screens, dynamic Unity dialogue, WOLF continuous edits/maps and speed behavior after skipping or animation need further compatibility work. Game modification remains experimental.
- **Automatic gender agreement, without extra setup.** Explicit English evidence within the same original text item can assist English, Spanish, French, German and Russian translation through the shared path. Limited Ren'Py role context can also assist across dialogue lines. Unknown or conflicting evidence keeps normal behavior; full character recognition across engines is not supported.
- The older Unity IL2CPP integration retains .NET 6.0.7, an unsupported runtime. The .NET 8/10 updates do not upgrade that component.

For a useful issue report, include the Fusion version, game engine/version, interface and target languages, and reproduction steps. Remove API keys and personal data from attachments. [Report an issue](https://github.com/zsbnt571/Game-AI-Translator/issues).
