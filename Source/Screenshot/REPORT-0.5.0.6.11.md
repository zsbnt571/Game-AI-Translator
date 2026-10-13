# Screenshot Translator 0.5.0.6.11 Candidate Report

Status: **Manual Acceptance Pending**

This is an independent repair candidate based on 0.5.0.6.10. It is not marked Stable or Accepted.

## Implemented

- Stable source identity chain from OCR source block through TranslationUnit, rendered region, right-text range, and left/right highlight.
- Highlight selection now resolves the exact stable source IDs instead of using the first source block as a shortcut.
- Glyph-shaped cleanup includes bold strokes, outlines, punctuation, clipped endpoints, italic/anti-aliased residuals, and a bounded residual pass.
- Readable fallback clears source glyphs before drawing and keeps ordinary body text on a consistent safe black/white color.
- Repeated choices/buttons share visual style, alignment, and bounded font scaling.
- Compact controls can use their verified owning surface instead of being constrained to the smaller OCR ink rectangle.
- Paragraph-local image exclusions no longer leak into unrelated paragraphs.
- Real capture confirmation disposes the overlay before preview/OCR work; a hidden prewarmed preview removes constructor delay.
- Runtime binding clear now removes the reported runtime binding instead of falling back to an empty configured record.

## Evidence

- 0.5.0.6.11 focused tests: 50/50 passed, 0 real API calls.
- Typography foundation: 21/21 passed.
- Real desktop screenshot path: overlay disposed 4 ms; preview responsive 55 ms; maximum confirm-phase UI block 50 ms; GC0/1/2 = 0; actual desktop capture/overlay/selection confirm all true.
- Blind holdout C: four previously unused layouts (long page, dialogue panel, tooltip, repeated actions), automated pass and manual contact-sheet review completed. No real API calls.
- Vision V2: 143/144; the remaining failure is the pre-existing DPI-sensitive Chinese settings-button width assertion, not the translation-image pipeline.
- Input interaction: 37/38 after fixing keyboard/mouse clear. The remaining physical SendInput hook-chain assertion could not observe the injected middle-button event in the managed desktop test environment.
- Legacy major-rework suite: 24/25. Its remaining assertion requires an unsafe translated region to stay pixel-identical, which conflicts with the later translation-first readable-fallback product rule. This is disclosed, not relabelled as pass.

## Performance provenance

The real desktop performance result was measured on the final capture/preview code path before the last renderer-only and version-label edits. A requested second run against the packaged executable was blocked by the execution approval quota. No renderer-only edit touched capture confirmation, preview prewarm, ownership transfer, or the timeline instrumentation.

## Acceptance

Please perform real manual acceptance on the supplied executable, especially:

1. unfamiliar game choice screens and inventory tooltips;
2. Faranna name/race/body color ownership;
3. long unfamiliar pages and exact left/right highlight mapping;
4. repeated screenshots for perceived confirmation latency.

Only the user can promote this candidate beyond **Manual Acceptance Pending**.
