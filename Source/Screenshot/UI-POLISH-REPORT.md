# 0.2.10 UI polish report

- Base: 0.2.9-image-fix-test
- TranslationService.cs remains byte-identical to 0.2.9.
- Real API calls during this change: 0.

## Typography profiles

| Group | Min | Max | Line spacing | Padding H/V | Max lines | Alignment | Expansion |
|---|---:|---:|---:|---:|---:|---|---|
| Title | 9 | 24 | 1.05 | 6/4 | 2 | left/center | 1.15x |
| Body | 7 | 14 | 1.08 | 8/6 | unlimited | left/top | 1.12x |
| Dialogue | 7 | 13 | 1.07 | 6/4 | 6 | left/top | 1.12x |
| ImageCaption | 7 | 10 | 1.03 | 4/3 | 4 | left/top | no |
| CharacterInfo | 7 | 12 | 1.04 | 5/3 | 3 | left/center | no |
| Time / MessageCount | 7 | 11 | 1.00 | 4/2 | 2 | left/center | no |
| Button | 8 | 13 | 1.00 | 5/3 | 2 | center/center | no |
| OtherLabel | 7 | 12 | 1.04 | 5/3 | 3 | left/center | no |

0.2.9 Body maximum was 16; 0.2.10 uses 14 and larger padding. Measurement and rendering use the same wrapping, line spacing, font, padding and alignment. If minimum size cannot fit, no cover or partial translation is drawn.

## Window isolation

Preview bitmap, OCR document/groups, translated bitmap, task, CTS, error, metrics, image/text mode and hover/lock state are all instance fields. The shared HttpClient remains the only shared network client. A remote disconnect is shown only in the originating PreviewForm and retains its OCR/groups/images for retry.

## Regression

49 local simulated checks passed, 0 failed. No real DeepSeek request was made. The April test uses synthetic fixed geometry because the actual April screenshot is not present in the workspace; it remains a manual visual check.
