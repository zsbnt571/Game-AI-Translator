# Screenshot Translator 0.5.0.6.8

Build: `0.5.0.6.8-input-pass-through-system-responsiveness`  
Status: **Manual Acceptance Pending**

## Outcome

This independent repair round fixes the input-consumption defect and moves heavy rendering away from the WinForms UI thread. Accepted 0.5.0.6.7 visual behavior remains frozen.

### Input pass-through

- Removed runtime use of `RegisterHotKey` for ordinary keyboard bindings. Windows global hotkey registration consumed the matched foreground input; the permanently registered bare `ESC` cancel binding was the direct game-interference path.
- Added a low-level keyboard observer and explicit `InputConsumptionPolicy` modes: `ObserveOnly`, `TriggerAndPassThrough`, and `ExclusiveCapture`.
- `Handled` and `Suppressed` are separate decisions.
- Idle/Main/Preview bindings trigger and pass through. Only active screenshot selection, binding capture, or an explicit protected modal capture can suppress necessary input.
- Screenshot-selection ESC remains exclusive for one cancel event. Ending/canceling capture immediately returns to pass-through.
- Mouse and wheel retain their accepted behavior: normal global bindings pass through; physical binding capture alone suppresses the captured event.
- Diagnostic logging exists only in test/diagnostic execution.

### System responsiveness

- Region rendering now runs off the UI thread under one global renderer lease at `BelowNormal` thread priority.
- The UI receives one final bitmap commit; no repeated preview refresh is added by the renderer.
- Full-image temporary `bool[,]` buffers used once per line were replaced with line/ROI-local buffers. This addresses April Long allocation and full-GC bursts without changing typography.
- Added CPU, memory, allocation, GC, bitmap/ROI, measurement, background and fallback metrics.

April Long comparable diagnostic:

| Metric | 0.5.0.6.7 | 0.5.0.6.8 |
|---|---:|---:|
| Renderer | 2190 ms | 2078 ms |
| Full bitmap clones | 1 | 1 |
| ROI bitmap allocations | 14 | 14 |
| Temporary ROI bytes | 8,401,344 | 8,401,344 |
| Managed allocated bytes | not recorded | 157,279,800 |
| Gen0 / Gen1 / Gen2 | not recorded | 7 / 6 / 5 |
| Runtime scheduling | UI-thread synchronous | single below-normal background renderer |
| UI commit | multiple work mixed with render | one final commit |

During development, before ROI-local temporary masks, the same 0.5.0.6.8 probe allocated 331,072,568 bytes and recorded 29/28/27 Gen0/1/2 collections. The final probe reduced those to 157,279,800 bytes and 7/6/5 collections. Manual gameplay responsiveness remains the required acceptance gate.

## Legacy 7-test audit

No test was deleted.

1. **Unsafe target preserves every original pixel** — old assertion required PreserveOriginal; current accepted behavior commits a complete readable fallback. Updated to verify full text and atomic fallback.
2. **Failed structure validation is atomic** — old assertion equated atomic with no pixel change. Updated to verify one atomic fallback transaction.
3. **Unsafe mapping preserves original pixels** — old PreserveOriginal expectation conflicted with Translation-First. Updated to verify unmodified translation text in fallback.
4. **Overflow explicit** — old assertion rejected any committed fallback. Updated to require either complete unmodified text or an explicit failed fallback, never silent truncation.
5. **Translation only to its Region** — the pixel probe assumed fallback could not alter its own anchor. Updated to verify fallback bounds do not intersect the sibling Region and the sibling is not committed.
6. **Background alpha diagnostics** — old assertion assumed the normal light-overlay path. Updated to distinguish normal configured alpha from the deliberately opaque readable-fallback path.
7. **Alpha has no blur coupling** — retained the zero-blur requirement while accepting either normal-path or fallback-path effective alpha.

Result: `144 / 144`.

## Verification

- 0.5.0.6.8 input/performance/runtime special tests: `21 / 21`
- 0.5.0.6.7 visual-freeze checks: `12 / 12`
- Typography foundation: `21 / 21`
- Vision/renderer regression: `144 / 144`
- Evidence cases: Cash Card, April Card, Ophone/item UI, Faranna, Magazine, April Long
- Build: `0 warnings`, `0 errors`
- Real API calls: `0`
- Active operations after tests: `0`
- Worker/process residue after tests: none
- Real packaged single-instance check: first instance remained alive, second instance exited with code 0, process count remained 1, final residue 0

## Frozen behavior

Translation-First, readable fallback, Card results, April Long typography, Faranna fields/name/race/anchors, Ophone coverage, title anchors, fonts, OCR, vision worker architecture, provider/prompt, history, tray, zoom/pan, and quantity `x1/x6` behavior were not redesigned.

## Manual acceptance gates

1. Verify ESC and ordinary keyboard input in a real game while the app is idle.
2. Verify mouse buttons and wheel pass through during normal gameplay.
3. Verify screenshot selection ESC cancels exactly once and immediately restores gameplay ESC.
4. Verify binding capture leaves no suppression after commit/cancel.
5. Judge April Long whole-system smoothness and confirm typography did not regress.
6. Confirm Cash, Ophone and Faranna translation coverage.
7. Repeat the already-passing packaged single-instance check on the user's normal installed configuration.
