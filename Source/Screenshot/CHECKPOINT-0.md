# CHECKPOINT-0 — RC2 Accepted copy

- Source baseline: `outputs/截图翻译界面测试器-0.3.0-rc2-accepted/source`
- Baseline status: **RC2 REAL-HARDWARE ACCEPTED BASELINE**
- Baseline automated result: **125 passed, 0 failed**
- RC3 work copy: `work/screenshot-translator-030-rc3-feature-complete`
- Release build of untouched copy: passed (0 errors, 2 pre-existing warnings)
- Real API calls: 0

## Safety preflight

The Codex execution environment cannot directly read the real AppData settings file. The user completed the preflight in a normal Windows environment. The original settings file and the safe workspace backup both have SHA256:

`04CD9F489B9E77AA44A821785591805B10584A23F33A9526E4586C2FEE983F0C`

All RC3 migration and safety tests use the backup, temporary AppData roots, or synthetic settings. The real user settings file is never an automated-test target.

## Frozen source hashes

| File | SHA256 |
|---|---|
| CaptureOverlay.cs | `391B6D8ED50F462DC56691576F45DE1136832721403C1FF9BD1C94CD17EB4B87` |
| MainForm.cs | `1744FB6A7B000D46E2843D2A09172A618F618B9AE81709A3FC42C4A84EE042C6` |
| PreviewForm.cs | `979C69D0A74E41EBAC4FD7A074700FD3D1138E9FA498762FF01E4A54BE70484F` |
| TranslationService.cs | `19C7339D8EE91771E31DE878AC2F2B1FDFEFF18E5FCBC75304918D1F90F36057` |
| OcrService.cs | `38DC1E0DB50FC17B88A70E680A8DCCF66715BF497DA1AC9F1F97A0DE66B80955` |
| ConfigurationManager.cs | `81C0A3021F129ADE3B9905904905FF9E9EB0F86E391CA17FB752A3EB7764D80E` |
| ScreenshotTranslationUiTester.csproj | `98393F16B30E6FA3FAF0D0EA5344A813819805C660716289311CAC19DE62F24C` |

