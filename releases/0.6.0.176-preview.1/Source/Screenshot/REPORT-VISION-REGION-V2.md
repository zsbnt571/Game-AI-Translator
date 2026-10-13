# Screenshot Translator 0.4.0 — Vision / Region Pipeline V2

## Status

**ARCHITECTURE DEVELOPMENT BUILD — NOT A MANUAL-ACCEPTANCE CANDIDATE**

This build creates an independent V2 source tree and validates the first architecture slice. It does not overwrite or rename the Settings 1.4 baseline. It must not replace the stable 0.3.0 executable yet.

## Frozen baseline verification

- Stable executable SHA256 rechecked: `32FD55B43E881050F4AB4D24DD2801E348415FC9D7E578DC16388DBCB6D6D8B5`
- Stable source was copied to a new directory with build outputs excluded.
- Stable executable/source/report were not modified.
- Settings 1.4 historical result remains `424 PASS / 0 FAIL`; this is a baseline fact, not a claim that the present V2 build passed all 424 unchanged tests.

## Implemented in this slice

- Independent 0.4.0 assembly/version and source tree.
- Settings schema 7 migration preserving API, translation, OCR, capture and normal preferences.
- OCR engine and Visual Model are separate settings.
- Explicit Visual Model list and labels: Off, PP-DocLayout-S/M/plus-L, PaddleOCR-VL-1.6, Qwen3-VL-4B-Instruct and Florence-2-base-ft.
- `IVisualAnalyzer`, `VisualAnalysisResult`, model diagnostics, role/order/layout hints.
- Polygon-first `RecognitionRegion` with stable ID, source block IDs, role, reading order, generations, parent, mapping, diagnostic and manual override fields.
- Three-source fusion contract: traditional CV + visual region + OCR detector; OCR-only regions are retained and visual-only regions remain eligible for crop OCR.
- V2 roles including CharacterName, Dialogue, Narration, Choice, Button and Caption.
- Strict Region → TranslationUnit → Region mapping with exact ID-set validation, preserve-original behavior and stale-generation discard.
- Headless Region Editor state operations: create/delete/move/resize/polygon/merge/split/role/order/ignore/preserve/region OCR/undo/redo.
- Basic Region Renderer V2 mapping, wrapping/font reduction and preserve-original overflow warning.
- Diagnostics fields required to isolate Vision/OCR/Fusion/Role/Translation/Renderer failures.

## Automated result

- Release compile: PASS
- V2 offline contract tests: `21 PASS / 0 FAIL`
- Real translation API calls: `0`
- ActiveOperations after tests: `0`
- Worker residue: `NONE`

## Not implemented / not verified yet

- Real inference adapters and model assets for PP-DocLayout, PaddleOCR-VL, Qwen and Florence.
- Traditional CV proposal generation wired into the live capture pipeline.
- Native-resolution crop OCR orchestration wired to the selected OCR runtime.
- Windows OCR isolation instrumentation and the complete required isolation matrix.
- Live Preview consumption of `RecognitionDocumentV2` (the existing Preview remains V1 in this development build).
- Full graphical Region Editor V2 with multi-select, polygon handles, zoom/pan and overlay layer switches.
- Complete reading-order graph/container UI and manual visual reanalysis.
- Full Renderer V2 background estimation/inpainting and role-aware font policy.
- Frozen-regression rerun and documented V1-superseded-test replacement table.
- Real `missing=GRP001` fixture under V2 TranslationUnit IDs.
- Manual real-image set and the four-model quality/latency/VRAM matrix.

## Required final answers (current development-build truth)

1. Settings 1.4 completely retained: **YES**
2. Independent V2 source created: **YES**
3. V2 limited to old recognition architecture: **NO**
4. API / Provider / translation settings retained: **YES**
5. OCR selection remains independent: **YES**
6. Vision Model independent from OCR: **YES**
7. Windows OCR isolation reverified: **NO — pending**
8. Rapid/Paddle never initialize Windows OCR: **NOT REVERIFIED**
9. Multiple selectable visual model names added: **YES (selection/configuration only)**
10. Parenthesized purpose text shown with each name: **YES**
11. Unified Visual Adapter contract: **YES**
12. Vision cannot overwrite authoritative OCR text: **YES by contract; live pipeline pending**
13. Region Proposal Fusion: **YES (core); live wiring pending**
14. OCR region retained when Vision misses: **YES**
15. Visual region crop OCR: **CONTRACT ONLY — live wiring pending**
16. RecognitionRegion V2: **YES (core)**
17. Polygon support: **YES**
18. Region ID through OCR/translation/renderer: **YES (core mapping); live UI pending**
19. Region Editor extensively rebuilt: **NO — state engine only**
20. Merge/Split/Role/ReadingOrder/Ignore: **YES in state engine; UI pending**
21. Structured Grouping based on Region V2: **PARTIAL**
22. Game CharacterName/Dialogue/Choice/Button: **YES in roles/tests; live fixtures pending**
23. Highlight consumes unified Region mapping: **NO — pending**
24. Missing Group Recovery migrated: **NO — pending live V2 integration**
25. Real `missing=GRP001` covered: **NO — pending fixture**
26. Full-batch blind retry avoided: **YES in retained Settings 1.4; V2 integration pending**
27. Renderer directly consumes Region: **YES in core renderer**
28. Basic in-place translated image V2: **YES in core renderer; live UI pending**
29. Region-based wrapping: **YES**
30. Basic auto font size: **YES**
31. Partial overwrite avoided on overflow: **YES**
32. Old tests explicitly superseded instead of silently deleted: **NO TESTS DELETED; replacement table pending**
33. All Frozen Regression 0 FAIL: **NOT YET RUN**
34. Real automated API calls: **0**
35. ActiveOperations: **0**
36. Worker residue: **NONE**

The build is intentionally not promoted until every pending item is implemented and the real-image/model matrix is available.
