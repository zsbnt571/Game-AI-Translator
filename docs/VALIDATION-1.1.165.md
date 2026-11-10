# Validation — Fusion 1.1.165

## Exact tested application

- Product version: **1.1.165**; file version: **1.1.0.165**.
- `GameTranslator.dll` SHA-256: `B539CCB5565A35010239F3A69299B25C8985745DED1ED93570D85A01F6FFC3E0`.
- Main self-contained runtime: **8.0.33**.
- The public archive preserves this exact assembly. Packaging only removes local deployment metadata, stale root release notes and debug symbols, and updates public documentation/inventories.

## Completed controlled checks

| Suite | Checks | Failures |
| --- | ---: | ---: |
| Settings165 | 156 | 0 |
| Library165 | 50 | 0 |
| Trial165 | 82 | 0 |
| Localization165 | 3,638 | 0 |
| Credential persistence/recovery | 41 | 0 |
| **Total** | **3,967** | **0** |

Nine languages, 207 UI states and 348 reviewed recent source strings were covered. Each of the eight non-Simplified-Chinese catalogs has 4,849 entries. Checks include placeholder preservation, dynamic counts, preset/glossary metadata, selected-option refresh, localized error summaries, shortcut/media notifications, experimental notices and retention of user-authored text.

Window acceptance included English/Japanese switching and restart/readback in an isolated copy, followed by startup of the installed application. Existing local library, configuration and chat files were preserved and verified locally; personal inventories are not distributed.

## Limits

- These are controlled component/UI checks. Live API requests: **0**. New original-game sessions in this release's final suite: **0**.
- Translation quality, all engine/game variants, every native assistant workflow and every Mod are not certified by these counts.
- Unchanged auxiliary components were reused from verified 1.1.164. A complete clean-machine rebuild of all components remains unverified.
- User content and model output retain their own language. Windows-owned file dialogs follow the OS language. Technical logs can contain provider/runtime detail.
- Built-in glossaries primarily cover English/Japanese to Simplified Chinese. Nine-language UI support does not imply all glossary language pairs.

The final archive CRC, streamed SHA-256 checks and byte sizes are recorded separately in `PACKAGE-VERIFICATION.json` and `SHA256SUMS.txt`.
