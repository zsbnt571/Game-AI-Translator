#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <string>
#include <cmath>

namespace FusionSpeed {
constexpr uint32_t Magic=0x46535044,Version=2,Size=256;
constexpr ULONGLONG LeaseMs=5000;
enum Error:DWORD {Success=0,BadArguments=1,BadMapping=2,TargetMismatch=3,WrongArchitecture=4,ForbiddenTarget=5,WolfConflict=6,LoaderFailure=7,RemoteTimeout=8,SessionBusy=9,HookFailure=10,OwnerUnavailable=11,IdentityChanged=12,InvalidRate=13,BadCommand=14,StartFailure=15,NativeTimeout=16,OtherBridge=17,OldBridge=18};
enum ClockKind:unsigned {CounterClock=0,TickClock=1,Tick64Clock=2,MultimediaClock=3,FileTimeClock=4,PreciseFileTimeClock=5,ClockKinds=6};
struct alignas(8) Shared {
 uint32_t magic,version,size,ownerPid;
 uint64_t ownerCreated;
 uint32_t targetPid;volatile LONG state;
 uint64_t targetCreated;
 char token[64];
 volatile LONG requestSeq,ackSeq;
 alignas(8) volatile LONG64 requestedRate,actualRate,heartbeat,statusTick;
 volatile LONG error;uint32_t reserved;
 alignas(8) volatile LONG64 calls;
 volatile LONG modules;uint32_t desiredMode;
 alignas(8) volatile LONG64 clockCalls[ClockKinds];
 alignas(8) volatile LONG64 commandClockBaseline,commandClockMask;
 uint8_t padding[24];
};
static_assert(sizeof(Shared)==256&&offsetof(Shared,requestedRate)==112&&offsetof(Shared,desiredMode)==164&&offsetof(Shared,clockCalls)==168&&offsetof(Shared,commandClockBaseline)==216&&offsetof(Shared,commandClockMask)==224,"IPC layout");
struct Bootstrap {uint32_t size,version;wchar_t mapping[160];char token[65];char padding[3];};
static_assert(sizeof(Bootstrap)==396,"Bootstrap layout");
inline LONG64 Read64(volatile LONG64* value){return InterlockedCompareExchange64(value,0,0);}
inline void Write64(volatile LONG64* value,LONG64 next){InterlockedExchange64(value,next);}
inline double Double(LONG64 bits){double value;memcpy(&value,&bits,8);return value;}
inline LONG64 Bits(double value){LONG64 bits;memcpy(&bits,&value,8);return bits;}
inline uint64_t Created(HANDLE process){FILETIME creation,exit,kernel,user;if(!GetProcessTimes(process,&creation,&exit,&kernel,&user))return 0;return (uint64_t(creation.dwHighDateTime)<<32)|creation.dwLowDateTime;}
inline bool Hex(const std::string& text){return text.size()==64&&text.find_first_not_of("0123456789ABCDEF")==text.npos;}
inline bool MappingName(const std::wstring& name){const std::wstring prefix=L"Local\\FusionSpeed_";if(name.rfind(prefix,0)!=0)return false;auto suffix=name.substr(prefix.size());return (suffix.size()==32||suffix.size()==64)&&suffix.find_first_not_of(L"0123456789ABCDEFabcdef")==suffix.npos;}
inline bool ValidRate(double value){return std::isfinite(value)&&value>=0.25&&value<=100;}
inline bool Header(Shared* data){return data&&data->magic==Magic&&data->version==Version&&data->size==Size&&data->ownerPid&&data->targetPid&&data->ownerPid!=data->targetPid&&data->ownerCreated&&data->targetCreated;}
inline const char* Message(DWORD error){switch(error){case Success:return "Speed bridge ready";case BadArguments:return "Invalid attach arguments";case BadMapping:return "Invalid speed session";case TargetMismatch:return "Target identity does not match";case WrongArchitecture:return "Helper and target architectures differ";case ForbiddenTarget:return "Target is not an eligible game process";case WolfConflict:return "Existing WOLF speed bridge must be used";case LoaderFailure:return "Windows did not permit loading the speed component";case RemoteTimeout:return "Remote initialization timed out; target was not terminated";case SessionBusy:return "A speed session is already active";case HookFailure:return "Required game clock hooks are unavailable";case OwnerUnavailable:return "Owner process is unavailable";case IdentityChanged:return "Speed session identity changed";case InvalidRate:return "Speed must be a finite number from 0.25 to 100";case BadCommand:return "Invalid speed command";case StartFailure:return "Speed worker could not start";case NativeTimeout:return "Speed initialization did not become ready";case OtherBridge:return "Another speed bridge is already loaded";case OldBridge:return "An older speed component is still loaded; exit and relaunch the game";default:return "Speed operation failed";}}
}
