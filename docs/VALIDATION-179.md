# 179 migration and validation / 迁移与验证

## Interface and architecture

The native WPF main window, library navigation/cards, game-detail layout, ten settings categories and screenshot-workspace layout share the existing settings, API, game sessions and translation queues. They do not create a second translation service. Specialized image/text panes, engine editors, seven key-recording controls and dialogs remain single-level WinForms leaf hosts. `--legacy-ui` retains the compatibility interface. Neither UI path selects a different in-game font.

简体中文为新安装默认语言；已有语言、主题、API 方案、游戏库和翻译偏好按原配置保留。API 方案和翻译内容仍分别保存。

## Controlled checks

- Native view construction, ten settings categories, bindings, protected API-key fields, full shortcut width, top-level HWND capture exclusion/restore, minimized preview, tray hide/restore: 40 checks.
- Pretranslation queue and language/provenance boundaries: 249 checks.
- Transferred RPG Maker installation recovery: 35 checks.
- Ren’Py AST reader against synthetic engine objects: 10 checks.

These checks use isolated data. They are not full game or visual acceptance. Exact package checks and final assembly hash are recorded separately in PACKAGE-VERIFICATION.json.

## Real application and game checks

- The desktop was exercised with an isolated copy of a populated library. Chinese/English switching, saved settings, detail/status dialogs, screenshot history, split/maximize, key recording, capture start/cancel, capture exclusion, tray hide and second-launch restoration were checked through the real UI.
- A controlled two-line image completed local OCR, real API translation, translated-image/text preview, history selection and UTF-8 text export. A repeated import reused translation output.
- Five isolated game copies were launched across RPG Maker MZ, Ren’Py, Godot 4, GameMaker and Unity Mono. Existing API credentials were used only in private testing. Public packages contain no credentials, libraries, histories, games or saves.
- RPG Maker: translated menus/options were visibly checked; disabling restored English. An old moved Fusion component was upgraded and the original manifest restored byte-for-byte.
- Ren’Py: before the fix, zero catalogued lines entered pretranslation; after the fix, 9,056 eligible directory lines were admitted and 83 were cached within a ten-request run. Remaining uncertain source rows stayed deferred. Disable/remove completed. The isolated game required process termination after disable; normal exit and long play were not accepted.
- Godot 4: eligible directory lines changed from 0 to 46; all 46 were cached in six requests, with runtime text reusing cache. Disable, normal exit and component removal passed. Godot 3 runtime behavior was not tested.
- GameMaker: startup/bridge/API worked and two opening strings were translated. A small ambiguous source catalogue remained deferred until sufficient runtime language evidence. No opening crash was observed; long gameplay was not tested.
- Unity Mono: catalogue/bridge/API and active text bindings were exercised. Pretranslation resources were admitted; the bounded API budget prioritized visible text. Disable, normal exit and component removal passed.
- GPU game captures in this environment were black. Except for RPG Maker menus, no complete game-pixel or glyph acceptance is claimed. “Squares before Chinese” and the historical Sloppy Fields crash remain open.

## Performance method and results

Same 950×630 viewport, the existing library model, synthetic scrolling, two-second warm-up and ten-second measurement; temporary timer resolution was restored on exit. These are CPU draw measurements and callback rates, not presented/display FPS or GPU completion. Each row is one measured trial.

| Cards | 178 mean CPU draw | 179 mean CPU draw | 178 / 179 draw callbacks per second |
| --- | --- | --- | --- |
| 82 | 7.504 ms | 0.436 ms | 39.90 / 39.90 |
| 1,000 | 15.781 ms | 0.451 ms | 39.58 / 39.79 |
| 10,000 | 95.735 ms | 0.443 ms | 10.17 / 39.88 |

At 82 cards, process CPU fell from about 21.1% to 5.3% of one logical core during the test. Private memory rose from 73.2 MiB to 111.9 MiB. The result supports substantially less CPU drawing work, not a claim that normal-library display FPS increased. The 10,000-card case is a stress test.

Real desktop checks used software rendering to make WPF captures inspectable; benchmark results above used default hardware rendering. The delivered default remains hardware rendering. Multi-monitor, cross-DPI, all-game and clean-Windows-VM acceptance remain outside this release's evidence.
