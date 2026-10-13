# Screenshot Translator 0.5.0.6.6

Status: **Manual Acceptance Pending**

Candidate: Translation-First Coverage / Low-Cost Fallback / Performance Repair

## Outcome

- A valid mapped translation now attempts the normal renderer first and a single readable local fallback second. Original pixels are retained only if that fallback also cannot place the complete translation safely.
- Coverage distinguishes `TranslationAvailable`, `NormalRendered`, `FallbackRendered`, and `PreservedOriginal`; final preserve reasons include both normal and fallback failure details.
- Cash Body, Ophone description, and Faranna diagnostic cases now render their mapped Chinese text. April Long remains on the existing typography path.
- The fallback reuses the mapped translation, role, source rectangle, background estimate and existing font policy. It does not repeat OCR, translation, semantic grouping, or full-page analysis.
- The fallback panel is fully opaque and constrained to the source OCR rectangle so it does not erase adjacent controls.

## Performance and safety

- Exactly one initial full-image clone; no per-region full-image clone or blit.
- Local ROI allocation is reported separately, with normal/fallback time and fallback-region count.
- Build: 0 warnings, 0 errors.
- Specialized tests: 21 passed, 0 failed.
- Real API calls during verification: 0.
- No PDB, history, cache, or runtime log is included in the package.

## Frozen areas

Font selection/settings/fallback policy, typography foundation, OCR/Vision, provider/prompt/input, tray/history, zoom/pan, and quantity labels such as `x1`/`x6` were not redesigned in this repair.

## Manual acceptance focus

Please recheck the real Cash, Ophone and Faranna inputs first, then April Long and the title-anchor cases. This candidate is not marked Accepted or Stable.
