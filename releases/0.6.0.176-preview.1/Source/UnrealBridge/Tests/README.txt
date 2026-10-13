Unreal common translation regression suite

Run with Python and lupa.lua54 available:
python run.py <UnrealBridge source directory> <fresh evidence directory>
An optional FUSION_LUA_PYTHON_PATH points to an existing local Lupa dependency.
No engine, game, API, OCR, original game data or model is started by this suite.

The neutral fixtures exercise localization identity/restore safety, native text
API shape detection, transient UMG cache application, dynamic TextBinding return
values, cache source rotation, byte/work bounds, font resets and latest-style
restoration. They contain no game-name rules. Run these for later changes to the
shared bridge; inspect actual native pixels separately before claiming display
acceptance. GetText/readback success is not sufficient glyph evidence.

Native tests must use a separate game copy and save path; never reuse an original
game installation. Current vendor interfaces are capability checked. Never turn
a display cache key into a made-up localization identity or override a bound
TextBlock with SetText. Preserve source properties and game delegates.
