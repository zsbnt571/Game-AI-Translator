# Native Unity translation bridge

This route is used only for nonstandard/missing IL2CPP metadata on recognized
Unity 2021.3 or 2022.3 x64 runtimes. Standard metadata, Mono, existing BepInEx
loaders, and specialized translators keep their existing routes.

The installer checks exported APIs without loading game DLLs into the desktop.
If names are unavailable, the 2021.3 loader must match one complete 229/230-entry
table, its instruction spacing, unique exports, and nine independent accessor
signatures. Unrecognized layouts are rejected. No game title, alias, RVA or
decrypted metadata is embedded in the shipped source.

An owned DLL, API map and binary fingerprint are installed in
`.fusion-native-runtime`. The usual hashed install ledger and update recovery
protect user changes. Only the process just launched by Fusion is attached.
Games launched outside Fusion are unchanged; metadata and executable files are
never rewritten. Existing translation-plugin conflict checks remain in force.

The runtime detours `il2cpp_runtime_invoke` to schedule whitelisted Unity UI and
TMP work on the original main thread, including the pre-canvas callback when
present. It does not traverse arbitrary game objects or invoke game methods.
Targets use weak handles; owned font state is restored on disable/disconnect.
TMP material restoration preserves a newer game material. Inactive targets are
also restored. Destroyed targets are dropped.

The existing authenticated, current-user named pipe and translation queue are
used. Startup cache is available before the handshake and survives the desktop
prepare/enable sequence when requested. Queued paused requests are explicitly
cancelled before returning a recoverable response. Started requests are never
reported as unstarted. Epochs reject stale results.

Limits: 8,192 tracked text objects; bounded discovery snapshots; 64 bindings
per refresh slice, with a 2 ms scheduling budget checked between calls; one
discovery family per 200 ms; bounded messages/cache. Individual Unity/font calls
are synchronous and can exceed the scheduling budget. Tests measured cold font
initialization as the largest such operation. This is not a hard real-time bound.

Legacy UI uses a dynamic OS font; TMP uses an installed font file and a dynamic
SDF asset. Every translation requires character-coverage confirmation. Fonts are
not redistributed. Unsupported glyphs retain the source text and are reported.

Generated fonts and TMP assets carry DontUnloadUnusedAsset. A managed GC handle
alone does not keep their native Unity asset alive during scene unloading. Every
reuse checks native lifetime; a destroyed asset also invalidates its coverage
cache. Legacy glyph queries use il2cpp_runtime_invoke with an exception result.
Some AOT players strip managed dynamic/path constructors; the fallback to their
registered native initializer catches C++ exceptions only during construction
of an unpublished font. It does not catch SEH/access violations or suppress
game-wide failures. Authored game fonts keep their existing lifetime flags.

The 152 lifetime/exception fix is retained. Version 153 also handles native
legacy UI.Text's integer font size and CJK line breaking. Only a translated
plain CJK string with a fixed, vertically truncated allocation uses this path.
OS font metrics select the largest fitting integer size below the source
ceiling; the display-only line buffer never changes translation cache keys.
Rich size/material markup, layout-owned expansion and other renderers retain
their existing behavior. The metric estimate has conservative padding and is
not a substitute for inspecting actual glyphs in each supported game.

A stretched leaf text node can have its Y scale corrected with inverse rectangle
height compensation; world corners and its ancestor button allocation remain
unchanged. Text nodes with children or their own Selectable/AspectRatioFitter
are not rescaled. Font size, best-fit and geometry changes have independent
ownership receipts. Disable restores only still-owned values; failed readback
remains pending and a newer game-owned value is preserved.

Text setters and enable callbacks queue an observation. Queued mutations are
serviced outside nested runtime callbacks and outside CanvasUpdateRegistry's
graphics/layout rebuild phase. Stable strings avoid repeated setters and
legacy geometry checks are paced. These rules are based on API capabilities,
control ownership and text script; no game title, scene or screenshot string
is used as a product rule.

Validation in isolated copies: FAP story mode 0.3.0x and Cumrooms 0.7 final
main-menu labels, startup on/off, cache handoff, repeated disable/enable, paused
main thread, stale epochs, disconnection, changed-file rejection and uninstall.
These are runtime property/font checks, not rendered-pixel or full-game visual
acceptance. Scene-specific scripts, world-space/custom text, complete dialogue,
and all game versions are not asserted to work.

152 regression: the prior runtime reproduced a Visual C++ runtime error on a
real isolated player's main-menu-to-game transition. A trace confirmed a
destroyed generated font immediately before the unguarded glyph query. The
fixed runtime completed that transition and three repeated scene cycles with
Chinese menu property readback and normal exit. See the 152 validation report
for the exact tested build, limitations, and separate controlled native tests.

153 validation adds captured real-player pixels for main menu, extra content
and the special warning panel, toggle/reentry checks, measured world corners,
button callback transitions and repeated game starts. The user's intermittent
body/button disappearance was not reproduced consistently; it remains a
specific retest item and is not inferred fixed from a successful text readback.

Build with `Tests/BuildRuntime153.ps1` and an x64 MSVC toolchain. MinHook v1.3.4
(`c3fcafdc10146beb5919319d0683e44e3c30d537`) and nlohmann/json v3.12.0 are
included as source dependencies in the candidate work directory; notices ship
in the desktop package. MSVC CRT is linked statically; user32 and gdi32 are
Windows system dependencies. GDI is used only for bounded offscreen font metrics.

Primary references read:
- https://github.com/TsudaKageyu/minhook
- https://github.com/nlohmann/json
- https://raw.githubusercontent.com/Unity-Technologies/UnityCsReference/2021.3/Modules/TextRendering/TextRendering.bindings.cs
- https://github.com/vfsfitvnm/frida-il2cpp-bridge

Unity reference code was consulted for API semantics; no Unity reference source
or Frida runtime is bundled. The bridge and API-table validation are implemented
locally; no metadata decryption is required by this route.
