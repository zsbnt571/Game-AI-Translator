# Unity embedded translation candidate (.75)

This is a generic Unity integration path. Existing specialized adapters remain separate and unchanged. Engine identification and successful compilation do not mean every game is supported.

## Components

- `UnityEmbeddedCatalog.Read(exe, cancellation)` reads serialized asset fields and structured TextAssets in the desktop process. It returns the existing `RpgTextCatalogResult`; its results must be supplied to the shared background queue. It does not load or execute game assemblies. Missing field layouts are reported for runtime fallback.
- `UnityEmbeddedAdapter` detects Mono/IL2CPP and architecture, installs only a known payload with a write-ahead ownership ledger, verifies installation, and restores only unchanged owned files. Existing translation loaders are not overwritten.
- `UnityBridgeRuntime` runs inside the game. Documented Unity UI Text, TMP Text, and TextMesh setters/enable/render hooks consult the same active cache. Runtime requests are dispatched on the Unity thread. Both Mono and IL2CPP recheck known components in bounded LateUpdate passes; dynamic values which bypass Mono hooks can therefore enter the same foreground queue. IL2CPP installs no native text detours.
- Component-family discovery avoids duplicate TMP subclass passes. Mono checks a family every 400 ms and IL2CPP every 200 ms. Available unsorted `FindObjectsByType` queries are preferred; older engines use `FindObjectsOfType`, then an active-object-filtered resource query when neither scene query works. The per-component loops are bounded; a Unity discovery call itself is not a hard realtime guarantee.
- Mono detection accepts conventional managed game assemblies or the conjunction of Unity managed assemblies and a supported Mono runtime directory. Assembly-definition projects do not need an `Assembly-CSharp.dll` game assembly. File hashes protect installation ownership; they are not a game whitelist.
- `MonoPlugin` targets the BepInEx 5 / .NET 3.5 plugin API. `Il2CppPlugin` targets BepInEx 6 and builds a small frame callback against the actual generated Unity interop type.

## Desktop connection protocol

Use `RpgGameDataConnection(exe, "UNITY")` through `IEmbeddedTranslationConnection`. Named pipe, secret and initial state use `FUSION_UNITY_PIPE`, `FUSION_UNITY_SECRET`, and `FUSION_UNITY_TRANSLATION_START`. `FUSION_UNITY_STARTUP_FILE` points to a bounded UTF-8 JSON object:

```json
{"enabled":true,"entries":[{"source":"Settings","text":"设置"}]}
```

The start flag is authoritative. The plugin reads the cache synchronously before hooks are installed. The pipe hello and request envelopes match the existing connection. `translationPrepare` retains the active cache/display while filling a staged cache; `translationEnable` commits it. `translationApply` rejects an old epoch. `translationDisable` restores owned text/font values and leaves the cache available. Disconnect/unload restores on the Unity thread. The bridge exposes translation only, not game-variable editing.

`translationDiagnostics` is read-only, with hook counters and observed component values. A counter confirms that the corresponding hook ran; it does not establish all-game first-frame correctness, font appearance, or visual acceptance.

## Build and dependencies

Run `build-payloads.ps1` in the isolated work root. The build creates three resource ZIPs for Mono x64/x86 and IL2CPP x64. No IL2CPP x86 payload is claimed. Builds use these pinned official releases:

- BepInEx Mono 5.4.23.5: https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5
- BepInEx IL2CPP 6.0.0-be.788+5b766a3: https://builds.bepinex.dev/projects/bepinex_be/788 . Official archive SHA256: `F4CC496BD098A0DF4164B81E3737297707F13A47C2478DBA2F60EEFAB784817A`. Pinned Cpp2IL development.1452 supports metadata header versions 23–106; passing header checks is not proof that every game's metadata or runtime is compatible.
- AssetsTools.NET 3.0.5 and AssetsTools.NET.MonoCecil 3.0.4 from NuGet (MIT upstream, https://github.com/nesrak1/AssetsTools.NET).
- Unity class database, UABEA commit `5adb448deeefa1b88881f1fa44243009b352db3a`: https://raw.githubusercontent.com/nesrak1/UABEA/5adb448deeefa1b88881f1fa44243009b352db3a/ReleaseFiles/classdata.tpk . SHA256 `129E1F80F930415DB6779FE6089AFA75280CB51462BCEE812BEAB6CD81A764C6`.

BepInEx binaries remain unmodified and retain their upstream licenses. The payload includes the upstream-license/source notice. The official BepInEx TestGame fixture is https://github.com/BepInEx/TestGame ; its single runtime script rotates a cube. It verifies loader/pipe behavior, not text rendering.

## Validation boundaries

`Tests/UnityEmbeddedTests.csproj` contains self-written stubs and parser/cache-state fixtures. Those tests do not exercise a real Unity renderer. `Tests/RuntimeProbe` instead uses the actual desktop connection and real independent Unity game copies, with fake translations and no paid API. It rejects an unexpected runtime company/product/persistent-data identity and closes only its launched test process. Test copies are prepared by changing their serialized PlayerSettings and app.info after static save-path audits; original games and player saves are not edited.

Current run evidence is stored under `Artifacts/Fusion-R1-0.6.0.75/`. See `DELIVERY.md` for exact samples and observed results. Neutral fixtures cover cache matching, update rollback, conflicts, launch exit, and minimized-window identity. Real isolated Mono and IL2CPP copies validate loader, pipe and text hooks with fake translations; Unity 6 metadata is parsed read-only. Generic IMGUI, custom renderers, asset-only textures, NGUI, unusual encrypted bundles, and every Unity 6 runtime are not automatically validated by these tests.

The .81 incremental evidence is in `Work/Fusion-R1-0.6.0.81/Research/Unity81/README.md`: 64 offline checks, rebuilt Mono x86/x64 and IL2CPP x64 payloads, legacy framework references, and an unchanged-input read-only metadata audit. These checks do not constitute new native or visual acceptance. UI Toolkit, IMGUI, NGUI, texture text and custom renderers remain outside the currently implemented Text/TMP/TextMesh contract; nonstandard metadata remains rejected by the current interop loader.
