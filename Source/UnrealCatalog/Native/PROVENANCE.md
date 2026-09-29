# External native decoder

The optional catalogue worker expects a locally supplied `oo2core_9_win64.dll` with SHA-256 `6F5D41A7892EA6B2DB420F2458DAD2F84A63901C9A93CE9497337B16C195F457` (637,952 bytes).

The binary is not included in this source repository. Its hash is an integrity constraint, not redistribution permission. Obtain the dependency from a source you are authorized to use. The licences of UE4SS and CUE4Parse do not relicense this proprietary dependency.

The build compiles the worker without copying this DLL, even when it exists in `Native/`. An explicit local-only build may use `-p:FusionIncludeLocalDependencies=true`; the existing pinned build check remains enabled, but the DLL is not copied. The worker verifies the descriptor, pinned length and SHA-256 immediately before native loading and holds a read-only handle while using it. Import the lawful matching DLL into `runtime-payloads/unreal-oodle/oodle9-6f5d41a7892e/oo2core_9_win64.dll` with its adjacent `.json` descriptor. The desktop passes its explicit dependency root to the worker through `GAME_AI_TRANSLATOR_PAYLOAD_ROOT`; set this absolute local root explicitly when invoking the worker manually. The old beside-worker path is not a fallback. Missing or changed dependencies stop catalogue extraction with a specific error; no download, alternate decoder, or arbitrary DLL fallback is used.

Local-only output must never be used as a public package. Default builds reject a stale Oodle DLL already present in their output directory instead of deleting user-provided files. Use separate clean output directories for public and internal builds.
