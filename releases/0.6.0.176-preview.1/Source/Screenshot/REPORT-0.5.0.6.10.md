# Screenshot Translator 0.5.0.6.10 Repair Report

Status: **Manual Acceptance Pending**

## Release scope

This independent candidate addresses real executable responsiveness, atomic preview visibility, and general layout geometry. Translation API recovery from 0.5.0.6.9, input policy from 0.5.0.6.8, typography/font policy, and accepted visual behavior are frozen.

## A. Algorithm / Regression Evidence

- New 0.5.0.6.10 specialty suite: 30/30 PASS
- 0.5.0.6.9 API reliability freeze: 23/23 PASS
- 0.5.0.6.8 input/performance freeze: 21/21 PASS
- Existing visual regression: 12/12 PASS
- Typography foundation: 21/21 PASS
- Vision/region suite: 144/144 PASS
- Automated real API calls: 0

Existing Cash, April Card, Faranna, Ophone, April Long, and Magazine evidence is classified only as the Regression Evidence Set. It is not used to claim generalization.

## B. Holdout Generalization

Final Holdout Generalization Set C: **12/12 PASS**. These generated page structures were created after the geometry implementation and were not used to select product thresholds or sample-specific rules. No filename, fixed OCR text, character name, or game name enters the layout algorithm.

Coverage includes long text, top portrait followed by full-width text, portrait affecting only the first two paragraphs, mixed anchors, mixed widths, dialogue/narration, header/body/metadata, short card, tooltip/item description, irregular columns, alternate aspect ratios, and long scrolling pages.

The earlier A and B holdout attempts are retained as failed-fixture audits. They revealed that the test fixture incorrectly assigned long Body text to compact labels and that the metric confused tight ink width with layout-container width. Product thresholds were not tuned against those failed samples; the observation model and role-correct fixture were repaired, followed by the new C set.

Implemented general geometry behavior:

- Paragraph geometry is computed independently from its own source lines.
- Left anchor, right extent, width, median line height, role, alignment, and local exclusions remain paragraph-local.
- Portrait/image/icon/side-panel exclusions are filtered by intersecting or neighboring Y range.
- An exclusion automatically releases after the paragraph is below it.
- Font fitting starts from source line height and correct local width.
- Generalization metrics include anchor drift, width preservation, font ratio, height inflation, invalid whitespace, exclusion leakage, bottom safety, overlap, missing text, and clipping.

## C. Real EXE End-to-End Runtime

The final executable opens a real WinForms Preview window with captured/cache fixtures and records visible frames before final commit, at the first final frame, and at +100ms, +250ms, +500ms, and +1000ms.

Measured candidate result:

- Final UI commit: 5 ms
- First final paint after commit: 4 ms
- Fixture hotkey-to-stable trace: 668 ms
- Max observed UI-thread scheduling block: 23 ms
- Process CPU time during harness: 281.25 ms
- Peak working set: 120,434,688 bytes
- Managed allocation: 1,167,760 bytes
- GC0 / GC1 / GC2: 0 / 0 / 0
- Capture bitmap copies: 1
- Capture bytes copied: 5,040,000
- Display-scale changes after first final frame: 0
- Scroll-extent changes after first final frame: 0
- Visible intermediate translation frames: 0
- Right-text Append count: 0

All five formal translated-frame hashes are identical. Final bitmap, final display scale, final scroll extent, and complete right-text document are prepared before one visible commit. Diagnostic screenshot capture no longer mutates the live PictureBox through 100% and Fit modes.

The automated E2E uses the final EXE and a deterministic captured/cache fixture, so OCR/API stages intentionally resolve without network latency. It does not claim that the user's physical hotkey-to-screen latency is zero. The physical hotkey, real screen capture, and user machine responsiveness remain part of manual acceptance.

## D. Manual Acceptance

Required checks:

1. Screenshot success no longer causes an obvious system hitch.
2. The first translated image shown is already stable and does not visibly stretch/reflow.
3. A new unfamiliar long page restores full width after portrait exclusions end.
4. Translation API remains normal.
5. ESC/game input remains normal.
6. Existing visual cases have no obvious regression.

This candidate is not Accepted or Stable. Final status remains **Manual Acceptance Pending**.

## Deferred to AI Embedded Translation Integration

- x1/x6 quantity semantics
- Magazine advanced small-text polish
- Faranna advanced visual recreation
- advanced style/background fusion
- complex game UI semantic classification
