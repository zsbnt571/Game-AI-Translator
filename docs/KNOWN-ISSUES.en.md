# FUSION R1 0.6.0.176 Preview 1 — Known issues and evidence


This is a public preview. Bundled dependencies do not imply compatibility with every game, engine or GPU.

Language limitations
The covered Ren'Py/Godot active-locale paths showed no bulk collection of inactive-pack-exclusive text. This result is limited to the tested games and paths.
English + Japanese intentionally preserves identified Korean; Automatic may translate it when actually encountered.
Short strings, names, Romanized text, shared Han characters and missing context remain ambiguous. Some longer Japanese names are still heuristic cases. Zero unnecessary API calls cannot be promised.
Previously observed requests can finish during a locale switch. Shared menus, hidden prebuilt controls and resource pretranslation can differ from strictly visible text.
Changing target/style does not replace every old cache entry. Manual translations retain priority.
Wrong-script English output and short Japanese/Korean mixed-span omission checks have improved, but semantic quality and layout are not guaranteed. The screenshot/OCR pipeline was not fully revalidated by these game-batch checks.

Real-game scope
MIST and Konoha: isolated actual game executables, native menus/language switching and limited opening scenes; no full playthrough.
Village: actual main menu only.
Brotato and Chillquarium: actual compressed language resources loaded in an engine host; their complete commercial executables were not launched.
Additional RPG Maker ending titles and some mixed dialogue: real resource samples, not reached/observed scenes.
Other engines, Unity IL2CPP and Unreal were not comprehensively rerun in this round. Extraction/cache success is not proof of in-game replacement, persistence, glyph correctness or layout.
Existing mods/loaders can conflict. First translations depend on service latency. UI improvements are not a new visual sign-off for every DPI, theme, resolution and dropdown.

Platform and dependency limits
No clean-Windows VM acceptance has been completed. Local isolated extraction/startup/OCR health checks are reported separately.
Windows OCR/legacy helpers still use OS .NET Framework 4.8 and installed language components.
The optional NVIDIA supplement reuses the verified previous dependency set; .176 GPU inference on all hardware has not been certified. Drivers are not bundled; CPU remains available.
Proprietary Oodle and optional heavy layout/vision models are excluded. Certain Unreal resources cannot be decompressed without an independently authorized compatible codec.
Translated-image rendering remains experimental.

Final build evidence
Main DLL SHA-256: aba0acf22717ea08a2d48752bc8dff72155d3b2d1f38f8031904c204f5891901
43 actual-game-source samples, including 24 runtime-observed and 19 resource-only, across Chinese/English/Japanese targets and Automatic/English+Japanese sources: 258 result rows, 60 jobs, 57 actual API requests, zero provider errors or checked language-boundary violations.
Independent checks passed: language policy 179; menus/endings 165; short Japanese/mixed spans 47; caches 18.
Natural style was used for the real API regression; this is not equivalent real semantic coverage for every personalization style.
Full game collection matrices used earlier builds in this test round. Final quality refinements were then checked on the frozen .176 DLL through API and rule tests; earlier runtime records are not claimed as a final-binary full gameplay acceptance.
