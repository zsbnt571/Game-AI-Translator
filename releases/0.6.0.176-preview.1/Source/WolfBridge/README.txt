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

Build: run Build.ps1 with Visual Studio C++ Build Tools (x86 target). All output
is within this candidate's Build/Wolf and Source/WolfBridge/Payload directories.
MinHook and nlohmann-json are distributed with their licenses. The CJK font and
OFL license are shared with the existing bundled GameMaker font dependency.

Tests/WolfFrameObserver.cpp and InjectWolfObserver.cpp are test-only tools.
They capture the owned test game's render buffer and are not shipped in the
application. Native tests use isolated game copies and a local mock provider.
No extracted game source, executable, asset, save or screenshot is included in
the source package.
