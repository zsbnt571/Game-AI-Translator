# Screenshot Translator 0.5.0.6.7

Status: **Manual Acceptance Pending**

## Repair scope

- Faranna profile metadata is split into independent semantic fields before TranslationUnit creation: Age, Height, Weight, Measurements, Likes and Dislikes retain separate source lines, rectangles, order and anchors.
- Exact Faranna name/race integrity is enforced at mapping time: `Faranne/Faranna -> 法兰妮`, `Dark Elf -> 暗精灵`. Numeric metadata values are retained when a translation response drops them.
- Decorative names attempt source-bound normal reconstruction and remain in their original title region. Race remains in its own subtitle region.
- Foreground extraction is field-local and clustered from that field's source pixels. No cross-region color inheritance was introduced.
- Flat-background cleanup is limited to the current field's source-line union plus small anti-alias padding; it no longer repaints an entire semantic container.
- Short game/UI text uses a source-height readability floor and cannot expand into an adjacent inventory area. Low-cost fallback uses the field's own source-line anchor.
- Magazine cleanup uses the same field-local bounds to reduce blur on small captions.

## Frozen baseline

Cash Card, April Card, other accepted Cards, April Long typography, title improvements, font system, font dropdown, OCR/Vision/provider/input/tray/history/zoom/pan remain outside this repair.

## Verification

- Build: 0 warnings, 0 errors.
- 0.5.0.6.7 specialized tests: 12 passed, 0 failed.
- Typography foundation: 21 passed, 0 failed.
- Vision V2 suite: 137 passed, 7 legacy assertions still expect the pre-Translation-First atomic-preserve/background-alpha behavior. They are retained in evidence and are not reported as passing.
- Real API calls during evidence generation: 0.
- April Long first diagnostic wall time: 12.2 seconds. The first-generation performance concern remains open and instrumented; it is not claimed fixed.

## Manual acceptance focus

Recheck Faranna field completeness, title/race anchors and colors; Magazine small-caption sharpness; and the backpack description size/placement. This candidate is not Accepted or Stable.
