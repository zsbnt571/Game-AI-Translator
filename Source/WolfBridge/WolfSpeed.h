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
inline uintptr_t first=0,last=0;
inline long long anchor=0,frequency=1;
inline double virtualCounter=0,virtualTicks=0;
inline std::atomic<double> rate{1};
inline bool Valid(double next){return std::isfinite(next)&&next>=0.25&&next<=100;}
#ifdef FUSION_TEST_CAPTURE
inline std::atomic<unsigned long long> gameClockCalls{0};
#endif
inline bool Game(void* address){auto p=(uintptr_t)address;bool game=p>=first&&p<last;
#ifdef FUSION_TEST_CAPTURE
 if(game)gameClockCalls.fetch_add(1,std::memory_order_relaxed);
#endif
 return game;}
inline void Set(double next){
    if(!Valid(next))return;
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
inline BOOL WINAPI Counter(LARGE_INTEGER* value){if(!Game(_ReturnAddress()))return counter(value);if(!value)return FALSE;value->QuadPart=(long long)Read(false);return TRUE;}
inline DWORD WINAPI Tick(){return Game(_ReturnAddress())?(DWORD)(ULONGLONG)Read(true):tick();}
inline ULONGLONG WINAPI Tick64(){return Game(_ReturnAddress())?(ULONGLONG)Read(true):tick64();}
inline DWORD WINAPI Multimedia(){return Game(_ReturnAddress())?(DWORD)(ULONGLONG)Read(true):multimedia();}
inline void WINAPI SleepScaled(DWORD ms){if(ms&&ms!=INFINITE&&Game(_ReturnAddress()))ms=(DWORD)(std::max)(1.0,std::ceil(ms/rate.load()));sleep(ms);}
inline bool Install(){
    auto base=(unsigned char*)GetModuleHandleW(nullptr);auto dos=(IMAGE_DOS_HEADER*)base;auto pe=(IMAGE_NT_HEADERS32*)(base+dos->e_lfanew);
    first=(uintptr_t)base;last=first+pe->OptionalHeader.SizeOfImage;
    LARGE_INTEGER now{},hz{};QueryPerformanceCounter(&now);QueryPerformanceFrequency(&hz);
    anchor=now.QuadPart;frequency=hz.QuadPart;virtualCounter=(double)now.QuadPart;virtualTicks=(double)GetTickCount64();
    if(MH_CreateHook((void*)QueryPerformanceCounter,(void*)Counter,(void**)&counter)!=MH_OK||
       MH_CreateHook((void*)GetTickCount,(void*)Tick,(void**)&tick)!=MH_OK||
       MH_CreateHook((void*)GetTickCount64,(void*)Tick64,(void**)&tick64)!=MH_OK||
       MH_CreateHook((void*)Sleep,(void*)SleepScaled,(void**)&sleep)!=MH_OK)return false;
    auto winmm=GetModuleHandleW(L"winmm.dll");auto address=winmm?GetProcAddress(winmm,"timeGetTime"):nullptr;
    return !address||MH_CreateHook((void*)address,(void*)Multimedia,(void**)&multimedia)==MH_OK;
}
}
