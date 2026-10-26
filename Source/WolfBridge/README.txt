Fusion WOLF native display adapter, protocol 133

Scope: unpacked WOLF 3 UTF-8 projects with the verified x86 24-byte string
layout and a unique renderer signature. Unknown signatures and encrypted
packages are rejected. There is no game-name, executable-hash or fixed-RVA
dispatch in the production adapter.

The launcher creates its own suspended child, resolves LoadLibraryW using the
remote module base, loads its recorded bridge, completes initialization and
then resumes the game's primary thread. The app tracks the real game PID.
Failure cleanup belongs to this child/job only; running games are not attached.

The bridge temporarily borrows the raw display string for one complete layout
call and restores all 24 original bytes on return. C++/FP/XMM state and original
return semantics are preserved. No event, condition, input or save field is
translated. The game's own layout and renderer draw the result. Missing CJK
glyph fallback is process-private and limited to translated layout calls.

Control prefixes are kept byte-for-byte. Unknown inline controls and mixed
binary/text fragments are retained in their original form, not truncated or
sent as per-character translation jobs. This is a current coverage limit.

The render path reads immutable cache shards. Request parsing, startup cache,
translation capture polling and JSON files belong to the worker; no network
calls run in the game. Captures, cache bytes, bindings, strings, fonts and
nesting have explicit limits. Disabling restores original display on the next
game layout; disposal never terminates a successfully launched game.

Speed authorization and coverage
-------------------------------
Translation startup initializes clock metadata but installs no speed hooks.
dataEnable (or explicit speedEnable) authorizes this process session; the first
authorized non-1 gameSpeed/dataSpeed request installs the scoped clock hooks.
dataDisable, speedDisable, stop and owner exit revoke authorization and restore
1x. The worker revokes speed before waiting for a main-thread dataDisable.
Status reads do not install hooks, inspect game variables or grant permission.
Restoring 1x is allowed without authorization and keeps previously virtualized
clocks continuous; dormant hooks remain until process exit to avoid clock jumps.

This is main-executable QPC/GetTickCount/GetTickCount64/timeGetTime/Sleep scaling,
not WOLF event-loop or engine timeScale control. Calls originating in other DLLs
stay on wall time; timeGetTime is covered when winmm is loaded at installation.
An existing FusionSpeed module prevents a second clock installation. Clock-call
counts show scoped activity only and do not prove plot, animation or audio speed.

Tests/RunSpeed113.ps1 compiles the full bridge and exercises these production
clock/protocol headers in owned x86 neutral processes and a separate DLL. It
checks deferred hook installation, authorization, cross-module clocks, normal
restoration and concurrent process independence. Synthetic data tests remain
separate; this does not constitute actual WOLF game, renderer or save acceptance.

Build: run Build.ps1 with Visual Studio C++ Build Tools (x86 target). All output
is within this candidate's Build/Wolf and Source/WolfBridge/Payload directories.
MinHook and nlohmann-json are distributed with their licenses. The CJK font and
OFL license are shared with the existing bundled GameMaker font dependency.

Tests/WolfFrameObserver.cpp and InjectWolfObserver.cpp are test-only tools.
They capture the owned test game's render buffer and are not shipped in the
application. Native tests use isolated game copies and a local mock provider.
No extracted game source, executable, asset, save or screenshot is included in
the source package.

Session file paths
------------------
WolfSessionFiles.h converts validated ordinary absolute local-drive paths to
extended Win32 paths only at the file I/O boundary. This supports deeply nested
Unicode data directories without relying on the launcher or host game's
manifest. Session identity, ownership tokens and marked-copy checks continue
to use their original strings. UNC/device namespaces, ADS, relative paths and
ambiguous dot/trailing-space components are rejected, consistent with the
managed data-root contract.

Launcher results, startup data, hello, requests, responses, stop markers and
resource-collection messages all use this shared file reader/writer. Test-only
bitmap output uses the same path conversion while retaining its private
desktop, explicit test-copy marker, no-reparse and CREATE_NEW requirements.

Tests/RunSessionFiles.ps1 compiles an x86 controlled test and checks synthetic
short, 264-, 500- and 2000-character Unicode session paths, atomic replacement,
bounded reads and rejected path forms. It preserves its isolated fixtures and
does not launch a game, access real user data or capture the desktop.
