# Repair4.1 OCR Quality Fix Report

## Scope and baseline

This version is copied from `0.3.0-rc3-feature-complete-repair4-ocr-quality-test`. The Repair4 source and output were not overwritten. Capture, translation, WDA, settings, cache and image-translation behavior remain frozen.

## Fixes

1. Preview OCR selector now reacts only to `SelectionChangeCommitted`. A real user selection immediately runs that engine; programmatic initialization does not create a duplicate OCR request. The rerun button remains available.
2. Every Preview owns an OCR CTS, Task, generation and RequestId. Rapid → Paddle → Rapid uses per-request identity and latest-wins checks. A late Paddle result cannot overwrite the current Rapid result. Close cancels the active OCR operation safely.
3. Confidence truth is explicit. Real block confidence is preserved; unavailable confidence is rendered as N/A rather than misleading `0`/low confidence.
4. Bounded preprocessing retry now records original raw text, retry raw text, selected text, reason and preprocessing type. It retries at most one suspicious block per image.
5. Corrections are structured, contextual and audited. There is no global replacement. Ambiguous isolated `O/0/I/1/l/G/6/q/9` sequences remain unchanged and are marked `UNRESOLVED_AMBIGUITY`.

## Verification

- Repair4 baseline: 213 passed, 0 failed.
- Repair4.1 final regression: 244 passed, 0 failed; therefore 31 additional tests.
- Real translation API calls: 0.
- Real Worker UI smoke: Rapid VALID, Paddle VALID, Windows experimental VALID.
- Mouth sample: Rapid 28 blocks, Paddle 28 blocks, all with real block confidence, ActiveOperations 0.
- April sample: Rapid 38 blocks, Paddle 39 blocks; real Paddle retry count 1; ActiveOperations 0.
- No residual OCR worker process after smoke completion.

## Correction cases

- `1O/3O` → `10/30`
- `5OOOyen` → `5000yen`
- `L0AD` → `LOAD`
- `LOVE`, `SOLO`, `BOOST`, `BOSS`, `SAVE`, `OPTION`, `POSITION`, `ONLINE`, `OFFLINE` remain undamaged.
- Isolated ambiguous tokens are not guessed.

## Deliberate non-features

- Hybrid OCR: NO.
- Model training: NO.
- Global replace: NO.
- Character confidence / Top-K / CTC logits: unavailable in current worker contracts and not fabricated.

## Manual acceptance items

Verify selector one-click behavior, rapid selector switching, displayed confidence/N/A, retry audit details, and ambiguous-token presentation on the user desktop. Automated and real-worker smoke evidence is complete, but this build remains a test candidate pending user acceptance.
