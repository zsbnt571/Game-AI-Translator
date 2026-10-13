# 0.5.0.6.13.2 Cursor Requirement Corrected

## Scope

This is a minimal correction of the 6.13.2 cursor requirement misunderstanding. The original 6.13.2 source and delivery remain unchanged.

## Correction

- Removed the Overlay `CursorVisibilityLease` and all `Cursor.Hide` / `Cursor.Show` calls.
- The real system cursor remains visible while the Overlay is active.
- Existing interactive cursor states (`Cross`, resize directions, move) remain unchanged.
- Confirm, Cancel, and Escape no longer participate in a cursor visibility lease.

## Frozen bitmap audit

- DXGI Desktop Duplication reads the acquired desktop texture only. It does not call `GetFramePointerShape`, read pointer metadata, or composite a pointer shape into the bitmap.
- GDI fallback calls `Graphics.CopyFromScreen` only. It has no `GetCursorInfo`, `DrawIcon`, or `DrawIconEx` cursor composition path.
- No capture architecture change was required.

## Frozen areas

The combination-key fix, bitmap ownership isolation, OCR, translation, DXGI architecture, startup-flash behavior, and post-confirm optimization were not changed.

## Verification

- Release build: PASS (0 errors; one pre-existing nullable warning in `PreviewForm.cs`).
- Targeted blocker suite: 15 PASS / 0 FAIL.
- Real translation API calls: 0.

## Manual acceptance from item 4

1. Enter capture: system cursor is visible.
2. Create selection: Cross cursor is visible.
3. Resize all edges/corners: resize cursor is visible.
4. Move selection: move cursor is visible.
5. Confirm: cursor remains visible.
6. Right-click cancel: cursor remains visible.
7. Escape cancel: cursor remains visible.
8. Inspect resulting frozen screenshot: no cursor pixels are present.

This build is an artificial-test candidate and is not declared accepted until real hardware validation is complete.
