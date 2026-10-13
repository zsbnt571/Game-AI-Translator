# Screenshot Translator 0.5.0.6.13.3 — Snapshot Ownership Frozen

## Final status

`SNAPSHOT OWNERSHIP FIX — FROZEN`

Freeze evidence is the combination of automated regression PASS, the user's `20 PASS / 0 FAIL` repeated-capture result on the repaired ownership logic, and a final manual smoke PASS on the self-contained 6.13.3 package.

## Previous status and A/B evidence

The historical ledger recorded `0.5.0.6.13.1 Bitmap Ownership Isolation — FROZEN`. Later controlled manual A/B testing found a residual race:

- Frozen Known-Good A: `6 PASS / 4 FAIL`
- Renderer Candidate 03 B: `6 PASS / 4 FAIL`

Therefore `FROZEN BUG CONFIRMED`; Candidate 03 was not the cause and remains `MANUAL VISUAL FAIL`.

## Root cause

Preview took ownership of `result.Image`, while MainForm later used that same GDI+ Bitmap to create a deferred Clipboard copy. Clipboard `new Bitmap(image)` could overlap OCR snapshot `new Bitmap(_original)`, producing intermittent `Object is currently in use elsewhere`.

A second lifecycle defect existed in reusable capture preparation: after a partial failure, cleanup could read `Image.Width` from an invalid/disposed Bitmap and replace the original failure with `System.ArgumentException: Parameter is not valid`.

## Minimal fix

- Create the independent Clipboard Bitmap before transferring the capture Bitmap to Preview.
- Keep Preview/OCR/Clipboard on distinct GDI+ Bitmap objects.
- Transfer reusable Overlay ownership only after preparation succeeds completely.
- Do not inspect Width/Height on a potentially invalid Bitmap during release.
- Make repeated release safe and preserve caller ownership on failed preparation.

## Frozen boundary

This freeze reopens and refreezes only `Snapshot / Bitmap Ownership Lifecycle`. No Renderer, OCR engine/model, Translation, Semantic Grouping, Worker architecture, Highlight, Font, Input, Cursor, or unrelated Preview behavior was changed.

## Source audit

Previous source: `ST-0506132-cursor-corrected-src`.

New source: `ST-0506133-snapshot-ownership-frozen-src`.

Expected changed files:

- `MainForm.cs` — independent deferred Clipboard copy.
- `CaptureOverlay.cs` — transactional prepare/ownership and idempotent release.
- `SnapshotOwnershipFixSelfTests.cs` — ownership regression coverage.
- `BuildIdentity.cs`, `ScreenshotTranslationUiTester.csproj` — 6.13.3 identity.
- `FinalStabilization0613Tests.cs` — matching version assertion only.

No unexpected Renderer/OCR/Grouping/Translation source difference was found.

Source identity SHA256: `E29E1788D6264DFF2633744BE986E4DA546FC8961F96178EF7EA5DE965744519`.

## Automated validation

- Build: `0 errors`, `1 existing nullable warning` in `PreviewForm.cs`.
- Snapshot ownership self-tests: `6 PASS / 0 FAIL`.
- Ownership/blocker regression: `15 PASS / 0 FAIL`.
- Stabilization regression: `28 PASS / 0 FAIL`.
- Screenshot startup/first-frame regression: `PASS`.
- Post-confirm/Preview regression: `PASS`.
- Repeated prepare/release test: 100 cycles, including double release: `PASS`.
- Runtime orchestrator import: `PASS`.
- Vision-runtime orchestrator import: `PASS`.

## Manual validation

User repeated-capture result on repaired ownership logic: `20 PASS / 0 FAIL`.

- `Object is currently in use elsewhere`: 0 occurrences.
- `Image.get_Width() / Parameter is not valid`: 0 occurrences.

Final self-contained 6.13.3 package smoke: `USER MANUAL PASS` for launch, screenshot/Confirm, Preview, OCR, Translation, image render, second capture, and cancel/exit flow.

## Package and provenance audit

Package: `ST-0506133-snapshot-ownership-frozen-known-good`.

- Self-contained: YES.
- Reparse points / Junctions / Symlinks: 0.
- Absolute development-path matches: 0.
- External path dependency count: 0.
- `ScreenshotTranslator.dll` count: 1.
- Main assembly provenance: CLEAN.
- Full payload comparison against Frozen runtime, excluding historical runtime logs: 91,631 files on each side; only `ScreenshotTranslator.exe`, `ScreenshotTranslator.dll`, `ScreenshotTranslator.pdb`, and `ScreenshotTranslator.deps.json` changed.

The first packaging attempt paired the new DLL with the old 6.13.2 EXE and produced a windowless process. That package was rejected. The final package uses the EXE and DLL from the same 6.13.3 build and passed manual launch smoke.

## Cryptographic identity

- EXE SHA256: `82B64F7B65C779F5CC3A690D39947EB9FF8A090D252DCC2CC5A78459712A3B7F`
- Main DLL SHA256: `236181D60932A753584CA4C9E82E13FD1AB09B98F7591362E284EEF552A39081`
- PDB SHA256: `3AADF760331F01CFFDDF60601B866AFAE93255DDCBDC88FEFBCC4BD338718627`
- deps.json SHA256: `342672F084B7D36F33F1B8C5C939836E96F7CD0E8E0750930852C262D5527EB4`
- Source identity SHA256: `E29E1788D6264DFF2633744BE986E4DA546FC8961F96178EF7EA5DE965744519`
- Payload manifest SHA256: `12E7A016D27E9F0E695E6761A1915EF373C91AA32F6B9EA64529D5459AC1333F`

## Final decision

`SNAPSHOT OWNERSHIP FIX — FROZEN`

`SNAP-A SAFE TO DELETE — YES`, but it is not deleted automatically.

`Renderer Candidate 03 — MANUAL VISUAL FAIL`

`Candidate 04 — NOT CREATED`
