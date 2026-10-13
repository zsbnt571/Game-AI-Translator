# 0.5.0.6.13.2 Final Minimal Fix Report

## Candidate status

- Source: `work/ST-0506132-src`
- Program: `outputs/ST-0506132/ScreenshotTranslator.exe`
- File version: `0.5.0.6132`
- Product version: `0.5.0.6.13.2`
- Previous versions were not overwritten.
- Real translation API calls: **0**.
- Full regression was intentionally not run.
- Status: **manual acceptance candidate**. Portable runtime execution passed, but the strict full-directory historical-text scan has the log-only limitation documented below.

## A. Combination capture

### Before

Modifier key-down reached `GlobalKeyboardBindingHost.Hook`, was accepted by `CaptureKeyboardFromHook`, and was then suppressed. The terminal key reconstructed modifiers with `GetAsyncKeyState`, producing plain `T` instead of the requested chord.

### Root cause

The first wrong stage was hook-side candidate assembly. Capture state did not own modifier state.

### Fix

- Added capture-session modifier state inside `GlobalKeyboardBindingHost`.
- Modifier key-down updates Ctrl/Alt/Shift/Win state without committing a binding.
- Modifier key-up clears the corresponding state.
- Terminal key assembly uses capture-session state, not a new `GetAsyncKeyState` guess.
- State is cleared on begin, commit, cancel, target switch, disposal, and when leaving binding-capture context.
- `InputDisplayNameProvider` was not changed.

### Verified candidates

| Input | Candidate |
|---|---|
| Ctrl then T | `Ctrl + T` |
| Ctrl then Shift then T | `Ctrl + Shift + T` |
| Alt then F2 | `Alt + F2` |
| Shift then A | `Shift + A` |
| Win then A | `Win + A` |
| F1 | `F1` |
| Mouse 4 display | unchanged |
| Wheel Up display | unchanged |

## B. Post-confirm ownership and timing

### Before synchronous clone location

`Selection Confirm -> CreateAndShowPreview -> Preview constructor/AdoptCapturedImage -> _ocrSource = new Bitmap(image) -> Preview.Show`

The full image copy ran synchronously on the UI thread before Preview visibility.

### After ownership path

1. Capture transfers the selected bitmap to Preview as the UI owner.
2. Preview is created and shown without creating `_ocrSource` in the confirm-to-show prefix.
3. Before the original bitmap is assigned to the PictureBox, a worker creates the isolated OCR snapshot.
4. After the snapshot completes, the UI-owned original becomes displayable.
5. OCR, Vision, and hashing use only the separate immutable snapshot.

Ownership remains isolated: the UI and background GDI+ consumers do not share a Bitmap.

### 4K trace

Trace: `evidence-06132/postconfirm-final/POST-CONFIRM-TRACE.jsonl`

| Stage | Thread | Duration |
|---|---:|---:|
| FinalCrop 3840x2160 | UI thread 1 | 12.3092 ms |
| SelectionConfirmHandler | UI thread 1 | 62.5302 ms |
| PreviewShow | UI thread 1 | 16.1569 ms |
| OCR isolation snapshot 3840x2160 | worker thread 5 | 13.0804 ms |
| RunOcr sync prefix | UI thread 1 | 1.5449 ms |
| First true async yield | UI thread 1 | 1.2026 ms |

- **LongestUIBlock:** 62.5302 ms (`SelectionConfirmHandler`).
- **TotalSyncPrefix (measured UI stages sum):** 80.2320 ms (`SelectionConfirmHandler + PreviewShow + RunOcrSyncPrefix`).
- **UIIdle wall-clock marker:** 144 ms from Preview wall-clock start.
- **FirstTrueAsyncYield:** 1.2026 ms after `RunOcrAsync` entry.
- **PNG Hash:** 0.
- **ImageHashCount:** 1/session.
- **Raw pixel hash:** 16 ms, background path.

### Full-image copy list

| Operation | Resolution | Bytes | Thread | Duration |
|---|---|---:|---:|---:|
| Final selection crop | 3840x2160 | 33,177,600 | UI 1 | 12.3092 ms |
| OcrIsolationSnapshot | 3840x2160 | 33,177,600 | worker 5 | 13.0804 ms |

`_ocrSource` is no longer cloned inside the UI confirm-to-show synchronous prefix.

### Remaining measured blocker

The trace still records a 62.5302 ms selection-confirm handler. Final crop explains 12.3092 ms, leaving approximately 50.22 ms not itemized by the current trace. This report does not guess its cause. The confirmed clone regression is removed; any further post-confirm work must start from this remaining measured interval.

## C. Screenshot cursor lifecycle

### Hide point

`CaptureOverlay.OnVisibleChanged(Visible=true)` acquires one `CursorVisibilityLease` and hides the system cursor once.

### Restore point

`Visible=false` atomically releases the lease. `Dispose` also releases the same lease as an idempotent safety path.

### Balance

- Confirm: overlay hides -> lease restored once.
- Cancel/right-click: overlay hides -> lease restored once.
- Esc: existing cancel path hides overlay -> lease restored once.
- Exception/disposal: disposal releases the lease if still held.
- Repeated screenshots: each visible session owns one independent lease.

Automated diagnostics verified one Hide and one Restore even when Dispose is called repeatedly, plus an actual overlay Show/Hide lifecycle.

## D. Portable Python/Vision runtime

### Before

Both `runtime/venv/pyvenv.cfg` and `vision-runtime/venv/pyvenv.cfg` pointed to the old `2026-08-10 / 0.4.2.7` development package.

### After runtime design

- OCR launches `runtime/python/python.exe` directly.
- OCR uses package-local `runtime/venv/Lib/site-packages` through `PYTHONPATH`.
- Vision launches `vision-runtime/python/python.exe` directly.
- Vision uses the selected package-local site-packages through `PYTHONPATH`.
- `PYTHONNOUSERSITE=1` prevents user-site fallback.
- The app no longer launches the venv interpreter stubs.
- `pyvenv.cfg` and activation metadata no longer contain the audited old development path.

### Real local smoke

Run from `outputs/ST-0506132` using the bundled base interpreters and local packages:

- Python worker: **PASS**
- RapidOCR actual engine: **PASS**
- Rapid model: `RapidOCR PP-OCRv6 + ONNX Runtime CPU`
- Rapid blocks returned: **35**
- PP-DocLayout-S: **PASS**
- Visual regions returned: **26**
- Real API calls: **0**
- Active operations after completion: **0**

Evidence: `outputs/ST-0506132/diagnostics/portable-real-smoke/`.

### Historical tree unavailable test

Not performed. The historical tree was not renamed or made inaccessible during this run.

### Absolute historical path scan

- Active runtime/venv metadata matching the audited `2026-08-10 / 0.4.2.7 / settings-1-4-0-3-0` paths: **0 files**.
- Full package scan: **3 files**, all inherited historical log files:
  - `runtime/logs/ocr-integrity.log`
  - `runtime/logs/rapid-worker.log`
  - `vision-runtime/logs/vision-worker.log`

These three matches are historical diagnostic text, not executable dependency metadata. Nevertheless, the requested literal standard was “no match anywhere in the final directory,” so that strict scan is recorded as **FAIL (log-only matches)** rather than falsely reported as pass.

## E. HotKey=False

Unchanged by design. It remains a derived indication of whether StartCapture is a keyboard binding and is not the combination-capture root cause.

## Automated verification

- Limited final-blocker suite: **15 PASS / 0 FAIL**.
- Covered: combination state assembly, F1, mouse/wheel display, bitmap ownership, 20x concurrent paint/hash isolation, 4K copy budget, cursor lease balance, actual overlay show/hide balance, portable runtime resolution.
- Post-confirm 4K harness: **PASS; manual acceptance pending**.
- Rapid + PP-DocLayout-S real local smoke: **PASS**.
- Full regression: **not run**, per instruction.
- Translation API calls: **0**.

## Changed files

- `GlobalKeyboardBindingHost.cs`
- `MainForm.cs`
- `PreviewForm.cs`
- `CaptureOverlay.cs`
- `CursorVisibilityLease.cs` (new)
- `OcrEngines.cs`
- `VisionRuntimeV2.cs`
- `PostConfirmPerformanceTrace.cs` usage expanded through call sites
- `BuildIdentity.cs`
- `ScreenshotTranslationUiTester.csproj`
- `FinalBlocker06131Tests.cs`
- `FinalStabilization0613Tests.cs` (version expectation only)

## Manual acceptance checklist

1. Ctrl + T
2. Ctrl + Shift + T
3. Mouse / Wheel bindings
4. Cursor hidden during screenshot selection
5. Cursor restored after Confirm
6. Cursor restored after Esc
7. Move mouse immediately after confirm and assess remaining pause
8. First and second OCR both complete normally
9. Ten consecutive runs without red-X or fatal crash

Do not treat this candidate as a stable release until manual acceptance and the later Final Full Regression are complete.
