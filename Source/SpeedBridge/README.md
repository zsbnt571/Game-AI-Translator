# Independent game speed bridge

The component attaches to an already confirmed local game process without restarting it. It is independent of translation and variable editing. It changes clocks used by the target executable and recognized engine DLLs loaded from the same game directory. It does not change the Windows system clock or write game files.

## Delivery and build

Run `Build.ps1` for x86 and x64 builds, or `Build.ps1 -Tests` for synthetic test programs. The script uses the repository's MinHook source and the locally installed Visual Studio Build Tools. Production delivery consists only of:

- `FusionSpeedHost.x86.exe` and `FusionSpeed.x86.dll`
- `FusionSpeedHost.x64.exe` and `FusionSpeed.x64.dll`
- `LICENSES.txt` (MinHook and HDE notices)

The helper and its matching DLL must be adjacent. Fusion installs these files in `adapters/speed`. Test executables, test DLLs, object files, and test reports must not be copied into a release payload.

## Attach contract

The managed caller first confirms the process identity and creates a 256-byte named mapping, then launches the matching helper with exactly five argument pairs:

```text
--pid <decimal PID> --created <decimal creation FILETIME> --exe <absolute EXE>
--mapping <Local\FusionSpeed_32-or-64-hex> --token <64 uppercase hex>
```

The helper returns constant JSON fields `ok`, `errorCode`, `message`, and `pid`; it never echoes the token or executable path. It checks the target's canonical executable, creation time, architecture, and liveness. It checks the mapping, owner identity, heartbeat, and requested mode immediately before each remote call. DLL initialization independently checks the mapping, process creation identity, owner, heartbeat, and stop state.

The loader resolves `LoadLibraryW` using the local function's actual owning module and its RVA in the corresponding target module. It finds the loaded speed DLL through a fresh target module snapshot, then reads that loaded image's bounded export table and `FusionSpeedProtocolVersion` before resolving its entry. A newer on-disk image cannot supply an RVA for an older resident DLL. It never interprets a 32-bit remote thread exit code as an x64 module pointer. A timed-out remote thread retains its argument buffer; neither the target nor its thread is terminated, and no automatic retry is performed.

## Shared memory version 2

All fields are little-endian, with 8-byte alignment for 64-bit atomic fields. C++ static assertions enforce size and key offsets.

| Offset | Type | Meaning |
| --- | --- | --- |
| 0 | uint32 | Magic `0x46535044` |
| 4 | uint32 | Version `2` |
| 8 | uint32 | Size `256` |
| 12 | uint32 | Owner PID |
| 16 | uint64 | Owner creation FILETIME |
| 24 | uint32 | Target PID |
| 28 | int32 | State: 0 waiting, 1 ready, 2 restored/stopped, 3 fatal |
| 32 | uint64 | Target creation FILETIME |
| 40 | char[64] | Session token, uppercase hexadecimal |
| 104 | int32 | Request sequence |
| 108 | int32 | Acknowledged sequence |
| 112 | float64 | Requested rate |
| 120 | float64 | Accepted clock rate; not measured gameplay speed |
| 128 | uint64 | Owner heartbeat in real `GetTickCount64` milliseconds |
| 136 | uint64 | Native status tick in the same real-clock units |
| 144 | int32 | Last error |
| 148 | uint32 | Reserved |
| 152 | uint64 | Number of scoped clock/sleep calls |
| 160 | uint32 | Number of eligible loaded modules |
| 168-208 | uint64[6] | Scoped reads of QPC, tick32, tick64, multimedia, FILETIME, precise FILETIME |
| 216 | uint64 | Sum of clock reads when the current command was accepted |
| 224 | uint64 | Clock APIs observed since the current command, bit 0-5 |
| 164 | uint32 | Desired mode: 1 run, 2 stop |
| 168 | byte[88] | Reserved, zero |

Initialize both rates to 1, desired mode to 1, both sequences and state to 0, and heartbeat to current wall-clock ticks. One managed writer publishes rate/mode before releasing the aligned 32-bit request sequence and waits for the prior acknowledgement. The worker reads a stable sequence, validates the request, and publishes accepted rate/error and a fresh clock-read baseline before the acknowledgement. The managed caller requires at least two new scoped clock reads before reporting a non-1x setting as applied. Sleep-only activity, old cumulative counts, and the bridge's original control clocks are insufficient. Restoring 1x does not require activity. This confirms interception of a clock used by the process, not a measured story or animation rate. Heartbeat updates are independent of requests. Rates must be finite and within 0.25–100 inclusive. Invalid values are acknowledged with an error and do not change the current rate.

## Scope and recovery

- Clocks: `QueryPerformanceCounter`, `GetTickCount`, `GetTickCount64`, `timeGetTime`, `GetSystemTimeAsFileTime`, and `GetSystemTimePreciseAsFileTime` (when available); delays: `Sleep` and `SleepEx`.
- Caller filtering covers the target executable and recognized Unity, Python, RGSS, SDL, Godot, NW/CEF, Cocos, and Unreal runtime module names whose canonical files are inside the game directory. It excludes Fusion bridge modules and unrelated/system DLL callers.
- Module snapshots are bounded to 4,096 entries, eligible address ranges to 128; refresh runs once per second outside the game's main thread. Path resolution is limited to candidate modules.
- DLL-internal control and lease timing use original clocks. A 100× game rate does not accelerate the five-second lease.
- Stop, owner exit, and expired heartbeat restore rate 1. The accumulated virtual clock offset remains so that the game's clocks do not jump backward. Hooks remain installed at rate 1 to avoid invalidating in-flight calls. A stopped session can be replaced by a new mapping using the same loaded DLL.
- Changing immutable identity fields restores rate 1 and reports an error.
- Existing WOLF speed bridges are rejected before attach. A WOLF bridge loaded later causes this component to restore rate 1 and report a conflict. The WOLF component must likewise avoid installing a second set of clock hooks. Neither path restarts or terminates the game.
- The component does not elevate privileges, bypass protected processes, or implement anti-cheat bypasses. A denied attach is reported as a failed operation.

This is a common clock mechanism, not an assertion that every game or every timer is accelerated. Engine timers based on other APIs, code in runtime modules outside the game directory, audio/video playback, external services, and already-customized timing may behave differently. Very high rates can also be limited by the game's update loop or hardware. Existing translation logic running inside an eligible interpreter/runtime may observe its scaled clocks; the independent native control channel remains on real time.

## Error codes

| Code | Meaning |
| --- | --- |
| 0 | Ready/success |
| 1 | Invalid arguments |
| 2 | Invalid mapping or initial identity |
| 3 | Target identity mismatch |
| 4 | Helper/target architecture mismatch |
| 5 | Ineligible system, Fusion, or self target |
| 6 | Existing or newly loaded WOLF speed bridge |
| 7 | Windows loading operation failed |
| 8 | Remote operation timed out, target retained |
| 9 | Another active speed session |
| 10 | Required clock hooks/module scope unavailable |
| 11 | Owner or heartbeat unavailable |
| 12 | Session identity changed |
| 13 | Invalid rate |
| 14 | Invalid command or already-cancelled attach |
| 15 | Worker initialization/runtime failure |
| 16 | Initialization did not become ready |
| 17 | Different speed DLL already loaded |
| 18 | Older resident speed protocol; exit and relaunch the game |

## Controlled verification

`SpeedTests.x86.exe` and `SpeedTests.x64.exe` each run 172 checks against synthetic child processes and the actual helper/DLL. Coverage includes identity/architecture rejection before loading; QPC, tick, multimedia clock and delay behavior; FILETIME-only and precise-FILETIME-only 0.25x/4x/10x/restore probes measured against an external clock; sleep-only and historical-read telemetry rejection; old resident protocol rejection; recognized versus unrelated module scope; fractional and 100× rates; invalid inputs; explicit stop; real five-second heartbeat expiry at 100×; owner exit; identity tampering; reconnect; concurrent sessions; and early/late WOLF conflicts. Fixtures close cooperatively. Reports are generated in the isolated verification output; only the four production DLL/EXE files are copied into `Payload` after verification.

These checks do not represent real-game acceptance or guarantee universal engine compatibility. The managed integration has its own controlled tests.
