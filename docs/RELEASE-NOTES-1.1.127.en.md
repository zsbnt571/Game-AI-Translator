# Fusion 1.1.127 — Release notes

This version includes the changes since the previous public 1.0.0 release.

- Added Simplified Chinese, Traditional Chinese, English, Japanese, Korean, Spanish, French, German and Russian interface coverage. New settings follow the system; saved selections remain valid after the list reorder.
- Improved resource discovery, pretranslation, duplicate-request handling and exact cache delivery in supported engine adapters. Unprepared dialogue still uses live translation.
- Improved ordinary-launch translation controls, per-game state, normal exit handling and independent game-modification controls.
- Improved custom-font selection and restoration while keeping the existing default font adaptation.
- Added limited Ren'Py dialogue-context assistance for grammatical gender. It requires a known speaker plus explicit English gender or pronoun evidence, and applies to English, Spanish, French, German and Russian targets. It is not cross-engine automatic gender detection. Chinese, Japanese, Korean and ordinary caches keep their previous behavior.
- Fixed compatible keyless API configuration; a dummy key is no longer needed.
- Updated applicable bundled runtimes to .NET 8.0.33 and 10.0.14.

Download **Fusion-1.1.127.zip**, extract it completely, and run **GameTranslator.exe**. Included .NET/Python/OCR components need no separate installation. Configure your own translation provider; optional Google cover-image search uses Windows WebView2.

See [known limitations](KNOWN-ISSUES.en.md), [validation](VALIDATION-1.1.127.md) and [build instructions](BUILD.en.md).
