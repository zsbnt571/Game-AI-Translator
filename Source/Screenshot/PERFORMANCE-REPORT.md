# Performance Report

## Capture regression

- Create: 63.3 FPS
- Move: 62.2 FPS
- Resize: 62.9 FPS
- MouseMove CopyFromScreen: 0
- MouseMove full bitmap copy: 0
- 50 cancel memory delta: 3,784 bytes
- 50 cancel GDI delta: 0
- 25 reusable-overlay cycles memory delta: 22,888 bytes
- 25 reusable-overlay cycles GDI delta: 6（非持续增长）

门槛 Create 55 / Move 55 / Resize 45 FPS，全部通过。CaptureOverlay SHA256 与 RC2 Accepted 完全一致。

## OCR

详见 `OCR-PHASE1-REAL-RESULTS-RC3.md`。Rapid 热运行最快；质量未人工评分，因此不自动更改默认引擎。
