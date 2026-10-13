# Translation Settings 1 Report

## Scope

Independent candidate based on `0.3.0-rc3-feature-complete-repair4.1.2-translation-state-hotfix-test`.
No OCR, capture, layout, highlight, image-translation, or real-time OCR behavior was changed.

## UI

- A formal **Translation Settings** dialog is available from the global Settings page and each Preview.
- Tabs: General, Provider, Prompt.
- Target choices: `zh-CN`, `zh-TW`, `en`, `ja`, `ko`, plus editable custom text.
- Source choices reuse the existing Auto/English/Simplified Chinese/Japanese/Korean/Mixed modes.
- Styles: Natural, Literal, GameLocalization (default), Custom.
- Provider fields: explicit provider type, display name, Base URL/API URL, masked API key, model.
- Prompt fields: custom instruction and independent preservation switches for IDs, numbers, variables/placeholders.

Global changes are persisted only through the existing settings-safe save flow. Preview changes are local to that Preview.
An in-flight request keeps its original immutable settings snapshot; the next request uses newly applied settings.

## Validation

- Release build: PASS
- Isolated startup: PASS
- Regression: 329 PASS / 0 FAIL
- Real API calls: 0

