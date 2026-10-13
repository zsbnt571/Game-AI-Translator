Common Unity runtime regression checks

These checks belong to the reusable runtime source, not to any named game.
No real game, saves, translation API or OCR is used. Supply an isolated FusionBuildRoot.

Build CommonRuntime/Probe/CommonRuntimeProbe.csproj in Release, then run GeneralRules.Probe.exe with:
  inactive-tail <absolute-result-json-path>
  unproven-prefix <absolute-result-json-path>
  auto-font-ownership <absolute-result-json-path>
  font-pool-bound <absolute-result-json-path>

The destination parent directory must exist. Each scenario runs in a separate process.
Required results: passed=true and failed=0.

Checks cover later inactive menu discovery and first display, unloaded scenes, cache-prefix safety,
isolated auto-fit font ownership, bounded pooling and recycling, third-party font preservation,
exact source restoration and callback removal. A stripped legacy Font API is simulated.

Native visual acceptance remains separate: test at the game's actual canvas scale, menu re-entry,
scene changes and a new process. Cached text/mesh counts alone do not establish visible glyphs.
