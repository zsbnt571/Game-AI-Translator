#include "Protocol.h"
#include "ProcessIdentity.h"
#include "Clock.h"
#include "MinHook.h"
#include <atomic>
#include <intrin.h>
#include <memory>
#include <vector>

using namespace FusionSpeed;
namespace {
Clock clockState;
decltype(&QueryPerformanceCounter) originalCounter=nullptr;
decltype(&GetTickCount) originalTick=nullptr;
decltype(&GetTickCount64) originalTick64=nullptr;
decltype(&Sleep) originalSleep=nullptr;
decltype(&SleepEx) originalSleepEx=nullptr;
decltype(&GetSystemTimeAsFileTime) originalFileTime=nullptr;
decltype(&GetSystemTimeAsFileTime) originalPreciseFileTime=nullptr;
DWORD (WINAPI* originalMultimedia)()=nullptr;
SRWLOCK rangeLock=SRWLOCK_INIT;
struct Range {uintptr_t first,last;};
Range ranges[128]{};unsigned rangeCount=0;
std::atomic<ULONGLONG> callCount{0};
std::atomic<ULONGLONG> clockCalls[ClockKinds]{};
std::atomic<ULONGLONG> commandClockMask{0};
std::atomic<bool> active{false};bool initialized=false;
std::wstring gamePath,gameRoot;
bool Scoped(void* caller){auto address=(uintptr_t)caller;bool found=false;AcquireSRWLockShared(&rangeLock);for(unsigned i=0;i<rangeCount;i++)if(address>=ranges[i].first&&address<ranges[i].last){found=true;break;}ReleaseSRWLockShared(&rangeLock);if(found)callCount.fetch_add(1,std::memory_order_relaxed);return found;}
LONGLONG CounterNow(){LARGE_INTEGER now{};originalCounter(&now);return now.QuadPart;}
void ObserveClock(unsigned kind){clockCalls[kind].fetch_add(1,std::memory_order_relaxed);commandClockMask.fetch_or(ULONGLONG(1)<<kind,std::memory_order_relaxed);}
ULONGLONG ClockReads(){ULONGLONG total=0;for(auto& count:clockCalls)total+=count.load(std::memory_order_relaxed);return total;}
BOOL WINAPI CounterHook(LARGE_INTEGER* value){if(!Scoped(_ReturnAddress()))return originalCounter(value);if(!value)return FALSE;value->QuadPart=(LONGLONG)clockState.Read(CounterNow(),CounterClock);ObserveClock(CounterClock);return TRUE;}
DWORD WINAPI TickHook(){if(!Scoped(_ReturnAddress()))return originalTick();auto value=(DWORD)(ULONGLONG)clockState.Read(CounterNow(),TickClock);ObserveClock(TickClock);return value;}
ULONGLONG WINAPI Tick64Hook(){if(!Scoped(_ReturnAddress()))return originalTick64();auto value=(ULONGLONG)clockState.Read(CounterNow(),Tick64Clock);ObserveClock(Tick64Clock);return value;}
DWORD WINAPI MultimediaHook(){if(!Scoped(_ReturnAddress()))return originalMultimedia();auto value=(DWORD)(ULONGLONG)clockState.Read(CounterNow(),MultimediaClock);ObserveClock(MultimediaClock);return value;}
ULONGLONG FileTimeValue(const FILETIME& value){return (ULONGLONG(value.dwHighDateTime)<<32)|value.dwLowDateTime;}
void ReadFileTime(FILETIME* value,unsigned kind){auto ticks=(ULONGLONG)clockState.Read(CounterNow(),kind);value->dwLowDateTime=(DWORD)ticks;value->dwHighDateTime=(DWORD)(ticks>>32);ObserveClock(kind);}
void WINAPI FileTimeHook(FILETIME* value){if(!value||!Scoped(_ReturnAddress())){originalFileTime(value);return;}ReadFileTime(value,FileTimeClock);}
void WINAPI PreciseFileTimeHook(FILETIME* value){if(!value||!Scoped(_ReturnAddress())){originalPreciseFileTime(value);return;}ReadFileTime(value,PreciseFileTimeClock);}
void WINAPI SleepHook(DWORD ms){originalSleep(Scoped(_ReturnAddress())?clockState.Delay(ms):ms);}
DWORD WINAPI SleepExHook(DWORD ms,BOOL alertable){return originalSleepEx(Scoped(_ReturnAddress())?clockState.Delay(ms):ms,alertable);}
bool RefreshRanges(){
 auto modules=Modules(GetCurrentProcessId());if(modules.empty()||HasWolf(modules))return false;
 Range next[128]{};unsigned count=0;
 for(const auto& module:modules){bool main=module.hModule==GetModuleHandleW(nullptr);if(!main&&!EngineModule(BaseName(module.szModule)))continue;auto path=FinalPath(module.szExePath);main=main&&path==gamePath;
  if(main||(Under(path,gameRoot)&&EngineModule(BaseName(path)))){if(count>=128)return false;next[count++]={(uintptr_t)module.modBaseAddr,(uintptr_t)module.modBaseAddr+module.modBaseSize};}}
 if(!count)return false;
 AcquireSRWLockExclusive(&rangeLock);memcpy(ranges,next,sizeof(next));rangeCount=count;ReleaseSRWLockExclusive(&rangeLock);return true;
}
bool Install(){
 if(initialized)return true;
 if(!RefreshRanges())return false;
 LARGE_INTEGER now{},frequency{};if(!QueryPerformanceCounter(&now)||!QueryPerformanceFrequency(&frequency)||frequency.QuadPart<=0)return false;
 auto multimedia=(void*)GetProcAddress(LoadLibraryW(L"winmm.dll"),"timeGetTime");
 DWORD mm=multimedia?((DWORD(WINAPI*)())multimedia)():GetTickCount();
 auto precise=(void*)GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"GetSystemTimePreciseAsFileTime");
 FILETIME ft{},preciseFt{};GetSystemTimeAsFileTime(&ft);if(precise)((decltype(originalPreciseFileTime))precise)(&preciseFt);else preciseFt=ft;
 clockState.Initialize(now.QuadPart,frequency.QuadPart,GetTickCount(),GetTickCount64(),mm,FileTimeValue(ft),FileTimeValue(preciseFt));
 if(MH_Initialize()!=MH_OK)return false;
 struct Hook {void* address;void* replacement;void** original;};
 std::vector<Hook> hooks={{(void*)QueryPerformanceCounter,(void*)CounterHook,(void**)&originalCounter},{(void*)GetTickCount,(void*)TickHook,(void**)&originalTick},{(void*)GetTickCount64,(void*)Tick64Hook,(void**)&originalTick64},{(void*)Sleep,(void*)SleepHook,(void**)&originalSleep},{(void*)SleepEx,(void*)SleepExHook,(void**)&originalSleepEx},{(void*)GetSystemTimeAsFileTime,(void*)FileTimeHook,(void**)&originalFileTime}};
 if(multimedia)hooks.push_back({multimedia,(void*)MultimediaHook,(void**)&originalMultimedia});
 if(precise)hooks.push_back({precise,(void*)PreciseFileTimeHook,(void**)&originalPreciseFileTime});
 std::vector<void*> created;
 for(const auto& hook:hooks){if(MH_CreateHook(hook.address,hook.replacement,hook.original)!=MH_OK){for(auto address:created)MH_RemoveHook(address);MH_Uninitialize();return false;}created.push_back(hook.address);}
 for(auto address:created)if(MH_QueueEnableHook(address)!=MH_OK){for(auto target:created)MH_RemoveHook(target);MH_Uninitialize();return false;}
 if(MH_ApplyQueued()!=MH_OK){for(auto address:created){MH_DisableHook(address);MH_RemoveHook(address);}MH_Uninitialize();return false;}
 initialized=true;return true;
}
struct Session {HANDLE mapping=nullptr,owner=nullptr;Shared* shared=nullptr;uint64_t ownerCreated=0,targetCreated=0;uint32_t ownerPid=0,targetPid=0;char token[64]{};~Session(){if(shared)UnmapViewOfFile(shared);if(mapping)CloseHandle(mapping);if(owner)CloseHandle(owner);}};
bool Identity(Session& session){auto data=session.shared;return Header(data)&&data->ownerPid==session.ownerPid&&data->targetPid==session.targetPid&&data->ownerCreated==session.ownerCreated&&data->targetCreated==session.targetCreated&&memcmp(data->token,session.token,64)==0;}
void Status(Shared* data,LONG state,DWORD error=Success){Write64(&data->actualRate,Bits(clockState.Rate()));Write64(&data->statusTick,(LONG64)originalTick64());Write64(&data->calls,(LONG64)callCount.load());for(unsigned i=0;i<ClockKinds;i++)Write64(&data->clockCalls[i],(LONG64)clockCalls[i].load());Write64(&data->commandClockMask,(LONG64)commandClockMask.load());InterlockedExchange(&data->modules,(LONG)rangeCount);InterlockedExchange(&data->error,(LONG)error);if(state>=0)InterlockedExchange(&data->state,state);}
DWORD WINAPI Worker(void* raw){
 std::unique_ptr<Session> session((Session*)raw);Shared* data=session->shared;LONG last=InterlockedCompareExchange(&data->ackSeq,0,0);ULONGLONG refreshAt=0;DWORD finalError=Success;
 try {Status(data,1);
 for(;;){
  auto now=originalTick64();
  if(!Identity(*session)){finalError=IdentityChanged;break;}
  auto heartbeat=(ULONGLONG)Read64(&data->heartbeat);
  if(WaitForSingleObject(session->owner,0)!=WAIT_TIMEOUT||heartbeat>now+1000||now>heartbeat+LeaseMs)break;
  if(now>=refreshAt){refreshAt=now+1000;if(!RefreshRanges()){finalError=HasWolf(Modules(GetCurrentProcessId()))?WolfConflict:HookFailure;break;}}
  LONG request=InterlockedCompareExchange(&data->requestSeq,0,0);
  if(request!=last){
   double rate=Double(Read64(&data->requestedRate));uint32_t mode=data->desiredMode;MemoryBarrier();
   if(request!=InterlockedCompareExchange(&data->requestSeq,0,0)){originalSleep(10);continue;}
   DWORD error=Success;if(mode!=1&&mode!=2)error=BadCommand;else if(mode==1&&!ValidRate(rate))error=InvalidRate;
   if(!error){clockState.Set(mode==2?1:rate,CounterNow());commandClockMask=0;Write64(&data->commandClockBaseline,(LONG64)ClockReads());}
   Status(data,-1,error);last=request;InterlockedExchange(&data->ackSeq,last);
   if(!error&&mode==2)break;
  }else Status(data,-1,(DWORD)InterlockedCompareExchange(&data->error,0,0));
  originalSleep(20);
 }}catch(...){finalError=StartFailure;}
 clockState.Set(1,CounterNow());Status(data,finalError?3:2,finalError);active=false;return 0;
}
}
extern "C" __declspec(dllexport) DWORD FusionSpeedProtocolVersion=Version;
extern "C" __declspec(dllexport) DWORD WINAPI FusionSpeedStart(Bootstrap* bootstrap){
 if(!bootstrap||bootstrap->size!=sizeof(Bootstrap)||bootstrap->version!=Version||bootstrap->mapping[159]||bootstrap->token[64])return BadArguments;
 std::wstring mapping=bootstrap->mapping;std::string token=bootstrap->token;
 if(!MappingName(mapping)||!Hex(token))return BadArguments;
 bool idle=false;if(!active.compare_exchange_strong(idle,true))return SessionBusy;
 auto fail=[](DWORD error){active=false;return error;};
 try{
  auto session=std::make_unique<Session>();session->mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,mapping.c_str());if(!session->mapping)return fail(BadMapping);
  session->shared=(Shared*)MapViewOfFile(session->mapping,FILE_MAP_ALL_ACCESS,0,0,Size);auto data=session->shared;
  if(!Header(data)||memcmp(data->token,token.data(),64)!=0||data->targetPid!=GetCurrentProcessId()||data->targetCreated!=Created(GetCurrentProcess()))return fail(BadMapping);
  if(data->desiredMode!=1)return fail(BadCommand);
  session->owner=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,data->ownerPid);
  if(!session->owner||Created(session->owner)!=data->ownerCreated||WaitForSingleObject(session->owner,0)!=WAIT_TIMEOUT)return fail(OwnerUnavailable);
  auto heartbeat=(ULONGLONG)Read64(&data->heartbeat),now=GetTickCount64();if(heartbeat>now+1000||now>heartbeat+LeaseMs)return fail(OwnerUnavailable);
  if(HasWolf(Modules(GetCurrentProcessId())))return fail(WolfConflict);
  gamePath=ProcessPath(GetCurrentProcess());gameRoot=gamePath.substr(0,gamePath.find_last_of(L'\\'));
  if(Forbidden(GetCurrentProcess(),GetCurrentProcessId(),data->ownerPid,gamePath,true))return fail(ForbiddenTarget);
  if(!Install())return fail(HookFailure);
  if(data->desiredMode!=1||WaitForSingleObject(session->owner,0)!=WAIT_TIMEOUT)return fail(OwnerUnavailable);
  session->ownerCreated=data->ownerCreated;session->targetCreated=data->targetCreated;session->ownerPid=data->ownerPid;session->targetPid=data->targetPid;memcpy(session->token,data->token,64);
  HANDLE worker=CreateThread(nullptr,0,Worker,session.get(),0,nullptr);if(!worker)return fail(StartFailure);session.release();CloseHandle(worker);return Success;
 }catch(...){return fail(StartFailure);}
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,void*){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
