#pragma once
#include <intrin.h>
// Virtual clocks are scoped to calls made by the game executable. Transport,
// deadlines and Windows UI clocks remain on wall time. Changes are continuous.
namespace WolfSpeed {
inline decltype(&QueryPerformanceCounter) counter=nullptr;
inline decltype(&GetTickCount) tick=nullptr;
inline decltype(&GetTickCount64) tick64=nullptr;
inline void (WINAPI* sleep)(DWORD)=nullptr;
inline DWORD (WINAPI* multimedia)()=nullptr;
inline SRWLOCK clockLock=SRWLOCK_INIT;
inline SRWLOCK controlLock=SRWLOCK_INIT;
inline uintptr_t first=0,last=0;
inline long long anchor=0,frequency=1;
inline double virtualCounter=0,virtualTicks=0;
inline std::atomic<double> rate{1};
inline std::atomic<bool> authorized{false},installed{false};
inline bool initialized=false;
inline bool GenericClockPresent(){return GetModuleHandleW(L"FusionSpeed.x86.dll")||GetModuleHandleW(L"FusionSpeed.x64.dll");}
inline bool Valid(double next){return std::isfinite(next)&&next>=0.25&&next<=100;}
inline std::atomic<unsigned long long> gameClockCalls{0};
inline std::atomic<unsigned long long> gameSleepCalls{0};
inline bool Game(void* address){auto p=(uintptr_t)address;return p>=first&&p<last;}
inline void Set(double next){
    if(!Valid(next)||!counter)return;
    AcquireSRWLockExclusive(&clockLock);LARGE_INTEGER now{};counter(&now);
    double elapsed=(double)(now.QuadPart-anchor)*rate.load();
    virtualCounter+=elapsed;virtualTicks+=elapsed*1000.0/frequency;anchor=now.QuadPart;rate=next;
    ReleaseSRWLockExclusive(&clockLock);
}
inline double Read(bool ticks){
    AcquireSRWLockShared(&clockLock);LARGE_INTEGER now{};counter(&now);
    double elapsed=(double)(now.QuadPart-anchor)*rate.load();
    double value=ticks?virtualTicks+elapsed*1000.0/frequency:virtualCounter+elapsed;
    ReleaseSRWLockShared(&clockLock);return value;
}
inline BOOL WINAPI Counter(LARGE_INTEGER* value){if(!Game(_ReturnAddress()))return counter(value);if(!value)return FALSE;gameClockCalls.fetch_add(1,std::memory_order_relaxed);value->QuadPart=(long long)Read(false);return TRUE;}
inline DWORD WINAPI Tick(){if(!Game(_ReturnAddress()))return tick();gameClockCalls.fetch_add(1,std::memory_order_relaxed);return (DWORD)(ULONGLONG)Read(true);}
inline ULONGLONG WINAPI Tick64(){if(!Game(_ReturnAddress()))return tick64();gameClockCalls.fetch_add(1,std::memory_order_relaxed);return (ULONGLONG)Read(true);}
inline DWORD WINAPI Multimedia(){if(!Game(_ReturnAddress()))return multimedia();gameClockCalls.fetch_add(1,std::memory_order_relaxed);return (DWORD)(ULONGLONG)Read(true);}
inline void WINAPI SleepScaled(DWORD ms){if(ms&&ms!=INFINITE&&Game(_ReturnAddress())){gameSleepCalls.fetch_add(1,std::memory_order_relaxed);ms=(DWORD)(std::max)(1.0,std::ceil(ms/rate.load()));}sleep(ms);}
inline bool Initialize(){
    // Translation and status reads must not install speed hooks. Remember the
    // original clocks; the first authorized non-1 request installs them below.
    if(initialized)return true;
    counter=QueryPerformanceCounter;tick=GetTickCount;tick64=GetTickCount64;sleep=Sleep;
    auto base=(unsigned char*)GetModuleHandleW(nullptr);auto dos=(IMAGE_DOS_HEADER*)base;auto pe=(IMAGE_NT_HEADERS32*)(base+dos->e_lfanew);
    first=(uintptr_t)base;last=first+pe->OptionalHeader.SizeOfImage;
    LARGE_INTEGER now{},hz{};if(!counter(&now)||!QueryPerformanceFrequency(&hz)||hz.QuadPart<=0)return false;
    anchor=now.QuadPart;frequency=hz.QuadPart;virtualCounter=(double)now.QuadPart;virtualTicks=(double)GetTickCount64();
    initialized=true;return true;
}
inline bool Install(){
    if(installed.load())return true;
    if(GenericClockPresent()||!Initialize())return false;
    void* targets[5]{};unsigned count=0;
    auto add=[&](void* target,void* detour,void** original){if(MH_CreateHook(target,detour,original)!=MH_OK)return false;targets[count++]=target;return true;};
    bool ready=add((void*)QueryPerformanceCounter,(void*)Counter,(void**)&counter)&&
        add((void*)GetTickCount,(void*)Tick,(void**)&tick)&&add((void*)GetTickCount64,(void*)Tick64,(void**)&tick64)&&
        add((void*)Sleep,(void*)SleepScaled,(void**)&sleep);
    auto winmm=GetModuleHandleW(L"winmm.dll");auto address=winmm?GetProcAddress(winmm,"timeGetTime"):nullptr;
    if(ready&&address)ready=add((void*)address,(void*)Multimedia,(void**)&multimedia);
    if(ready)for(unsigned n=0;n<count;n++)if(MH_EnableHook(targets[n])!=MH_OK){ready=false;break;}
    if(!ready){
        // Remove only hooks created by this attempt, never another component's.
        for(unsigned n=0;n<count;n++){MH_DisableHook(targets[n]);MH_RemoveHook(targets[n]);}
        counter=QueryPerformanceCounter;tick=GetTickCount;tick64=GetTickCount64;sleep=Sleep;multimedia=nullptr;
        return false;
    }
    installed=true;return true;
}
inline void Authorize(){AcquireSRWLockExclusive(&controlLock);authorized=true;ReleaseSRWLockExclusive(&controlLock);}
inline void Revoke(){
    AcquireSRWLockExclusive(&controlLock);authorized=false;Set(1);ReleaseSRWLockExclusive(&controlLock);
}
inline bool Apply(double next,const char*& error){
    AcquireSRWLockExclusive(&controlLock);bool ok=false;
    if(!Valid(next))error=u8"变速范围为 0.25 到 100 倍。";
    else if(next==1){Set(1);ok=true;}
    else if(!authorized.load())error=u8"请先开启游戏修改。";
    else if(GenericClockPresent())error=u8"速度连接已变化。请保存并退出游戏，再从新版启动后重试。";
    else if(!Install())error=u8"未能建立当前游戏的速度连接，未应用倍率。";
    else{Set(next);ok=true;}
    ReleaseSRWLockExclusive(&controlLock);return ok;
}
}
