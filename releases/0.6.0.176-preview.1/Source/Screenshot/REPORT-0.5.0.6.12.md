# Screenshot Translator 0.5.0.6.12 Targeted Root-Cause Fix

Status: **Manual Acceptance Pending**

This candidate is not Accepted or Stable. It only addresses the root causes authorized for ST-050612.

## Capture stall

- Old production path: full virtual-screen `Graphics.CopyFromScreen` executed synchronously on the UI thread.
- New primary path: persistent DXGI Desktop Duplication device/output contexts, frame acquisition and GPU readback on a dedicated worker thread, virtual-desktop composition, display-change rebuild, and automatic recovery.
- Fallback: GDI `Graphics.CopyFromScreen`, retained and executed off the UI thread.
- Controlled 3840x2160 test: caller dispatch remained below the 30-40 ms UI-block limit; forced primary failure selected the fallback and reported it in metrics.
- The automated runner has no interactive desktop-capture permission (`DXGI E_ACCESSDENIED`; GDI invalid desktop handle), so a real desktop baseline comparison and real-game load result were not fabricated. `HotkeyToFrozenFrameVisibleMs`, acquisition/copy/compose time, frame age, backend, bytes, copies, and capture thread are recorded for manual runs.

Real game manual acceptance is required.

## Right text lifecycle

- Preview shell and splitter are stabilized before the final translation assignment.
- Identical whole-text assignments are suppressed.
- Final translation assignment is recorded separately from placeholder/raw text.
- Real WinForms integration result: `RightTranslationFinalSetCount=1`, `RightTextAppendCount=0`, post-final automatic panel-width/font/layout changes all `0`.

## Right text to image mapping

- Old incorrect path: right SemanticGroup ID was converted again through the Recognition V2 graph.
- New path: click locates `RightTextRangeIdentity`, then uses its exact `SourceIds[]`; `RenderUnitIds[]` and role are retained in the same identity.
- Real `PreviewForm + RichTextBox + translated image` integration covered a title, a body, a two-source paragraph, consecutive clicks, and left-to-right selection. Title/body identities did not cross.

## Source glyph cleanup

Runtime trace for the known Ophelia failure found:

- `How did you know my / name?` retained both SourceIds and both line polygons.
- The old plan classified the semi-transparent game choice as Texture, rejected cleanup, changed `0` pixels, then the Chinese label was drawn over the preserved English.
- `Bye` already had a safe committed mask in the traced path.

The fix admits only complete glyph-shaped masks for repeated compact UI/Choice surfaces and reconstructs those glyph pixels by horizontal local surface continuation. It does not erase or repaint the full button rectangle. The final owned-mask bounds are recomputed after neighboring UI protection. Normal, bold, outline, shadow, multiline, selected-choice, short-choice, neighbor-safe, and background-preservation regressions pass.

## Layout and blind holdout

No broad layout-engine rewrite or Evidence-specific tuning was performed. New Blind Set D was run once and not used for tuning: two long pages, an avatar/body page, dialogue, choices, and irregular UI. Automated structural checks passed; visual review remains pending.

## Regression and resources

- ST-050611 focused suite: 50/50 passed.
- Translation reliability: 22/22 functional checks passed, real API calls 0. The old suite's separate path/version identity assertion remains intentionally baseline-specific.
- Earlier 0.5.0.6.4-0.5.0.6.10 failures are byte-for-byte the same as the ST-050611 baseline; no new failure was introduced.
- Preview open/close lifecycle: 50/50 passed.
- Release: 0 errors, 0 warnings.

## Manual acceptance requested

1. High-load game hotkey to frozen screenshot.
2. The same game Choice UI: no English residue; Chinese placement and group sizing.
3. Long text: no right-side pull/reflow.
4. Click different right-side paragraphs: exact left highlight.
5. Two or three never-used images.
6. Ten to twenty consecutive captures.
7. ESC, WASD, mouse, and wheel.
8. Cancel, reselect, and close Preview.

Only after those checks may this candidate be promoted beyond **Manual Acceptance Pending**.
